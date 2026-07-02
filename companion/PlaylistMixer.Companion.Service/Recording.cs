using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlaylistMixer.Companion.Service;

public enum RecordingStatus { Scheduled, Recording, Completed, Failed }

/// <summary>
/// One recording, persisted as a JSON sidecar (<c>&lt;id&gt;.json</c>) beside its media
/// (<c>&lt;id&gt;.mp4</c>). Timestamps are the original program window in UTC, before padding;
/// the scheduler applies padding when it computes the FFmpeg <c>-t</c> duration.
/// </summary>
public sealed class Recording
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public RecordingStatus Status { get; set; } = RecordingStatus.Scheduled;
    public string Title { get; set; } = "";
    public string ChannelName { get; set; } = "";
    public string? ChannelLogo { get; set; }
    public string PlaylistId { get; set; } = "";
    public string PlaylistName { get; set; } = "";
    public int MaxConnections { get; set; } = 1;
    public string StreamUrl { get; set; } = "";
    public DateTime StartUtc { get; set; }
    public DateTime? StopUtc { get; set; }
    public int PrePadSec { get; set; }
    public int PostPadSec { get; set; }
    /// <summary>True for an immediate start (record-now / current program) — pre-pad is not applied.</summary>
    public bool Immediate { get; set; }
    public string FilePath { get; set; } = "";
    public long FileBytes { get; set; }
    /// <summary>Actual recorded media length in whole seconds (from FFmpeg), set when the recording
    /// finishes or is stopped. 0 until then (and for entries reconciled after a companion restart).</summary>
    public int DurationSeconds { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string? ErrorReason { get; set; }
}

/// <summary>The POST /recordings body — a self-contained snapshot built by the SPA.</summary>
public sealed class RecordingSnapshot
{
    public string Title { get; set; } = "";
    public string ChannelName { get; set; } = "";
    public string? ChannelLogo { get; set; }
    public string PlaylistId { get; set; } = "";
    public string PlaylistName { get; set; } = "";
    public int MaxConnections { get; set; } = 1;
    public string StreamUrl { get; set; } = "";
    public DateTime StartUtc { get; set; }
    public DateTime? StopUtc { get; set; }
    public int? PrePadSec { get; set; }
    public int? PostPadSec { get; set; }
    public bool Immediate { get; set; }
    /// <summary>Bypass the connection-limit check (the user's explicit "Record Anyway").</summary>
    public bool Force { get; set; }
}

/// <summary>Shared serializer: camelCase props, lowercase status string, ISO-8601 UTC dates.</summary>
public static class RecordingJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
