using FinSim.Contracts;
using FinSim.Infrastructure.Cache;
using FinSim.Infrastructure.Security;
using FinSim.Infrastructure.Slurm;
using FinSimProxy.Services;
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
        new HttpClient(new RecordingHandler(_ =>
        {
            submitCount++;
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
            ["Paths:DataRoot"] = Path.Combine(temporaryRoot, "data")
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
