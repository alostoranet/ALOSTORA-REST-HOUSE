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
}
