using System.Globalization;
using System.Text.RegularExpressions;

namespace FinSim.Contracts;

/// <summary>
/// Centralizes the only key grammar accepted by the demo cache. Callers pass
/// opaque keys; no key can become an arbitrary filesystem path.
/// </summary>
public static partial class CacheKeys
{
    private const int MaximumChunkIndex = 999_999;

    public static string Input(Guid workflowId) =>
        $"workflow/{workflowId:D}/input";

    public static string State(Guid workflowId) =>
        $"workflow/{workflowId:D}/state";

    public static string MasterIntent(Guid workflowId) =>
        $"workflow/{workflowId:D}/intent/master";

    public static string MasterJob(Guid workflowId) =>
        $"workflow/{workflowId:D}/job/master";

    public static string WorkerIntent(Guid workflowId) =>
        $"workflow/{workflowId:D}/intent/workers";

    public static string WorkerJob(Guid workflowId) =>
        $"workflow/{workflowId:D}/job/workers";

    public static string Chunk(Guid workflowId, int chunkIndex) =>
        $"workflow/{workflowId:D}/chunk/{ValidateIndex(chunkIndex)}";

    public static string Result(Guid workflowId, int chunkIndex) =>
        $"workflow/{workflowId:D}/result/{ValidateIndex(chunkIndex)}";

    public static bool IsValid(string key) =>
        key.Length <= 96 && KeyPattern().IsMatch(key);

    private static string ValidateIndex(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index > MaximumChunkIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return index.ToString("D6", CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(
        "^workflow/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/(?:input|state|(?:intent|job)/(?:master|workers)|(?:chunk|result)/[0-9]{6})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
