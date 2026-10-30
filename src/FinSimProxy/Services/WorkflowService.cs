using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FinSim.Contracts;
using FinSim.Infrastructure.Cache;
using FinSim.Infrastructure.Security;
using FinSim.Infrastructure.Slurm;

namespace FinSimProxy.Services;

public sealed partial class WorkflowService
{
    private const int MaximumValues = 10_000;
    private const long MaximumAbsoluteValue = 1_000_000_000;
    private readonly IKeyValueStore cache;
    private readonly SlurmRestClient slurm;
    private readonly Uri cacheUrl;
    private readonly string cacheTokenFile;
    private readonly string jobCacheTokenFile;
    private readonly bool allowInsecureCache;
    private readonly Uri slurmUrl;
    private readonly string slurmTokenFile;
    private readonly string masterPartition;
    private readonly string masterFeature;
    private readonly string workerPartition;
    private readonly string workerFeature;
    private readonly string dataRoot;
    private readonly string applicationExecutable;
    private readonly bool allowInsecureSlurm;

    public WorkflowService(
        IKeyValueStore cache,
        SlurmRestClient slurm,
        IConfiguration configuration)
    {
        this.cache = cache;
        this.slurm = slurm;
        allowInsecureCache = configuration.GetValue("Cache:AllowInsecureHttp", false);
        cacheUrl = RequiredUri(
            configuration,
            "Cache:BaseUrl",
            allowInsecureCache);
        cacheTokenFile = Required(configuration, "Cache:TokenFile");
        jobCacheTokenFile = configuration["Cache:JobTokenFile"] ?? cacheTokenFile;
        allowInsecureSlurm = configuration.GetValue("Slurm:AllowInsecureHttp", false);
        slurmUrl = RequiredUri(configuration, "Slurm:BaseUrl", allowInsecureSlurm);
        slurmTokenFile = Required(configuration, "Slurm:TokenFile");
        masterPartition = SchedulerName(configuration["Slurm:MasterPartition"] ?? "debug");
        masterFeature = SchedulerName(configuration["Slurm:MasterFeature"] ?? "finsim-master");
        workerPartition = SchedulerName(configuration["Slurm:WorkerPartition"] ?? "debug");
        workerFeature = SchedulerName(configuration["Slurm:WorkerFeature"] ?? "finsim-worker");
        dataRoot = Path.GetFullPath(configuration["Paths:DataRoot"] ?? "/finsim-data");
        applicationExecutable = Path.GetFullPath(
            Required(configuration, "Paths:ApplicationExecutable"));
        if (!applicationExecutable.StartsWith(
                "/finsim-app/releases/",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The application executable must be an existing immutable release path.");
        }
    }

    public async Task<CreateSimulationResponse> CreateAsync(
        SimulationRequest request,
        string subject,
        string idempotencyKey,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        ValidateIdempotencyKey(idempotencyKey);
        var workflowId = DeterministicWorkflowId(subject, idempotencyKey);
        var owner = new WorkflowOwner(workflowId, SubjectDigest(subject));
        var ownerResult = await cache.PutIfAbsentAsync(
            CacheKeys.State(workflowId),
            JsonSerializer.SerializeToElement(owner),
            cancellationToken);
        if (ownerResult == PutResult.Conflict)
        {
            throw new WorkflowConflictException("Workflow owner state conflicts.");
        }

        var input = JsonSerializer.SerializeToElement(request);
        var inputResult = await cache.PutIfAbsentAsync(
            CacheKeys.Input(workflowId),
            input,
            cancellationToken);
        if (inputResult == PutResult.Conflict)
        {
            throw new WorkflowConflictException(
                "Idempotency key was already used with a different request.");
        }

        if (await ReadRecordAsync(CacheKeys.MasterJob(workflowId), cancellationToken) is not null)
        {
            return new CreateSimulationResponse(workflowId, "MASTER_SUBMITTED");
        }

        var marker = Marker(workflowId, "master");
        var intent = new SubmissionIntent(
            workflowId,
            "master",
            marker);
        var intentResult = await cache.PutIfAbsentAsync(
            CacheKeys.MasterIntent(workflowId),
            JsonSerializer.SerializeToElement(intent),
            cancellationToken);

        if (intentResult == PutResult.Replayed)
        {
            var matches = await slurm.FindJobsByCommentAsync(
                marker,
                correlationId,
                cancellationToken);
            if (matches.Count == 1)
            {
                await StoreRecordAsync(
                    workflowId,
                    "master",
                    marker,
                    matches[0].JobId,
                    cancellationToken);
                return new CreateSimulationResponse(workflowId, "MASTER_SUBMITTED");
            }

            return new CreateSimulationResponse(workflowId, "MASTER_SUBMISSION_UNKNOWN");
        }

        PrepareWorkflowDirectories(workflowId);
        try
        {
            var jobId = await slurm.SubmitJobAsync(
                BuildMasterRequest(workflowId, request, marker),
                correlationId,
                cancellationToken);
            await StoreRecordAsync(
                workflowId,
                "master",
                marker,
                jobId,
                cancellationToken);
            return new CreateSimulationResponse(workflowId, "MASTER_SUBMITTED");
        }
        catch (SlurmAmbiguousException)
        {
            return new CreateSimulationResponse(workflowId, "MASTER_SUBMISSION_UNKNOWN");
        }
    }

    public async Task<WorkflowStatusResponse> GetStatusAsync(
        Guid workflowId,
        string subject,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (!await IsOwnerAsync(workflowId, subject, cancellationToken))
        {
            return new WorkflowStatusResponse(workflowId, "NOT_FOUND", null, null, null);
        }

        var report = await ReadReportFileAsync(workflowId, cancellationToken);
        if (report is not null)
        {
            return new WorkflowStatusResponse(
                workflowId,
                report.Status,
                (await ReadRecordAsync(CacheKeys.MasterJob(workflowId), cancellationToken))?.JobId,
                (await ReadRecordAsync(CacheKeys.WorkerJob(workflowId), cancellationToken))?.JobId,
                report.Error);
        }

        var marker = Marker(workflowId, "master");
        var jobs = await slurm.FindJobsByCommentAsync(marker, correlationId, cancellationToken);
        if (jobs.Count > 1)
        {
            return new WorkflowStatusResponse(
                workflowId,
                "NEEDS_ATTENTION",
                null,
                null,
                "Multiple master jobs have the same marker.");
        }

        if (jobs.Count == 0)
        {
            var intent = await cache.GetAsync(CacheKeys.MasterIntent(workflowId), cancellationToken);
            return new WorkflowStatusResponse(
                workflowId,
                intent is null ? "NOT_FOUND" : "MASTER_SUBMISSION_UNKNOWN",
                null,
                null,
                null);
        }

        var worker = await ReadRecordAsync(CacheKeys.WorkerJob(workflowId), cancellationToken);
        return new WorkflowStatusResponse(
            workflowId,
            MapState(jobs[0].State),
            jobs[0].JobId,
            worker?.JobId,
            null);
    }

    public async Task CancelAsync(
        Guid workflowId,
        string subject,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (!await IsOwnerAsync(workflowId, subject, cancellationToken))
        {
            throw new WorkflowNotFoundException();
        }

        foreach (var role in new[] { "master", "workers" })
        {
            var jobs = await slurm.FindJobsByCommentAsync(
                Marker(workflowId, role),
                correlationId,
                cancellationToken);
            foreach (var job in jobs)
            {
                await slurm.CancelJobAsync(job.JobId, correlationId, cancellationToken);
            }
        }
    }

    public async Task<string?> ReadReportAsync(
        Guid workflowId,
        string subject,
        CancellationToken cancellationToken)
    {
        if (!await IsOwnerAsync(workflowId, subject, cancellationToken))
        {
            return null;
        }

        var report = await ReadReportFileAsync(workflowId, cancellationToken);
        return report is null ? null : JsonSerializer.Serialize(report);
    }

    public bool IsReady()
    {
        try
        {
            _ = PrivateTokenFile.Read(cacheTokenFile);
            _ = PrivateTokenFile.Read(slurmTokenFile);
            return Directory.Exists(dataRoot) &&
                   new DirectoryInfo(dataRoot).LinkTarget is null &&
                   File.Exists(applicationExecutable) &&
                   new FileInfo(applicationExecutable).LinkTarget is null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private JsonObject BuildMasterRequest(
        Guid workflowId,
        SimulationRequest request,
        string marker)
    {
        var logDirectory = Path.Combine(dataRoot, "logs", workflowId.ToString("D"));
        var environment = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/local/bin:/usr/bin:/bin",
            ["FINSIM_WORKFLOW_ID"] = workflowId.ToString("D"),
            ["FINSIM_CACHE_URL"] = cacheUrl.ToString(),
            ["FINSIM_CACHE_TOKEN_FILE"] = jobCacheTokenFile,
            ["FINSIM_CACHE_ALLOW_INSECURE_HTTP"] = allowInsecureCache ? "1" : "0",
            ["FINSIM_SLURM_REST_URL"] = slurmUrl.ToString(),
            ["FINSIM_SLURM_TOKEN_FILE"] = slurmTokenFile,
            ["FINSIM_SLURM_ALLOW_INSECURE_HTTP"] = allowInsecureSlurm ? "1" : "0",
            ["FINSIM_DATA_ROOT"] = dataRoot,
            ["FINSIM_APPLICATION_EXECUTABLE"] = applicationExecutable,
            ["FINSIM_DEADLINE_SECONDS"] = "600",
            ["FINSIM_MAX_PARALLEL"] = request.MaxParallel.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["FINSIM_WORKER_PARTITION"] = workerPartition,
            ["FINSIM_WORKER_FEATURE"] = workerFeature
        };
        var environmentArray = new JsonArray(
            environment.Select(pair => JsonValue.Create($"{pair.Key}={pair.Value}")).ToArray());
        var name = $"finsim-{workflowId:N}"[..19] + "-m";
        return new JsonObject
        {
            ["script"] = "#!/bin/bash\nset -Eeuo pipefail\numask 077\nexec \"${FINSIM_APPLICATION_EXECUTABLE}\" master\n",
            ["job"] = new JsonObject
            {
                ["name"] = name,
                ["comment"] = marker,
                ["partition"] = masterPartition,
                ["constraints"] = masterFeature,
                ["time_limit"] = 10,
                ["cpus_per_task"] = 1,
                ["memory_per_node"] = 256,
                ["current_working_directory"] = "/",
                ["standard_output"] = Path.Combine(logDirectory, "master-%j.out"),
                ["standard_error"] = Path.Combine(logDirectory, "master-%j.err"),
                ["environment"] = environmentArray
            }
        };
    }

    private async Task StoreRecordAsync(
        Guid workflowId,
        string role,
        string marker,
        string jobId,
        CancellationToken cancellationToken)
    {
        var key = role == "master"
            ? CacheKeys.MasterJob(workflowId)
            : CacheKeys.WorkerJob(workflowId);
        var record = new SubmissionRecord(
            workflowId,
            role,
            marker,
            jobId);
        var result = await cache.PutIfAbsentAsync(
            key,
            JsonSerializer.SerializeToElement(record),
            cancellationToken);
        if (result == PutResult.Conflict)
        {
            throw new WorkflowConflictException("Submission record conflicts with existing state.");
        }
    }

    private async Task<SubmissionRecord?> ReadRecordAsync(
        string key,
        CancellationToken cancellationToken)
    {
        var value = await cache.GetAsync(key, cancellationToken);
        return value?.Deserialize<SubmissionRecord>();
    }

    private async Task<bool> IsOwnerAsync(
        Guid workflowId,
        string subject,
        CancellationToken cancellationToken)
    {
        var value = await cache.GetAsync(CacheKeys.State(workflowId), cancellationToken);
        var owner = value?.Deserialize<WorkflowOwner>();
        return owner is not null &&
               CryptographicOperations.FixedTimeEquals(
                   Convert.FromHexString(owner.SubjectSha256),
                   Convert.FromHexString(SubjectDigest(subject)));
    }

    private void PrepareWorkflowDirectories(Guid workflowId)
    {
        foreach (var path in new[]
        {
            dataRoot,
            Path.Combine(dataRoot, "logs"),
            Path.Combine(dataRoot, "logs", workflowId.ToString("D")),
            Path.Combine(dataRoot, "reports")
        })
        {
            Directory.CreateDirectory(path);
            if (new DirectoryInfo(path).LinkTarget is not null)
            {
                throw new InvalidOperationException("Workflow directory must not be a symbolic link.");
            }

            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    private string ReportPath(Guid workflowId) =>
        Path.Combine(dataRoot, "reports", $"{workflowId:D}.json");

    private async Task<SimulationReport?> ReadReportFileAsync(
        Guid workflowId,
        CancellationToken cancellationToken)
    {
        var path = ReportPath(workflowId);
        if (!IsPrivateRegularFile(path, maximumBytes: 1_048_576))
        {
            return null;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var report = await JsonSerializer.DeserializeAsync<SimulationReport>(
            stream,
            cancellationToken: cancellationToken);
        return report?.WorkflowId == workflowId &&
               report.Status is "SUCCEEDED" or "FAILED"
            ? report
            : throw new InvalidDataException("Final report content is invalid.");
    }

    private static bool IsPrivateRegularFile(string path, long maximumBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null ||
            (info.Attributes & FileAttributes.Directory) != 0 ||
            info.Length is <= 0 or > 1_048_576 || info.Length > maximumBytes)
        {
            return false;
        }

        return !OperatingSystem.IsLinux() ||
               (File.GetUnixFileMode(path) &
                (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                 UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) == 0;
    }

    private static string MapState(string state)
    {
        var normalized = state.Split('+', StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant();
        return normalized switch
        {
            "PENDING" or "CONFIGURING" => "MASTER_PENDING",
            "RUNNING" or "COMPLETING" => "MASTER_RUNNING",
            "COMPLETED" => "FINALIZING",
            "CANCELLED" => "CANCELLED",
            "FAILED" or "TIMEOUT" or "OUT_OF_MEMORY" or "NODE_FAIL" => "FAILED",
            _ => "MASTER_STATE_UNKNOWN"
        };
    }

    private static Guid DeterministicWorkflowId(string subject, string idempotencyKey)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{subject}\n{idempotencyKey}"));
        Span<byte> bytes = stackalloc byte[16];
        digest.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    private static string SubjectDigest(string subject) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(subject)));

    private static void ValidateRequest(SimulationRequest request)
    {
        if (request.Values is null || request.Values.Count is < 1 or > MaximumValues)
        {
            throw new ArgumentException($"Values must contain 1 to {MaximumValues} entries.");
        }

        if (request.Values.Any(value => value is < -MaximumAbsoluteValue or > MaximumAbsoluteValue))
        {
            throw new ArgumentException("A value is outside the allowed numeric range.");
        }

        if (request.ChunkSize is < 1 or > 1_000 || request.ChunkSize > request.Values.Count)
        {
            throw new ArgumentException("ChunkSize is outside policy.");
        }

        var chunks = (request.Values.Count + request.ChunkSize - 1) / request.ChunkSize;
        if (request.MaxParallel is < 1 or > 100 || request.MaxParallel > chunks)
        {
            throw new ArgumentException("MaxParallel is outside policy.");
        }
    }

    private static void ValidateIdempotencyKey(string value)
    {
        if (!IdempotencyPattern().IsMatch(value))
        {
            throw new ArgumentException("Idempotency-Key is invalid.");
        }
    }

    public static string Marker(Guid workflowId, string role) =>
        $"finsim={workflowId:D};role={role}";

    private static string Required(IConfiguration configuration, string key) =>
        string.IsNullOrWhiteSpace(configuration[key])
            ? throw new InvalidOperationException($"Configuration {key} is required.")
            : configuration[key]!;

    private static string SchedulerName(string value) =>
        Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$")
            ? value
            : throw new InvalidOperationException("Slurm partition or feature name is invalid.");

    private static Uri RequiredUri(IConfiguration configuration, string key, bool allowHttp)
    {
        var value = Required(configuration, key);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(allowHttp && uri.Scheme == Uri.UriSchemeHttp)))
        {
            throw new InvalidOperationException($"Configuration {key} is not an allowed URL.");
        }

        return uri;
    }

    [GeneratedRegex("^[A-Za-z0-9._~-]{8,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdempotencyPattern();
}

public sealed class WorkflowConflictException(string message) : Exception(message)
{
}

public sealed class WorkflowNotFoundException : Exception
{
}
