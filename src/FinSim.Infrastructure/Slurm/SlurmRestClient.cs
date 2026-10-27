using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FinSim.Infrastructure.Security;

namespace FinSim.Infrastructure.Slurm;

public sealed record SlurmJobSnapshot(string JobId, string State, string? Comment);

/// <summary>
/// Minimal, pinned Slurm REST v0.0.45 client. Mutations distinguish definitive
/// rejection from an ambiguous transport failure so callers never retry a
/// possibly accepted submission blindly.
/// </summary>
public sealed class SlurmRestClient
{
    public const string ApiPrefix = "/slurm/v0.0.45";
    private readonly HttpClient httpClient;
    private readonly Uri baseUri;
    private readonly string tokenFile;
    private readonly string user;

    public SlurmRestClient(
        HttpClient httpClient,
        Uri baseUri,
        string tokenFile,
        string user,
        bool allowInsecureHttp = false)
    {
        if (!baseUri.IsAbsoluteUri ||
            (baseUri.Scheme != Uri.UriSchemeHttps &&
             !(allowInsecureHttp && baseUri.Scheme == Uri.UriSchemeHttp)))
        {
            throw new ArgumentException("Slurm REST URL must use HTTPS.", nameof(baseUri));
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(user, "^[a-z_][a-z0-9_-]{0,31}$"))
        {
            throw new ArgumentException("Slurm user is invalid.", nameof(user));
        }

        this.httpClient = httpClient;
        this.baseUri = baseUri;
        this.tokenFile = tokenFile;
        this.user = user;
    }

    public async Task<string> SubmitJobAsync(
        JsonObject requestBody,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = NewRequest(
                HttpMethod.Post,
                $"{ApiPrefix}/job/submit",
                correlationId);
            request.Content = new StringContent(
                requestBody.ToJsonString(),
                Encoding.UTF8,
                "application/json");
            using var response = await SendAsync(request, mutation: true, cancellationToken);
            using var payload = await ReadJsonAsync(response, cancellationToken);
            return ReadJobId(payload.RootElement) ??
                throw new SlurmAmbiguousException(
                    "Slurm accepted submission without a job identifier.");
        }
        catch (SlurmException)
        {
            throw;
        }
        catch (Exception error) when (
            error is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new SlurmAmbiguousException("Slurm submission transport failed.", error);
        }
    }

    public async Task<IReadOnlyList<SlurmJobSnapshot>> FindJobsByCommentAsync(
        string comment,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        using var request = NewRequest(
            HttpMethod.Get,
            $"{ApiPrefix}/jobs?update_time=0",
            correlationId);
        using var response = await SendAsync(request, mutation: false, cancellationToken);
        using var payload = await ReadJsonAsync(response, cancellationToken);
        if (!payload.RootElement.TryGetProperty("jobs", out var jobs) ||
            jobs.ValueKind != JsonValueKind.Array)
        {
            throw new SlurmUnavailableException("Slurm jobs response is invalid.");
        }

        var matches = new List<SlurmJobSnapshot>();
        foreach (var job in jobs.EnumerateArray())
        {
            var jobComment = ReadString(job, "comment");
            if (!string.Equals(jobComment, comment, StringComparison.Ordinal))
            {
                continue;
            }

            var id = ReadString(job, "job_id");
            var state = ReadString(job, "job_state") ?? ReadString(job, "state");
            if (id is not null && state is not null)
            {
                matches.Add(new SlurmJobSnapshot(id, state, jobComment));
            }
        }

        return matches
            .GroupBy(job => job.JobId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(job => job.JobId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task CancelJobAsync(
        string jobId,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(jobId, "^[0-9]+(?:_[0-9]+)?$"))
        {
            throw new ArgumentException("Slurm job ID is invalid.", nameof(jobId));
        }

        using var request = NewRequest(
            HttpMethod.Delete,
            $"{ApiPrefix}/job/{Uri.EscapeDataString(jobId)}",
            correlationId);
        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound || response.IsSuccessStatusCode)
            {
                return;
            }

            if ((int)response.StatusCode is >= 400 and < 500)
            {
                throw new SlurmRejectedException(
                    $"Slurm cancellation was rejected with HTTP {(int)response.StatusCode}.");
            }

            throw new SlurmAmbiguousException(
                $"Slurm cancellation returned HTTP {(int)response.StatusCode}.");
        }
        catch (SlurmException)
        {
            throw;
        }
        catch (Exception error) when (
            error is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new SlurmAmbiguousException("Slurm cancellation transport failed.", error);
        }
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path, string correlationId)
    {
        if (!Guid.TryParse(correlationId, out _))
        {
            throw new ArgumentException("Correlation ID must be a UUID.", nameof(correlationId));
        }

        var request = new HttpRequestMessage(method, new Uri(baseUri, path));
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Add("X-Correlation-ID", correlationId);
        request.Headers.Add("X-SLURM-USER-NAME", user);
        request.Headers.Add("X-SLURM-USER-TOKEN", PrivateTokenFile.Read(tokenFile));
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        bool mutation,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var status = (int)response.StatusCode;
            response.Dispose();
            if (status is >= 400 and < 500)
            {
                throw new SlurmRejectedException(
                    $"Slurm request was rejected with HTTP {status}.");
            }

            throw mutation
                ? new SlurmAmbiguousException($"Slurm mutation returned HTTP {status}.")
                : new SlurmUnavailableException($"Slurm query returned HTTP {status}.");
        }
        catch (SlurmException)
        {
            throw;
        }
        catch (Exception error) when (
            error is HttpRequestException or TaskCanceledException or IOException)
        {
            throw mutation
                ? new SlurmAmbiguousException("Slurm mutation transport failed.", error)
                : new SlurmUnavailableException("Slurm query transport failed.", error);
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        const int maximumBytes = 8 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new SlurmUnavailableException("Slurm response is too large.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                return JsonDocument.Parse(
                    output.ToArray(),
                    new JsonDocumentOptions { MaxDepth = 64 });
            }

            if (output.Length + count > maximumBytes)
            {
                throw new SlurmUnavailableException("Slurm response is too large.");
            }

            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }

    private static string? ReadJobId(JsonElement root)
    {
        if (ReadString(root, "job_id") is { } direct)
        {
            return direct;
        }

        if (root.TryGetProperty("jobs", out var jobs) && jobs.ValueKind == JsonValueKind.Array)
        {
            foreach (var job in jobs.EnumerateArray())
            {
                return ReadString(job, "job_id");
            }
        }

        if (root.TryGetProperty("job_submit_response", out var submit))
        {
            return ReadString(submit, "job_id");
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var wrapper in new[] { "string", "number", "value" })
            {
                if (value.TryGetProperty(wrapper, out var unwrapped))
                {
                    value = unwrapped;
                    break;
                }
            }
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }
}
