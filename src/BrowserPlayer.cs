using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace FFXIVTv;

/// <summary>
/// Browser-based content renderer using Microsoft WebView2.
///
/// Renders any URL (YouTube, Reddit, any website) via a hidden WebView2 instance
/// running on a dedicated background STA thread. No yt-dlp required — the browser
/// handles all video formats natively.
///
/// Frame capture: periodic PNG screenshot via CapturePreviewAsync (~6fps) decoded
/// to BGRA and uploaded to a D3D11 dynamic texture for the renderer to bind.
/// This is lighter than a full shared-texture implementation but works without
/// native code or DirectComposition interop.
///
/// RAM: one browser process (~150–300 MB flat cost). Each additional screen is
/// a separate tab in the same process (~20–50 MB extra).
///
/// Requires: Microsoft WebView2 Runtime (pre-installed with Windows 11 / Edge).
/// If the runtime is not found, Status will show an error message.
/// </summary>
public sealed class BrowserPlayer : IDisposable
{
    // ── WebView2 (STA thread only) ─────────────────────────────────────────────
    private Form?    _hostForm;
    private WebView2? _webView;
    private Thread?  _staThread;
    private readonly string _pluginDir;
    private readonly string _userDataFolder;

    // ── Capture timer ──────────────────────────────────────────────────────────
    private System.Threading.Timer? _captureTimer;
    private volatile bool _capturing;

    // ── Frame buffer (written STA thread, read render thread) ─────────────────
    private byte[]?      _frameBuffer;
    private int          _frameW = 1920;
    private int          _frameH = 1080;
    private readonly object _frameLock = new();
    private volatile bool _frameDirty;

    // ── D3D11 texture (render thread only) ────────────────────────────────────
    private ID3D11Device?             _device;
    private ID3D11Texture2D?          _dynTex;
    private ID3D11ShaderResourceView? _srv;
    private int _texW, _texH;

    // ── State ─────────────────────────────────────────────────────────────────
    private string _currentUrl = string.Empty;
    private volatile bool _webViewReady;

    private string _status = "Stopped";
    public string Status => _status;

    public ID3D11ShaderResourceView? FrameSrv  => _srv;
    public bool HasTexture => _srv != null && _webViewReady;

    // ── Constructor ───────────────────────────────────────────────────────────
    public BrowserPlayer(string pluginDir)
    {
        _pluginDir      = pluginDir;
        _userDataFolder = Path.Combine(pluginDir, "webview2-data");
    }

    public void SetDevice(ID3D11Device device) => _device = device;

    // ── Navigation ────────────────────────────────────────────────────────────

    /// <summary>Navigate to a URL. Starts the browser thread on first call.</summary>
    public void Navigate(string url)
    {
        _currentUrl = url;

        if (_staThread == null || !_staThread.IsAlive)
        {
            _status = "Starting browser...";
            StartBrowserThread();
            // URL will be picked up in the Shown handler once WebView2 is ready.
            return;
        }

        // Use CoreWebView2 != null as the "initialized" check — _webViewReady can be
        // false after Stop() even though the browser is still running and ready to navigate.
        if (_hostForm != null && _webView?.CoreWebView2 != null)
        {
            _hostForm.BeginInvoke(() =>
            {
                _webView?.CoreWebView2.Navigate(url);
                _status = "Loading...";
            });
        }
        else
        {
            _status = "Waiting for browser...";
        }
    }

    public void Stop()
    {
        _currentUrl = string.Empty;
        _captureTimer?.Dispose();
        _captureTimer = null;
        _status       = "Stopped";

        _srv?.Dispose();    _srv    = null;
        _dynTex?.Dispose(); _dynTex = null;
    }

    // ── Browser thread ────────────────────────────────────────────────────────

    private void StartBrowserThread()
    {
        _staThread = new Thread(BrowserThreadMain)
        {
            IsBackground = true,
            Name         = "FFXIV-TV-Browser",
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
    }

    private void BrowserThreadMain()
    {
        // Create a hidden form as the HWND host for WebView2.
        // Positioned far off-screen so it never flickers into view even for the
        // one frame before we call Hide() in the Shown handler.
        _hostForm = new Form
        {
            Width             = _frameW,
            Height            = _frameH,
            ShowInTaskbar     = false,
            FormBorderStyle   = FormBorderStyle.None,
            StartPosition     = FormStartPosition.Manual,
            Location          = new Point(-30000, -30000),
        };

        // Prevent the form being accidentally closed — just hide it instead.
        _hostForm.FormClosing += (s, e) =>
        {
            e.Cancel = true;
            _hostForm.Hide();
        };

        _webView = new WebView2 { Dock = DockStyle.Fill };
        _hostForm.Controls.Add(_webView);

        _hostForm.Shown += async (s, e) =>
        {
            _hostForm.Hide(); // immediately invisible

            try
            {
                // Preload WebView2Loader.dll from the plugin folder before WebView2 initializes.
                // P/Invoke searches the game exe directory first; without this the OS finds an
                // x86 WebView2Loader.dll (from Edge or system) and throws BadImageFormatException.
                string loaderPath = Path.Combine(_pluginDir, "WebView2Loader.dll");
                if (File.Exists(loaderPath))
                    NativeLibrary.Load(loaderPath);

                // If the primary data folder is locked (e.g. previous plugin instance hasn't
                // fully exited yet), retry with a process-unique subfolder so two instances
                // never collide on hot-reload.
                CoreWebView2Environment env;
                try
                {
                    env = await CoreWebView2Environment.CreateAsync(null, _userDataFolder);
                }
                catch (Exception folderEx)
                {
                    string uniqueFolder = _userDataFolder + "_" + System.Diagnostics.Process.GetCurrentProcess().Id;
                    Plugin.Log.Warning($"[FFXIV-TV] BrowserPlayer: primary data folder failed ({folderEx.GetType().Name}: {folderEx.Message}) — retrying with process-unique folder: {uniqueFolder}");
                    env = await CoreWebView2Environment.CreateAsync(null, uniqueFolder);
                }
                await _webView.EnsureCoreWebView2Async(env);

                _webView.CoreWebView2.Settings.IsScriptEnabled      = true;
                _webView.CoreWebView2.Settings.AreDevToolsEnabled   = false;
                _webView.CoreWebView2.Settings.IsStatusBarEnabled   = false;
                // Spoof a real Edge UA so sites don't block the embedded WebView2 as a bot.
                _webView.CoreWebView2.Settings.UserAgent =
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                    "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0";

                _webView.CoreWebView2.NavigationCompleted += (s2, e2) =>
                {
                    _status = e2.IsSuccess ? "Ready" : "Error: navigation failed";
                    if (e2.IsSuccess && _captureTimer == null)
                        StartCaptureTimer();
                };

                _webViewReady = true;

                if (!string.IsNullOrEmpty(_currentUrl))
                {
                    _webView.CoreWebView2.Navigate(_currentUrl);
                    _status = "Loading...";
                }
            }
            catch (WebView2RuntimeNotFoundException)
            {
                _status = "Error: WebView2 Runtime not installed (install Microsoft Edge)";
                Plugin.Log.Error("[FFXIV-TV] BrowserPlayer: WebView2 Runtime not found. Install from microsoft.com/edge or the WebView2 installer.");
            }
            catch (Exception ex)
            {
                _status = $"Error: {ex.GetType().Name}: {ex.Message}";
                Plugin.Log.Error($"[FFXIV-TV] BrowserPlayer init failed [{ex.GetType().Name}]: {ex.Message}");
            }
        };

        Application.Run(_hostForm);
    }

    // ── Screenshot capture ────────────────────────────────────────────────────

    private void StartCaptureTimer()
    {
        _captureTimer?.Dispose();
        // 500ms initial delay (let page load), then 150ms interval (~6fps).
        _captureTimer = new System.Threading.Timer(_ => ScheduleCapture(), null, 500, 150);
    }

    private void ScheduleCapture()
    {
        if (_capturing || !_webViewReady || _hostForm == null || _webView == null) return;
        _capturing = true;

        // Post async capture work onto the STA thread (where WebView2 lives).
        var tcs = new TaskCompletionSource();
        _hostForm.BeginInvoke(async () =>
        {
            try
            {
                using var ms = new MemoryStream();
                await _webView.CoreWebView2.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Png, ms);
                ms.Position = 0;

                using var bmp = new Bitmap(ms);
                var bd = bmp.LockBits(
                    new Rectangle(0, 0, bmp.Width, bmp.Height),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppArgb);

                int bytes = bmp.Height * Math.Abs(bd.Stride);
                var buf   = new byte[bytes];
                Marshal.Copy(bd.Scan0, buf, 0, bytes);
                bmp.UnlockBits(bd);

                lock (_frameLock)
                {
                    _frameBuffer = buf;
                    _frameW      = bmp.Width;
                    _frameH      = bmp.Height;
                }
                _frameDirty = true;
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[FFXIV-TV] BrowserPlayer capture failed: {ex.Message}");
                tcs.TrySetException(ex);
            }
            finally
            {
                _capturing = false;
            }
        });
    }

    // ── Render-thread upload ──────────────────────────────────────────────────

    public void UploadFrame(ID3D11DeviceContext ctx)
    {
        if (!_frameDirty || _device == null) return;

        byte[]? buf;
        int w, h;
        lock (_frameLock)
        {
            buf = _frameBuffer;
            w   = _frameW;
            h   = _frameH;
        }
        if (buf == null) return;
        _frameDirty = false;

        EnsureTexture(w, h);
        if (_dynTex == null) return;

        try
        {
            ctx.Map(_dynTex, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None, out var mapped);
            int srcStride = w * 4;
            unsafe
            {
                byte* dst = (byte*)mapped.DataPointer;
                fixed (byte* src = buf)
                {
                    for (int y = 0; y < h; y++)
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
            Plugin.Log.Warning($"[FFXIV-TV] BrowserPlayer.UploadFrame failed: {ex.Message}");
        }
    }

    private void EnsureTexture(int w, int h)
    {
        if (_device == null) return;
        if (_dynTex != null && _texW == w && _texH == h) return;

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
        _dynTex = _device.CreateTexture2D(desc);
        _srv    = _device.CreateShaderResourceView(_dynTex);
        _texW   = w;
        _texH   = h;
        Plugin.Log.Info($"[FFXIV-TV] BrowserPlayer: created {w}x{h} texture.");
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _captureTimer?.Dispose();
        _captureTimer = null;

        _srv?.Dispose();    _srv    = null;
        _dynTex?.Dispose(); _dynTex = null;

        try
        {
            _hostForm?.BeginInvoke(() => Application.ExitThread());
            _staThread?.Join(3000);
        }
        catch { }
    }
}
