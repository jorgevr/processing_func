using MediatR;

namespace DatasetProcessingFunction.Application.Commands;

public sealed record ProcessDatasetCommand(
    string DatasetId,
    Uri StoragePath,
    string VendorId,
    string SchemaVersion,
    string CorrelationId) : IRequest<ProcessDatasetResult>;
