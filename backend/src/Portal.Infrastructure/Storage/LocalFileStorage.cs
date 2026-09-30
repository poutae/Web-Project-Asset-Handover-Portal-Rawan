using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Portal.Infrastructure.Storage;

/// <summary>Stores files on local disk under one root, sharded by the first two characters of the key.</summary>
public sealed class LocalFileStorage : IFileStorage
{
    private readonly string _root;

    public LocalFileStorage(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public static string NewKey() => Guid.NewGuid().ToString("N");

    public async Task<StoredFile> SaveAsync(string key, Stream content, long maxBytes, CancellationToken ct)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;

            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        throw new FileTooLargeException(maxBytes);
                    }

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            // Publish atomically: readers never see a half-written file.
            File.Move(temp, path, overwrite: false);
            return new StoredFile(total, hash.GetHashAndReset());
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        Stream stream = new FileStream(PathFor(key), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        TryDelete(PathFor(key));
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<StoredObject> ListAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        foreach (var shard in ShardDirectories())
        {
            ct.ThrowIfCancellationRequested();
            foreach (var file in Directory.EnumerateFiles(shard))
            {
                var name = Path.GetFileName(file);
                if (IsKey(name) && name.StartsWith(Path.GetFileName(shard), StringComparison.Ordinal))
                {
                    yield return new StoredObject(name, new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero));
                }
            }
        }
    }

    public async Task<int> DeleteAbandonedUploadsAsync(DateTimeOffset olderThan, CancellationToken ct)
    {
        await Task.Yield();
        var removed = 0;
        foreach (var shard in ShardDirectories())
        {
            ct.ThrowIfCancellationRequested();
            foreach (var file in Directory.EnumerateFiles(shard, "*.tmp"))
            {
                var name = Path.GetFileName(file);
                var isUploadTemp = name.Length == 32 + 1 + 32 + 4 && IsKey(name[..32]) && name[32] == '.' && IsKey(name.Substring(33, 32));
                if (isUploadTemp && File.GetLastWriteTimeUtc(file) < olderThan.UtcDateTime)
                {
                    TryDelete(file);
                    if (!File.Exists(file))
                    {
                        removed++;
                    }
                }
            }
        }

        return removed;
    }

    /// <summary>Only the two-character shard folders; anything else under the root is not ours to touch.</summary>
    private IEnumerable<string> ShardDirectories() =>
        Directory.EnumerateDirectories(_root)
            .Where(directory => Path.GetFileName(directory) is { Length: 2 } name && name.All(char.IsAsciiHexDigitLower));

    private static bool IsKey(string value) => value.Length == 32 && value.All(char.IsAsciiHexDigitLower);

    private string PathFor(string key)
    {
        if (key.Length != 32 || !key.All(char.IsAsciiHexDigitLower))
        {
            throw new ArgumentException("Storage keys are 32 lowercase hex characters.", nameof(key));
        }

        var path = Path.GetFullPath(Path.Combine(_root, key[..2], key));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("The key resolves outside the storage root.", nameof(key));
        }

        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: an orphaned temp file is harmless and is not reachable through any key.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
