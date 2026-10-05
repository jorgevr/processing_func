using DatasetProcessingFunction.Infrastructure.Contracts;
using FluentAssertions;

namespace DatasetProcessingFunction.UnitTests.Infrastructure.Contracts;

/// <summary>
/// R3.7 (ADR 0002, ADR 0004 rule 3). Cases are inline literals — not read from
/// contracts/examples/ — so this test passes in a standalone clone of this repo alone, not just
/// inside the anomalia-platform workspace.
/// </summary>
public sealed class DatasetAvailableEventValidatorTests
{
    private readonly DatasetAvailableEventValidator _validator = new();

    private const string ValidMinimalLocal = """
        {
          "specversion": "1.0",
          "type": "solar.pvdaq.dataset.available.v1",
          "source": "/energy-ingestion-boundary/pvdaq",
          "id": "3f2a7c58-9b1e-4d0a-8f6c-2e5b1a7d9c04",
          "time": "2026-09-24T06:12:41.884213+00:00",
          "datacontenttype": "application/json",
          "dataschema": "https://github.com/jorgevr/anomalIA-app/contracts/dataset-available.v1.json",
          "tenant_id": "anomalia-dev",
          "source_vendor": "PVDAQ",
          "schema_version": "v1",
          "mapping_version": "unknown",
          "correlation_id": "b1d4e8f0-6a22-4c17-9e83-5d0c7f31ab99",
          "ingestion_timestamp": "2026-09-24T06:12:41.884213+00:00",
          "traceparent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
          "data": {
            "site_id": 9068,
            "category": "ac_power",
            "file_format": "csv",
            "storage_path": "http://127.0.0.1:10000/devstoreaccount1/bronze/source=pvdaq/dataset=9068_ac_power/ingestion_date=2026-09-24/9068_ac_power_v1.csv",
            "version": 1,
            "ingestion_id": "b1d4e8f0-6a22-4c17-9e83-5d0c7f31ab99",
            "source_url": "https://oedi-data-lake.s3.amazonaws.com/pvdaq/2023-solar-data-prize/9068_OEDI/data/9068_ac_power_data.csv",
            "file_size": 1048576,
            "file_hash": "1d4b479980b6a38bd006688e33f804f3f7bcb48ad273a3a4e99e1703e50cbb87"
          }
        }
        """;

    private const string ValidRerunCloudWithAdditiveDeviceId = """
        {
          "specversion": "1.0",
          "type": "solar.pvdaq.dataset.available.v1",
          "source": "/energy-ingestion-boundary/pvdaq",
          "id": "7c9e1b40-2d35-4a6f-b8e1-04c7a2f95d63",
          "time": "2026-09-25T03:01:09.220145+00:00",
          "datacontenttype": "application/json",
          "dataschema": "https://github.com/jorgevr/anomalIA-app/contracts/dataset-available.v1.json",
          "tenant_id": "anomalia-dev",
          "source_vendor": "PVDAQ",
          "schema_version": "v1",
          "mapping_version": "v2",
          "correlation_id": "5e3b0a71-c4d8-4f29-9a06-7b81e2d3c5f4",
          "ingestion_timestamp": "2026-09-25T03:01:09.220145+00:00",
          "traceparent": "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
          "data": {
            "site_id": 9068,
            "category": "ac_power",
            "file_format": "csv",
            "storage_path": "https://anomaliadata.blob.core.windows.net/bronze/source=pvdaq/dataset=9068_ac_power/ingestion_date=2026-09-25/9068_ac_power_v3.csv",
            "version": 3,
            "ingestion_id": "5e3b0a71-c4d8-4f29-9a06-7b81e2d3c5f4",
            "source_url": "https://oedi-data-lake.s3.amazonaws.com/pvdaq/2023-solar-data-prize/9068_OEDI/data/9068_ac_power_data.csv",
            "file_size": 1153434,
            "file_hash": "5ec7300926308cd9304077d9c5f9d7aae8330ff255fb047a124e4ff9b5ec1bf8",
            "device_id": "9068-inv-02"
          }
        }
        """;

    private const string InvalidAbfssStoragePath = """
        {
          "specversion": "1.0",
          "type": "solar.pvdaq.dataset.available.v1",
          "source": "/energy-ingestion-boundary/pvdaq",
          "id": "3f2a7c58-9b1e-4d0a-8f6c-2e5b1a7d9c04",
          "time": "2026-09-24T06:12:41.884213+00:00",
          "datacontenttype": "application/json",
          "dataschema": "https://github.com/jorgevr/anomalIA-app/contracts/dataset-available.v1.json",
          "tenant_id": "anomalia-dev",
          "source_vendor": "PVDAQ",
          "schema_version": "v1",
          "mapping_version": "unknown",
          "correlation_id": "b1d4e8f0-6a22-4c17-9e83-5d0c7f31ab99",
          "ingestion_timestamp": "2026-09-24T06:12:41.884213+00:00",
          "traceparent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
          "data": {
            "site_id": 9068,
            "category": "ac_power",
            "file_format": "csv",
            "storage_path": "abfss://bronze@anomaliadata.dfs.core.windows.net/source=pvdaq/dataset=9068_ac_power/ingestion_date=2026-09-24/9068_ac_power_v1.csv",
            "version": 1,
            "ingestion_id": "b1d4e8f0-6a22-4c17-9e83-5d0c7f31ab99",
            "source_url": "https://oedi-data-lake.s3.amazonaws.com/pvdaq/2023-solar-data-prize/9068_OEDI/data/9068_ac_power_data.csv",
            "file_size": 1048576,
            "file_hash": "1d4b479980b6a38bd006688e33f804f3f7bcb48ad273a3a4e99e1703e50cbb87"
          }
        }
        """;

    private const string InvalidMissingDataschema = """
        {
          "specversion": "1.0",
          "type": "solar.pvdaq.dataset.available.v1",
          "source": "/energy-ingestion-boundary/pvdaq",
          "id": "3f2a7c58-9b1e-4d0a-8f6c-2e5b1a7d9c04",
          "time": "2026-09-24T06:12:41.884213+00:00",
          "datacontenttype": "application/json",
          "tenant_id": "anomalia-dev",
          "source_vendor": "PVDAQ",
          "schema_version": "v1",
          "mapping_version": "unknown",
          "correlation_id": "b1d4e8f0-6a22-4c17-9e83-5d0c7f31ab99",
          "ingestion_timestamp": "2026-09-24T06:12:41.884213+00:00",
          "traceparent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
          "data": {
            "site_id": 9068,
            "category": "ac_power",
            "file_format": "csv",
            "storage_path": "http://127.0.0.1:10000/devstoreaccount1/bronze/source=pvdaq/dataset=9068_ac_power/ingestion_date=2026-09-24/9068_ac_power_v1.csv",
            "version": 1,
            "ingestion_id": "b1d4e8f0-6a22-4c17-9e83-5d0c7f31ab99",
            "source_url": "https://oedi-data-lake.s3.amazonaws.com/pvdaq/2023-solar-data-prize/9068_OEDI/data/9068_ac_power_data.csv",
            "file_size": 1048576,
            "file_hash": "1d4b479980b6a38bd006688e33f804f3f7bcb48ad273a3a4e99e1703e50cbb87"
          }
        }
        """;

    private const string InvalidUnknownEnvelopeAttribute = """
        {
          "specversion": "1.0",
          "type": "solar.pvdaq.dataset.available.v1",
          "source": "/energy-ingestion-boundary/pvdaq",
          "id": "3f2a7c58-9b1e-4d0a-8f6c-2e5b1a7d9c04",
          "time": "2026-09-24T06:12:41.884213+00:00",
          "datacontenttype": "application/json",
          "dataschema": "https://github.com/jorgevr/anomalIA-app/contracts/dataset-available.v1.json",
          "tenant_id": "anomalia-dev",
          "source_vendor": "PVDAQ",
          "schema_version": "v1",
          "mapping_version": "unknown",
          "correlation_id": "b1d4e8f0-6a22-4c17-9e83-5d0c7f31ab99",
          "ingestion_timestamp": "2026-09-24T06:12:41.884213+00:00",
          "traceparent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
          "data": {
            "site_id": 9068,
            "category": "ac_power",
            "file_format": "csv",
            "storage_path": "http://127.0.0.1:10000/devstoreaccount1/bronze/source=pvdaq/dataset=9068_ac_power/ingestion_date=2026-09-24/9068_ac_power_v1.csv",
            "version": 1,
            "ingestion_id": "b1d4e8f0-6a22-4c17-9e83-5d0c7f31ab99",
            "source_url": "https://oedi-data-lake.s3.amazonaws.com/pvdaq/2023-solar-data-prize/9068_OEDI/data/9068_ac_power_data.csv",
            "file_size": 1048576,
            "file_hash": "1d4b479980b6a38bd006688e33f804f3f7bcb48ad273a3a4e99e1703e50cbb87"
          },
          "mapping_hint": "auto"
        }
        """;

    private const string InvalidUnversionedType = """
        {
          "specversion": "1.0",
          "type": "solar.pvdaq.dataset.available",
          "source": "/energy-ingestion-boundary/pvdaq",
          "id": "3f2a7c58-9b1e-4d0a-8f6c-2e5b1a7d9c04",
          "time": "2026-09-24T06:12:41.884213+00:00",
          "datacontenttype": "application/json",
          "dataschema": "https://github.com/jorgevr/anomalIA-app/contracts/dataset-available.v1.json",
          "tenant_id": "anomalia-dev",
          "source_vendor": "PVDAQ",
          "schema_version": "v1",
          "mapping_version": "unknown",
          "correlation_id": "b1d4e8f0-6a22-4c17-9e83-5d0c7f31ab99",
          "ingestion_timestamp": "2026-09-24T06:12:41.884213+00:00",
          "traceparent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
          "data": {
            "site_id": 9068,
            "category": "ac_power",
            "file_format": "csv",
            "storage_path": "http://127.0.0.1:10000/devstoreaccount1/bronze/source=pvdaq/dataset=9068_ac_power/ingestion_date=2026-09-24/9068_ac_power_v1.csv",
            "version": 1,
            "ingestion_id": "b1d4e8f0-6a22-4c17-9e83-5d0c7f31ab99",
            "source_url": "https://oedi-data-lake.s3.amazonaws.com/pvdaq/2023-solar-data-prize/9068_OEDI/data/9068_ac_power_data.csv",
            "file_size": 1048576,
            "file_hash": "1d4b479980b6a38bd006688e33f804f3f7bcb48ad273a3a4e99e1703e50cbb87"
          }
        }
        """;

    private const string InvalidZeroedTraceparent = """
        {
          "specversion": "1.0",
          "type": "solar.pvdaq.dataset.available.v1",
          "source": "/energy-ingestion-boundary/pvdaq",
          "id": "3f2a7c58-9b1e-4d0a-8f6c-2e5b1a7d9c04",
          "time": "2026-09-24T06:12:41.884213+00:00",
          "datacontenttype": "application/json",
          "dataschema": "https://github.com/jorgevr/anomalIA-app/contracts/dataset-available.v1.json",
          "tenant_id": "anomalia-dev",
          "source_vendor": "PVDAQ",
          "schema_version": "v1",
          "mapping_version": "unknown",
          "correlation_id": "b1d4e8f0-6a22-4c17-9e83-5d0c7f31ab99",
          "ingestion_timestamp": "2026-09-24T06:12:41.884213+00:00",
          "traceparent": "00-00000000000000000000000000000000-00f067aa0ba902b7-01",
          "data": {
            "site_id": 9068,
            "category": "ac_power",
            "file_format": "csv",
            "storage_path": "http://127.0.0.1:10000/devstoreaccount1/bronze/source=pvdaq/dataset=9068_ac_power/ingestion_date=2026-09-24/9068_ac_power_v1.csv",
            "version": 1,
            "ingestion_id": "b1d4e8f0-6a22-4c17-9e83-5d0c7f31ab99",
            "source_url": "https://oedi-data-lake.s3.amazonaws.com/pvdaq/2023-solar-data-prize/9068_OEDI/data/9068_ac_power_data.csv",
            "file_size": 1048576,
            "file_hash": "1d4b479980b6a38bd006688e33f804f3f7bcb48ad273a3a4e99e1703e50cbb87"
          }
        }
        """;

    [Theory]
    [InlineData(ValidMinimalLocal)]
    [InlineData(ValidRerunCloudWithAdditiveDeviceId)]
    public void Validate_ValidEnvelope_ReturnsValid(string json)
    {
        var result = _validator.Validate(json);

        result.IsValid.Should().BeTrue();
        result.IsUnrecognizedType.Should().BeFalse();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_AdditiveUnknownDataField_StillValidates()
    {
        // ADR 0002 rule 2: data is open — an additive field (device_id, here) must not fail
        // validation against an older vendored copy, or ADR 0006 forward compatibility breaks.
        var result = _validator.Validate(ValidRerunCloudWithAdditiveDeviceId);

        result.IsValid.Should().BeTrue("an additive data field is a minor version, not a violation");
    }

    [Fact]
    public void Validate_UnversionedType_ReturnsUnrecognizedType_DistinctFromOtherFailures()
    {
        var result = _validator.Validate(InvalidUnversionedType);

        result.IsValid.Should().BeFalse();
        result.IsUnrecognizedType.Should().BeTrue(
            "a wrong/unversioned type must be reported distinctly from a malformed envelope");
        result.ActualType.Should().Be("solar.pvdaq.dataset.available");
    }

    [Theory]
    [InlineData(InvalidAbfssStoragePath)]
    [InlineData(InvalidMissingDataschema)]
    [InlineData(InvalidUnknownEnvelopeAttribute)]
    [InlineData(InvalidZeroedTraceparent)]
    public void Validate_InvalidEnvelope_ReturnsInvalid_NotUnrecognizedType(string json)
    {
        var result = _validator.Validate(json);

        result.IsValid.Should().BeFalse();
        result.IsUnrecognizedType.Should().BeFalse(
            "these violate the envelope shape, not the type — must use the other reason code");
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Validate_UnknownEnvelopeAttribute_ErrorNamesTheAttribute()
    {
        var result = _validator.Validate(InvalidUnknownEnvelopeAttribute);

        result.Errors.Should().Contain(e => e.Contains("mapping_hint", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_MissingDataschema_ErrorNamesTheMissingField()
    {
        var result = _validator.Validate(InvalidMissingDataschema);

        result.Errors.Should().Contain(e => e.Contains("dataschema", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_MissingTypeAltogether_ReturnsUnrecognizedTypeWithNullActualType()
    {
        var result = _validator.Validate("""{"specversion":"1.0"}""");

        result.IsUnrecognizedType.Should().BeTrue();
        result.ActualType.Should().BeNull();
    }

    [Fact]
    public void Validate_MalformedJson_Throws()
    {
        // The caller (ProcessDatasetFunction) must catch this the same way it already catches any
        // other deserialization failure — "not valid JSON at all" is not a schema violation.
        var act = () => _validator.Validate("not json");

        act.Should().Throw<System.Text.Json.JsonException>();
    }
}
