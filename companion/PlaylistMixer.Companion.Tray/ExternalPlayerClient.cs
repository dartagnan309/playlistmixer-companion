using System.Net.Http.Json;

namespace PlaylistMixer.Companion.Tray;

/// <summary>Reads/writes the service's external-player settings over loopback (same port range as /health).</summary>
public static class ExternalPlayerClient
{
    private static readonly int[] Ports = [36400, 36401, 36402];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    public sealed record Settings(bool Enabled, string Name, string Path);

    public static async Task<(int port, Settings settings)?> GetAsync()
    {
        foreach (var port in Ports)
        {
            try
            {
                var s = await Http.GetFromJsonAsync<Settings>($"http://127.0.0.1:{port}/settings/external-player");
                if (s is not null) return (port, s);
            }
            catch { /* not this port — try next */ }
        }
        return null;
    }

    public static async Task SaveAsync(int port, Settings settings)
    {
        var res = await Http.PostAsJsonAsync($"http://127.0.0.1:{port}/settings/external-player", settings);
        res.EnsureSuccessStatusCode();
    }
}
