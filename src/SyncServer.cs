using System;
using System.Collections.Generic;
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
/// </summary>
public sealed class SyncServer : IDisposable
{
    private TcpListener?        _listener;
    private CancellationTokenSource? _cts;
    private readonly List<WebSocket> _clients = new();
    private readonly object     _lock = new();

    public bool   IsRunning   => _listener != null;
    public string LastError   { get; private set; } = string.Empty;
    public int    ClientCount { get { lock (_lock) return _clients.Count; } }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Start(int port)
    {
        Stop();
        LastError = string.Empty;
        try
        {
            _cts      = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _ = Task.Run(() => AcceptLoop(_cts.Token));
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
        _cts?.Cancel();
        _cts = null;

        try { _listener?.Stop(); } catch { }
        _listener = null;

        lock (_lock)
        {
            foreach (var ws in _clients)
                try { ws.Abort(); } catch { }
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

    public void BroadcastSeek(float position) =>
        Broadcast(new { type = "seek", position });

    private void Broadcast(object msg)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(msg));
        List<WebSocket> snapshot;
        lock (_lock) snapshot = new(_clients);

        foreach (var ws in snapshot)
        {
            if (ws.State != WebSocketState.Open) continue;
            // Fire-and-forget. Concurrent-send exceptions are caught here
            // (InvalidOperationException thrown synchronously if a send is in-flight).
            try { _ = ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
            catch { /* drop — client will get next message */ }
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
        WebSocket? ws = null;
        try
        {
            ws = await HandshakeAsync(tcp.GetStream());
            if (ws == null) { tcp.Dispose(); return; }

            lock (_lock) _clients.Add(ws);
            Plugin.Log.Info($"[FFXIV-TV] Sync client connected: {tcp.Client.RemoteEndPoint}");

            // Keep-alive receive loop — reads and discards; breaks on Close or disconnect.
            var buf = new byte[256];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buf, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
                    catch { }
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Plugin.Log.Debug($"[FFXIV-TV] Sync client error: {ex.Message}"); }
        finally
        {
            if (ws != null)
            {
                lock (_lock) _clients.Remove(ws);
                ws.Dispose();
            }
            tcp.Dispose();
            Plugin.Log.Info("[FFXIV-TV] Sync client disconnected");
        }
    }

    // ── WebSocket handshake ───────────────────────────────────────────────────

    /// <summary>
    /// Reads HTTP headers byte-by-byte (safe — cursor ends exactly at first WebSocket frame),
    /// sends the 101 upgrade response, then wraps the stream as a server-side WebSocket.
    /// </summary>
    private static async Task<WebSocket?> HandshakeAsync(NetworkStream stream)
    {
        // Read until \r\n\r\n, accumulate into a string builder.
        var sb = new StringBuilder(512);
        int b0 = 0, b1 = 0, b2 = 0, b3 = 0;
        while (true)
        {
            int b = stream.ReadByte();  // sync read; fine for one-time handshake
            if (b < 0) return null;
            sb.Append((char)b);
            b0 = b1; b1 = b2; b2 = b3; b3 = b;
            if (b0 == '\r' && b1 == '\n' && b2 == '\r' && b3 == '\n') break;
            if (sb.Length > 8192) return null;  // malformed / too large
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
        if (wsKey == null) return null;

        // RFC 6455 accept-key computation
        string accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.UTF8.GetBytes(wsKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

        byte[] response = Encoding.UTF8.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Connection: Upgrade\r\n"              +
            "Upgrade: websocket\r\n"               +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n");

        await stream.WriteAsync(response);

        return WebSocket.CreateFromStream(
            stream, isServer: true, subProtocol: null,
            keepAliveInterval: TimeSpan.FromSeconds(20));
    }

    public void Dispose() => Stop();
}
