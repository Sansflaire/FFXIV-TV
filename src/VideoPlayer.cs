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
    private readonly LibVLC      _libVlc;
    private readonly MediaPlayer _player;
    private Media?  _media;

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
    // False when stopped — even if the GPU texture still exists from the last frame.
    // This causes D3DRenderer to skip the video quad and show the idle gradient instead.
    public bool HasTexture => _srv != null && _player.State != VLCState.Stopped;
    public bool IsPlaying  => _player.IsPlaying;
    public bool IsPaused   => _player.State == VLCState.Paused;

    /// <summary>Playback position as a fraction 0–1. Returns 0 when stopped.</summary>
    public float Position => (IsPlaying || IsPaused) ? Math.Clamp(_player.Position, 0f, 1f) : 0f;

    /// <summary>Current playback time in milliseconds. -1 when unknown.</summary>
    public long TimeMs => _player.Time;

    /// <summary>Total duration in milliseconds. -1 for live streams or unknown.</summary>
    public long LengthMs => _player.Length;

    /// <summary>Seek to a position in the media (0 = start, 1 = end).</summary>
    public void Seek(float position) => _player.Position = Math.Clamp(position, 0f, 1f);

    /// <summary>Playback volume 0–100. Applied immediately to the LibVLC player.</summary>
    public int Volume
    {
        get => _player.Volume;
        set => _player.Volume = Math.Clamp(value, 0, 100);
    }

    /// <summary>Mute/unmute audio without affecting the stored volume level.</summary>
    public bool Muted
    {
        get => _player.Mute;
        set => _player.Mute = value;
    }

    // ── A-B loop ──────────────────────────────────────────────────────────────
    private float _loopA       = 0f;
    private float _loopB       = 1f;
    private bool  _abLoopActive = false;

    public float LoopA        => _loopA;
    public float LoopB        => _loopB;
    public bool  AbLoopActive => _abLoopActive;

    public void SetLoopA()     => _loopA = _player.Position;
    public void SetLoopB()     => _loopB = _player.Position;
    public void ToggleAbLoop() => _abLoopActive = !_abLoopActive;
    public void ClearAbLoop()  { _loopA = 0f; _loopB = 1f; _abLoopActive = false; }

    // ── Constructor ───────────────────────────────────────────────────────────
    /// <param name="pluginDir">Directory containing libvlc.dll (= devPlugins/FFXIV-TV/).</param>
    public VideoPlayer(string pluginDir)
    {
        _pluginDir = pluginDir;

        Core.Initialize(pluginDir);
        _libVlc = new LibVLC(enableDebugLogs: false);
        _player = new MediaPlayer(_libVlc);

        _lockCb    = LockCallback;
        _unlockCb  = UnlockCallback;
        _displayCb = DisplayCallback;

        _player.EndReached += OnEndReached;
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
        // If paused, resume rather than restart.
        if (IsPaused)
        {
            _player.Play();
            _status = "Playing";
            return;
        }

        // Increment version BEFORE Stop() so any in-flight async task sees the new
        // version and aborts before calling StartPlayback().
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
        _media      = null;
        _frameDirty = false;
        _status     = "Stopped";
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

            var media = new Media(_libVlc, new Uri(path));
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
            var media = new Media(_libVlc, new Uri(streamUrl));

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

        _player.SetVideoFormat("BGRA", vw, vh, vw * 4);
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
        public ResolvedStream(string url, Dictionary<string, string>? headers)
        { Url = url; Headers = headers; }
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
            // -j dumps JSON including url + http_headers.
            // format "best" (no mp4 restriction) allows DASH/HLS manifests —
            // LibVLC handles these natively and demuxes audio+video internally.
            var psi = new ProcessStartInfo(ytdlp, $"-j --no-playlist --format \"best\" \"{url}\"")
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

            if (string.IsNullOrWhiteSpace(json))
            {
                string errLine = stderr.Trim().Split('\n')[0].Trim();
                _status = string.IsNullOrEmpty(errLine) ? "Error: yt-dlp returned no output" : $"Error: {errLine}";
                Plugin.Log.Warning($"[FFXIV-TV] yt-dlp returned no JSON. stderr: {stderr.Trim()}");
                return null;
            }

            var obj = JObject.Parse(json);
            string? streamUrl = obj["url"]?.ToString();
            if (string.IsNullOrEmpty(streamUrl))
            {
                Plugin.Log.Warning("[FFXIV-TV] yt-dlp JSON has no 'url' field.");
                return null;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var httpHeaders = obj["http_headers"];
            if (httpHeaders != null)
            {
                foreach (var prop in httpHeaders.Children<JProperty>())
                    headers[prop.Name] = prop.Value.ToString();
            }

            Plugin.Log.Info($"[FFXIV-TV] yt-dlp resolved: {streamUrl[..Math.Min(80, streamUrl.Length)]}...");
            return new ResolvedStream(streamUrl, headers.Count > 0 ? headers : null);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[FFXIV-TV] yt-dlp process failed: {ex.Message}");
            return null;
        }
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
        _vlcWriting = true;
        Marshal.WriteIntPtr(planes,
            _pixelsHandle.IsAllocated ? _pixelsHandle.AddrOfPinnedObject() : IntPtr.Zero);
        return IntPtr.Zero;
    }

    private void UnlockCallback(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
        _vlcWriting = false;
    }

    private void DisplayCallback(IntPtr opaque, IntPtr picture)
    {
        _frameDirty = true;
        _status     = "Playing";
        _framesDecoded++;

        // A-B loop: when position reaches B, jump back to A.
        // Dispatched via Task.Run to avoid re-entering LibVLC from its own callback.
        if (_abLoopActive && _loopA < _loopB && _player.Position >= _loopB)
            Task.Run(() => _player.Position = _loopA);
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
        _player.EndReached -= OnEndReached;
        Interlocked.Increment(ref _playVersion); // cancel any in-flight tasks
        Stop();
        _player.Dispose();
        _media?.Dispose();
        if (_pixelsHandle.IsAllocated) _pixelsHandle.Free();
        _pixels = null;
        _srv?.Dispose();    _srv    = null;
        _dynTex?.Dispose(); _dynTex = null;
        _libVlc.Dispose();
    }
}
