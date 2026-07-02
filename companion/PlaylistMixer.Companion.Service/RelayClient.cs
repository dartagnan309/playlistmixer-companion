using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.SignalR.Client;

namespace PlaylistMixer.Companion.Service;

/// <summary>
/// Holds the persistent reverse connection to the central server and executes relayed upstream
/// requests from THIS machine's IP. Server sends ExecuteRelay(requestId, method, url, headers); we fetch
/// the provider and stream the response back as RelayHead → RelayChunk* → RelayEnd (or RelayError).
/// Auto-reconnects; rebuilds the connection when pairing changes.
/// </summary>
public sealed class RelayClient : IHostedService, IAsyncDisposable
{
    private const int ChunkSize = 64 * 1024;

    private readonly PairingStore _pairing;
    private readonly IHttpClientFactory _httpFactory;
    private readonly RecordingManager _recordings;
    private readonly ILogger<RelayClient> _log;
    private HubConnection? _conn;
    private readonly object _gate = new();

    public RelayClient(PairingStore pairing, IHttpClientFactory httpFactory, RecordingManager recordings, ILogger<RelayClient> log)
    {
        _pairing = pairing;
        _httpFactory = httpFactory;
        _recordings = recordings;
        _log = log;
    }

    public bool Connected => _conn?.State == HubConnectionState.Connected;

    public Task StartAsync(CancellationToken ct) { _ = ConnectIfPairedAsync(); return Task.CompletedTask; }

    public async Task StopAsync(CancellationToken ct)
    {
        HubConnection? conn;
        lock (_gate) { conn = _conn; _conn = null; }
        if (conn is not null) await conn.DisposeAsync();
    }

    /// <summary>Called by the /pair endpoint after the store is updated, to (re)establish the connection.</summary>
    public async Task ReconnectAsync()
    {
        HubConnection? old;
        lock (_gate) { old = _conn; _conn = null; }
        if (old is not null) { try { await old.DisposeAsync(); } catch { } }
        await ConnectIfPairedAsync();
    }

    private async Task ConnectIfPairedAsync()
    {
        var p = _pairing.Current;
        if (!p.Paired) { _log.LogInformation("Companion not paired; relay idle."); return; }

        var hubUrl = $"{p.ServerUrl.TrimEnd('/')}/hubs/companion-relay" +
                     $"?access_token={Uri.EscapeDataString(p.Token)}&machine={Uri.EscapeDataString(Environment.MachineName)}";

        var conn = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .AddMessagePackProtocol()
            .WithAutomaticReconnect()
            .Build();

        conn.On<string, string, string, Dictionary<string, string[]>>("ExecuteRelay", ExecuteRelayAsync);
        conn.On<string>("AbortRelay", _ => { /* best-effort: an in-flight fetch ends when the server stops reading */ });

        // Centralized recordings: the server pulls this Companion's list and routes delete/stop here
        // (SignalR client results). Reuses RecordingManager — no new recording logic.
        conn.On("ListRecordingsJson", () =>
            System.Text.Json.JsonSerializer.Serialize(_recordings.List(), RecordingJson.Options));
        conn.On<string, bool, bool>("RemoveRecording", (id, deleteFile) =>
            deleteFile ? _recordings.Delete(id) : _recordings.Stop(id));

        lock (_gate) _conn = conn;
        try
        {
            await conn.StartAsync();
            _log.LogInformation("Companion relay connected to {Server}.", p.ServerUrl);
        }
        catch (Exception ex)
        {
            // WithAutomaticReconnect only retries an established connection; the FIRST connect must be
            // retried by us. Simple backoff loop.
            _log.LogWarning(ex, "Relay initial connect failed; retrying in 10s.");
            await Task.Delay(TimeSpan.FromSeconds(10));
            if (_pairing.Current.Paired) await ConnectIfPairedAsync();
        }
    }

    private async Task ExecuteRelayAsync(string requestId, string method, string url, Dictionary<string, string[]> headers)
    {
        var conn = _conn;
        if (conn is null) return;
        try
        {
            if (!IsRelayableUrl(url))
                throw new InvalidOperationException("URL is not a public http(s) address.");

            using var req = new HttpRequestMessage(new HttpMethod(method), url);
            foreach (var (k, v) in headers)
                req.Headers.TryAddWithoutValidation(k, v);

            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromMinutes(5);

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

            var respHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in resp.Headers) respHeaders[h.Key] = h.Value.ToArray();
            foreach (var h in resp.Content.Headers) respHeaders[h.Key] = h.Value.ToArray();

            await conn.SendAsync("RelayHead", requestId, (int)resp.StatusCode, respHeaders);

            await using var stream = await resp.Content.ReadAsStreamAsync();
            var buffer = new byte[ChunkSize];
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                var chunk = read == buffer.Length ? buffer : buffer[..read];
                await conn.SendAsync("RelayChunk", requestId, chunk);
            }
            await conn.SendAsync("RelayEnd", requestId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Relay request {Id} failed.", requestId);
            try { await conn.SendAsync("RelayError", requestId, ex.Message); } catch { }
        }
    }

    /// <summary>SSRF guard: only http(s), and never a loopback/private/link-local host, so a misbehaving
    /// server can't use the Companion to probe the home LAN.</summary>
    internal static bool IsRelayableUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        // If the host is a literal IP, reject private ranges. Hostnames are allowed (provider FQDNs);
        // resolved-IP filtering is intentionally out of scope for v1.
        if (IPAddress.TryParse(uri.Host, out var ip))
        {
            if (IPAddress.IsLoopback(ip)) return false;
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                if (b[0] == 10) return false;                              // 10.0.0.0/8
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false; // 172.16.0.0/12
                if (b[0] == 192 && b[1] == 168) return false;              // 192.168.0.0/16
                if (b[0] == 169 && b[1] == 254) return false;              // 169.254.0.0/16 link-local
            }
            if (ip.AddressFamily == AddressFamily.InterNetworkV6 && (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal)) return false;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        HubConnection? conn;
        lock (_gate) { conn = _conn; _conn = null; }
        if (conn is not null) await conn.DisposeAsync();
    }
}
