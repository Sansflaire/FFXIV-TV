using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Vortice.Direct3D11;

namespace FFXIVTv;

/// <summary>
/// Sits between the UI / slash commands and VideoPlayer.
/// In Host mode: executes video commands locally AND broadcasts them to sync clients.
/// In Client mode: local play controls are ignored; playback is driven by the server.
/// In Off mode: delegates directly to VideoPlayer with no networking.
///
/// D3DRenderer keeps its own VideoPlayer reference for frame upload / SRV access.
/// </summary>
public sealed class SyncCoordinator : IDisposable
{
    private readonly VideoPlayer _vp;

    public readonly SyncServer Server = new();
    public readonly SyncClient Client = new();

    public NetworkMode Mode { get; set; } = NetworkMode.Off;

    // ── Pass-through VideoPlayer properties ───────────────────────────────────
    public int    Volume       { get => _vp.Volume; set => _vp.Volume = value; }
    public bool   Muted        { get => _vp.Muted;  set => _vp.Muted  = value; }
    public string VideoStatus  => _vp.Status;
    public bool   IsPlaying    => _vp.IsPlaying;
    public bool   IsPaused     => _vp.IsPaused;
    public float  Position     => _vp.Position;
    public long   TimeMs       => _vp.TimeMs;
    public long   LengthMs     => _vp.LengthMs;
    public float  LoopA        => _vp.LoopA;
    public float  LoopB        => _vp.LoopB;
    public bool   AbLoopActive => _vp.AbLoopActive;

    public string YtDlpPath
    {
        get => _vp.YtDlpPath;
        set => _vp.YtDlpPath = value;
    }

    // ── Playlist state (synced from Config each frame by Plugin.cs) ───────────
    public List<string>? Playlist      { get; set; }
    public int           PlaylistIndex { get; set; } = -1;
    public bool          PlaylistLoop  { get; set; } = true;

    /// <summary>Fired (on background thread) when the playlist advances. Arg = new index.</summary>
    public event Action<int>? OnPlaylistAdvanced;

    // ── Sync state cache ──────────────────────────────────────────────────────

    // The URL that was last broadcast to clients (resolved, HTTP-served, or direct).
    // Used to build the "state" message for clients that connect mid-session.
    private string? _lastBroadcastUrl;

    // ── Constructor ───────────────────────────────────────────────────────────

    public SyncCoordinator(VideoPlayer vp)
    {
        _vp = vp;

        _vp.EndOfMedia += OnVideoEndOfMedia;

        // Wire incoming client messages → VideoPlayer. These do NOT re-broadcast
        // (we're a viewer receiving commands, not a host issuing them).
        Client.OnPlay        += OnClientPlay;
        Client.OnPause       += OnClientPause;
        Client.OnResume      += OnClientResume;
        Client.OnStop        += () => _vp.Stop();
        Client.OnSeek        += pos => _vp.Seek(pos);

        // Provide the server with a state snapshot for newly-connecting clients.
        Server.GetStateJson = GetCurrentStateJson;
    }

    // ── End-of-media / playlist advancement ──────────────────────────────────

    private void OnVideoEndOfMedia() => HandleEndOfMedia();

    private void HandleEndOfMedia()
    {
        // Clients are driven by the host — don't auto-advance locally.
        if (Mode == NetworkMode.Client) return;

        var items = Playlist;
        if (items == null || items.Count == 0)
        {
            string url = _vp.CurrentPath;
            if (!string.IsNullOrEmpty(url))
            {
                if (_vp.FramesDecoded == 0)
                {
                    Plugin.Log.Warning($"[FFXIV-TV] Stream ended with 0 frames decoded — stopping instead of looping: {url}");
                    _vp.Stop();
                    return;
                }
                Play(url);
            }
            return;
        }

        int next = PlaylistIndex + 1;
        if (next >= items.Count)
        {
            if (PlaylistLoop) next = 0;
            else              { _vp.Stop(); return; }
        }

        PlaylistIndex = next;
        Play(items[next]);
        OnPlaylistAdvanced?.Invoke(next);
    }

    // ── Client event handlers ─────────────────────────────────────────────────

    private void OnClientPlay(string url, float position)
    {
        _vp.Play(url);
        if (position > 0.01f)
            Task.Delay(800).ContinueWith(_ => _vp.Seek(position));
    }

    private void OnClientPause()  { if (_vp.IsPlaying) _vp.TogglePause(); }
    private void OnClientResume() { if (_vp.IsPaused)  _vp.TogglePause(); }

    // ── Host-side control methods ─────────────────────────────────────────────

    /// <summary>
    /// Play a URL or local file path.
    /// In Host mode:
    ///   - URLs: resolved via yt-dlp on the host, then broadcast (clients don't need yt-dlp).
    ///   - Local files: served over HTTP from the host's server, broadcast as an http:// URL.
    /// </summary>
    public void Play(string pathOrUrl)
    {
        _vp.Play(pathOrUrl);

        if (Mode == NetworkMode.Host)
        {
            if (IsUrl(pathOrUrl))
            {
                // Resolve via yt-dlp before broadcasting so clients receive a direct stream URL.
                _ = Task.Run(async () =>
                {
                    string broadcastUrl = await _vp.ResolveForBroadcastAsync(pathOrUrl);
                    _lastBroadcastUrl   = broadcastUrl;
                    Server.BroadcastPlay(broadcastUrl, 0f);
                });
            }
            else if (Server.IsRunning)
            {
                // Local file: serve via HTTP from the host's own server port.
                // Build the URL clients will use to stream the file.
                Server.ServedFilePath = pathOrUrl;
                string fileUrl        = Server.BuildFileUrl();
                _lastBroadcastUrl     = fileUrl;
                Server.BroadcastPlay(fileUrl, 0f);
                Plugin.Log.Info($"[FFXIV-TV] Serving local file to clients at {fileUrl}");
            }
        }
    }

    public void TogglePause()
    {
        bool wasPaused = _vp.IsPaused;
        _vp.TogglePause();

        if (Mode == NetworkMode.Host)
        {
            if (wasPaused) Server.BroadcastResume();
            else           Server.BroadcastPause();
        }
    }

    public void Stop()
    {
        _vp.Stop();
        if (Mode == NetworkMode.Host)
        {
            Server.BroadcastStop();
            Server.ServedFilePath = null;
            _lastBroadcastUrl     = null;
        }
    }

    public void Seek(float position)
    {
        _vp.Seek(position);
        if (Mode == NetworkMode.Host) Server.BroadcastSeek(position);
    }

    // ── A-B loop pass-throughs (host-only controls, no sync needed) ───────────
    public void SetLoopA()     => _vp.SetLoopA();
    public void SetLoopB()     => _vp.SetLoopB();
    public void ToggleAbLoop() => _vp.ToggleAbLoop();
    public void ClearAbLoop()  => _vp.ClearAbLoop();

    // ── State snapshot for new clients ────────────────────────────────────────

    /// <summary>
    /// Returns a JSON "state" message describing what the host is currently playing.
    /// Called by SyncServer when a new client connects so they immediately sync up.
    /// Returns null if nothing is playing or no broadcast URL is known.
    /// </summary>
    private string? GetCurrentStateJson()
    {
        if (_lastBroadcastUrl == null) return null;
        if (!_vp.IsPlaying && !_vp.IsPaused) return null;

        var state = new
        {
            type     = "state",
            url      = _lastBroadcastUrl,
            position = _vp.Position,
            isPaused = _vp.IsPaused,
        };
        return JsonConvert.SerializeObject(state);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsUrl(string s) =>
        s.StartsWith("http://",  StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        _vp.EndOfMedia  -= OnVideoEndOfMedia;
        Client.OnPlay   -= OnClientPlay;
        Client.OnPause  -= OnClientPause;
        Client.OnResume -= OnClientResume;
        Server.Dispose();
        Client.Dispose();
    }
}
