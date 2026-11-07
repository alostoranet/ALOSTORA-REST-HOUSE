using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Alostora.RestHouse;

public sealed record DownloadSpeedLimitSettings(int LimitMbps = 0);

public sealed class DownloadSpeedLimitService
{
    private readonly string _path;
    private readonly object _settingsGate = new();
    private readonly object _scheduleGate = new();
    private int _limitMbps;
    private long _nextAvailableTimestamp;

    public DownloadSpeedLimitService(string applicationRoot)
    {
        Directory.CreateDirectory(applicationRoot);
        _path = Path.Combine(applicationRoot, "download-speed-settings.json");
        try
        {
            if (File.Exists(_path))
            {
                var settings = JsonSerializer.Deserialize<DownloadSpeedLimitSettings>(File.ReadAllText(_path));
                _limitMbps = settings?.LimitMbps is >= 0 and <= 1000 ? settings.LimitMbps : 0;
            }
        }
        catch
        {
            _limitMbps = 0;
        }
    }

    public DownloadSpeedLimitSettings GetSettings() => new(Volatile.Read(ref _limitMbps));

    public DownloadSpeedLimitSettings Save(DownloadSpeedLimitSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.LimitMbps is < 0 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(settings), "حد السرعة يجب أن يكون بين 0 و1000 ميجابت/ثانية.");

        lock (_settingsGate)
        {
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, overwrite: true);
            Interlocked.Exchange(ref _limitMbps, settings.LimitMbps);
            lock (_scheduleGate) _nextAvailableTimestamp = Stopwatch.GetTimestamp();
            return settings;
        }
    }

    // One shared reservation schedule limits aggregate download traffic across all clients.
    // Video and audio playback endpoints do not use this limiter.
    public async Task WaitAsync(int byteCount, CancellationToken cancellationToken)
    {
        if (byteCount <= 0) return;
        var limitMbps = Volatile.Read(ref _limitMbps);
        if (limitMbps <= 0) return;

        long delayTicks;
        lock (_scheduleGate)
        {
            var now = Stopwatch.GetTimestamp();
            var scheduledStart = Math.Max(now, _nextAvailableTimestamp);
            delayTicks = scheduledStart - now;
            var seconds = byteCount / (limitMbps * 125000d);
            var durationTicks = Math.Max(1L, (long)Math.Ceiling(seconds * Stopwatch.Frequency));
            _nextAvailableTimestamp = scheduledStart + durationTicks;
        }

        if (delayTicks > 0)
        {
            var delay = TimeSpan.FromSeconds(delayTicks / (double)Stopwatch.Frequency);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }
}
