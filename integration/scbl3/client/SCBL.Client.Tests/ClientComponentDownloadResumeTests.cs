using SplinterCellCNLauncher.ApplicationLayer;
using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace SCBL.Client.Tests;

public sealed class ClientComponentDownloadResumeTests
{
    [Fact]
    public async Task TimedOutDownloadResumesWithStrict206AndFinalHash()
    {
        byte[] payload = CreatePayload();
        const int prefixLength = 96 * 1024;
        var handler = new ResumeHandler(payload, prefixLength, ResumeMode.PartialContent);
        await RunTimeoutThenResumeAsync(payload, handler);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Null(handler.Requests[0].Range);
        Assert.Equal(prefixLength, handler.Requests[1].Range);
        Assert.Equal("\"fixture-v1\"", handler.Requests[1].IfRange);
    }

    [Fact]
    public async Task RangeIgnoredByOriginRestartsFromSame200Response()
    {
        byte[] payload = CreatePayload();
        const int prefixLength = 64 * 1024;
        var handler = new ResumeHandler(payload, prefixLength, ResumeMode.FullContent);
        await RunTimeoutThenResumeAsync(payload, handler);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(prefixLength, handler.Requests[1].Range);
    }

    [Fact]
    public async Task UnsatisfiedRangeIsDiscardedAndRetriedOnceFromZero()
    {
        byte[] payload = CreatePayload();
        const int prefixLength = 48 * 1024;
        var handler = new ResumeHandler(payload, prefixLength, ResumeMode.UnsatisfiedThenFull);
        await RunTimeoutThenResumeAsync(payload, handler);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(prefixLength, handler.Requests[1].Range);
        Assert.Null(handler.Requests[2].Range);
    }

    [Fact]
    public async Task TamperedPartialIsRejectedBeforeNetworkRequest()
    {
        byte[] payload = CreatePayload();
        const int prefixLength = 32 * 1024;
        var handler = new ResumeHandler(payload, prefixLength, ResumeMode.PartialContent);
        string root = CreateTemporaryRoot();
        string destination = Path.Combine(root, "SCBL.exe.download");
        try
        {
            using var http = new HttpClient(handler);
            var service = CreateService(http);
            await Assert.ThrowsAsync<TimeoutException>(() => service.DownloadComponentForTestAsync(
                new Uri("http://updates.invalid/SCBL.exe"),
                destination,
                "test",
                "launcher",
                "2.2.2",
                Convert.ToHexString(SHA256.HashData(payload)),
                payload.LongLength,
                TimeSpan.FromMilliseconds(75)));

            byte[] tampered = File.ReadAllBytes(destination);
            tampered[0] ^= 0x5A;
            File.WriteAllBytes(destination, tampered);

            long completed = await service.DownloadComponentForTestAsync(
                new Uri("http://updates.invalid/SCBL.exe"),
                destination,
                "test",
                "launcher",
                "2.2.2",
                Convert.ToHexString(SHA256.HashData(payload)),
                payload.LongLength,
                TimeSpan.FromSeconds(5));

            Assert.Equal(payload.LongLength, completed);
            Assert.Equal(payload, File.ReadAllBytes(destination));
            Assert.Null(handler.Requests[1].Range);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TargetGenerationChangeDiscardsOldCheckpoint()
    {
        byte[] payload = CreatePayload();
        var handler = new ResumeHandler(payload, 40 * 1024, ResumeMode.PartialContent);
        string root = CreateTemporaryRoot();
        string destination = Path.Combine(root, "SCBL.exe.download");
        try
        {
            using var http = new HttpClient(handler);
            var service = CreateService(http);
            string hash = Convert.ToHexString(SHA256.HashData(payload));
            Uri source = new("http://updates.invalid/SCBL.exe");
            await Assert.ThrowsAsync<TimeoutException>(() => service.DownloadComponentForTestAsync(
                source, destination, "test", "launcher", "2.2.2", hash,
                payload.LongLength, TimeSpan.FromMilliseconds(75)));

            await service.DownloadComponentForTestAsync(
                source, destination, "test", "launcher", "2.2.3", hash,
                payload.LongLength, TimeSpan.FromSeconds(5));

            Assert.Null(handler.Requests[1].Range);
            Assert.Equal(payload, File.ReadAllBytes(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PartialLengthBeyondCheckpointIsRejected()
    {
        byte[] payload = CreatePayload();
        var handler = new ResumeHandler(payload, 24 * 1024, ResumeMode.PartialContent);
        string root = CreateTemporaryRoot();
        string destination = Path.Combine(root, "SCBL.exe.download");
        try
        {
            using var http = new HttpClient(handler);
            var service = CreateService(http);
            string hash = Convert.ToHexString(SHA256.HashData(payload));
            Uri source = new("http://updates.invalid/SCBL.exe");
            await Assert.ThrowsAsync<TimeoutException>(() => service.DownloadComponentForTestAsync(
                source, destination, "test", "launcher", "2.2.2", hash,
                payload.LongLength, TimeSpan.FromMilliseconds(75)));
            await File.AppendAllBytesAsync(destination, [0xA5]);

            await service.DownloadComponentForTestAsync(
                source, destination, "test", "launcher", "2.2.2", hash,
                payload.LongLength, TimeSpan.FromSeconds(5));

            Assert.Null(handler.Requests[1].Range);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CallerCancellationPreservesVerifiedCheckpoint()
    {
        byte[] payload = CreatePayload();
        var handler = new ResumeHandler(payload, 28 * 1024, ResumeMode.PartialContent);
        string root = CreateTemporaryRoot();
        string destination = Path.Combine(root, "SCBL.exe.download");
        try
        {
            using var http = new HttpClient(handler);
            var service = CreateService(http);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(75));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadComponentForTestAsync(
                new Uri("http://updates.invalid/SCBL.exe"),
                destination,
                "test",
                "launcher",
                "2.2.2",
                Convert.ToHexString(SHA256.HashData(payload)),
                payload.LongLength,
                TimeSpan.FromSeconds(5),
                cancellation.Token));

            Assert.True(File.Exists(destination));
            Assert.True(File.Exists(destination + ".json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ChangedEntityTagDiscardsRangeAndRetriesFromZero()
    {
        byte[] payload = CreatePayload();
        var handler = new ResumeHandler(payload, 56 * 1024, ResumeMode.ChangedEntityTagThenFull);
        await RunTimeoutThenResumeAsync(payload, handler);

        Assert.Equal(3, handler.Requests.Count);
        Assert.NotNull(handler.Requests[1].Range);
        Assert.Null(handler.Requests[2].Range);
    }

    [Fact]
    public async Task CompletedPayloadWithWrongShaIsRejectedAndDeleted()
    {
        byte[] payload = CreatePayload();
        var handler = new StaticPayloadHandler(payload);
        string root = CreateTemporaryRoot();
        string destination = Path.Combine(root, "SCBL.exe.download");
        try
        {
            using var http = new HttpClient(handler);
            var service = CreateService(http);
            string wrongHash = new('0', 64);
            await Assert.ThrowsAsync<CryptographicException>(() => service.DownloadComponentForTestAsync(
                new Uri("http://updates.invalid/SCBL.exe"),
                destination,
                "test",
                "launcher",
                "2.2.2",
                wrongHash,
                payload.LongLength,
                TimeSpan.FromSeconds(5)));

            Assert.False(File.Exists(destination));
            Assert.False(File.Exists(destination + ".json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentSameTargetUsesSingleNetworkDownload()
    {
        byte[] payload = CreatePayload();
        var handler = new StaticPayloadHandler(payload, TimeSpan.FromMilliseconds(50));
        string root = CreateTemporaryRoot();
        string destination = Path.Combine(root, "SCBL.exe.download");
        try
        {
            using var http = new HttpClient(handler);
            var service = CreateService(http);
            string hash = Convert.ToHexString(SHA256.HashData(payload));
            Uri source = new("http://updates.invalid/SCBL.exe");
            Task<long> first = service.DownloadComponentForTestAsync(
                source, destination, "test", "launcher", "2.2.2", hash,
                payload.LongLength, TimeSpan.FromSeconds(5));
            Task<long> second = service.DownloadComponentForTestAsync(
                source, destination, "test", "launcher", "2.2.2", hash,
                payload.LongLength, TimeSpan.FromSeconds(5));

            long[] results = await Task.WhenAll(first, second);
            Assert.All(results, result => Assert.Equal(payload.LongLength, result));
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RunTimeoutThenResumeAsync(byte[] payload, ResumeHandler handler)
    {
        string root = CreateTemporaryRoot();
        string destination = Path.Combine(root, "SCBL.exe.download");
        try
        {
            using var http = new HttpClient(handler);
            var service = CreateService(http);
            string hash = Convert.ToHexString(SHA256.HashData(payload));
            Uri source = new("http://updates.invalid/SCBL.exe");

            await Assert.ThrowsAsync<TimeoutException>(() => service.DownloadComponentForTestAsync(
                source,
                destination,
                "test",
                "launcher",
                "2.2.2",
                hash,
                payload.LongLength,
                TimeSpan.FromMilliseconds(75)));
            Assert.True(File.Exists(destination));
            Assert.True(File.Exists(destination + ".json"));

            long completed = await service.DownloadComponentForTestAsync(
                source,
                destination,
                "test",
                "launcher",
                "2.2.2",
                hash,
                payload.LongLength,
                TimeSpan.FromSeconds(5));

            Assert.Equal(payload.LongLength, completed);
            Assert.Equal(payload, File.ReadAllBytes(destination));
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination))));
            Assert.False(File.Exists(destination + ".json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ClientComponentUpdateService CreateService(HttpClient http)
        => new(
            new LauncherRuntimeContext(ClientUpdateChannel.Stable),
            httpClient: http);

    private static byte[] CreatePayload()
        => Enumerable.Range(0, 512 * 1024)
            .Select(index => (byte)((index * 31 + 17) % 251))
            .ToArray();

    private static string CreateTemporaryRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "scbl-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private enum ResumeMode
    {
        PartialContent,
        FullContent,
        UnsatisfiedThenFull,
        ChangedEntityTagThenFull
    }

    private sealed class ResumeHandler(byte[] payload, int prefixLength, ResumeMode mode)
        : HttpMessageHandler
    {
        private int _callCount;

        public List<(long? Range, string? IfRange)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _callCount);
            long? range = request.Headers.Range?.Ranges.Single().From;
            Requests.Add((range, request.Headers.IfRange?.EntityTag?.ToString()));
            if (call == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new PrefixThenBlockStream(payload[..prefixLength]))
                };
                response.Content.Headers.ContentLength = payload.LongLength;
                response.Headers.ETag = new EntityTagHeaderValue("\"fixture-v1\"");
                return Task.FromResult(response);
            }

            if (mode == ResumeMode.UnsatisfiedThenFull && call == 2)
            {
                var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(payload.LongLength);
                return Task.FromResult(response);
            }

            if (mode == ResumeMode.ChangedEntityTagThenFull && call == 2)
            {
                long changedOffset = range ?? throw new InvalidOperationException("Expected a range request.");
                var changed = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(payload[(int)changedOffset..])
                };
                changed.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                    changedOffset,
                    payload.LongLength - 1,
                    payload.LongLength);
                changed.Headers.ETag = new EntityTagHeaderValue("\"fixture-v2\"");
                return Task.FromResult(changed);
            }

            if (range is null || mode == ResumeMode.FullContent || call == 3)
                return Task.FromResult(CreateFullResponse());

            long offset = range ?? throw new InvalidOperationException("Expected a range request.");
            byte[] remainder = payload[(int)offset..];
            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(remainder)
            };
            partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                offset,
                payload.LongLength - 1,
                payload.LongLength);
            partial.Headers.ETag = new EntityTagHeaderValue("\"fixture-v1\"");
            return Task.FromResult(partial);
        }

        private HttpResponseMessage CreateFullResponse()
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"fixture-v1\"");
            return response;
        }
    }

    private sealed class StaticPayloadHandler(byte[] payload, TimeSpan? delay = null)
        : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            if (delay is TimeSpan value)
                await Task.Delay(value, cancellationToken);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"static-v1\"");
            return response;
        }
    }

    private sealed class PrefixThenBlockStream(byte[] prefix) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => prefix.LongLength;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= prefix.Length)
                throw new NotSupportedException("Synchronous blocking reads are not supported.");
            int read = Math.Min(count, prefix.Length - _position);
            prefix.AsSpan(_position, read).CopyTo(buffer.AsSpan(offset, read));
            _position += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position < prefix.Length)
            {
                int read = Math.Min(buffer.Length, prefix.Length - _position);
                prefix.AsMemory(_position, read).CopyTo(buffer);
                _position += read;
                return read;
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
