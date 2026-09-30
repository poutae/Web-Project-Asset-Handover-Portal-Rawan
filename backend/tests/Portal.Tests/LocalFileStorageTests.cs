using System.Security.Cryptography;
using Portal.Infrastructure.Storage;

namespace Portal.Tests;

/// <summary>File-system behaviour of the storage layer; no database involved.</summary>
public sealed class LocalFileStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"portal-storage-{Guid.NewGuid():N}");
    private readonly LocalFileStorage _storage;

    public LocalFileStorageTests() => _storage = new LocalFileStorage(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Saves_the_bytes_and_reports_their_size_and_sha256()
    {
        var data = RandomNumberGenerator.GetBytes(200_000);
        var key = LocalFileStorage.NewKey();

        var stored = await _storage.SaveAsync(key, new MemoryStream(data), maxBytes: 1_000_000, Ct);

        Assert.Equal(data.Length, stored.SizeBytes);
        Assert.Equal(SHA256.HashData(data), stored.Sha256);
        await using var read = await _storage.OpenReadAsync(key, Ct);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy, Ct);
        Assert.Equal(data, copy.ToArray());
    }

    [Fact]
    public async Task Stops_reading_at_the_limit_and_leaves_nothing_behind()
    {
        var key = LocalFileStorage.NewKey();

        await Assert.ThrowsAsync<FileTooLargeException>(() =>
            _storage.SaveAsync(key, new MemoryStream(new byte[10_000]), maxBytes: 4_096, Ct));

        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
        await Assert.ThrowsAnyAsync<IOException>(() => _storage.OpenReadAsync(key, Ct));
    }

    [Fact]
    public async Task A_failed_read_leaves_nothing_behind()
    {
        var key = LocalFileStorage.NewKey();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _storage.SaveAsync(key, new ThrowingStream(), maxBytes: 1_000, Ct));

        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Deleting_removes_the_file_and_deleting_a_missing_file_is_fine()
    {
        var key = LocalFileStorage.NewKey();
        await _storage.SaveAsync(key, new MemoryStream([1, 2, 3]), 100, Ct);

        await _storage.DeleteAsync(key, Ct);
        await _storage.DeleteAsync(key, Ct);

        await Assert.ThrowsAnyAsync<IOException>(() => _storage.OpenReadAsync(key, Ct));
    }

    [Theory]
    [InlineData("../../evil")]
    [InlineData("..\\evil")]
    [InlineData("/etc/passwd")]
    [InlineData("short")]
    [InlineData("ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    public async Task Rejects_keys_that_are_not_server_generated(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.SaveAsync(key, new MemoryStream([1]), 100, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.OpenReadAsync(key, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.DeleteAsync(key, Ct));
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("boom");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Lists_only_finished_files_with_their_modification_time()
    {
        var key = LocalFileStorage.NewKey();
        await _storage.SaveAsync(key, new MemoryStream(new byte[100]), maxBytes: 1_000, Ct);
        var shard = Path.Combine(_root, key[..2]);
        File.WriteAllText(Path.Combine(shard, $"{key}.{Guid.NewGuid():N}.tmp"), "half written");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "not ours");
        Directory.CreateDirectory(Path.Combine(_root, "deploy"));
        File.WriteAllText(Path.Combine(_root, "deploy", new string('a', 32)), "someone else's file");
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(shard, key), stamp);

        var listed = new List<StoredObject>();
        await foreach (var file in _storage.ListAsync(Ct))
        {
            listed.Add(file);
        }

        var only = Assert.Single(listed);
        Assert.Equal(key, only.Key);
        Assert.Equal(new DateTimeOffset(stamp), only.LastModified);
    }

    [Fact]
    public async Task Removes_only_old_half_written_uploads()
    {
        var key = LocalFileStorage.NewKey();
        await _storage.SaveAsync(key, new MemoryStream(new byte[100]), maxBytes: 1_000, Ct);
        var shard = Path.Combine(_root, key[..2]);
        var old = Path.Combine(shard, $"{key}.{Guid.NewGuid():N}.tmp");
        var recent = Path.Combine(shard, $"{key}.{Guid.NewGuid():N}.tmp");
        var stranger = Path.Combine(shard, "something-else.tmp");
        foreach (var path in new[] { old, recent, stranger })
        {
            File.WriteAllText(path, "x");
        }

        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(stranger, DateTime.UtcNow.AddDays(-3));

        var removed = await _storage.DeleteAbandonedUploadsAsync(DateTimeOffset.UtcNow.AddDays(-1), Ct);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(stranger));
        Assert.True(File.Exists(Path.Combine(shard, key)));
    }
}
