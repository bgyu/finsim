using System.Text.Json;
using System.Text.Json.Nodes;
using FinSim.Contracts;
using FinSim.Infrastructure.Cache;
using FinSim.Infrastructure.Slurm;

namespace FinSimApp.Batch;

public sealed class BatchRunner(
    BatchSettings settings,
    IKeyValueStore cache,
    SlurmRestClient? slurm = null)
{
    private static readonly HashSet<string> FailureStates = new(StringComparer.Ordinal)
    {
        "BOOT_FAIL", "CANCELLED", "DEADLINE", "FAILED", "NODE_FAIL",
        "OUT_OF_MEMORY", "PREEMPTED", "TIMEOUT"
    };

    public async Task<int> RunMasterAsync(CancellationToken cancellationToken)
    {
        if (slurm is null)
        {
            throw new InvalidOperationException("Master requires a Slurm REST client.");
        }

        var chunkCount = 0;
        try
        {
            var inputValue = await cache.GetAsync(
                CacheKeys.Input(settings.WorkflowId),
                cancellationToken) ?? throw new InvalidDataException("Workflow input is missing.");
            var request = inputValue.Deserialize<SimulationRequest>() ??
                throw new InvalidDataException("Workflow input is invalid.");
            ValidateInput(request);

            chunkCount = (request.Values.Count + request.ChunkSize - 1) / request.ChunkSize;
            for (var index = 0; index < chunkCount; index++)
            {
                var values = request.Values
                    .Skip(index * request.ChunkSize)
                    .Take(request.ChunkSize)
                    .ToArray();
                var chunk = new ChunkData(settings.WorkflowId, index, values);
                var result = await cache.PutIfAbsentAsync(
                    CacheKeys.Chunk(settings.WorkflowId, index),
                    JsonSerializer.SerializeToElement(chunk),
                    cancellationToken);
                if (result == PutResult.Conflict)
                {
                    throw new InvalidDataException("A chunk key has conflicting content.");
                }
            }

            var marker = $"finsim={settings.WorkflowId:D};role=workers";
            var intentResult = await cache.PutIfAbsentAsync(
                CacheKeys.WorkerIntent(settings.WorkflowId),
                JsonSerializer.SerializeToElement(
                    new SubmissionIntent(settings.WorkflowId, "workers", marker)),
                cancellationToken);

            var record = await ReadRecordAsync(CacheKeys.WorkerJob(settings.WorkflowId), cancellationToken);
            string jobId;
            if (record is not null)
            {
                jobId = record.JobId;
            }
            else if (intentResult == PutResult.Replayed)
            {
                var matches = await slurm.FindJobsByCommentAsync(
                    marker,
                    Guid.NewGuid().ToString("D"),
                    cancellationToken);
                if (matches.Count != 1)
                {
                    throw new SlurmAmbiguousException(
                        "Worker-array submission cannot be reconciled safely.");
                }

                jobId = matches[0].JobId;
                await StoreRecordAsync(marker, jobId, cancellationToken);
            }
            else if (intentResult == PutResult.Created)
            {
                jobId = await slurm.SubmitJobAsync(
                    BuildWorkerRequest(chunkCount, request.MaxParallel, marker),
                    Guid.NewGuid().ToString("D"),
                    cancellationToken);
                await StoreRecordAsync(marker, jobId, cancellationToken);
            }
            else
            {
                throw new InvalidDataException("Worker submission intent conflicts.");
            }

            await WaitForWorkersAsync(jobId, cancellationToken);
            var report = await AggregateAsync(chunkCount, cancellationToken);
            await WriteReportAsync(report, cancellationToken);
            WriteLog("master_completed", chunkCount, jobId);
            return 0;
        }
        catch (Exception error)
        {
            var failure = new SimulationReport(
                settings.WorkflowId,
                "FAILED",
                chunkCount,
                0,
                0,
                0,
                0,
                DateTimeOffset.UtcNow,
                error.GetType().Name);
            await TryWriteFailureReportAsync(failure, cancellationToken);
            Console.Error.WriteLine(
                JsonSerializer.Serialize(new
                {
                    event_name = "master_failed",
                    workflow_id = settings.WorkflowId,
                    error = error.GetType().Name
                }));
            return 1;
        }
    }

    public async Task<int> RunWorkerAsync(int chunkIndex, CancellationToken cancellationToken)
    {
        try
        {
            if (chunkIndex is < 0 or > 999_999)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkIndex));
            }

            var value = await cache.GetAsync(
                CacheKeys.Chunk(settings.WorkflowId, chunkIndex),
                cancellationToken) ?? throw new InvalidDataException("Chunk input is missing.");
            var chunk = value.Deserialize<ChunkData>() ??
                throw new InvalidDataException("Chunk input is invalid.");
            if (chunk.WorkflowId != settings.WorkflowId || chunk.ChunkIndex != chunkIndex)
            {
                throw new InvalidDataException("Chunk identity does not match the allocation.");
            }

            var result = ChunkCalculator.Calculate(chunk);
            var stored = await cache.PutIfAbsentAsync(
                CacheKeys.Result(settings.WorkflowId, chunkIndex),
                JsonSerializer.SerializeToElement(result),
                cancellationToken);
            if (stored == PutResult.Conflict)
            {
                var existing = await cache.GetAsync(
                    CacheKeys.Result(settings.WorkflowId, chunkIndex),
                    cancellationToken);
                if (existing?.Deserialize<ChunkResult>() != result)
                {
                    throw new InvalidDataException("Worker result conflicts with an earlier attempt.");
                }
            }

            WriteLog("worker_completed", 1, Environment.GetEnvironmentVariable("SLURM_JOB_ID"));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                JsonSerializer.Serialize(new
                {
                    event_name = "worker_failed",
                    workflow_id = settings.WorkflowId,
                    chunk_index = chunkIndex,
                    error = error.GetType().Name
                }));
            return 1;
        }
    }

    private JsonObject BuildWorkerRequest(int chunkCount, int maxParallel, string marker)
    {
        var logDirectory = Path.Combine(
            settings.DataRoot,
            "logs",
            settings.WorkflowId.ToString("D"));
        var environment = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/local/bin:/usr/bin:/bin",
            ["FINSIM_WORKFLOW_ID"] = settings.WorkflowId.ToString("D"),
            ["FINSIM_CACHE_URL"] = settings.CacheUrl.ToString(),
            ["FINSIM_CACHE_TOKEN_FILE"] = settings.CacheTokenFile,
            ["FINSIM_CACHE_ALLOW_INSECURE_HTTP"] = settings.CacheAllowInsecureHttp ? "1" : "0",
            ["FINSIM_DATA_ROOT"] = settings.DataRoot
        };
        var environmentArray = new JsonArray(
            environment.Select(pair => JsonValue.Create($"{pair.Key}={pair.Value}")).ToArray());
        return new JsonObject
        {
            ["script"] = "#!/bin/bash\nset -Eeuo pipefail\numask 077\nexec /opt/finsim/FinSim/FinSim worker\n",
            ["job"] = new JsonObject
            {
                ["name"] = $"finsim-{settings.WorkflowId:N}"[..19] + "-w",
                ["comment"] = marker,
                ["partition"] = settings.WorkerPartition,
                ["constraints"] = settings.WorkerFeature,
                ["array"] = $"0-{chunkCount - 1}%{maxParallel}",
                ["time_limit"] = 5,
                ["cpus_per_task"] = 1,
                ["memory_per_node"] = 128,
                ["current_working_directory"] = "/",
                ["standard_output"] = Path.Combine(logDirectory, "worker-%A_%a.out"),
                ["standard_error"] = Path.Combine(logDirectory, "worker-%A_%a.err"),
                ["environment"] = environmentArray
            }
        };
    }

    private async Task WaitForWorkersAsync(string jobId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(settings.DeadlineSeconds);
        var delay = TimeSpan.FromSeconds(1);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var jobs = await slurm!.GetJobAsync(
                jobId,
                Guid.NewGuid().ToString("D"),
                cancellationToken);
            if (jobs.Count != 0)
            {
                var states = jobs.Select(job => NormalizeState(job.State)).ToArray();
                if (states.Any(FailureStates.Contains))
                {
                    throw new InvalidOperationException("At least one worker failed.");
                }

                if (states.All(state => state == "COMPLETED"))
                {
                    return;
                }
            }

            await Task.Delay(delay, cancellationToken);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 1.5, 10));
        }

        throw new TimeoutException("Worker array exceeded the workflow deadline.");
    }

    private async Task<SimulationReport> AggregateAsync(
        int chunkCount,
        CancellationToken cancellationToken)
    {
        var valueCount = 0;
        long sum = 0;
        var minimum = long.MaxValue;
        var maximum = long.MinValue;
        checked
        {
            for (var index = 0; index < chunkCount; index++)
            {
                var value = await cache.GetAsync(
                    CacheKeys.Result(settings.WorkflowId, index),
                    cancellationToken) ?? throw new InvalidDataException("Worker result is missing.");
                var result = value.Deserialize<ChunkResult>() ??
                    throw new InvalidDataException("Worker result is invalid.");
                if (result.WorkflowId != settings.WorkflowId || result.ChunkIndex != index ||
                    result.Count <= 0)
                {
                    throw new InvalidDataException("Worker result identity is invalid.");
                }

                valueCount += result.Count;
                sum += result.Sum;
                minimum = Math.Min(minimum, result.Minimum);
                maximum = Math.Max(maximum, result.Maximum);
            }
        }

        return new SimulationReport(
            settings.WorkflowId,
            "SUCCEEDED",
            chunkCount,
            valueCount,
            sum,
            minimum,
            maximum,
            DateTimeOffset.UtcNow);
    }

    private async Task StoreRecordAsync(
        string marker,
        string jobId,
        CancellationToken cancellationToken)
    {
        var result = await cache.PutIfAbsentAsync(
            CacheKeys.WorkerJob(settings.WorkflowId),
            JsonSerializer.SerializeToElement(
                new SubmissionRecord(settings.WorkflowId, "workers", marker, jobId)),
            cancellationToken);
        if (result == PutResult.Conflict)
        {
            throw new InvalidDataException("Worker submission record conflicts.");
        }
    }

    private async Task<SubmissionRecord?> ReadRecordAsync(
        string key,
        CancellationToken cancellationToken)
    {
        var value = await cache.GetAsync(key, cancellationToken);
        return value?.Deserialize<SubmissionRecord>();
    }

    private async Task WriteReportAsync(
        SimulationReport report,
        CancellationToken cancellationToken)
    {
        var reportDirectory = Path.Combine(settings.DataRoot, "reports");
        Directory.CreateDirectory(reportDirectory);
        if (new DirectoryInfo(reportDirectory).LinkTarget is not null)
        {
            throw new InvalidOperationException("Report directory must not be a symbolic link.");
        }

        var destination = Path.Combine(reportDirectory, $"{settings.WorkflowId:D}.json");
        if (File.Exists(destination))
        {
            var existing = await ReadExistingReportAsync(destination, cancellationToken);
            if (existing.WorkflowId == report.WorkflowId &&
                existing.Status == report.Status &&
                (report.Status != "SUCCEEDED" ||
                 (existing.ChunkCount == report.ChunkCount &&
                  existing.ValueCount == report.ValueCount &&
                  existing.Sum == report.Sum &&
                  existing.Minimum == report.Minimum &&
                  existing.Maximum == report.Maximum)))
            {
                return;
            }

            throw new InvalidDataException("Existing report conflicts with this attempt.");
        }

        var temporary = Path.Combine(reportDirectory, $".pending-{Guid.NewGuid():N}");
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                if (OperatingSystem.IsLinux())
                {
                    File.SetUnixFileMode(
                        temporary,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                await JsonSerializer.SerializeAsync(stream, report, cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporary, destination, overwrite: false);
            }
            catch (IOException) when (File.Exists(destination))
            {
                // A requeued master may race with its earlier successful attempt.
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private async Task TryWriteFailureReportAsync(
        SimulationReport report,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteReportAsync(report, cancellationToken);
        }
        catch (Exception error) when (
            error is IOException or InvalidDataException or
                UnauthorizedAccessException or OperationCanceledException)
        {
            // The process still exits non-zero; Slurm preserves the authoritative failure.
        }
    }

    private static async Task<SimulationReport> ReadExistingReportAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length is <= 0 or > 1_048_576)
        {
            throw new InvalidDataException("Existing report file is invalid.");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<SimulationReport>(
                   stream,
                   cancellationToken: cancellationToken) ??
               throw new InvalidDataException("Existing report JSON is invalid.");
    }

    private static string NormalizeState(string state) =>
        state.Split('+', StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant();

    private static void ValidateInput(SimulationRequest request)
    {
        if (request.Values is null || request.Values.Count is < 1 or > 10_000 ||
            request.ChunkSize is < 1 or > 1_000 || request.ChunkSize > request.Values.Count)
        {
            throw new InvalidDataException("Workflow input is outside policy.");
        }

        var chunkCount = (request.Values.Count + request.ChunkSize - 1) / request.ChunkSize;
        if (request.MaxParallel is < 1 or > 100 || request.MaxParallel > chunkCount)
        {
            throw new InvalidDataException("Workflow parallelism is outside policy.");
        }
    }

    private void WriteLog(string eventName, int count, string? jobId)
    {
        Console.WriteLine(
            JsonSerializer.Serialize(new
            {
                event_name = eventName,
                workflow_id = settings.WorkflowId,
                count,
                job_id = jobId
            }));
    }
}
