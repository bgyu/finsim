using System.Text.Json;
using FinSim.Contracts;
using FinSim.Infrastructure.Cache;
using FinSim.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = FileKeyValueStore.MaximumValueBytes);
builder.Services.AddFinSimJwtAuthentication(
    builder.Configuration.GetSection("Jwt"),
    "finsim.cache");

var cacheRoot = builder.Configuration["Cache:DataRoot"] ?? "/var/lib/finsim/cache";
builder.Services.AddSingleton(new FileKeyValueStore(cacheRoot));

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", (FileKeyValueStore store) =>
    store.IsReady() ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));

var api = app.MapGroup("/v1/kv").RequireAuthorization(JwtAuthentication.ApiPolicy);

api.MapPut("/{**key}", async (
    string key,
    JsonElement value,
    IKeyValueStore store,
    CancellationToken cancellationToken) =>
{
    if (!CacheKeys.IsValid(key))
    {
        return Results.BadRequest(new { error = "invalid cache key" });
    }

    try
    {
        return await store.PutIfAbsentAsync(key, value, cancellationToken) switch
        {
            PutResult.Created => Results.Created($"/v1/kv/{key}", null),
            PutResult.Replayed => Results.Ok(new { status = "replayed" }),
            PutResult.Conflict => Results.Conflict(new { error = "key already has different content" }),
            _ => Results.StatusCode(500)
        };
    }
    catch (ArgumentOutOfRangeException)
    {
        return Results.BadRequest(new { error = "cache value is too large" });
    }
});

api.MapGet("/{**key}", async (
    string key,
    IKeyValueStore store,
    CancellationToken cancellationToken) =>
{
    if (!CacheKeys.IsValid(key))
    {
        return Results.BadRequest(new { error = "invalid cache key" });
    }

    var value = await store.GetAsync(key, cancellationToken);
    return value is null ? Results.NotFound() : Results.Json(value.Value);
});

api.MapDelete("/{**key}", async (
    string key,
    IKeyValueStore store,
    CancellationToken cancellationToken) =>
{
    if (!CacheKeys.IsValid(key))
    {
        return Results.BadRequest(new { error = "invalid cache key" });
    }

    return await store.DeleteAsync(key, cancellationToken)
        ? Results.NoContent()
        : Results.NotFound();
});

app.Run();
