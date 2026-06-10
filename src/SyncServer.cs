using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace FFXIVTv;

/// <summary>
/// Lightweight WebSocket server for FFXIV-TV host mode.
/// Uses TcpListener + manual HTTP upgrade — no urlacl or admin required.
/// Broadcasts play/pause/resume/stop/seek control messages to all connected clients.
/// Also serves the currently-playing local file over HTTP at GET /file (same port).
///
/// Two start modes:
///   Start(port)            — direct TCP, requires firewall/port-forward for internet clients.
///   StartWithTunnel(dir, port) — direct TCP + cloudflared tunnel; no config needed for any client.
/// </summary>
public sealed class SyncServer : IDisposable
{
    private TcpListener?             _listener;
    private CancellationTokenSource? _cts;
    private readonly List<WebSocket> _clients = new();
    private readonly object          _lock    = new();
    private Timer?                   _heartbeat;
    private int                      _serverPort = 9834;
    private CloudflaredHelper?       _cloudflared;

    public bool   IsRunning   => _listener != null;
    public string LastError   { get; private set; } = string.Empty;
    public int    ClientCount { get { lock (_lock) return _clients.Count; } }

    // UPnP state — updated on background thread, read by UI thread.
    public string UPnPStatus { get; private set; } = string.Empty;
    public string PublicIp   { get; private set; } = string.Empty;

    // cloudflared tunnel URL — non-null once the tunnel is ready.
    public string? TunnelUrl    => _cloudflared?.TunnelUrl;
    public string? TunnelStatus => _cloudflared?.Status;
    public bool    TunnelMode   => _cloudflared != null;

    // Latest screen config JSON — sent to each new client on connect.
    private string? _latestScreenJson;

    /// <summary>Path of the local file currently being served via HTTP at GET /file.</summary>
    public string? ServedFilePath { get; set; }

    /// <summary>Callback that returns current playback state as JSON. Called on new client connect.</summary>
    public Func<string?>? GetStateJson { get; set; }

    private UPnPHelper.GatewayInfo? _upnpGateway;
    private int                     _upnpMappedPort = -1;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Start in direct TCP mode. Clients must be able to reach this machine on <paramref name="port"/>
    /// (same LAN, or UPnP/manual port forward for internet). Windows Firewall rule may be needed.
    /// </summary>
    public void Start(int port)
    {
        Stop();
        LastError = string.Empty;
        StartListener(port);
        if (IsRunning)
        {
            UPnPStatus = "Mapping...";
            PublicIp   = string.Empty;
            _ = Task.Run(() => SetupUPnPAsync(port));
        }
    }

    /// <summary>
    /// Start in tunnel mode: local TCP server + cloudflared quick tunnel.
    /// The tunnel creates an outbound HTTPS/WSS connection through Cloudflare's network,
    /// giving clients a public URL to connect to — no firewall rules or port forwarding needed.
    /// cloudflared.exe is downloaded once to the plugin directory on first use (~30 MB).
    /// </summary>
    public void StartWithTunnel(string pluginDir, int port)
    {
        Stop();
        LastError = string.Empty;
        StartListener(port);
        if (!IsRunning) return;

        _cloudflared = new CloudflaredHelper(pluginDir);
        _ = Task.Run(async () => await _cloudflared.StartAsync(port));
    }

    private void StartListener(int port)
    {
        try
        {
            _serverPort = port;
            _cts        = new CancellationTokenSource();
            _listener   = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _ = Task.Run(() => AcceptLoop(_cts.Token));
            _heartbeat  = new Timer(_ => Heartbeat(), null, 20_000, 20_000);
            Plugin.Log.Info($"[FFXIV-TV] SyncServer started on port {port}");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _listener = null;
            Plugin.Log.Error($"[FFXIV-TV] SyncServer failed to start: {ex.Message}");
        }
    }

    public void Stop()
    {
        _cloudflared?.Stop();
        _cloudflared?.Dispose();
        _cloudflared = null;

        _heartbeat?.Dispose();
        _heartbeat = null;
        _cts?.Cancel();
        _cts = null;

        try { _listener?.Stop(); } catch { }
        _listener = null;

        if (_upnpGateway != null && _upnpMappedPort >= 0)
        {
            var gw   = _upnpGateway;
            var prt  = _upnpMappedPort;
            _ = Task.Run(async () =>
            {
                await UPnPHelper.DeletePortMappingAsync(gw, prt);
                Plugin.Log.Info($"[FFXIV-TV] UPnP: removed port mapping for {prt}");
            });
            _upnpGateway    = null;
            _upnpMappedPort = -1;
        }
        UPnPStatus = string.Empty;
        PublicIp   = string.Empty;

        lock (_lock)
        {
            foreach (var ws in _clients) try { ws.Abort(); } catch { }
            _clients.Clear();
        }

        Plugin.Log.Info("[FFXIV-TV] SyncServer stopped");
    }

    // ── Broadcast ─────────────────────────────────────────────────────────────

    public void BroadcastPlay(string url, float position) =>
        Broadcast(new { type = "play", url, position });
    public void BroadcastPause()  => Broadcast(new { type = "pause"  });
    public void BroadcastResume() => Broadcast(new { type = "resume" });
    public void BroadcastStop()   => Broadcast(new { type = "stop"   });
    public void BroadcastSeek(float position) => Broadcast(new { type = "seek", position });

    public void BroadcastScreenConfig(ScreenDefinition screen)
    {
        var msg = new {
            type   = "screen",
            cx     = screen.Center.X,
            cy     = screen.Center.Y,
            cz     = screen.Center.Z,
            yaw    = screen.YawDegrees,
            width  = screen.Width,
            height = screen.Height,
        };
        _latestScreenJson = JsonConvert.SerializeObject(msg);
        Broadcast(msg);
    }

    private void Broadcast(object msg)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(msg));
        List<WebSocket> snapshot;
        lock (_lock) snapshot = new(_clients);
        foreach (var ws in snapshot)
        {
            if (ws.State != WebSocketState.Open) continue;
            try { _ = ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
            catch { }
        }
    }

    // ── File URL helper ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns the URL clients should use to stream the currently-served local file.
    /// Prefers the cloudflared tunnel URL (works over internet), then UPnP public IP,
    /// then LAN IP.
    /// </summary>
    public string BuildFileUrl()
    {
        if (TunnelUrl != null)
        {
            // Tunnel URL is wss://xxx.trycloudflare.com — serve file over http.
            string httpBase = TunnelUrl.Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase);
            return $"{httpBase}/file";
        }
        string host = !string.IsNullOrEmpty(PublicIp) ? PublicIp : (UPnPHelper.GetLocalIp() ?? "127.0.0.1");
        return $"http://{host}:{_serverPort}/file";
    }

    // ── Heartbeat ─────────────────────────────────────────────────────────────

    private void Heartbeat()
    {
        if (!IsRunning) return;
        Broadcast(new { type = "ping" });
        List<WebSocket>? dead = null;
        lock (_lock)
        {
            foreach (var ws in _clients)
                if (ws.State != WebSocketState.Open) { dead ??= new List<WebSocket>(); dead.Add(ws); }
            if (dead != null)
                foreach (var ws in dead) { _clients.Remove(ws); ws.Dispose(); }
        }
        if (dead?.Count > 0)
            Plugin.Log.Info($"[FFXIV-TV] SyncServer: pruned {dead.Count} disconnected client(s)");
    }

    // ── UPnP ──────────────────────────────────────────────────────────────────

    private async Task SetupUPnPAsync(int port)
    {
        try
        {
            var publicIpTask = UPnPHelper.GetPublicIpAsync();
            var gatewayTask  = UPnPHelper.DiscoverAsync();
            await Task.WhenAll(publicIpTask, gatewayTask);

            if (publicIpTask.Result != null) PublicIp = publicIpTask.Result;
            var gateway = gatewayTask.Result;

            if (gateway == null)
            {
                UPnPStatus = "No UPnP router found";
                Plugin.Log.Warning("[FFXIV-TV] UPnP: no gateway found");
                return;
            }

            string? localIp = UPnPHelper.GetLocalIp();
            if (localIp == null) { UPnPStatus = "Could not determine local IP"; return; }

            bool ok = await UPnPHelper.AddPortMappingAsync(gateway, port, port, localIp);
            if (ok)
            {
                _upnpGateway    = gateway;
                _upnpMappedPort = port;
                UPnPStatus      = $"UPnP mapped TCP {port} ✓";
                Plugin.Log.Info($"[FFXIV-TV] UPnP: mapped TCP {port} → {localIp}:{port}");
            }
            else
            {
                UPnPStatus = "UPnP mapping failed";
                Plugin.Log.Warning("[FFXIV-TV] UPnP: AddPortMapping returned failure");
            }
        }
        catch (Exception ex)
        {
            UPnPStatus = "UPnP error";
            Plugin.Log.Warning($"[FFXIV-TV] UPnP: {ex.Message}");
        }
    }

    // ── Accept loop ───────────────────────────────────────────────────────────

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var tcp = await _listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(tcp, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (IsRunning)
            {
                Plugin.Log.Warning($"[FFXIV-TV] SyncServer accept: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
    {
        try
        {
            Plugin.Log.Info($"[FFXIV-TV] SyncServer: TCP accepted from {tcp.Client.RemoteEndPoint}");
            var stream = tcp.GetStream();
            var (headers, wsKey) = await ReadHttpHeadersAsync(stream);

            if (wsKey != null)
            {
                var ws = await CompleteWsHandshakeAsync(stream, wsKey);
                await HandleWebSocketClientAsync(ws, tcp, ct);
            }
            else if (headers.Contains("GET /file ") || headers.Contains("GET /file\r"))
            {
                string? filePath = ServedFilePath;
                if (filePath != null && File.Exists(filePath))
                    await ServeFileAsync(stream, headers, filePath, ct);
                else
                    await SendHttpResponseAsync(stream, 404, "Not Found", "No file is currently being served.");
                tcp.Dispose();
            }
            else
            {
                tcp.Dispose();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[FFXIV-TV] SyncServer HandleClient: {ex.GetType().Name}: {ex.Message}");
            tcp.Dispose();
        }
    }

    private async Task HandleWebSocketClientAsync(WebSocket ws, TcpClient tcp, CancellationToken ct)
    {
        try
        {
            lock (_lock) _clients.Add(ws);
            Plugin.Log.Info($"[FFXIV-TV] Sync client connected: {tcp.Client.RemoteEndPoint}");

            if (_latestScreenJson != null)
                await ws.SendAsync(Encoding.UTF8.GetBytes(_latestScreenJson), WebSocketMessageType.Text, true, CancellationToken.None);

            string? stateJson = GetStateJson?.Invoke();
            if (stateJson != null)
                await ws.SendAsync(Encoding.UTF8.GetBytes(stateJson), WebSocketMessageType.Text, true, CancellationToken.None);

            var buf = new byte[256];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buf, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] SyncServer client error: {ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            lock (_lock) _clients.Remove(ws);
            ws.Dispose();
            tcp.Dispose();
            Plugin.Log.Info("[FFXIV-TV] Sync client disconnected");
        }
    }

    // ── HTTP file server ──────────────────────────────────────────────────────

    private static async Task ServeFileAsync(NetworkStream stream, string headers, string filePath, CancellationToken ct)
    {
        try
        {
            long fileLen = new FileInfo(filePath).Length;
            string ext  = Path.GetExtension(filePath).ToLowerInvariant();
            string mime = ext switch {
                ".mp4"  => "video/mp4",
                ".mkv"  => "video/x-matroska",
                ".webm" => "video/webm",
                ".avi"  => "video/x-msvideo",
                ".mov"  => "video/quicktime",
                ".flv"  => "video/x-flv",
                ".mp3"  => "audio/mpeg",
                ".ogg"  => "audio/ogg",
                ".wav"  => "audio/wav",
                _       => "application/octet-stream",
            };

            long start = 0, end = fileLen - 1;
            bool isRange = false;
            foreach (var line in headers.Split('\n'))
            {
                if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                {
                    var rangeStr = line.Substring(line.IndexOf('=') + 1).Trim().TrimEnd('\r');
                    var parts    = rangeStr.Split('-');
                    if (parts.Length == 2)
                    {
                        if (long.TryParse(parts[0], out long s)) start = s;
                        if (long.TryParse(parts[1], out long e)) end   = e;
                        isRange = true;
                    }
                    break;
                }
            }

            long length = end - start + 1;
            var hdr = isRange
                ? Encoding.UTF8.GetBytes(
                    $"HTTP/1.1 206 Partial Content\r\nContent-Type: {mime}\r\nContent-Length: {length}\r\n" +
                    $"Content-Range: bytes {start}-{end}/{fileLen}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n\r\n")
                : Encoding.UTF8.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: {mime}\r\nContent-Length: {length}\r\n" +
                    $"Accept-Ranges: bytes\r\nConnection: close\r\n\r\n");

            await stream.WriteAsync(hdr, ct);

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            fs.Seek(start, SeekOrigin.Begin);
            var buf       = new byte[65536];
            long remaining = length;
            while (remaining > 0 && !ct.IsCancellationRequested)
            {
                int toRead = (int)Math.Min(buf.Length, remaining);
                int read   = await fs.ReadAsync(buf.AsMemory(0, toRead), ct);
                if (read == 0) break;
                await stream.WriteAsync(buf.AsMemory(0, read), ct);
                remaining -= read;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] SyncServer ServeFile: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static async Task SendHttpResponseAsync(NetworkStream stream, int code, string status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 {code} {status}\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
        await stream.WriteAsync(bytes);
    }

    // ── WebSocket handshake ───────────────────────────────────────────────────

    private static async Task<(string headers, string? wsKey)> ReadHttpHeadersAsync(NetworkStream stream)
    {
        var sb = new StringBuilder(512);
        int b0 = 0, b1 = 0, b2 = 0, b3 = 0;
        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0) return (sb.ToString(), null);
            sb.Append((char)b);
            b0 = b1; b1 = b2; b2 = b3; b3 = b;
            if (b0 == '\r' && b1 == '\n' && b2 == '\r' && b3 == '\n') break;
            if (sb.Length > 8192) return (sb.ToString(), null);
        }

        string headers = sb.ToString();
        string? wsKey  = null;
        foreach (var line in headers.Split('\n'))
        {
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            {
                wsKey = line.Substring(line.IndexOf(':') + 1).Trim().TrimEnd('\r');
                break;
            }
        }
        return (headers, wsKey);
    }

    private static async Task<WebSocket> CompleteWsHandshakeAsync(NetworkStream stream, string wsKey)
    {
        string accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.UTF8.GetBytes(wsKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.UTF8.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n"));
        return WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null,
            keepAliveInterval: TimeSpan.FromSeconds(20));
    }

    public void Dispose() => Stop();
}
