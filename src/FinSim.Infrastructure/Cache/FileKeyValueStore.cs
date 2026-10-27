using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FinSim.Contracts;

namespace FinSim.Infrastructure.Cache;

/// <summary>
/// Local-only durable implementation of the Couchbase-shaped key/value
/// boundary. Filenames are SHA-256 digests of already validated logical keys,
/// and writes are made durable before an atomic, non-overwriting rename.
/// </summary>
public sealed class FileKeyValueStore : IKeyValueStore
{
    public const int MaximumValueBytes = 128 * 1024;
    private readonly string root;

    public FileKeyValueStore(string root)
    {
        this.root = Path.GetFullPath(root);
        Directory.CreateDirectory(this.root);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                this.root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var info = new DirectoryInfo(this.root);
        if (info.LinkTarget is not null)
        {
            throw new InvalidOperationException("Cache root must not be a symbolic link.");
        }
    }

    public async Task<PutResult> PutIfAbsentAsync(
        string key,
        JsonElement value,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        var valueBytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (valueBytes.Length > MaximumValueBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Cache value is too large.");
        }

        var digest = Convert.ToHexStringLower(SHA256.HashData(valueBytes));
        var envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(
            new StoredEnvelope(key, digest, value));
        var destination = PathFor(key);
        var temporary = Path.Combine(root, $".pending-{Guid.NewGuid():N}");
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
                await stream.WriteAsync(envelopeBytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporary, destination, overwrite: false);
                return PutResult.Created;
            }
            catch (IOException) when (File.Exists(destination))
            {
                var existing = await ReadEnvelopeAsync(destination, cancellationToken);
                return existing.Key == key && existing.Digest == digest
                    ? PutResult.Replayed
                    : PutResult.Conflict;
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

    public async Task<JsonElement?> GetAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        var path = PathFor(key);
        if (!File.Exists(path))
        {
            return null;
        }

        var envelope = await ReadEnvelopeAsync(path, cancellationToken);
        if (!string.Equals(envelope.Key, key, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Cache key digest collision detected.");
        }

        return envelope.Value.Clone();
    }

    public Task<bool> DeleteAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);
        var path = PathFor(key);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    public bool IsReady() => Directory.Exists(root) && new DirectoryInfo(root).LinkTarget is null;

    private async Task<StoredEnvelope> ReadEnvelopeAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length > MaximumValueBytes * 2L)
        {
            throw new InvalidDataException("Cache record is invalid.");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var envelope = await JsonSerializer.DeserializeAsync<StoredEnvelope>(
            stream,
            cancellationToken: cancellationToken);
        return envelope ?? throw new InvalidDataException("Cache record is empty.");
    }

    private string PathFor(string key)
    {
        var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(root, $"{name}.json");
    }

    private static void ValidateKey(string key)
    {
        if (!CacheKeys.IsValid(key))
        {
            throw new ArgumentException("Cache key is invalid.", nameof(key));
        }
    }

    private sealed record StoredEnvelope(string Key, string Digest, JsonElement Value);
}
