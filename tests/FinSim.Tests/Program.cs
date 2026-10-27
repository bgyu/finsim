using FinSim.Contracts;
using FinSim.Infrastructure.Cache;
using System.Text.Json;

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
