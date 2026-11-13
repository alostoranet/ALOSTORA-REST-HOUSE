using Alostora.RestHouse;
using Xunit;

namespace Alostora.RestHouse.Tests;

public sealed class BoundedImageUploadReaderTests
{
    [Fact]
    public async Task ReadsPayloadAtExactLimit()
    {
        var expected = Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();
        using var source = new MemoryStream(expected);

        var actual = await BoundedImageUploadReader.ReadAsync(source, expected.Length);

        Assert.Equal(expected, actual);
        Assert.Equal(expected.Length, source.Position);
    }

    [Fact]
    public async Task RejectsOversizedPayloadWithoutReadingRemainder()
    {
        using var source = new MemoryStream(new byte[1024]);

        var actual = await BoundedImageUploadReader.ReadAsync(source, 10);

        Assert.Null(actual);
        Assert.Equal(11, source.Position);
    }

    [Fact]
    public async Task ReadsNonSeekableStreamWithoutLengthAtExactLimit()
    {
        var expected = Enumerable.Range(0, 128).Select(x => (byte)x).ToArray();
        using var source = new NonSeekableReadStream(expected);

        var actual = await BoundedImageUploadReader.ReadAsync(source, expected.Length);

        Assert.Equal(expected, actual);
        Assert.Equal(expected.Length, source.BytesRead);
    }

    [Fact]
    public async Task RejectsOversizedNonSeekableStreamAfterSentinelByte()
    {
        using var source = new NonSeekableReadStream(new byte[1024]);

        var actual = await BoundedImageUploadReader.ReadAsync(source, 10);

        Assert.Null(actual);
        Assert.Equal(11, source.BytesRead);
    }

    [Fact]
    public async Task ReturnsEmptyPayloadForEmptyStream()
    {
        using var source = new MemoryStream();

        var actual = await BoundedImageUploadReader.ReadAsync(source, 10);

        Assert.NotNull(actual);
        Assert.Empty(actual);
    }

    [Fact]
    public async Task RejectsNonPositiveLimit()
    {
        using var source = new MemoryStream([1]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => BoundedImageUploadReader.ReadAsync(source, 0));
    }

    private sealed class NonSeekableReadStream(byte[] data) : Stream
    {
        private int _position;

        public int BytesRead => _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, data.Length - _position);
            Array.Copy(data, _position, buffer, offset, read);
            _position += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Math.Min(buffer.Length, data.Length - _position);
            data.AsMemory(_position, read).CopyTo(buffer);
            _position += read;
            return ValueTask.FromResult(read);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
