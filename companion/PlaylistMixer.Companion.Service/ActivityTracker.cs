namespace PlaylistMixer.Companion.Service;

/// <summary>
/// Tracks whether the companion is actively serving a stream, so the tray can show "Streaming".
/// A single remux is one long-lived request (caught by the in-flight count); HLS playback is many
/// short segment fetches with gaps between them (caught by the recent-activity window).
/// </summary>
public sealed class ActivityTracker
{
    private static readonly TimeSpan RecentWindow = TimeSpan.FromSeconds(12);

    private int _inFlight;
    private long _lastActivityTicks;

    /// <summary>Marks a request in-flight for the lifetime of the returned scope.</summary>
    public IDisposable BeginRequest()
    {
        Interlocked.Increment(ref _inFlight);
        Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
        return new Scope(this);
    }

    public bool IsStreaming
    {
        get
        {
            if (Volatile.Read(ref _inFlight) > 0) return true;
            var last = new DateTime(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);
            return DateTime.UtcNow - last < RecentWindow;
        }
    }

    private sealed class Scope(ActivityTracker owner) : IDisposable
    {
        public void Dispose()
        {
            Interlocked.Decrement(ref owner._inFlight);
            Interlocked.Exchange(ref owner._lastActivityTicks, DateTime.UtcNow.Ticks);
        }
    }
}
