using System.IO;

namespace Alostora.RestHouse;

/// <summary>
/// Reads an uploaded image without buffering more than the configured limit.
/// Returns null when the source exceeds the limit; the source stream is not disposed.
/// </summary>
public static class BoundedImageUploadReader
{
    public static async Task<byte[]?> ReadAsync(
        Stream source,
        int maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        // Avoid reserving the full upload limit for empty or small requests.
        using var buffer = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var chunk = new byte[64 * 1024];

        while (true)
        {
            // Read at most one byte beyond the limit so an oversized request is
            // rejected immediately even when Content-Length is absent or incorrect.
            var remainingWithSentinel = maximumBytes - (int)buffer.Length + 1;
            var readLimit = Math.Min(chunk.Length, remainingWithSentinel);
            var read = await source.ReadAsync(
                chunk.AsMemory(0, readLimit), cancellationToken).ConfigureAwait(false);

            if (read == 0)
                return buffer.ToArray();

            if (buffer.Length + read > maximumBytes)
                return null;

            // Grow geometrically, but never let MemoryStream reserve more than
            // the configured payload limit.
            var requiredCapacity = checked((int)buffer.Length + read);
            if (requiredCapacity > buffer.Capacity)
            {
                var doubledCapacity = buffer.Capacity > maximumBytes / 2
                    ? maximumBytes
                    : buffer.Capacity * 2;
                buffer.Capacity = Math.Min(
                    maximumBytes,
                    Math.Max(requiredCapacity, doubledCapacity));
            }

            await buffer.WriteAsync(
                chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}
