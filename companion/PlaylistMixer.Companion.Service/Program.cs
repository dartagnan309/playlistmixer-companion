using System.Net;
using System.Net.Sockets;
using System.Reflection;
using PlaylistMixer.Companion.Service;
using PlaylistMixer.Companion.Service.Platform;
using PlaylistMixer.Playback;

// PlaylistMixer Companion — a tiny loopback HTTP service that takes over stream proxying + FFmpeg
// remux for browser playback on this PC, so the central PlaylistMixer server spends no bandwidth or
// CPU on this user's playback. The SPA detects it via GET /health and points hls.js / <video> at it.
// Runs as a Windows service (install-and-forget); see the tray app for start/stop control.

// Candidate loopback ports, probed in order. 36400 is verified clear of popular apps (Plex 32400,
// Jellyfin 8096, qBittorrent 8080, Stremio 11470/12470, Plexio 7777) and below the Windows
// ephemeral range (49152+). The SPA probes this same list to discover us.
int[] candidatePorts = [36400, 36401, 36402];
var port = FirstFreeLoopbackPort(candidatePorts)
           ?? throw new InvalidOperationException(
               $"None of the candidate ports ({string.Join(", ", candidatePorts)}) are free on loopback.");

var builder = WebApplication.CreateBuilder(args);

// OS seam: Windows (LocalSystem service) vs macOS (per-user LaunchAgent). Everything platform-specific
// — state paths, secret protection, FFmpeg location, host lifecycle, player launch — lives behind this.
var platform = PlatformInfo.Create();
builder.Services.AddSingleton(platform);
platform.ConfigureHost(builder.Host); // UseWindowsService on Windows; no-op elsewhere

builder.Services.AddHttpClient();
builder.Services.AddSingleton<ActivityTracker>();
builder.Services.AddSingleton<ExternalPlayerStore>();
// Reverse-channel relay: persistent connection out to the central server so server-initiated upstream
// calls (sync/EPG/account) run from THIS machine's IP. Idle until paired by the SPA.
builder.Services.AddSingleton<PairingStore>();
builder.Services.AddSingleton<RelayClient>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RelayClient>());

// Bind loopback only — never expose this on the network.
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));

var loopbackBase = $"http://127.0.0.1:{port}";

// FFmpeg ships next to the executable in the installed layout (ffmpeg.exe on Windows, ffmpeg on
// macOS/Unix); override via Ffmpeg:Path for dev.
var configuredFfmpeg = builder.Configuration["Ffmpeg:Path"];
var ffmpegPath = string.IsNullOrWhiteSpace(configuredFfmpeg)
    ? platform.DefaultFfmpegPath
    : configuredFfmpeg;

// DVR (pause/rewind) capture — records live streams into a rolling on-disk HLS window. Registered as a
// singleton so it owns FFmpeg processes for their lifetime; disposed on shutdown (kills them, deletes dirs).
var dvrOptions = builder.Configuration.GetSection("Dvr").Get<DvrOptions>() ?? new DvrOptions();
builder.Services.AddSingleton(sp =>
    new DvrManager(dvrOptions, ffmpegPath, loopbackBase, sp.GetRequiredService<ILogger<DvrManager>>()));

// Recordings (persistent DVR). Singleton so it owns FFmpeg processes + the schedule; instantiated
// eagerly below so its scheduler runs without waiting for the first request.
builder.Services.AddSingleton(sp =>
    new RecordingManager(dvrOptions, ffmpegPath, platform, sp.GetRequiredService<ILogger<RecordingManager>>()));

var app = builder.Build();
var log = app.Logger;

// Force-create the recording manager now so startup reconciliation + the scheduler timer start
// (DI singletons are otherwise lazy until first resolved by a request).
var recordingManager = app.Services.GetRequiredService<RecordingManager>();

// Clear any DVR captures left over from a previous (possibly crashed) run before we start serving.
try
{
    var dvrRoot = dvrOptions.ResolveRootDir();
    if (Directory.Exists(dvrRoot)) Directory.Delete(dvrRoot, recursive: true);
}
catch (Exception ex) { log.LogWarning(ex, "Could not clear stale DVR directory on startup."); }

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

// CORS + Private Network Access. A public HTTPS SPA calling this loopback service triggers a PNA
// preflight (Chromium) that requires `Access-Control-Allow-Private-Network: true`. We reflect the
// request Origin and short-circuit OPTIONS here; the GET handlers also set Allow-Origin themselves.
app.Use(async (ctx, next) =>
{
    var headers = ctx.Response.Headers;
    var origin = ctx.Request.Headers.Origin.ToString();
    headers["Access-Control-Allow-Origin"] = string.IsNullOrEmpty(origin) ? "*" : origin;
    headers["Vary"] = "Origin";
    headers["Access-Control-Allow-Private-Network"] = "true";
    headers["Access-Control-Allow-Methods"] = "GET, POST, DELETE, OPTIONS";
    headers["Access-Control-Allow-Headers"] = "Range, Content-Type";
    headers["Access-Control-Expose-Headers"] = "Content-Range, Accept-Ranges, Content-Length";

    if (HttpMethods.IsOptions(ctx.Request.Method))
    {
        ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        return;
    }
    await next();
});

// Discovery probe: the SPA hits this to learn the companion is present and on which port; the tray
// reads `streaming` to show "Streaming" while playback is active.
app.MapGet("/health", (ActivityTracker activity, DvrManager dvr, RecordingManager rec, ExternalPlayerStore players, PairingStore pairing, RelayClient relay) => Results.Json(new
{
    name = "PlaylistMixer Companion",
    version,
    port,
    streaming = activity.IsStreaming,
    // Reverse-channel relay: whether this companion is paired to a server and the connection is live.
    paired = pairing.Current.Paired,
    relayConnected = relay.Connected,
    // Advertises pause/rewind support + the window length so the SPA knows to route live playback
    // through /dvr and how far back the user can seek.
    dvr = dvr.Enabled,
    dvrWindowSeconds = dvr.WindowSeconds,
    // Persistent recording capability + live counts (always present; SPA defaults them when absent).
    recording = true,
    recordingsActive = rec.ActiveCount,
    recordingsScheduled = rec.ScheduledCount,
    // The resolved recordings folder, so the user-session tray can open it in Explorer.
    recordingsDir = rec.Dir,
    // External-player launch capability + the chosen player's display name (for the SPA's button label).
    externalPlayer = new { configured = players.Current.Configured, name = players.Current.Name },
}));

// External-player settings: the tray reads the current choice and writes the user's selection here.
app.MapGet("/settings/external-player", (ExternalPlayerStore store) =>
{
    var s = store.Current;
    return Results.Json(new { enabled = s.Enabled, name = s.Name, path = s.Path });
});

app.MapPost("/settings/external-player", async (HttpContext ctx, ExternalPlayerStore store) =>
{
    ExternalPlayerSettings? body;
    try { body = await ctx.Request.ReadFromJsonAsync<ExternalPlayerSettings>(); }
    catch { return Results.BadRequest(new { error = "Invalid body." }); }
    if (body is null) return Results.BadRequest(new { error = "Body required." });

    var path = (body.Path ?? "").Trim();
    if (body.Enabled && !ExternalPlayerSettings.PathExists(path))
        return Results.BadRequest(new { error = "Player path does not exist." });

    try { store.Save(new ExternalPlayerSettings(body.Enabled, (body.Name ?? "").Trim(), path)); }
    catch { return Results.Json(new { error = "Could not save settings." }, statusCode: 500); }
    return Results.NoContent();
});

// Launch the configured external player with the RAW upstream URL (no proxy/remux).
app.MapPost("/play/external", (string? url, ExternalPlayerStore store) =>
{
    if (string.IsNullOrWhiteSpace(url)) return Results.BadRequest(new { error = "url required." });
    var s = store.Current;
    if (!s.Configured) return Results.Json(new { error = "No external player configured." }, statusCode: 409);
    try
    {
        platform.LaunchPlayer(s.Path, url);
        return Results.NoContent();
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "External-player launch failed.");
        return Results.Json(new { error = ex.Message }, statusCode: 409);
    }
});

// ── Pairing (reverse-channel relay) ──
// The SPA, which is logged in to the server AND can reach this loopback service, brokers pairing: it
// fetches a token from the server then POSTs it here. We persist it and (re)open the reverse connection.
app.MapGet("/pair", (PairingStore store, RelayClient relay) =>
{
    var s = store.Current;
    return Results.Json(new { paired = s.Paired, serverUrl = s.ServerUrl, connected = relay.Connected });
});

app.MapPost("/pair", async (HttpContext ctx, PairingStore store, RelayClient relay) =>
{
    PairBody? body;
    try { body = await ctx.Request.ReadFromJsonAsync<PairBody>(); }
    catch { return Results.BadRequest(new { error = "Invalid body." }); }
    if (body is null || string.IsNullOrWhiteSpace(body.Token) || string.IsNullOrWhiteSpace(body.ServerUrl))
        return Results.BadRequest(new { error = "token and serverUrl required." });

    store.Save(new PairingSettings(body.Token!, body.ServerUrl!));
    await relay.ReconnectAsync();
    return Results.NoContent();
});

app.MapDelete("/pair", (PairingStore store, RelayClient relay) =>
{
    store.Clear();
    _ = relay.ReconnectAsync(); // tears down the live connection (now unpaired → idle)
    return Results.NoContent();
});

// Proxy + HLS-manifest rewrite. Segment URLs loop back through THIS service.
app.MapGet("/proxy/stream", async (string? url, IHttpClientFactory http, ActivityTracker activity, HttpContext ctx, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(url)) { ctx.Response.StatusCode = 400; return; }
    using var _ = activity.BeginRequest();
    var proxyBase = $"{loopbackBase}/proxy/stream?url=";
    await StreamProxy.StreamAsync(http.CreateClient(), ctx, url, proxyBase, ct);
});

app.MapGet("/proxy/image", async (string? url, IHttpClientFactory http, HttpContext ctx, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(url)) { ctx.Response.StatusCode = 400; return; }
    await StreamProxy.ImageAsync(http.CreateClient(), ctx, url, ct);
});

// Raw MPEG-TS → fragmented MP4 via bundled FFmpeg.
app.MapGet("/remux", async (string? url, ActivityTracker activity, HttpContext ctx, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(url)) { ctx.Response.StatusCode = 400; return; }
    using var _ = activity.BeginRequest();
    await StreamRemuxer.RemuxAsync(ctx, url, ffmpegPath, log, ct);
});

// DVR (pause/rewind): record the live stream into a rolling on-disk HLS window and serve it locally.
// hls.js loads this manifest and re-polls it for the latest window; it can seek back across the whole
// retained window, so pause/rewind work natively. Returns the FFmpeg-written playlist verbatim — its
// segment URLs already point back at /dvr/{id}/ (FFmpeg -hls_base_url).
app.MapGet("/dvr/stream.m3u8", async (string? url, DvrManager dvr, ActivityTracker activity, HttpContext ctx, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(url)) { ctx.Response.StatusCode = 400; return; }
    if (!dvr.Enabled) { ctx.Response.StatusCode = 404; return; }
    using var _ = activity.BeginRequest();

    var session = await dvr.GetOrStartAsync(url, ct);
    if (session is null) { ctx.Response.StatusCode = 502; return; }

    ctx.Response.ContentType = "application/vnd.apple.mpegurl";
    ctx.Response.Headers["Cache-Control"] = "no-cache, no-store";
    try { await ctx.Response.SendFileAsync(session.Writer.PlaylistPath, ct); }
    catch (OperationCanceledException) { /* client went away */ }
    catch (FileNotFoundException) { ctx.Response.StatusCode = 404; }
});

// Serves a DVR session's segment files (and its manifest, if referenced that way). The session id and
// file name are validated to keep this confined to the capture directory.
app.MapGet("/dvr/{sessionId}/{file}", async (string sessionId, string file, DvrManager dvr, ActivityTracker activity, HttpContext ctx, CancellationToken ct) =>
{
    using var _ = activity.BeginRequest();
    var session = dvr.Find(sessionId);
    if (session is null) { ctx.Response.StatusCode = 404; return; }

    // Whitelist: only "index.m3u8" or "seg_NNNNNN.ts" — never a path that could escape the dir.
    if (file is not "index.m3u8" && !System.Text.RegularExpressions.Regex.IsMatch(file, @"^seg_\d{1,9}\.ts$"))
    {
        ctx.Response.StatusCode = 400; return;
    }

    var path = Path.Combine(session.Dir, file);
    if (!File.Exists(path)) { ctx.Response.StatusCode = 404; return; }

    ctx.Response.ContentType = file.EndsWith(".m3u8") ? "application/vnd.apple.mpegurl" : "video/mp2t";
    ctx.Response.Headers["Cache-Control"] = file.EndsWith(".m3u8") ? "no-cache, no-store" : "public, max-age=60";
    try { await ctx.Response.SendFileAsync(path, ct); }
    catch (OperationCanceledException) { /* client went away */ }
    catch (FileNotFoundException) { ctx.Response.StatusCode = 404; }
});

// ── Recordings (persistent DVR) ──────────────────────────────────────────────────────────────
var idRe = new System.Text.RegularExpressions.Regex("^[0-9a-fA-F]{32}$");

app.MapGet("/recordings", (RecordingManager rm) => Results.Json(rm.List(), RecordingJson.Options));

app.MapPost("/recordings", async (HttpContext ctx, RecordingManager rm) =>
{
    RecordingSnapshot? snap;
    try { snap = await ctx.Request.ReadFromJsonAsync<RecordingSnapshot>(RecordingJson.Options); }
    catch { return Results.BadRequest(new { error = "Invalid body." }); }
    if (snap is null || string.IsNullOrWhiteSpace(snap.StreamUrl)) return Results.BadRequest(new { error = "streamUrl required." });

    var (rec, conflict) = rm.Create(snap);
    return rec is null
        ? Results.Json(new { reason = conflict }, RecordingJson.Options, statusCode: StatusCodes.Status409Conflict)
        : Results.Json(rec, RecordingJson.Options);
});

app.MapDelete("/recordings/{id}", (string id, bool? deleteFile, RecordingManager rm) =>
{
    if (!idRe.IsMatch(id)) return Results.BadRequest();
    var ok = deleteFile == true ? rm.Delete(id) : rm.Stop(id);
    return ok ? Results.NoContent() : Results.NotFound();
});

app.MapGet("/recordings/{id}/file", (string id, RecordingManager rm) =>
{
    if (!idRe.IsMatch(id)) return Results.BadRequest();
    var rec = rm.Get(id);
    if (rec is null) return Results.NotFound();
    // Root the stored path: Results.File requires an absolute path, and legacy entries may hold a
    // relative one (older builds under LocalSystem couldn't resolve a Videos folder).
    var path = Path.GetFullPath(rec.FilePath);
    if (!File.Exists(path)) return Results.NotFound();
    // Range-enabled; for a still-growing recording the length is taken at request time and the browser
    // re-requests as playback advances (fragmented MP4 is progressively readable).
    return Results.File(path, "video/mp4", enableRangeProcessing: true);
});

log.LogInformation("PlaylistMixer Companion v{Version} listening on {Base} (ffmpeg: {Ffmpeg})",
    version, loopbackBase, ffmpegPath);

app.Run();

// Returns the first candidate port with no listener bound on loopback, or null if all are taken.
static int? FirstFreeLoopbackPort(int[] candidates)
{
    foreach (var p in candidates)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, p);
            listener.Start();
            listener.Stop();
            return p;
        }
        catch (SocketException) { /* in use — try next */ }
    }
    return null;
}

record PairBody(string? Token, string? ServerUrl);
