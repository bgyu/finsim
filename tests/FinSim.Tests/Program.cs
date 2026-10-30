using FinSim.Contracts;
using FinSim.Infrastructure.Cache;
using FinSim.Infrastructure.Security;
using FinSim.Infrastructure.Slurm;
using FinSimProxy.Services;
using FinSimApp.Batch;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

var workflowId = Guid.Parse("11111111-2222-3333-4444-555555555555");
var failures = new List<string>();

Check(
    CacheKeys.Chunk(workflowId, 7) ==
        "workflow/11111111-2222-3333-4444-555555555555/chunk/000007",
    "chunk key is deterministic");
Check(CacheKeys.IsValid(CacheKeys.Input(workflowId)), "input key is valid");
Check(!CacheKeys.IsValid("../../etc/passwd"), "path traversal is rejected");
Check(!CacheKeys.IsValid("workflow/not-a-uuid/input"), "invalid UUID is rejected");

var temporaryRoot = Path.Combine("/tmp", $"finsim-tests-{Guid.NewGuid():N}");
try
{
    var store = new FileKeyValueStore(temporaryRoot);
    var key = CacheKeys.Chunk(workflowId, 7);
    var value = JsonSerializer.SerializeToElement(new { values = new[] { 1L, 2L, 3L } });
    var different = JsonSerializer.SerializeToElement(new { values = new[] { 9L } });
    Check(await store.PutIfAbsentAsync(key, value) == PutResult.Created, "cache creates value");
    Check(await store.PutIfAbsentAsync(key, value) == PutResult.Replayed, "cache replay is idempotent");
    Check(await store.PutIfAbsentAsync(key, different) == PutResult.Conflict, "cache rejects conflict");
    var loaded = await store.GetAsync(key);
    Check(loaded?.GetProperty("values").GetArrayLength() == 3, "cache returns stored JSON");
    Check(await store.DeleteAsync(key), "cache deletes existing key");
    Check(await store.GetAsync(key) is null, "deleted key is absent");

    var tokenFile = Path.Combine(temporaryRoot, "slurm.jwt");
    await File.WriteAllTextAsync(tokenFile, "test.jwt.value\n");
    if (OperatingSystem.IsLinux())
    {
        File.SetUnixFileMode(tokenFile, UnixFileMode.UserRead);
    }

    var handler = new RecordingHandler(request =>
    {
        Check(
            request.RequestUri?.AbsolutePath == "/slurm/v0.0.45/job/submit",
            "Slurm submit uses pinned API path");
        Check(
            request.Headers.GetValues("X-SLURM-USER-NAME").Single() == "finsim",
            "Slurm submit uses fixed identity");
        Check(
            request.Headers.GetValues("X-SLURM-USER-TOKEN").Single() == "test.jwt.value",
            "Slurm submit reloads file token");
        Check(
            request.Content?.Headers.ContentType?.ToString() == "application/json",
            "Slurm submit uses the exact registered JSON MIME type");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"job_id\":{\"number\":42}}")
        };
    });
    var slurm = new SlurmRestClient(
        new HttpClient(handler),
        new Uri("https://slurm.example/"),
        tokenFile,
        "finsim");
    var submitted = await slurm.SubmitJobAsync(
        new JsonObject { ["script"] = "true", ["job"] = new JsonObject() },
        Guid.NewGuid().ToString("D"));
    Check(submitted == "42", "Slurm submit unwraps job identifier");

    var queryHandler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "{\"jobs\":[{\"job_id\":17,\"job_state\":\"RUNNING\",\"comment\":\"wanted\"}," +
            "{\"job_id\":18,\"job_state\":\"PENDING\",\"comment\":\"other\"}]}")
    });
    var queryClient = new SlurmRestClient(
        new HttpClient(queryHandler),
        new Uri("https://slurm.example/"),
        tokenFile,
        "finsim");
    var matches = await queryClient.FindJobsByCommentAsync(
        "wanted",
        Guid.NewGuid().ToString("D"));
    Check(matches.Count == 1 && matches[0].JobId == "17", "Slurm query matches exact marker");

    var submitCount = 0;
    var workflowSlurm = new SlurmRestClient(
        new HttpClient(new RecordingHandler(request =>
        {
            submitCount++;
            var body = JsonNode.Parse(
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
            Check(
                body["job"]?["partition"]?.GetValue<string>() == "debug" &&
                body["job"]?["constraints"]?.GetValue<string>() == "finsim-master",
                "proxy constrains master to the credential-bearing pool");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"job_id\":71}")
            };
        })),
        new Uri("http://slurm.test/"),
        tokenFile,
        "finsim",
        allowInsecureHttp: true);
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cache:BaseUrl"] = "http://cache.test/",
            ["Cache:AllowInsecureHttp"] = "true",
            ["Cache:TokenFile"] = tokenFile,
            ["Slurm:BaseUrl"] = "http://slurm.test/",
            ["Slurm:AllowInsecureHttp"] = "true",
            ["Slurm:TokenFile"] = tokenFile,
            ["Paths:DataRoot"] = Path.Combine(temporaryRoot, "data"),
            ["Paths:ApplicationExecutable"] = "/finsim-app/releases/test/FinSim/FinSim"
        })
        .Build();
    var workflows = new WorkflowService(store, workflowSlurm, configuration);
    var simulation = new SimulationRequest([1, 2, 3, 4], ChunkSize: 2, MaxParallel: 2);
    var first = await workflows.CreateAsync(
        simulation,
        "test-subject",
        "request-0001",
        Guid.NewGuid().ToString("D"),
        CancellationToken.None);
    var replay = await workflows.CreateAsync(
        simulation,
        "test-subject",
        "request-0001",
        Guid.NewGuid().ToString("D"),
        CancellationToken.None);
    Check(first.WorkflowId == replay.WorkflowId, "proxy idempotency returns the same workflow");
    Check(submitCount == 1, "proxy idempotency submits one master");
    try
    {
        await workflows.CreateAsync(
            new SimulationRequest([9, 10], ChunkSize: 1, MaxParallel: 1),
            "test-subject",
            "request-0001",
            Guid.NewGuid().ToString("D"),
            CancellationToken.None);
        Check(false, "changed idempotent request must conflict");
    }
    catch (WorkflowConflictException)
    {
        Check(true, "changed idempotent request is rejected");
    }

    var workerWorkflow = Guid.NewGuid();
    var workerChunk = new ChunkData(workerWorkflow, 0, [4, 7, -2]);
    await store.PutIfAbsentAsync(
        CacheKeys.Chunk(workerWorkflow, 0),
        JsonSerializer.SerializeToElement(workerChunk));
    var workerSettings = new BatchSettings(
        workerWorkflow,
        new Uri("https://cache.test/"),
        tokenFile,
        false,
        Path.Combine(temporaryRoot, "worker-data"),
        "/finsim-app/releases/test/FinSim/FinSim",
        null,
        null,
        false,
        30);
    var workerExit = await new BatchRunner(workerSettings, store)
        .RunWorkerAsync(0, CancellationToken.None);
    var workerResult = (await store.GetAsync(CacheKeys.Result(workerWorkflow, 0)))?
        .Deserialize<ChunkResult>();
    Check(workerExit == 0, "worker exits successfully");
    Check(
        workerResult is { Count: 3, Sum: 9, Minimum: -2, Maximum: 7 },
        "worker calculates deterministic aggregate");

    var masterWorkflow = Guid.NewGuid();
    var masterInput = new SimulationRequest([1, 2, 3, 4], ChunkSize: 2, MaxParallel: 2);
    await store.PutIfAbsentAsync(
        CacheKeys.Input(masterWorkflow),
        JsonSerializer.SerializeToElement(masterInput));
    var resultsPublished = false;
    var masterHandler = new RecordingHandler(request =>
    {
        if (request.Method == HttpMethod.Post)
        {
            var body = JsonNode.Parse(
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
            Check(
                body["job"]?["partition"]?.GetValue<string>() == "debug" &&
                body["job"]?["constraints"]?.GetValue<string>() == "finsim-worker",
                "master constrains workers to the credential-free pool");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"job_id\":91}")
            };
        }

        if (!resultsPublished)
        {
            for (var index = 0; index < 2; index++)
            {
                var chunkValue = store.GetAsync(CacheKeys.Chunk(masterWorkflow, index))
                    .GetAwaiter().GetResult();
                var chunk = chunkValue?.Deserialize<ChunkData>() ??
                    throw new InvalidOperationException("test chunk missing");
                store.PutIfAbsentAsync(
                        CacheKeys.Result(masterWorkflow, index),
                        JsonSerializer.SerializeToElement(ChunkCalculator.Calculate(chunk)))
                    .GetAwaiter().GetResult();
            }

            resultsPublished = true;
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"jobs\":[{\"job_id\":91,\"job_state\":[\"COMPLETED\"]}]}")
        };
    });
    var masterSettings = new BatchSettings(
        masterWorkflow,
        new Uri("https://cache.test/"),
        tokenFile,
        false,
        Path.Combine(temporaryRoot, "master-data"),
        "/finsim-app/releases/test/FinSim/FinSim",
        new Uri("https://slurm.test/"),
        tokenFile,
        false,
        30);
    var masterSlurm = new SlurmRestClient(
        new HttpClient(masterHandler),
        masterSettings.SlurmUrl!,
        tokenFile,
        "finsim");
    var masterExit = await new BatchRunner(masterSettings, store, masterSlurm)
        .RunMasterAsync(CancellationToken.None);
    var reportPath = Path.Combine(
        masterSettings.DataRoot,
        "reports",
        $"{masterWorkflow:D}.json");
    var report = JsonSerializer.Deserialize<SimulationReport>(
        await File.ReadAllTextAsync(reportPath));
    Check(masterExit == 0, "master exits successfully");
    Check(
        report is { Status: "SUCCEEDED", ValueCount: 4, Sum: 10, Minimum: 1, Maximum: 4 },
        "master aggregates worker results and writes report");

    var failureClient = new SlurmRestClient(
        new HttpClient(new RecordingHandler(_ => throw new HttpRequestException("lost"))),
        new Uri("https://slurm.example/"),
        tokenFile,
        "finsim");
    try
    {
        await failureClient.SubmitJobAsync(
            new JsonObject { ["script"] = "true", ["job"] = new JsonObject() },
            Guid.NewGuid().ToString("D"));
        Check(false, "ambiguous submission must throw");
    }
    catch (SlurmAmbiguousException)
    {
        Check(true, "transport failure is classified as ambiguous");
    }

    if (OperatingSystem.IsLinux())
    {
        File.SetUnixFileMode(tokenFile, UnixFileMode.UserRead | UnixFileMode.GroupRead);
        try
        {
            _ = PrivateTokenFile.Read(tokenFile);
            Check(false, "broad token mode must fail");
        }
        catch (InvalidOperationException)
        {
            Check(true, "broad token mode is rejected");
        }
    }
}
finally
{
    if (Directory.Exists(temporaryRoot))
    {
        Directory.Delete(temporaryRoot, recursive: true);
    }
}

if (failures.Count != 0)
{
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"FAIL: {failure}");
    }

    return 1;
}

Console.WriteLine("All FinSim contract tests passed.");
return 0;

void Check(bool condition, string name)
{
    if (!condition)
    {
        failures.Add(name);
    }
}

sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(responder(request));
    }
}
