using System;
using System.Threading.Tasks;
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

    // ── Constructor ───────────────────────────────────────────────────────────

    public SyncCoordinator(VideoPlayer vp)
    {
        _vp = vp;

        // Wire incoming client messages → VideoPlayer. These do NOT re-broadcast
        // (we're a viewer receiving commands, not a host issuing them).
        Client.OnPlay   += OnClientPlay;
        Client.OnPause  += OnClientPause;
        Client.OnResume += OnClientResume;
        Client.OnStop   += () => _vp.Stop();
        Client.OnSeek   += pos => _vp.Seek(pos);
    }

    // ── Client event handlers ─────────────────────────────────────────────────

    private void OnClientPlay(string url, float position)
    {
        _vp.Play(url);
        // VideoPlayer.Play is async — give it a moment to start before seeking.
        if (position > 0.01f)
            Task.Delay(800).ContinueWith(_ => _vp.Seek(position));
    }

    private void OnClientPause()  { if (_vp.IsPlaying) _vp.TogglePause(); }
    private void OnClientResume() { if (_vp.IsPaused)  _vp.TogglePause(); }

    // ── Host-side control methods ─────────────────────────────────────────────

    /// <summary>
    /// Play a URL or local file path.
    /// Broadcasts to clients only if in Host mode and the input is a URL.
    /// Local file paths are never broadcast (clients can't fetch a file from the host's disk).
    /// </summary>
    public void Play(string pathOrUrl)
    {
        _vp.Play(pathOrUrl);

        if (Mode == NetworkMode.Host && IsUrl(pathOrUrl))
        {
            // Resolve via yt-dlp on the host before broadcasting so clients receive
            // a direct stream URL and don't need yt-dlp themselves.
            _ = Task.Run(async () =>
            {
                string broadcastUrl = await _vp.ResolveForBroadcastAsync(pathOrUrl);
                Server.BroadcastPlay(broadcastUrl, 0f);
            });
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
        if (Mode == NetworkMode.Host) Server.BroadcastStop();
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsUrl(string s) =>
        s.StartsWith("http://",  StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        Client.OnPlay   -= OnClientPlay;
        Client.OnPause  -= OnClientPause;
        Client.OnResume -= OnClientResume;
        Server.Dispose();
        Client.Dispose();
    }
}
