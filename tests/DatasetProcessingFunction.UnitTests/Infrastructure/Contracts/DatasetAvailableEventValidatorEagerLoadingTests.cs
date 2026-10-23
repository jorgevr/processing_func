using DatasetProcessingFunction.Application.Interfaces;
using DatasetProcessingFunction.Infrastructure.Configuration;
using DatasetProcessingFunction.Infrastructure.Contracts;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DatasetProcessingFunction.UnitTests.Infrastructure.Contracts;

/// <summary>
/// R3.7b: <see cref="DatasetAvailableEventValidator"/>'s constructor eagerly loads, parses and
/// sanity-checks the vendored schema — so a missing, unreadable, invalid, or mismatched vendored
/// copy fails host start, not the first inbound message. All cases here are inline literals or
/// direct calls against this same real assembly — no filesystem/workspace dependency, so this
/// passes in a standalone clone of this repo alone, not just inside the anomalia-platform
/// workspace.
/// </summary>
public sealed class DatasetAvailableEventValidatorEagerLoadingTests
{
    private static readonly string ExpectedId = DatasetAvailableEventValidator.ExpectedSchemaId;

    // ---------------------------------------------------------------------------------------
    // (a) Eager-at-registration mechanism — this is what the mutation proof (task step 4) targets.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The real vendored schema is good in this repo, so this cannot demonstrate a literal
    /// "missing schema" failure through the public registration path without actually breaking
    /// the embedded resource. What it proves instead is the mechanism the fix depends on:
    /// <c>AddDatasetEventValidator</c> must construct the validator (forcing the schema check)
    /// BEFORE registering it, not hand DI a type mapping it would only construct lazily. That
    /// difference is structural and observable on the <see cref="ServiceDescriptor"/> itself —
    /// <see cref="ServiceDescriptor.ImplementationInstance"/> is set for an already-built
    /// instance, <see langword="null"/> for a type mapping DI would construct later. Reverting
    /// <c>AddDatasetEventValidator</c> to the pre-fix <c>AddSingleton&lt;IDatasetEventValidator,
    /// DatasetAvailableEventValidator&gt;()</c> flips this to a type mapping and this test fails
    /// (see the R3.7b report for the mutation run).
    /// </summary>
    [Fact]
    public void AddDatasetEventValidator_RegistersAnAlreadyConstructedInstance_NotATypeMapping()
    {
        var services = new ServiceCollection();

        services.AddDatasetEventValidator();

        var descriptor = services.Single(d => d.ServiceType == typeof(IDatasetEventValidator));
        descriptor.ImplementationInstance.Should().NotBeNull(
            "the validator — and therefore its schema load — must already be constructed by the " +
            "time it is registered, not deferred to the first time something resolves " +
            "IDatasetEventValidator from the container");
        descriptor.ImplementationInstance.Should().BeOfType<DatasetAvailableEventValidator>();
    }

    // ---------------------------------------------------------------------------------------
    // (a) "missing" / "unreadable" — the exact code path the constructor's eager check runs,
    // exercised directly with inputs the real embedded resource can't represent (it exists and is
    // readable in this repo).
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void LoadFromEmbeddedResource_ResourceDoesNotExist_ThrowsNamingTheResource_BeforeAnyEventIsEverValidated()
    {
        var act = () => DatasetAvailableEventValidator.LoadFromEmbeddedResource(
            typeof(DatasetAvailableEventValidator).Assembly, "does.not.exist.dataset-available.v1.json");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*does.not.exist.dataset-available.v1.json*")
            .WithMessage("*missing*");
    }

    [Fact]
    public void LoadFromStream_StreamThrowsOnRead_ThrowsNamingTheSchemaAndTheUnderlyingCause()
    {
        using var unreadable = new UnreadableStream();

        var act = () => DatasetAvailableEventValidator.LoadFromStream(unreadable, "dataset-available.v1.json");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*dataset-available.v1.json*")
            .WithMessage("*disk is on fire*");
    }

    private sealed class UnreadableStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("disk is on fire");
    }

    // ---------------------------------------------------------------------------------------
    // (b) "not a valid JSON Schema" — throws at startup, message names the schema.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ParseAndValidate_NotWellFormedJson_ThrowsNamingTheSchemaAndTheParseFailure()
    {
        var act = () => DatasetAvailableEventValidator.ParseAndValidate(
            "{ this is not json at all !!!", "dataset-available.v1.json");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*dataset-available.v1.json*")
            .WithMessage("*not a valid JSON Schema*");
    }

    // ---------------------------------------------------------------------------------------
    // $id/dataschema mismatch — valid JSON Schema, but not the schema this validator expects.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ParseAndValidate_WellFormedSchemaButWrongId_ThrowsNamingBothTheActualAndExpectedId()
    {
        const string wrongId = "urn:r3-7b-test:wrong-schema-id";
        var schemaWithWrongId = $$"""{ "$id": "{{wrongId}}", "type": "object" }""";

        var act = () => DatasetAvailableEventValidator.ParseAndValidate(
            schemaWithWrongId, "dataset-available.v1.json");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*dataset-available.v1.json*")
            .WithMessage($"*{wrongId}*")
            .WithMessage($"*{ExpectedId}*");
    }

    // ---------------------------------------------------------------------------------------
    // (c) Real vendored schema: host builds, and Validate() behaviour is unchanged.
    // ---------------------------------------------------------------------------------------

    private const string ValidV1Event = """
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

    private const string UnversionedTypeEvent = """
        {"specversion":"1.0","type":"solar.pvdaq.dataset.available","source":"/energy-ingestion-boundary/pvdaq"}
        """;

    [Fact]
    public void RealVendoredSchema_HostBuilds_AndValidatorStillAcceptsV1AndRejectsUnversionedType()
    {
        var services = new ServiceCollection();

        // The exact call Program.cs makes. If this throws, the test itself fails here — proving
        // "the host builds" is not a separate claim from "this call does not throw".
        services.AddDatasetEventValidator();

        using var provider = services.BuildServiceProvider();
        var validator = provider.GetRequiredService<IDatasetEventValidator>();

        var validResult = validator.Validate(ValidV1Event);
        validResult.IsValid.Should().BeTrue("existing behaviour: a well-formed v1 event must still validate");
        validResult.IsUnrecognizedType.Should().BeFalse();

        var unversionedResult = validator.Validate(UnversionedTypeEvent);
        unversionedResult.IsValid.Should().BeFalse();
        unversionedResult.IsUnrecognizedType.Should().BeTrue(
            "existing behaviour: an unversioned type must still be rejected as unrecognized");
        unversionedResult.ActualType.Should().Be("solar.pvdaq.dataset.available");
    }
}
