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
