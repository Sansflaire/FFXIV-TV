using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace FFXIVTv;

/// <summary>
/// WebSocket client for FFXIV-TV viewer mode.
/// Connects to a SyncServer, receives control messages, and fires events
/// that SyncCoordinator translates into VideoPlayer calls.
/// Reconnects automatically with exponential backoff on disconnect.
/// </summary>
public sealed class SyncClient : IDisposable
{
    private CancellationTokenSource? _cts;
    private bool _running;

    public bool   IsConnected { get; private set; }
    public string Status      { get; private set; } = "Disconnected";

    // ── Events (fired on background thread) ──────────────────────────────────
    public event Action<string, float>?                        OnPlay;         // url, position
    public event Action?                                       OnPause;
    public event Action?                                       OnResume;
    public event Action?                                       OnStop;
    public event Action<float>?                                OnSeek;         // position 0–1
    public event Action<float, float, float, float, float, float>? OnScreenConfig; // cx,cy,cz,yaw,w,h

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Connect(string address)
    {
        Disconnect();
        _running = true;
        _cts     = new CancellationTokenSource();
        _ = Task.Run(() => ConnectLoop(address, _cts.Token));
    }

    public void Disconnect()
    {
        _running     = false;
        IsConnected  = false;
        Status       = "Disconnected";
        _cts?.Cancel();
        _cts = null;
    }

    // ── Connection loop with auto-reconnect ───────────────────────────────────

    private async Task ConnectLoop(string address, CancellationToken ct)
    {
        // Normalise to ws:// URI
        string uri = address.StartsWith("ws://",  StringComparison.OrdinalIgnoreCase) ||
                     address.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)
            ? address
            : $"ws://{address}/";

        int delayMs = 2000;
        while (_running && !ct.IsCancellationRequested)
        {
            try
            {
                Status = "Connecting...";
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(uri), ct);

                IsConnected = true;
                Status      = "Connected";
                delayMs     = 2000;  // reset backoff on success
                Plugin.Log.Info($"[FFXIV-TV] SyncClient connected to {uri}");

                await ReceiveLoop(ws, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                IsConnected = false;
                // Show the actual failure reason so the user can diagnose it
                // (connection refused = wrong IP/port or firewall; timeout = firewall dropping)
                Status = $"Failed: {ex.Message}";
                Plugin.Log.Warning($"[FFXIV-TV] SyncClient: {ex.Message}");
            }

            if (!_running) break;

            IsConnected = false;
            int delaySec = delayMs / 1000;
            Status = $"Reconnecting in {delaySec}s...";
            try { await Task.Delay(delayMs, ct); } catch (OperationCanceledException) { break; }
            delayMs = Math.Min(delayMs * 2, 30_000);
        }

        IsConnected = false;
        // Only write "Disconnected" if this loop is the one that was explicitly stopped,
        // not if Connect() cancelled us in order to start a new loop.
        if (!_running)
            Status = "Disconnected";
    }

    private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[8192];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try { result = await ws.ReceiveAsync(buf, ct); }
            catch (OperationCanceledException) { break; }

            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.MessageType != WebSocketMessageType.Text)  continue;

            try
            {
                var msg  = JObject.Parse(Encoding.UTF8.GetString(buf, 0, result.Count));
                string? type = msg["type"]?.Value<string>();
                switch (type)
                {
                    case "play":
                        OnPlay?.Invoke(
                            msg["url"]?.Value<string>() ?? "",
                            msg["position"]?.Value<float>() ?? 0f);
                        break;
                    case "pause":  OnPause?.Invoke();                                     break;
                    case "resume": OnResume?.Invoke();                                    break;
                    case "stop":   OnStop?.Invoke();                                      break;
                    case "seek":   OnSeek?.Invoke(msg["position"]?.Value<float>() ?? 0f); break;
                    case "screen":
                        OnScreenConfig?.Invoke(
                            msg["cx"]?.Value<float>() ?? 0f,
                            msg["cy"]?.Value<float>() ?? 0f,
                            msg["cz"]?.Value<float>() ?? 0f,
                            msg["yaw"]?.Value<float>() ?? 0f,
                            msg["width"]?.Value<float>()  ?? 4f,
                            msg["height"]?.Value<float>() ?? 2.25f);
                        break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[FFXIV-TV] SyncClient parse error: {ex.Message}");
            }
        }
    }

    public void Dispose() => Disconnect();
}
