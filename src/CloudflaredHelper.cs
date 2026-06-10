using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FFXIVTv;

/// <summary>
/// Manages a cloudflared quick-tunnel subprocess.
/// When started, cloudflared creates an outbound HTTPS tunnel to a local port and
/// returns a public wss:// URL that anyone on the internet can connect to — no
/// port forwarding or firewall rules required on the host's machine.
///
/// The binary is downloaded once to the plugin directory (~30 MB) and reused.
/// Quick tunnels are free, require no Cloudflare account, and work immediately.
/// </summary>
public sealed class CloudflaredHelper : IDisposable
{
    private const string DownloadUrl =
        "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";
    private const string ExeName = "cloudflared.exe";

    private readonly string _pluginDir;
    private Process?        _process;

    public string  Status    { get; private set; } = string.Empty;
    public string? TunnelUrl { get; private set; }  // wss://xxx.trycloudflare.com
    public bool    IsReady   => TunnelUrl != null;
    public bool    IsRunning => _process is { HasExited: false };

    /// <summary>Fired (on background thread) when the tunnel URL becomes available.</summary>
    public event Action<string>? OnReady;

    public string ExePath => Path.Combine(_pluginDir, ExeName);

    public CloudflaredHelper(string pluginDir) => _pluginDir = pluginDir;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public async Task StartAsync(int port, CancellationToken ct = default)
    {
        TunnelUrl = null;

        if (!File.Exists(ExePath))
        {
            Status = "Downloading cloudflared (~30 MB)...";
            Plugin.Log.Info("[FFXIV-TV] cloudflared.exe not found — downloading");
            try
            {
                using var http = new HttpClient();
                http.Timeout   = TimeSpan.FromMinutes(3);
                var bytes      = await http.GetByteArrayAsync(DownloadUrl, ct);
                await File.WriteAllBytesAsync(ExePath, bytes, ct);
                Plugin.Log.Info($"[FFXIV-TV] cloudflared downloaded ({bytes.Length / 1024 / 1024} MB)");
            }
            catch (Exception ex)
            {
                Status = $"Download failed: {ex.Message}";
                Plugin.Log.Error($"[FFXIV-TV] cloudflared download failed: {ex.Message}");
                return;
            }
        }

        Status = "Starting tunnel...";

        try
        {
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = ExePath,
                    Arguments              = $"tunnel --url http://localhost:{port} --no-autoupdate",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                },
                EnableRaisingEvents = true,
            };

            _process.OutputDataReceived += (_, e) => ParseLine(e.Data);
            _process.ErrorDataReceived  += (_, e) => ParseLine(e.Data);
            _process.Exited             += (_, _) =>
            {
                if (TunnelUrl != null)
                {
                    TunnelUrl = null;
                    Status    = "Tunnel closed";
                    Plugin.Log.Warning("[FFXIV-TV] cloudflared exited unexpectedly");
                }
            };

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            Plugin.Log.Info($"[FFXIV-TV] cloudflared started (PID {_process.Id}), waiting for tunnel URL...");
        }
        catch (Exception ex)
        {
            Status = $"Failed to start: {ex.Message}";
            Plugin.Log.Error($"[FFXIV-TV] cloudflared start failed: {ex.Message}");
        }
    }

    public void Stop()
    {
        TunnelUrl = null;
        Status    = string.Empty;
        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); }
            catch { }
            Plugin.Log.Info("[FFXIV-TV] cloudflared stopped");
        }
        _process?.Dispose();
        _process = null;
    }

    // ── URL parsing ───────────────────────────────────────────────────────────

    private static readonly Regex UrlRegex = new(
        @"https://[\w-]+\.trycloudflare\.com",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private void ParseLine(string? line)
    {
        if (line == null || TunnelUrl != null) return;

        var match = UrlRegex.Match(line);
        if (!match.Success) return;

        // cloudflared gives https:// — convert to wss:// for our WebSocket client.
        TunnelUrl = match.Value.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase);
        Status    = "Ready";
        Plugin.Log.Info($"[FFXIV-TV] Tunnel ready: {TunnelUrl}");
        OnReady?.Invoke(TunnelUrl);
    }

    public void Dispose() => Stop();
}
