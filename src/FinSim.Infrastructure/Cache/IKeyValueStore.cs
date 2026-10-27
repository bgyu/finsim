using System.Text.Json;

namespace FinSim.Infrastructure.Cache;

public enum PutResult
{
    Created,
    Replayed,
    Conflict
}

public interface IKeyValueStore
{
    Task<PutResult> PutIfAbsentAsync(
        string key,
        JsonElement value,
        CancellationToken cancellationToken = default);

    Task<JsonElement?> GetAsync(
        string key,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        string key,
        CancellationToken cancellationToken = default);
}
