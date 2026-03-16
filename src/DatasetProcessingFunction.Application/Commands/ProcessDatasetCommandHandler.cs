using System.Diagnostics;
using DatasetProcessingFunction.Application.Interfaces;
using DatasetProcessingFunction.Application.Notifications;
using DatasetProcessingFunction.Domain.Services;
using DatasetProcessingFunction.Domain.ValueObjects;
using DatasetProcessingFunction.Domain.Telemetry;
using MediatR;

namespace DatasetProcessingFunction.Application.Commands;

public sealed class ProcessDatasetCommandHandler : IRequestHandler<ProcessDatasetCommand, ProcessDatasetResult>
{
    private readonly IDatasetReader _reader;
    private readonly IBronzeWriter _writer;
    private readonly ISchemaRegistry _registry;
    private readonly IMediator _mediator;
    private readonly CsvParserService _csvParser;
    private readonly SchemaTransformer _transformer;
    private readonly DataQualityValidator _validator;
    private readonly RecordEnricher _enricher;

    public ProcessDatasetCommandHandler(
        IDatasetReader reader,
        IBronzeWriter writer,
        ISchemaRegistry registry,
        IMediator mediator,
        CsvParserService csvParser,
        SchemaTransformer transformer,
        DataQualityValidator validator)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        _csvParser = csvParser ?? throw new ArgumentNullException(nameof(csvParser));
        _transformer = transformer ?? throw new ArgumentNullException(nameof(transformer));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _enricher = new RecordEnricher();
    }

    public async Task<ProcessDatasetResult> Handle(
        ProcessDatasetCommand request,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        using var rootActivity = DatasetActivitySource.Source.StartActivity("dataset.process");
        rootActivity?.SetTag("dataset_id", request.DatasetId);
        rootActivity?.SetTag("vendor_id", request.VendorId);

        // 1. Resolve schema mapping
        var mapping = await _registry.GetAsync(request.VendorId, request.SchemaVersion, cancellationToken);
        if (mapping is null)
            return ProcessDatasetResult.Fail($"No schema mapping found for {request.VendorId}:{request.SchemaVersion}");

        try
        {
            // 2. Read CSV from ADLS
            Stream csvStream;
            using (var readActivity = DatasetActivitySource.Source.StartActivity("dataset.adls.read"))
            {
                readActivity?.SetTag("dataset_id", request.DatasetId);
                csvStream = await _reader.ReadAsync(request.StoragePath, cancellationToken);
            }

            // 3. Parse CSV
            IReadOnlyList<RawRecord> rawRecords;
            using (var parseActivity = DatasetActivitySource.Source.StartActivity("dataset.csv.parse"))
            {
                parseActivity?.SetTag("dataset_id", request.DatasetId);
                rawRecords = await _csvParser.ParseAsync(csvStream, mapping.Delimiter, cancellationToken).ToListAsync(cancellationToken);
                parseActivity?.SetTag("record_count", rawRecords.Count);
            }
            await csvStream.DisposeAsync();

            // 4. Validate
            ValidationResult validationResult;
            using (var validateActivity = DatasetActivitySource.Source.StartActivity("dataset.validation.run"))
            {
                validateActivity?.SetTag("dataset_id", request.DatasetId);
                validationResult = _validator.Validate(rawRecords, mapping);
                validateActivity?.SetTag("pass_count", validationResult.PassCount);
                validateActivity?.SetTag("fail_count", validationResult.FailCount);
            }

            if (!validationResult.IsSuccess)
                throw new DatasetValidationException(request.DatasetId, validationResult);

            // 5. Transform + Enrich
            IReadOnlyList<CanonicalRecord> canonicalRecords;
            using (var transformActivity = DatasetActivitySource.Source.StartActivity("dataset.transform"))
            {
                transformActivity?.SetTag("dataset_id", request.DatasetId);
                var ingestionTime = DateTimeOffset.UtcNow;
                var transformed = _transformer.Transform(rawRecords, mapping);
                canonicalRecords = transformed
                    .Select(r => _enricher.Enrich(r, request.DatasetId, request.SchemaVersion, ingestionTime))
                    .ToList();
                transformActivity?.SetTag("record_count", canonicalRecords.Count);
            }

            // 6. Write Bronze
            Uri bronzePath;
            using (var writeActivity = DatasetActivitySource.Source.StartActivity("dataset.bronze.write"))
            {
                writeActivity?.SetTag("dataset_id", request.DatasetId);
                var date = DateOnly.FromDateTime(DateTime.UtcNow);
                bronzePath = await _writer.WriteAsync(new DatasetId(request.DatasetId), date, canonicalRecords, cancellationToken);
                writeActivity?.SetTag("bronze_path", bronzePath.ToString());
            }

            // 7. Publish downstream notification
            await _mediator.Publish(
                new DatasetBronzeAvailableNotification(
                    request.DatasetId,
                    canonicalRecords.Count,
                    bronzePath,
                    request.SchemaVersion,
                    request.CorrelationId,
                    DateTimeOffset.UtcNow),
                cancellationToken);

            sw.Stop();
            return ProcessDatasetResult.Ok(canonicalRecords.Count, bronzePath);
        }
        catch (DatasetValidationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            rootActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }
}
