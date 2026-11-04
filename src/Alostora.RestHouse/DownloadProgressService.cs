using System.Collections.Concurrent;

namespace Alostora.RestHouse;

public sealed record DownloadProgressItem(
    string DownloadId, string ItemId, string FileName, long BytesTransferred,
    long TotalBytes, string Status, DateTime UpdatedUtc)
{
    public int Percent => TotalBytes <= 0 ? 0 : (int)Math.Clamp(BytesTransferred * 100d / TotalBytes, 0, 100);
}

public sealed class DownloadProgressService
{
    private readonly ConcurrentDictionary<string, DownloadProgressItem> _downloads = new(StringComparer.Ordinal);

    private static string Key(string clientId, string downloadId) => clientId + "|" + downloadId;

    public void Begin(string clientId, string downloadId, string itemId, string fileName, long totalBytes, long rangeStart)
    {
        var key = Key(clientId, downloadId);
        _downloads.AddOrUpdate(key,
            _ => new(downloadId, itemId, fileName, Math.Clamp(rangeStart, 0, totalBytes), totalBytes, "جارٍ التنزيل", DateTime.UtcNow),
            (_, old) => old with
            {
                ItemId = itemId,
                FileName = fileName,
                TotalBytes = totalBytes,
                BytesTransferred = rangeStart > 0 ? Math.Clamp(rangeStart, 0, totalBytes) : 0,
                Status = "جارٍ التنزيل",
                UpdatedUtc = DateTime.UtcNow
            });
        PruneClientHistory(clientId, downloadId);
    }

    public void Report(string clientId, string downloadId, long bytesTransferred)
    {
        var key = Key(clientId, downloadId);
        while (_downloads.TryGetValue(key, out var old))
        {
            var updated = old with
            {
                BytesTransferred = Math.Clamp(bytesTransferred, 0, old.TotalBytes),
                Status = "جارٍ التنزيل",
                UpdatedUtc = DateTime.UtcNow
            };
            if (_downloads.TryUpdate(key, updated, old)) return;
        }
    }

    public void Complete(string clientId, string downloadId)
    {
        var key = Key(clientId, downloadId);
        if (_downloads.TryGetValue(key, out var old))
            _downloads[key] = old with { BytesTransferred = old.TotalBytes, Status = "اكتمل", UpdatedUtc = DateTime.UtcNow };
    }

    public void Interrupt(string clientId, string downloadId)
    {
        var key = Key(clientId, downloadId);
        if (_downloads.TryGetValue(key, out var old) && old.Status != "اكتمل")
            _downloads[key] = old with { Status = "توقف الاتصال — يمكن محاولة الاستئناف من مدير التنزيلات", UpdatedUtc = DateTime.UtcNow };
    }

    private void PruneClientHistory(string clientId, string keepDownloadId)
    {
        var prefix = clientId + "|";
        var stale = _downloads.Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal) && x.Value.DownloadId != keepDownloadId)
            .OrderByDescending(x => x.Value.UpdatedUtc)
            .Skip(29)
            .Select(x => x.Key)
            .ToArray();
        foreach (var key in stale) _downloads.TryRemove(key, out _);
    }

    public IReadOnlyList<DownloadProgressItem> List(string clientId) =>
        _downloads.Where(x => x.Key.StartsWith(clientId + "|", StringComparison.Ordinal))
            .Select(x => x.Value).OrderByDescending(x => x.UpdatedUtc).Take(30).ToArray();

    public bool Remove(string clientId, string downloadId) =>
        _downloads.TryRemove(Key(clientId, downloadId), out _);
}
