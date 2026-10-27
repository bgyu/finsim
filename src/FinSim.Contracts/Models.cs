namespace FinSim.Contracts;

/// <summary>
/// A deliberately small deterministic workload. Bounds are enforced at the
/// API boundary; records remain transport-only and contain no policy logic.
/// </summary>
public sealed record SimulationRequest(
    IReadOnlyList<long> Values,
    int ChunkSize = 10,
    int MaxParallel = 5);

public sealed record ChunkData(
    Guid WorkflowId,
    int ChunkIndex,
    IReadOnlyList<long> Values);

public sealed record ChunkResult(
    Guid WorkflowId,
    int ChunkIndex,
    int Count,
    long Sum,
    long Minimum,
    long Maximum);

public sealed record SimulationReport(
    Guid WorkflowId,
    string Status,
    int ChunkCount,
    int ValueCount,
    long Sum,
    long Minimum,
    long Maximum,
    DateTimeOffset CompletedAt,
    string? Error = null);

public sealed record CreateSimulationResponse(Guid WorkflowId, string Status);

public sealed record WorkflowStatusResponse(
    Guid WorkflowId,
    string Status,
    string? MasterJobId,
    string? WorkerJobId,
    string? Error);

public sealed record SubmissionIntent(
    Guid WorkflowId,
    string Role,
    string Marker);

public sealed record SubmissionRecord(
    Guid WorkflowId,
    string Role,
    string Marker,
    string JobId);

public sealed record WorkflowOwner(Guid WorkflowId, string SubjectSha256);
