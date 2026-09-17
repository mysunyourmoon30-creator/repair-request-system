using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Attachments;
using RepairRequest.Infrastructure.Files;
using RepairRequest.IntegrationTests.Authentication;

namespace RepairRequest.IntegrationTests.Attachments;

/// <summary>Local private storage adapter: opaque key validation, no overwrite, no partial files, root confinement.</summary>
public sealed class LocalFileStorageTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rr-storage-tests", Guid.NewGuid().ToString("N"));
    private readonly AuthenticationTestHost _host;

    public LocalFileStorageTests()
    {
        _host = new AuthenticationTestHost(services => services.Configure<FileStorageOptions>(options => options.RootPath = _root));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private IFileStorage Storage() => _host.CreateScope().ServiceProvider.GetRequiredService<IFileStorage>();

    [Fact]
    public async Task SaveOpenDelete_RoundTripsUnderTheRoot()
    {
        var storage = Storage();
        var key = AttachmentFileRules.NewStorageKey(Guid.NewGuid());
        byte[] content = [1, 2, 3, 4, 5];

        await storage.SaveAsync(key, new MemoryStream(content), CancellationToken.None);
        await using (var stream = await storage.OpenReadAsync(key, CancellationToken.None))
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.Equal(content, copy.ToArray());
        }

        var files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);
        Assert.StartsWith(Path.GetFullPath(_root), Assert.Single(files), StringComparison.Ordinal);

        await storage.DeleteAsync(key, CancellationToken.None);
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../../../../windows/system32")]
    [InlineData("abc")]
    [InlineData("0123456789abcdef0123456789abcdef/..")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF/0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef\\0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef/0123456789abcdef0123456789abcdef.png")]
    public async Task NonServerKeys_AreRejected(string key)
    {
        var storage = Storage();

        await Assert.ThrowsAsync<ArgumentException>(() => storage.SaveAsync(key, new MemoryStream([1]), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.OpenReadAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task Save_NeverOverwritesAnExistingObject()
    {
        var storage = Storage();
        var key = AttachmentFileRules.NewStorageKey(Guid.NewGuid());
        await storage.SaveAsync(key, new MemoryStream([1, 1, 1]), CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => storage.SaveAsync(key, new MemoryStream([9, 9, 9]), CancellationToken.None));

        await using var stream = await storage.OpenReadAsync(key, CancellationToken.None);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        Assert.Equal(new byte[] { 1, 1, 1 }, copy.ToArray());
        Assert.Single(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task FailedSave_LeavesNoFinalOrPartialFile()
    {
        var storage = Storage();
        var key = AttachmentFileRules.NewStorageKey(Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidDataException>(() => storage.SaveAsync(key, new FailingStream(), CancellationToken.None));

        Assert.Empty(Directory.Exists(_root) ? Directory.GetFiles(_root, "*", SearchOption.AllDirectories) : []);
    }

    /// <summary>Returns one chunk of data, then fails, so a copy is interrupted mid-stream.</summary>
    private sealed class FailingStream : Stream
    {
        private int _reads;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Next(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Next(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Next(buffer.AsSpan(offset, count)));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Next(Span<byte> buffer)
        {
            if (_reads++ > 0)
            {
                throw new InvalidDataException("Simulated read failure.");
            }

            buffer.Fill(7);
            return buffer.Length;
        }
    }
}
