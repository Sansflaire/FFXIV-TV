using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using LibVLCSharp.Shared;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace FFXIVTv;

/// <summary>
/// Decodes a local video file or network stream with LibVLC and provides decoded BGRA
/// frames as a D3D11 dynamic texture SRV for D3DRenderer to bind.
///
/// Threading model:
///   LibVLC decode thread → LockCallback provides pinned pixel buffer → UnlockCallback signals done
///                        → DisplayCallback marks _frameDirty = true
///   Render thread        → UploadFrame: if dirty and not being written, Map/memcpy/Unmap dynamic tex
///   Background tasks     → PlayFileAsync / PlayNetworkAsync; abort if _playVersion changed
///
/// Race condition guards:
///   _playVersion  — incremented on every Play() call. Async tasks capture it at start and
///                   abort before touching shared state if the version has since changed.
///                   Prevents stale tasks from calling StartPlayback() after a newer Play().
///   _vlcWriting   — set true by LockCallback, false by UnlockCallback. Stop() spin-waits
///                   until false after _player.Stop() rather than forcing it, so UploadFrame
///                   never reads a buffer that LibVLC is still writing to.
/// </summary>
public sealed class VideoPlayer : IDisposable
{
    // ── LibVLC ────────────────────────────────────────────────────────────────
    // Initialized lazily on first Play() call — keeps ~200 native VLC plugin DLLs
    // out of the process until video is actually used.
    private LibVLC?      _libVlc;
    private MediaPlayer? _player;
    private Media?       _media;

    // ── Pixel buffer (LibVLC writes decoded BGRA frames here) ─────────────────
    private byte[]?  _pixels;
    private GCHandle _pixelsHandle;
    private int      _pixelWidth;
    private int      _pixelHeight;

    private volatile bool _frameDirty;
    private volatile bool _vlcWriting;

    // ── Play versioning (stale-task guard) ────────────────────────────────────
    // Incremented atomically on every Play() call. Async tasks capture the version
    // at the point they are launched and abort before StartPlayback() if it changed.
    private volatile int _playVersion = 0;

    // ── yt-dlp pipe process (for video-only DASH streams, e.g. Reddit) ────────
    // When yt-dlp detects acodec=none (video-only), we pipe yt-dlp's merged
    // output to LibVLC via StreamMediaInput instead of passing a raw stream URL.
    private Process? _ytdlpProc;

    // ── Loop-transition guard ─────────────────────────────────────────────────
    // Set true in Play() before Stop() so HasTexture stays true during Stop→restart.
    // Set false when the first decoded frame of the new play arrives (DisplayCallback),
    // and also when Stop() is called externally so the gradient correctly appears.
    private volatile bool _transitioning = false;

    // ── Frame decode counter ──────────────────────────────────────────────────
    // Reset to 0 on each StartPlayback(); incremented by DisplayCallback().
    // SyncCoordinator checks this at EndReached: if 0, the stream was unplayable
    // and the loop should not retry (prevents infinite Resolving→Playing→Resolving spam).
    private int _framesDecoded = 0;
    public int FramesDecoded => _framesDecoded;

    // ── D3D11 resources ───────────────────────────────────────────────────────
    private ID3D11Device?             _device;
    private ID3D11Texture2D?          _dynTex;
    private ID3D11ShaderResourceView? _srv;
    private int _texWidth;
    private int _texHeight;

    private volatile bool _needsNewTexture;
    private int _pendingTexW;
    private int _pendingTexH;

    // ── Delegate fields (prevent GC collection) ───────────────────────────────
    private readonly MediaPlayer.LibVLCVideoLockCb    _lockCb;
    private readonly MediaPlayer.LibVLCVideoUnlockCb  _unlockCb;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;

    // ── Pending Volume/Mute (stored before VLC is initialized) ───────────────
    private int  _pendingVolume = 100;
    private bool _pendingMuted  = false;

    // ── Config ────────────────────────────────────────────────────────────────
    private readonly string _pluginDir;

    /// <summary>
    /// Optional path to yt-dlp.exe. If empty, auto-discovers from plugin dir then system PATH.
    /// </summary>
    public string YtDlpPath { get; set; } = string.Empty;

    // ── Status ────────────────────────────────────────────────────────────────
    private string _status = "Stopped";
    public string Status => _status;

    /// <summary>The path or URL most recently passed to Play(). Empty when stopped.</summary>
    public string CurrentPath { get; private set; } = string.Empty;

    /// <summary>
    /// Fired (on a background thread) when the current media reaches its end.
    /// Subscriber is responsible for deciding whether to loop, advance playlist, etc.
    /// </summary>
    public event Action? EndOfMedia;

    // ── Public API ────────────────────────────────────────────────────────────
    public ID3D11ShaderResourceView? FrameSrv => _srv;
    // True when there is a valid frame to display. Stays true during Stop→loop restarts
    // (_transitioning) so the last decoded frame holds on screen with no gradient flash.
    public bool HasTexture => _srv != null && _player != null && (_player.State != VLCState.Stopped || _transitioning);
    public bool IsPlaying  => _player?.IsPlaying ?? false;
    public bool IsPaused   => _player != null && _player.State == VLCState.Paused;

    /// <summary>Playback position as a fraction 0–1. Returns 0 when stopped.</summary>
    // _player is non-null when IsPlaying or IsPaused (both check _player != null first)
    public float Position => (IsPlaying || IsPaused) ? Math.Clamp(_player!.Position, 0f, 1f) : 0f;

    /// <summary>Current playback time in milliseconds. -1 when unknown.</summary>
    public long TimeMs => _player?.Time ?? -1;

    /// <summary>Total duration in milliseconds. -1 for live streams or unknown.</summary>
    public long LengthMs => _player?.Length ?? -1;

    /// <summary>Seek to a position in the media (0 = start, 1 = end).</summary>
    public void Seek(float position) { if (_player != null) _player.Position = Math.Clamp(position, 0f, 1f); }

    /// <summary>Playback volume 0–100. Applied immediately to the LibVLC player, or stored for when it initializes.</summary>
    public int Volume
    {
        get => _player?.Volume ?? _pendingVolume;
        set { _pendingVolume = Math.Clamp(value, 0, 100); if (_player != null) _player.Volume = _pendingVolume; }
    }

    /// <summary>Mute/unmute audio without affecting the stored volume level.</summary>
    public bool Muted
    {
        get => _player?.Mute ?? _pendingMuted;
        set { _pendingMuted = value; if (_player != null) _player.Mute = value; }
    }

    // ── A-B loop ──────────────────────────────────────────────────────────────
    private float _loopA       = 0f;
    private float _loopB       = 1f;
    private bool  _abLoopActive = false;

    public float LoopA        => _loopA;
    public float LoopB        => _loopB;
    public bool  AbLoopActive => _abLoopActive;

    public void SetLoopA()     => _loopA = _player?.Position ?? 0f;
    public void SetLoopB()     => _loopB = _player?.Position ?? 1f;
    public void ToggleAbLoop() => _abLoopActive = !_abLoopActive;
    public void ClearAbLoop()  { _loopA = 0f; _loopB = 1f; _abLoopActive = false; }

    // ── Constructor ───────────────────────────────────────────────────────────
    /// <param name="pluginDir">Directory containing libvlc.dll (= devPlugins/FFXIV-TV/).</param>
    public VideoPlayer(string pluginDir)
    {
        _pluginDir = pluginDir;

        // LibVLC is NOT initialized here — deferred to first Play() call.
        // This prevents ~200 native VLC plugin DLLs from loading at plugin startup.

        _lockCb    = LockCallback;
        _unlockCb  = UnlockCallback;
        _displayCb = DisplayCallback;
    }

    /// <summary>
    /// Initializes LibVLC on first use. Loads ~200 native VLC plugin DLLs into the process.
    /// Called exactly once, from Play(), before any MediaPlayer interaction.
    /// </summary>
    private void EnsureVlcInitialized()
    {
        if (_libVlc != null) return;

        Core.Initialize(_pluginDir);
        _libVlc = new LibVLC(enableDebugLogs: false);
        _player = new MediaPlayer(_libVlc);
        _player.Volume = _pendingVolume;
        _player.Mute   = _pendingMuted;
        _player.EndReached += OnEndReached;

        Plugin.Log.Info("[FFXIV-TV] VideoPlayer: LibVLC initialized (first Play call).");
    }

    /// <summary>Called once D3DRenderer is initialized. Must be called before Play().</summary>
    public void SetDevice(ID3D11Device device) => _device = device;

    // ── Playback ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Plays a local file path OR an HTTP/HTTPS URL (including YouTube).
    /// Cancels any in-flight Play() task before starting the new one.
    /// </summary>
    public void Play(string pathOrUrl)
    {
        // Initialize LibVLC on first use (loads native VLC DLLs into the process).
        EnsureVlcInitialized();

        // If paused, resume rather than restart.
        if (IsPaused)
        {
            _player!.Play();
            _status = "Playing";
            return;
        }

        // Set transitioning BEFORE Stop() so HasTexture stays true during Stop→restart.
        // The last decoded frame holds on screen instead of flashing the idle gradient.
        _transitioning = true;
        int version = Interlocked.Increment(ref _playVersion);
        Stop();
        CurrentPath = pathOrUrl;
        _status = "Loading...";

        bool isUrl = pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        if (isUrl)
            Task.Run(() => PlayNetworkAsync(pathOrUrl, version));
        else
            Task.Run(() => PlayFileAsync(pathOrUrl, version));
    }

    public void TogglePause()
    {
        if (_player == null) return;
        if (_player.IsPlaying)
        {
            _player.Pause();
            _status = "Paused";
        }
        else
        {
            _player.Play();
            _status = "Playing";
        }
    }

    public void Stop()
    {
        // Kill any yt-dlp pipe process first so its stdout closes before LibVLC stops.
        var pipeProc = Interlocked.Exchange(ref _ytdlpProc, null);
        try { pipeProc?.Kill(entireProcessTree: true); pipeProc?.Dispose(); } catch { }

        if (_player == null)
        {
            _frameDirty    = false;
            _transitioning = false;
            _status        = "Stopped";
            return;
        }

        _player.Stop();

        // _player.Stop() is synchronous and waits for LibVLC to finish its decode loop,
        // but the current LockCallback invocation may still be in-flight on the decode
        // thread. Spin-wait until _vlcWriting clears so we never free the pinned pixel
        // buffer while LibVLC holds a pointer to it.
        var sw = Stopwatch.StartNew();
        while (_vlcWriting && sw.ElapsedMilliseconds < 500)
            Thread.SpinWait(100);

        // Do NOT forcefully set _vlcWriting = false here — it is owned by the LibVLC
        // callbacks. After Stop() + spin-wait it is already false.

        _media?.Dispose();
        _media         = null;
        _frameDirty    = false;
        _transitioning = false; // explicit Stop — gradient should appear
        _status        = "Stopped";
    }

    // ── Render-thread upload ──────────────────────────────────────────────────

    public void UploadFrame(ID3D11DeviceContext ctx)
    {
        if (_needsNewTexture)
        {
            _needsNewTexture = false;
            EnsureTexture(_pendingTexW, _pendingTexH);
        }

        if (!_frameDirty || _vlcWriting || _pixels == null || _dynTex == null) return;
        _frameDirty = false;

        try
        {
            ctx.Map(_dynTex, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None, out var mapped);
            int srcStride = _pixelWidth * 4;
            unsafe
            {
                byte* dst = (byte*)mapped.DataPointer;
                fixed (byte* src = _pixels)
                {
                    for (int y = 0; y < _pixelHeight; y++)
                        Buffer.MemoryCopy(
                            src + (long)y * srcStride,
                            dst + (long)y * (int)mapped.RowPitch,
                            srcStride, srcStride);
                }
            }
            ctx.Unmap(_dynTex, 0);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[FFXIV-TV] VideoPlayer.UploadFrame failed: {ex.Message}");
        }
    }

    // ── Internal playback helpers ─────────────────────────────────────────────

    private async Task PlayFileAsync(string path, int version)
    {
        try
        {
            if (!File.Exists(path))
            {
                _status = "File not found";
                Plugin.Log.Warning($"[FFXIV-TV] VideoPlayer: file not found: '{path}'");
                return;
            }

            var media = new Media(_libVlc!, new Uri(path));
            await media.Parse(MediaParseOptions.ParseLocal);

            // Abort if a newer Play() was called while we were parsing.
            if (_playVersion != version) { media.Dispose(); return; }

            uint vw = 1920, vh = 1080;
            foreach (var track in media.Tracks)
            {
                if (track.TrackType == TrackType.Video)
                {
                    if (track.Data.Video.Width  > 0) vw = track.Data.Video.Width;
                    if (track.Data.Video.Height > 0) vh = track.Data.Video.Height;
                    break;
                }
            }

            Plugin.Log.Info($"[FFXIV-TV] VideoPlayer: {vw}x{vh} — '{Path.GetFileName(path)}'");
            StartPlayback(media, vw, vh, version);
        }
        catch (Exception ex)
        {
            _status = $"Error: {ex.Message}";
            Plugin.Log.Error($"[FFXIV-TV] VideoPlayer.PlayFileAsync failed: {ex.Message}");
        }
    }

    // Extensions that LibVLC can open directly without yt-dlp extraction.
    private static readonly string[] DirectMediaExtensions =
        { ".mp4", ".mkv", ".webm", ".avi", ".mov", ".flv", ".m3u8", ".ts", ".wmv", ".ogg" };

    private static bool IsDirectMediaUrl(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            foreach (var ext in DirectMediaExtensions)
                if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { /* malformed URI — let yt-dlp handle it */ }
        return false;
    }

    private async Task PlayNetworkAsync(string url, int version)
    {
        try
        {
            string streamUrl = url;
            Dictionary<string, string>? headers = null;

            if (!IsDirectMediaUrl(url))
            {
                _status = "Resolving URL...";
                var resolved = await ResolveWithYtDlpAsync(url);

                // Abort if a newer Play() was called while yt-dlp was running.
                if (_playVersion != version) return;

                if (resolved.HasValue)
                {
                    Plugin.Log.Info($"[FFXIV-TV] VideoPlayer: yt-dlp resolved stream URL");

                    if (resolved.Value.VideoOnly)
                    {
                        // Video-only stream (e.g. Reddit DASH with separate audio track).
                        // LibVLC can't merge separate DASH A/V; pipe through yt-dlp instead.
                        if (_playVersion != version) return;
                        string? pipePath = FindYtDlp();
                        if (pipePath != null)
                        {
                            await PlayViaPipeAsync(url, pipePath, version);
                            return;
                        }
                        // yt-dlp disappeared — fall through and let LibVLC try the URL directly.
                        Plugin.Log.Warning("[FFXIV-TV] VideoPlayer: yt-dlp not found for pipe fallback — trying LibVLC directly (likely no audio).");
                    }

                    streamUrl = resolved.Value.Url;
                    headers   = resolved.Value.Headers;
                }
                else
                {
                    Plugin.Log.Warning("[FFXIV-TV] VideoPlayer: yt-dlp failed or not found — trying LibVLC directly.");
                }
            }

            if (_playVersion != version) return;

            _status = "Connecting...";
            Plugin.Log.Info($"[FFXIV-TV] VideoPlayer: opening network stream");
            var media = new Media(_libVlc!, new Uri(streamUrl));

            // Pass HTTP headers so CDN streams that require Referer/User-Agent/Origin succeed.
            if (headers != null)
            {
                if (headers.TryGetValue("Referer", out var referer) && !string.IsNullOrEmpty(referer))
                    media.AddOption($":http-referrer={referer}");
                if (headers.TryGetValue("User-Agent", out var ua) && !string.IsNullOrEmpty(ua))
                    media.AddOption($":http-user-agent={ua}");
                if (headers.TryGetValue("Origin", out var origin) && !string.IsNullOrEmpty(origin))
                    media.AddOption($":http-referrer={origin}"); // LibVLC uses referrer for origin too
            }

            // Generous network buffer — CDN streams can spike in latency.
            media.AddOption(":network-caching=5000");

            StartPlayback(media, 1920, 1080, version);
        }
        catch (Exception ex)
        {
            _status = $"Error: {ex.Message}";
            Plugin.Log.Error($"[FFXIV-TV] VideoPlayer.PlayNetworkAsync failed: {ex.Message}");
        }
    }

    private void StartPlayback(Media media, uint vw, uint vh, int version)
    {
        // Final version check immediately before touching shared state.
        // Guards against the window between the last async check and this call.
        if (_playVersion != version)
        {
            media.Dispose();
            return;
        }

        _framesDecoded   = 0;
        AllocatePixelBuffer((int)vw, (int)vh);
        _pendingTexW     = (int)vw;
        _pendingTexH     = (int)vh;
        _needsNewTexture = true;

        // _player is guaranteed non-null here: EnsureVlcInitialized() ran in Play() before
        // Task.Run, and all async tasks abort early if _playVersion changes.
        _player!.SetVideoFormat("BGRA", vw, vh, vw * 4);
        _player.SetVideoCallbacks(_lockCb, _unlockCb, _displayCb);

        _media        = media;
        _player.Media = media;
        _player.Play();
        _status = "Playing";
    }

    // ── yt-dlp integration ────────────────────────────────────────────────────

    private readonly struct ResolvedStream
    {
        public readonly string                      Url;
        public readonly Dictionary<string, string>? Headers;
        /// <summary>True when yt-dlp returned acodec=none (video-only, e.g. Reddit DASH).
        /// In this case PlayViaPipeAsync is used instead of passing the URL directly to LibVLC.</summary>
        public readonly bool                        VideoOnly;
        public ResolvedStream(string url, Dictionary<string, string>? headers, bool videoOnly = false)
        { Url = url; Headers = headers; VideoOnly = videoOnly; }
    }

    /// <summary>
    /// Resolves a URL for broadcasting to sync clients.
    /// Direct media URLs are returned as-is; YouTube/etc. are resolved via yt-dlp
    /// so clients receive a plain stream URL and don't need yt-dlp themselves.
    /// Returns the original URL if resolution fails.
    /// </summary>
    public async Task<string> ResolveForBroadcastAsync(string url)
    {
        if (IsDirectMediaUrl(url)) return url;
        var resolved = await ResolveWithYtDlpAsync(url);
        return resolved?.Url ?? url;
    }

    /// <summary>
    /// Uses yt-dlp -j (JSON dump) to extract both the stream URL and HTTP headers.
    /// Headers (Referer, User-Agent, Origin) are required by many CDN streams.
    /// Returns null if yt-dlp is not found or fails.
    /// </summary>
    private async Task<ResolvedStream?> ResolveWithYtDlpAsync(string url)
    {
        string? ytdlp = FindYtDlp();
        if (ytdlp == null)
        {
            Plugin.Log.Warning("[FFXIV-TV] yt-dlp not found. Drop yt-dlp.exe into the plugin folder or set YtDlpPath in settings.");
            _status = "Error: yt-dlp not found";
            return null;
        }

        try
        {
            // -j dumps JSON including url, manifest_url, and http_headers.
            // No -f flag: let yt-dlp pick the best available format per site.
            // "-f best" was broken for DASH-only sites (Reddit) — it selected a non-existent
            // pre-merged format and returned nothing. Without -f, yt-dlp picks correctly.
            var psi = new ProcessStartInfo(ytdlp, $"-j --no-playlist --js-runtimes node \"{url}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return null;

            // Read both stdout (JSON) and stderr (error messages) concurrently.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, proc.WaitForExitAsync());
            string json   = stdoutTask.Result;
            string stderr = stderrTask.IsCompleted ? stderrTask.Result : string.Empty;

            // Surface JS-runtime warning prominently — YouTube extraction requires deno/node.
            if (stderr.Contains("No supported JavaScript runtime"))
                Plugin.Log.Warning($"[FFXIV-TV] yt-dlp: YouTube requires a JS runtime (deno). Install deno and add it to PATH, or use Browser (WebView2) mode for YouTube. stderr: {stderr.Trim()}");

            if (string.IsNullOrWhiteSpace(json))
            {
                string errLine = stderr.Trim().Split('\n')[0].Trim();
                _status = string.IsNullOrEmpty(errLine) ? "Error: yt-dlp returned no output" : $"Error: {errLine}";
                Plugin.Log.Warning($"[FFXIV-TV] yt-dlp returned no JSON. stderr: {stderr.Trim()}");
                return null;
            }

            var obj = JObject.Parse(json);

            // Prefer manifest_url (DASH/HLS playlist) over url — it includes audio+video
            // for sites like Reddit that serve separate streams. LibVLC plays DASH natively.
            string? streamUrl = obj["manifest_url"]?.ToString();
            if (string.IsNullOrEmpty(streamUrl))
                streamUrl = obj["url"]?.ToString();

            // YouTube (and some other sites) don't hoist the URL to the top level — they put
            // per-format URLs inside the formats[] array. Fall back to the best merged format.
            JObject? selectedFormat = null;
            if (string.IsNullOrEmpty(streamUrl))
            {
                var formats = obj["formats"] as JArray;
                if (formats != null)
                {
                    // Pick the last format that has both audio and video and a non-empty url.
                    foreach (var fmt in formats)
                    {
                        string? fAcodec = fmt["acodec"]?.ToString();
                        string? fVcodec = fmt["vcodec"]?.ToString();
                        string? fUrl    = fmt["url"]?.ToString();
                        if (!string.IsNullOrEmpty(fUrl)
                            && !string.Equals(fAcodec, "none", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(fVcodec, "none", StringComparison.OrdinalIgnoreCase))
                        {
                            streamUrl      = fUrl;
                            selectedFormat = fmt as JObject;
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(streamUrl))
            {
                Plugin.Log.Warning("[FFXIV-TV] yt-dlp JSON has no 'url' or 'manifest_url' field.");
                return null;
            }

            // HTTP headers: prefer format-level headers, fall back to top-level.
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var httpHeaders = (selectedFormat?["http_headers"] ?? obj["http_headers"]) as JObject;
            if (httpHeaders != null)
            {
                foreach (var prop in httpHeaders.Children<JProperty>())
                    headers[prop.Name] = prop.Value.ToString();
            }

            // acodec=none means yt-dlp selected a video-only format (e.g. Reddit DASH).
            // Caller will use PlayViaPipeAsync to let yt-dlp mux audio+video before handing to LibVLC.
            var acodecSource = selectedFormat ?? obj;
            bool videoOnly = string.Equals(
                acodecSource["acodec"]?.ToString(), "none", StringComparison.OrdinalIgnoreCase);

            Plugin.Log.Info($"[FFXIV-TV] yt-dlp resolved: {streamUrl[..Math.Min(80, streamUrl.Length)]}... (videoOnly={videoOnly})");
            return new ResolvedStream(streamUrl, headers.Count > 0 ? headers : null, videoOnly);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[FFXIV-TV] yt-dlp process failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Streams URL through a yt-dlp subprocess piped into LibVLC via StreamMediaInput.
    /// Used for sites like Reddit that serve separate DASH audio+video tracks — yt-dlp
    /// merges them in-process and outputs a single MP4/webm stream to stdout.
    /// </summary>
    private async Task PlayViaPipeAsync(string url, string ytdlpPath, int version)
    {
        Plugin.Log.Info("[FFXIV-TV] VideoPlayer: piping through yt-dlp (video-only DASH → merged A/V stream)");
        _status = "Loading (yt-dlp pipe)...";

        // bestvideo+bestaudio: yt-dlp merges the best video and audio tracks.
        // /best: fallback to a single combined format if merging is impossible.
        var psi = new ProcessStartInfo(ytdlpPath,
            $"-f \"bestvideo+bestaudio/best\" -o - --no-playlist --js-runtimes node \"{url}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex)
        {
            _status = $"Error: {ex.Message}";
            Plugin.Log.Error($"[FFXIV-TV] VideoPlayer: yt-dlp pipe failed to start: {ex.Message}");
            return;
        }

        if (proc == null) { _status = "Error: yt-dlp pipe failed to start"; return; }

        // Drain stderr in background — prevents the pipe buffer from filling and blocking yt-dlp.
        _ = proc.StandardError.ReadToEndAsync();

        // Kill any previous pipe process, store the new one.
        var old = Interlocked.Exchange(ref _ytdlpProc, proc);
        try { old?.Kill(entireProcessTree: true); old?.Dispose(); } catch { }

        // Version check: abort if a newer Play() was called while we set up the process.
        if (_playVersion != version)
        {
            try { proc.Kill(entireProcessTree: true); proc.Dispose(); } catch { }
            Interlocked.CompareExchange(ref _ytdlpProc, null, proc);
            return;
        }

        _status = "Connecting...";
        Plugin.Log.Info("[FFXIV-TV] VideoPlayer: yt-dlp pipe ready — streaming to LibVLC");

        var media = new Media(_libVlc!, new StreamMediaInput(proc.StandardOutput.BaseStream));
        StartPlayback(media, 1920, 1080, version);
    }

    private string? FindYtDlp()
    {
        if (!string.IsNullOrEmpty(YtDlpPath) && File.Exists(YtDlpPath))
            return YtDlpPath;

        string pluginDirPath = Path.Combine(_pluginDir, "yt-dlp.exe");
        if (File.Exists(pluginDirPath))
            return pluginDirPath;

        try
        {
            using var proc = Process.Start(new ProcessStartInfo("where", "yt-dlp")
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            });
            if (proc != null)
            {
                string result = proc.StandardOutput.ReadLine() ?? string.Empty;
                proc.WaitForExit();
                if (!string.IsNullOrEmpty(result) && File.Exists(result.Trim()))
                    return result.Trim();
            }
        }
        catch { }

        return null;
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private void AllocatePixelBuffer(int w, int h)
    {
        if (_pixelsHandle.IsAllocated) _pixelsHandle.Free();
        _pixels       = new byte[w * h * 4];
        _pixelsHandle = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
        _pixelWidth   = w;
        _pixelHeight  = h;
    }

    private void EnsureTexture(int w, int h)
    {
        if (_device == null) return;
        if (_dynTex != null && _texWidth == w && _texHeight == h) return;

        _srv?.Dispose();    _srv    = null;
        _dynTex?.Dispose(); _dynTex = null;

        var desc = new Texture2DDescription
        {
            Width             = (uint)w,
            Height            = (uint)h,
            MipLevels         = 1,
            ArraySize         = 1,
            Format            = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage             = ResourceUsage.Dynamic,
            BindFlags         = BindFlags.ShaderResource,
            CPUAccessFlags    = CpuAccessFlags.Write,
        };

        _dynTex    = _device.CreateTexture2D(desc);
        _srv       = _device.CreateShaderResourceView(_dynTex);
        _texWidth  = w;
        _texHeight = h;
        Plugin.Log.Info($"[FFXIV-TV] VideoPlayer: created {w}x{h} dynamic texture.");
    }

    // ── LibVLC callbacks (decode thread) ──────────────────────────────────────

    private IntPtr LockCallback(IntPtr opaque, IntPtr planes)
    {
        try
        {
            _vlcWriting = true;
            Marshal.WriteIntPtr(planes,
                _pixelsHandle.IsAllocated ? _pixelsHandle.AddrOfPinnedObject() : IntPtr.Zero);
        }
        catch { _vlcWriting = false; }
        return IntPtr.Zero;
    }

    private void UnlockCallback(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
        _vlcWriting = false;
    }

    private void DisplayCallback(IntPtr opaque, IntPtr picture)
    {
        try
        {
            _frameDirty    = true;
            _transitioning = false; // new frame received — transition complete
            _status        = "Playing";
            _framesDecoded++;

            // A-B loop: when position reaches B, jump back to A.
            // Dispatched via Task.Run to avoid re-entering LibVLC from its own callback.
            // Capture _playVersion so the seek is cancelled if Play() is called before it fires.
            if (_abLoopActive && _loopA < _loopB && _player != null && _player.Position >= _loopB)
            {
                int v = _playVersion;
                Task.Run(() => { if (_playVersion == v && _player != null) _player.Position = _loopA; });
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[FFXIV-TV] DisplayCallback exception (suppressed): {ex.Message}");
        }
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        // Fire EndOfMedia on a background thread to get off LibVLC's callback thread.
        // The subscriber (SyncCoordinator) decides whether to loop, advance playlist, etc.
        _ = Task.Run(() => EndOfMedia?.Invoke());
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_player != null) _player.EndReached -= OnEndReached;
        Interlocked.Increment(ref _playVersion); // cancel any in-flight tasks
        Stop();
        _player?.Dispose();
        _media?.Dispose();
        if (_pixelsHandle.IsAllocated) _pixelsHandle.Free();
        _pixels = null;
        _srv?.Dispose();    _srv    = null;
        _dynTex?.Dispose(); _dynTex = null;
        _libVlc?.Dispose();
    }
}
