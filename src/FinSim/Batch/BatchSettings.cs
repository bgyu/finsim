namespace FinSimApp.Batch;

public sealed record BatchSettings(
    Guid WorkflowId,
    Uri CacheUrl,
    string CacheTokenFile,
    bool CacheAllowInsecureHttp,
    string DataRoot,
    string ApplicationExecutable,
    Uri? SlurmUrl,
    string? SlurmTokenFile,
    bool SlurmAllowInsecureHttp,
    int DeadlineSeconds,
    string WorkerPartition = "debug",
    string WorkerFeature = "finsim-worker")
{
    public static BatchSettings FromEnvironment(bool requireSlurm)
    {
        var workflowId = Guid.Parse(Required("FINSIM_WORKFLOW_ID"));
        var cacheAllowHttp = Flag("FINSIM_CACHE_ALLOW_INSECURE_HTTP");
        var cacheUrl = RequiredUri("FINSIM_CACHE_URL", cacheAllowHttp);
        var dataRoot = Path.GetFullPath(Required("FINSIM_DATA_ROOT"));
        var applicationExecutable = Path.GetFullPath(Required("FINSIM_APPLICATION_EXECUTABLE"));
        if (!applicationExecutable.StartsWith("/finsim-app/releases/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "FINSIM_APPLICATION_EXECUTABLE must identify an immutable release.");
        }
        var deadline = OptionalInt("FINSIM_DEADLINE_SECONDS", 600, 30, 86_400);

        Uri? slurmUrl = null;
        string? slurmToken = null;
        var slurmAllowHttp = false;
        if (requireSlurm)
        {
            slurmAllowHttp = Flag("FINSIM_SLURM_ALLOW_INSECURE_HTTP");
            slurmUrl = RequiredUri("FINSIM_SLURM_REST_URL", slurmAllowHttp);
            slurmToken = Required("FINSIM_SLURM_TOKEN_FILE");
        }

        return new BatchSettings(
            workflowId,
            cacheUrl,
            Required("FINSIM_CACHE_TOKEN_FILE"),
            cacheAllowHttp,
            dataRoot,
            applicationExecutable,
            slurmUrl,
            slurmToken,
            slurmAllowHttp,
            deadline,
            requireSlurm ? RequiredSchedulerName("FINSIM_WORKER_PARTITION") : "debug",
            requireSlurm ? RequiredSchedulerName("FINSIM_WORKER_FEATURE") : "finsim-worker");
    }

    private static string Required(string name) =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))
            ? throw new InvalidOperationException($"Required setting {name} is missing.")
            : Environment.GetEnvironmentVariable(name)!;

    private static bool Flag(string name) =>
        string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal);

    private static Uri RequiredUri(string name, bool allowHttp)
    {
        var value = Required(name);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(allowHttp && uri.Scheme == Uri.UriSchemeHttp)))
        {
            throw new InvalidOperationException($"Setting {name} is not an allowed URL.");
        }

        return uri;
    }

    private static int OptionalInt(string name, int defaultValue, int minimum, int maximum)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value, out var result) || result < minimum || result > maximum)
        {
            throw new InvalidOperationException($"Setting {name} is outside policy.");
        }

        return result;
    }

    private static string RequiredSchedulerName(string name)
    {
        var value = Required(name);
        return System.Text.RegularExpressions.Regex.IsMatch(
            value,
            "^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$")
            ? value
            : throw new InvalidOperationException($"Setting {name} is invalid.");
    }
}
