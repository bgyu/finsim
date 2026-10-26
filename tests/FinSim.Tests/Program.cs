using FinSim.Contracts;

var workflowId = Guid.Parse("11111111-2222-3333-4444-555555555555");
var failures = new List<string>();

Check(
    CacheKeys.Chunk(workflowId, 7) ==
        "workflow/11111111-2222-3333-4444-555555555555/chunk/000007",
    "chunk key is deterministic");
Check(CacheKeys.IsValid(CacheKeys.Input(workflowId)), "input key is valid");
Check(!CacheKeys.IsValid("../../etc/passwd"), "path traversal is rejected");
Check(!CacheKeys.IsValid("workflow/not-a-uuid/input"), "invalid UUID is rejected");

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
