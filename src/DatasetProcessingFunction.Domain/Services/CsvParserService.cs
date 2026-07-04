using System.Runtime.CompilerServices;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using DatasetProcessingFunction.Domain.Exceptions;
using DatasetProcessingFunction.Domain.ValueObjects;

namespace DatasetProcessingFunction.Domain.Services;

public sealed class CsvParserService
{
    public async IAsyncEnumerable<RawRecord> ParseAsync(
        Stream csv,
        char delimiter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Validate encoding before parsing — copy to memory for safe re-reading
        var buffer = new MemoryStream();
        await csv.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        ValidateEncoding(buffer.ToArray());
        buffer.Position = 0;

        var config = new CsvConfiguration(System.Globalization.CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter.ToString(),
            HasHeaderRecord = true,
            MissingFieldFound = null,
            BadDataFound = null
        };

        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: false);
        using var csvReader = new CsvReader(reader, config);

        if (!await csvReader.ReadAsync())
            yield break;

        csvReader.ReadHeader();
        var headers = csvReader.HeaderRecord ?? Array.Empty<string>();

        var rowIndex = 0;
        var hasRows = false;
        while (await csvReader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowIndex++;
            hasRows = true;
            var fields = new Dictionary<string, string>(headers.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var header in headers)
            {
                fields[header] = csvReader.GetField(header) ?? string.Empty;
            }
            yield return new RawRecord(rowIndex, fields);
        }

        // FR-006c: header-only CSV (zero data rows) must be rejected
        if (!hasRows)
            throw new EmptyDatasetException();
    }

    private static void ValidateEncoding(byte[] bytes)
    {
        // Reject known non-UTF-8 BOMs
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            throw new UnsupportedEncodingException("UTF-16 LE");
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            throw new UnsupportedEncodingException("UTF-16 BE");
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            throw new UnsupportedEncodingException("UTF-32 BE");

        // Check for invalid UTF-8 byte sequences in first 8KB sample
        var sampleLength = Math.Min(bytes.Length, 8192);
        var sample = bytes.AsSpan(0, sampleLength);

        var decoderState = Encoding.UTF8.GetDecoder();
        var charBuffer = new char[sampleLength];
        try
        {
            decoderState.Convert(sample, charBuffer, true, out _, out _, out _);
        }
        catch (DecoderFallbackException)
        {
            throw new UnsupportedEncodingException("non-UTF-8");
        }

        // Check for replacement characters indicating encoding mismatch
        var decoded = Encoding.UTF8.GetString(sample);
        if (decoded.Contains('\uFFFD') && sampleLength > 0)
            throw new UnsupportedEncodingException("non-UTF-8");
    }
}
