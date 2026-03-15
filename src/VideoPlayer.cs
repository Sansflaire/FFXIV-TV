using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace FFXIVTv;

/// <summary>
/// Decodes a local video file with LibVLC and provides decoded BGRA frames as a
/// D3D11 dynamic texture SRV for D3DRenderer to bind.
///
/// Threading model:
///   LibVLC decode thread → LockCallback provides pinned pixel buffer → UnlockCallback signals done
///                        → DisplayCallback marks _frameDirty = true
///   Render thread        → UploadFrame: if dirty and not being written, Map/memcpy/Unmap dynamic tex
/// </summary>
public sealed class VideoPlayer : IDisposable
{
    // ── LibVLC ────────────────────────────────────────────────────────────────
    private readonly LibVLC    _libVlc;
    private readonly MediaPlayer _player;
    private Media?  _media;

    // ── Pixel buffer (LibVLC writes decoded BGRA frames here) ─────────────────
    // Must be pinned so the GC never moves it while LibVLC holds a pointer to it.
    private byte[]?  _pixels;
    private GCHandle _pixelsHandle;
    private int      _pixelWidth;
    private int      _pixelHeight;

    // Flags set/cleared by LibVLC decode thread and render thread respectively.
    private volatile bool _frameDirty;   // a new frame is ready for upload
    private volatile bool _vlcWriting;   // LibVLC currently holds the buffer lock

    // ── D3D11 resources ───────────────────────────────────────────────────────
    // Created lazily on the render thread once video dimensions are known.
    private ID3D11Device?             _device;
    private ID3D11Texture2D?          _dynTex;
    private ID3D11ShaderResourceView? _srv;
    private int _texWidth;
    private int _texHeight;

    // Set by Play() on background thread; consumed by UploadFrame() on render thread.
    private volatile bool _needsNewTexture;
    private int _pendingTexW;
    private int _pendingTexH;

    // ── Delegate fields (must be stored — prevents GC from collecting them) ───
    private readonly MediaPlayer.LibVLCVideoLockCb    _lockCb;
    private readonly MediaPlayer.LibVLCVideoUnlockCb  _unlockCb;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;

    // ── Public API ────────────────────────────────────────────────────────────
    public ID3D11ShaderResourceView? FrameSrv  => _srv;
    public bool HasTexture  => _srv != null;
    public bool IsPlaying   => _player.IsPlaying;
    public bool IsPaused    => _player.State == VLCState.Paused;

    // ── Constructor ───────────────────────────────────────────────────────────
    /// <param name="pluginDir">Directory containing libvlc.dll (= devPlugins/FFXIV-TV/).</param>
    public VideoPlayer(string pluginDir)
    {
        Core.Initialize(pluginDir);
        _libVlc = new LibVLC(enableDebugLogs: false);
        _player = new MediaPlayer(_libVlc);

        // Store as fields — delegates passed to native code MUST outlive the call site.
        _lockCb    = LockCallback;
        _unlockCb  = UnlockCallback;
        _displayCb = DisplayCallback;

        _player.EndReached += OnEndReached;
    }

    /// <summary>Called once D3DRenderer is initialized. Must be called before Play().</summary>
    public void SetDevice(ID3D11Device device) => _device = device;

    // ── Playback ──────────────────────────────────────────────────────────────

    public void Play(string path)
    {
        if (!File.Exists(path))
        {
            Plugin.Log.Warning($"[FFXIV-TV] VideoPlayer: file not found: '{path}'");
            return;
        }

        Stop();

        // Parse the media on a background thread to get video dimensions,
        // then configure the player and start playback.
        Task.Run(async () =>
        {
            try
            {
                var media = new Media(_libVlc, new Uri(path));
                await media.Parse(MediaParseOptions.ParseLocal);

                // Determine video dimensions from parsed tracks.
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

                Plugin.Log.Info($"[FFXIV-TV] VideoPlayer: video {vw}x{vh} — '{Path.GetFileName(path)}'");

                AllocatePixelBuffer((int)vw, (int)vh);

                // Signal render thread to create/resize the D3D11 texture.
                _pendingTexW      = (int)vw;
                _pendingTexH      = (int)vh;
                _needsNewTexture  = true;

                // Configure LibVLC to output BGRA at the video's native resolution.
                _player.SetVideoFormat("BGRA", vw, vh, vw * 4);
                _player.SetVideoCallbacks(_lockCb, _unlockCb, _displayCb);

                _media = media;
                _player.Media = media;
                _player.Play();
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[FFXIV-TV] VideoPlayer.Play failed: {ex.Message}");
            }
        });
    }

    public void TogglePause()
    {
        if (_player.IsPlaying) _player.Pause();
        else                   _player.Play();
    }

    public void Stop()
    {
        _player.Stop();
        _media?.Dispose();
        _media        = null;
        _frameDirty   = false;
        _vlcWriting   = false;
    }

    // ── Render-thread upload ──────────────────────────────────────────────────

    /// <summary>
    /// Called from D3DRenderer.Draw() on the render thread.
    /// Re-creates the D3D11 texture if dimensions changed, then uploads the latest
    /// decoded frame to the GPU dynamic texture.
    /// </summary>
    public void UploadFrame(ID3D11DeviceContext ctx)
    {
        // Re-create D3D11 texture on render thread if Play() set new dimensions.
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
        return IntPtr.Zero; // picture identifier (unused)
    }

    private void UnlockCallback(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
        _vlcWriting = false;
    }

    private void DisplayCallback(IntPtr opaque, IntPtr picture)
    {
        _frameDirty = true;
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        // Loop by stopping and restarting on a separate thread.
        // Cannot call Play()/Stop() directly from the EndReached callback.
        Task.Run(() =>
        {
            _player.Stop();
            _player.Play();
        });
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _player.EndReached -= OnEndReached;
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
