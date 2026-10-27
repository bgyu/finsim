using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FinSim.Contracts;
using FinSim.Infrastructure.Security;

namespace FinSim.Infrastructure.Cache;

/// <summary>HTTP implementation used unchanged by proxy, master, and workers.</summary>
public sealed class HttpKeyValueStore(
    HttpClient httpClient,
    Uri baseUri,
    string tokenFile,
    bool allowInsecureHttp = false) : IKeyValueStore
{
    private readonly Uri validatedBaseUri = ValidateBaseUri(baseUri, allowInsecureHttp);

    public async Task<PutResult> PutIfAbsentAsync(
        string key,
        JsonElement value,
        CancellationToken cancellationToken = default)
    {
        Validate(key);
        using var request = NewRequest(HttpMethod.Put, key);
        request.Content = JsonContent.Create(value);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.StatusCode switch
        {
            HttpStatusCode.Created => PutResult.Created,
            HttpStatusCode.OK => PutResult.Replayed,
            HttpStatusCode.Conflict => PutResult.Conflict,
            _ => throw new HttpRequestException(
                $"Cache PUT failed with HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode)
        };
    }

    public async Task<JsonElement?> GetAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        Validate(key);
        using var request = NewRequest(HttpMethod.Get, key);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Cache GET failed with HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        if (response.Content.Headers.ContentLength > FileKeyValueStore.MaximumValueBytes)
        {
            throw new InvalidDataException("Cache response is too large.");
        }

        var bytes = await ReadBoundedAsync(response, cancellationToken);
        var value = JsonSerializer.Deserialize<JsonElement>(bytes);
        return value.Clone();
    }

    public async Task<bool> DeleteAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        Validate(key);
        using var request = NewRequest(HttpMethod.Delete, key);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.StatusCode switch
        {
            HttpStatusCode.NoContent => true,
            HttpStatusCode.NotFound => false,
            _ => throw new HttpRequestException(
                $"Cache DELETE failed with HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode)
        };
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string key)
    {
        var escaped = string.Join('/', key.Split('/').Select(Uri.EscapeDataString));
        var request = new HttpRequestMessage(
            method,
            new Uri(validatedBaseUri, $"v1/kv/{escaped}"));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            PrivateTokenFile.Read(tokenFile));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static void Validate(string key)
    {
        if (!CacheKeys.IsValid(key))
        {
            throw new ArgumentException("Cache key is invalid.", nameof(key));
        }
    }

    private static Uri ValidateBaseUri(Uri uri, bool allowInsecureHttp)
    {
        if (!uri.IsAbsoluteUri ||
            (uri.Scheme != Uri.UriSchemeHttps &&
             !(allowInsecureHttp && uri.Scheme == Uri.UriSchemeHttp)))
        {
            throw new ArgumentException("Cache URL must use HTTPS.", nameof(uri));
        }

        return uri;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8 * 1024];
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                return output.ToArray();
            }

            if (output.Length + count > FileKeyValueStore.MaximumValueBytes)
            {
                throw new InvalidDataException("Cache response is too large.");
            }

            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }
}
