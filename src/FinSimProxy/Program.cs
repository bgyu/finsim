using System.Security.Claims;
using System.Text.Json;
using FinSim.Contracts;
using FinSim.Infrastructure.Cache;
using FinSim.Infrastructure.Security;
using FinSim.Infrastructure.Slurm;
using FinSimProxy.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 256 * 1024);
builder.Services.AddFinSimJwtAuthentication(
    builder.Configuration.GetSection("Jwt"),
    "finsim.submit");
builder.Services.AddHttpClient("cache", client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient("slurm", client => client.Timeout = TimeSpan.FromSeconds(15));

builder.Services.AddSingleton<IKeyValueStore>(services =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    return new HttpKeyValueStore(
        services.GetRequiredService<IHttpClientFactory>().CreateClient("cache"),
        RequiredUri(
            configuration,
            "Cache:BaseUrl",
            configuration.GetValue("Cache:AllowInsecureHttp", false)),
        Required(configuration, "Cache:TokenFile"));
});
builder.Services.AddSingleton(services =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var allowHttp = configuration.GetValue("Slurm:AllowInsecureHttp", false);
    return new SlurmRestClient(
        services.GetRequiredService<IHttpClientFactory>().CreateClient("slurm"),
        RequiredUri(configuration, "Slurm:BaseUrl", allowHttp),
        Required(configuration, "Slurm:TokenFile"),
        configuration["Slurm:User"] ?? "finsim",
        allowHttp);
});
builder.Services.AddSingleton<WorkflowService>();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", (WorkflowService service) =>
    service.IsReady() ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));

var api = app.MapGroup("/v1/simulations")
    .RequireAuthorization(JwtAuthentication.ApiPolicy);

api.MapPost("/", async (
    SimulationRequest request,
    HttpContext context,
    WorkflowService service,
    CancellationToken cancellationToken) =>
{
    var subject = context.User.FindFirstValue("sub");
    var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
    var correlationId = CorrelationId(context);
    if (string.IsNullOrWhiteSpace(subject))
    {
        return Results.Forbid();
    }

    try
    {
        var result = await service.CreateAsync(
            request,
            subject,
            idempotencyKey,
            correlationId,
            cancellationToken);
        return Results.Accepted($"/v1/simulations/{result.WorkflowId:D}", result);
    }
    catch (ArgumentException error)
    {
        return Results.BadRequest(new { error = error.Message });
    }
    catch (WorkflowConflictException error)
    {
        return Results.Conflict(new { error = error.Message });
    }
    catch (SlurmRejectedException error)
    {
        return Results.UnprocessableEntity(new { error = error.Message });
    }
    catch (SlurmUnavailableException)
    {
        return Results.StatusCode(503);
    }
});

api.MapGet("/{workflowId:guid}", async (
    Guid workflowId,
    HttpContext context,
    WorkflowService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        var subject = context.User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(subject))
        {
            return Results.Forbid();
        }

        var result = await service.GetStatusAsync(
            workflowId,
            subject,
            CorrelationId(context),
            cancellationToken);
        return result.Status == "NOT_FOUND" ? Results.NotFound() : Results.Ok(result);
    }
    catch (SlurmUnavailableException)
    {
        return Results.StatusCode(503);
    }
});

api.MapGet("/{workflowId:guid}/report", async (
    Guid workflowId,
    HttpContext context,
    WorkflowService service,
    CancellationToken cancellationToken) =>
{
    var subject = context.User.FindFirstValue("sub");
    if (string.IsNullOrWhiteSpace(subject))
    {
        return Results.Forbid();
    }

    var report = await service.ReadReportAsync(workflowId, subject, cancellationToken);
    return report is null
        ? Results.NotFound()
        : Results.Text(report, "application/json", System.Text.Encoding.UTF8);
});

api.MapDelete("/{workflowId:guid}", async (
    Guid workflowId,
    HttpContext context,
    WorkflowService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        var subject = context.User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(subject))
        {
            return Results.Forbid();
        }

        await service.CancelAsync(
            workflowId,
            subject,
            CorrelationId(context),
            cancellationToken);
        return Results.Accepted($"/v1/simulations/{workflowId:D}");
    }
    catch (SlurmException)
    {
        return Results.StatusCode(503);
    }
    catch (WorkflowNotFoundException)
    {
        return Results.NotFound();
    }
});

app.Run();

static string CorrelationId(HttpContext context)
{
    var supplied = context.Request.Headers["X-Correlation-ID"].ToString();
    var value = Guid.TryParse(supplied, out var parsed) ? parsed : Guid.NewGuid();
    context.Response.Headers["X-Correlation-ID"] = value.ToString("D");
    return value.ToString("D");
}

static string Required(IConfiguration configuration, string key) =>
    string.IsNullOrWhiteSpace(configuration[key])
        ? throw new InvalidOperationException($"Configuration {key} is required.")
        : configuration[key]!;

static Uri RequiredUri(IConfiguration configuration, string key, bool allowHttp)
{
    var value = Required(configuration, key);
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
        (uri.Scheme != Uri.UriSchemeHttps && !(allowHttp && uri.Scheme == Uri.UriSchemeHttp)))
    {
        throw new InvalidOperationException($"Configuration {key} is not an allowed URL.");
    }

    return uri;
}
