using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Threading;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FFXIVTv;

/// <summary>
/// Local HTTP diagnostic API — confirms which version is loaded and exposes full system state.
///
/// Endpoints:
///   GET http://localhost:17777/version  → {"version":"X.Y.Z","loaded":true}
///   GET http://localhost:17777/state    → full system snapshot (all sections combined)
///   GET http://localhost:17777/render   → D3D renderer: initialized, inject count, DSV/RTV learned, activeSrv
///   GET http://localhost:17777/video    → video player: status, frames decoded, position, texture dimensions
///   GET http://localhost:17777/browser  → browser player: initialized, hasTexture, currentUrl
///   GET http://localhost:17777/sync     → network sync: mode, server running/clients, client connected
///   GET http://localhost:17777/config   → active config: mode, visible, bloomCap, brightness, URL, etc.
///   GET http://localhost:17777/rect     → screen-space corners + 9-point color samples from the CPU pixel buffer
/// </summary>
internal sealed class StatusApi : IDisposable
{
    private const string Prefix = "http://localhost:17777/";

    private readonly HttpListener _listener = new();
    private readonly Thread       _thread;
    private volatile bool         _running;

    // Subsystem references — set via SetSubsystems after all objects are constructed.
    private D3DRenderer?      _d3d;
    private VideoPlayer?      _vp;
    private BrowserPlayer?    _bp;
    private SyncCoordinator?  _sync;
    private Configuration?    _cfg;
    private IGameGui?         _gui;
    private CopyBlitRenderer? _cb;

    internal StatusApi()
    {
        _listener.Prefixes.Add(Prefix);
        try
        {
            _listener.Start();
            _running = true;
            _thread  = new Thread(Loop) { IsBackground = true, Name = "FFXIV-TV StatusApi" };
            _thread.Start();
            Plugin.Log.Info($"[FFXIV-TV] StatusApi listening on {Prefix}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[FFXIV-TV] StatusApi failed to start: {ex.Message}");
            _thread = new Thread(() => { }); // dummy so field is assigned
        }
    }

    /// <summary>Wire up subsystem references after all objects are constructed in Plugin.cs.</summary>
    internal void SetSubsystems(D3DRenderer d3d, VideoPlayer vp,
        BrowserPlayer bp, SyncCoordinator sync, Configuration cfg, IGameGui gui)
    {
        _d3d  = d3d;
        _vp   = vp;
        _bp   = bp;
        _sync = sync;
        _cfg  = cfg;
        _gui  = gui;
    }

    /// <summary>Wire up the CopyBlit renderer once it's constructed in Plugin.cs.</summary>
    internal void SetCopyBlit(CopyBlitRenderer cb) => _cb = cb;

    public void Dispose()
    {
        _running = false;
        try { _listener.Stop(); } catch { }
    }

    private void Loop()
    {
        while (_running)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch { break; }
            try { Handle(ctx); }
            catch { /* never crash the background thread */ }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant() ?? "";

        string? json;
        if (path.StartsWith("/set/") || path == "/set")
            json = HandleSet(path, ctx.Request.Url);
        else
            json = path switch
            {
                "/version" or "/status" or "" => BuildVersion(),
                "/render"                      => BuildRender(),
                "/copyblit" or "/get/copyblit" => BuildCopyBlit(),
                "/video"                       => BuildVideo(),
                "/browser"                     => BuildBrowser(),
                "/sync"                        => BuildSync(),
                "/config"                      => BuildConfig(),
                "/rect"                        => BuildRect(),
                "/inject"                      => BuildInject(),
                "/hud"                         => BuildHud(),
                "/state"                       => BuildState(),
                "/get/all" or "/get"           => BuildGetAll(),
                _                              => null,
            };

        if (json == null) { Respond(ctx, 404, "{\"error\":\"not found\"}"); return; }
        Respond(ctx, 200, json);
    }

    // ── Query-string helpers ──────────────────────────────────────────────────

    private static Dictionary<string, string> ParseQuery(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return result;
        var q = query.TrimStart('?');
        foreach (var part in q.Split('&'))
        {
            var idx = part.IndexOf('=');
            if (idx < 0) result[part] = "";
            else result[part[..idx]] = Uri.UnescapeDataString(part[(idx + 1)..]);
        }
        return result;
    }

    private static bool TryFloatQ(Dictionary<string, string> q, string key, out float val)
    {
        if (q.TryGetValue(key, out var s) && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out val))
            return true;
        val = 0f;
        return false;
    }

    private static bool TryBoolQ(Dictionary<string, string> q, string key, out bool val)
    {
        if (q.TryGetValue(key, out var s))
        {
            if (s == "1" || s.Equals("true",  StringComparison.OrdinalIgnoreCase)) { val = true;  return true; }
            if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase)) { val = false; return true; }
        }
        val = false;
        return false;
    }

    // ── Setter endpoints ──────────────────────────────────────────────────────
    // Usage (curl): curl "http://localhost:17777/set/bloomcap?v=0.05"
    //               curl "http://localhost:17777/set/gradient?s=0.8&v=0.3&speed=0.02"
    //               curl "http://localhost:17777/set/tint?r=1&g=0.5&b=0.5&a=1"
    //               curl "http://localhost:17777/set/visible?v=true"
    //               curl "http://localhost:17777/set/save"
    //               curl "http://localhost:17777/get/all"

    private string HandleSet(string path, Uri? url)
    {
        var q = ParseQuery(url?.Query);
        var c = _cfg;
        if (c == null) return "{\"error\":\"not ready\"}";

        switch (path)
        {
            case "/set/bloomcap":
                if (TryFloatQ(q, "v", out float bc)) { c.BloomCap = Math.Clamp(bc, 0f, 1f); c.Save(); }
                return $"{{\"bloomCap\":{F(c.BloomCap)}}}";

            case "/set/brightness":
                if (TryFloatQ(q, "v", out float br)) { c.Brightness = Math.Clamp(br, 0f, 4f); c.Save(); }
                return $"{{\"brightness\":{F(c.Brightness)}}}";

            case "/set/gamma":
                if (TryFloatQ(q, "v", out float gm)) { c.Gamma = Math.Clamp(gm, 0.1f, 3f); c.Save(); }
                return $"{{\"gamma\":{F(c.Gamma)}}}";

            case "/set/contrast":
                if (TryFloatQ(q, "v", out float ct)) { c.Contrast = Math.Clamp(ct, 0f, 3f); c.Save(); }
                return $"{{\"contrast\":{F(c.Contrast)}}}";

            case "/set/tint":
            {
                float r = c.TintR, g = c.TintG, b = c.TintB, a = c.TintA;
                if (TryFloatQ(q, "r", out float tr)) r = Math.Clamp(tr, 0f, 1f);
                if (TryFloatQ(q, "g", out float tg)) g = Math.Clamp(tg, 0f, 1f);
                if (TryFloatQ(q, "b", out float tb)) b = Math.Clamp(tb, 0f, 1f);
                if (TryFloatQ(q, "a", out float ta)) a = Math.Clamp(ta, 0f, 1f);
                c.TintR = r; c.TintG = g; c.TintB = b; c.TintA = a;
                c.Save();
                return $"{{\"tint\":{{\"r\":{F(r)},\"g\":{F(g)},\"b\":{F(b)},\"a\":{F(a)}}}}}";
            }

            case "/set/gradient":
                if (TryFloatQ(q, "s",     out float gs))  D3DRenderer.GradientS     = Math.Clamp(gs,  0f, 1f);
                if (TryFloatQ(q, "v",     out float gv))  D3DRenderer.GradientV     = Math.Clamp(gv,  0f, 1f);
                if (TryFloatQ(q, "speed", out float gsp)) D3DRenderer.GradientSpeed = Math.Clamp(gsp, 0f, 0.5f);
                return $"{{\"gradient\":{{\"s\":{F(D3DRenderer.GradientS)},\"v\":{F(D3DRenderer.GradientV)},\"speed\":{F(D3DRenderer.GradientSpeed)}}}}}";

            case "/set/visible":
                if (TryBoolQ(q, "v", out bool vis)) { c.Screen.Visible = vis; c.Save(); }
                return $"{{\"visible\":{B(c.Screen.Visible)}}}";

            case "/set/alwaysdraw":
                if (TryBoolQ(q, "v", out bool ad)) { c.AlwaysDraw = ad; c.Save(); }
                return $"{{\"alwaysDraw\":{B(c.AlwaysDraw)}}}";

            case "/set/showbacking":
                if (TryBoolQ(q, "v", out bool sb)) { c.ShowBlackBacking = sb; c.Save(); }
                return $"{{\"showBlackBacking\":{B(c.ShowBlackBacking)}}}";

            case "/set/save":
                c.Save();
                return "{\"saved\":true}";

            // XMP-style CopyBlit renderer toggle. Default is ON.
            //   curl "http://localhost:17777/set/copyblit?v=true"   → force XMP-style path
            //   curl "http://localhost:17777/set/copyblit?v=false"  → revert to legacy hook path
            //   curl "http://localhost:17777/get/copyblit"          → status snapshot
            case "/set/copyblit":
                if (TryBoolQ(q, "v", out bool cbEnabled))
                {
                    c.UseCopyBlitRenderer = cbEnabled;
                    c.Save();
                }
                return $"{{\"useCopyBlitRenderer\":{B(c.UseCopyBlitRenderer)}}}";

            // Inject-path controls — no config save needed (runtime only).
            case "/set/omsetrtenable":
                if (TryBoolQ(q, "v", out bool ome)) D3DRenderer.OmSetRtInjectEnabled = ome;
                return $"{{\"omSetRtInjectEnabled\":{B(D3DRenderer.OmSetRtInjectEnabled)}}}";

            case "/set/bbdrawskip":
                if (q.TryGetValue("n", out var ns) && int.TryParse(ns, out int skip))
                    D3DRenderer.BbDrawSkip = Math.Max(0, skip);
                return $"{{\"bbDrawSkip\":{D3DRenderer.BbDrawSkip}}}";

            case "/set/ldrlog":
                if (TryBoolQ(q, "v", out bool ll)) D3DRenderer.LdrLog = ll;
                return $"{{\"ldrLog\":{B(D3DRenderer.LdrLog)}}}";

            case "/set/clearrtvinject":
                if (TryBoolQ(q, "v", out bool cri)) D3DRenderer.ClearRtvInjectEnabled = cri;
                return $"{{\"clearRtvInjectEnabled\":{B(D3DRenderer.ClearRtvInjectEnabled)}}}";

            case "/set/cfdi":
                if (TryBoolQ(q, "v", out bool cfdi)) D3DRenderer.CfDiEnabled = cfdi;
                return $"{{\"cfDiEnabled\":{B(D3DRenderer.CfDiEnabled)}}}";

            case "/set/cfdraw":
                if (TryBoolQ(q, "v", out bool cfdw)) D3DRenderer.CfDrawEnabled = cfdw;
                return $"{{\"cfDrawEnabled\":{B(D3DRenderer.CfDrawEnabled)}}}";

            case "/set/cfdispatch":
                if (TryBoolQ(q, "v", out bool cfd)) D3DRenderer.CfDispatchEnabled = cfd;
                return $"{{\"cfDispatchEnabled\":{B(D3DRenderer.CfDispatchEnabled)}}}";

            case "/set/cfdispatchskip":
                if (q.TryGetValue("n", out var cds) && int.TryParse(cds, out int cdsk))
                    D3DRenderer.CfDispatchSkip = Math.Max(0, cdsk);
                return $"{{\"cfDispatchSkip\":{D3DRenderer.CfDispatchSkip}}}";

            case "/set/cfdrawhunskip":
                if (q.TryGetValue("n", out var dhs) && int.TryParse(dhs, out int dhsk))
                    D3DRenderer.CfDrawHudSkip = Math.Max(0, dhsk);
                return $"{{\"cfDrawHudSkip\":{D3DRenderer.CfDrawHudSkip}}}";

            case "/set/cfdi_preinject":
                if (TryBoolQ(q, "v", out bool pi)) D3DRenderer.CfDrawPreInject = pi;
                return $"{{\"cfDrawPreInject\":{B(D3DRenderer.CfDrawPreInject)}}}";

            default:
                return "{\"error\":\"unknown set endpoint\"}";
        }
    }

    // ── /get/all — dump all tunable values in one call ────────────────────────
    private string BuildGetAll()
    {
        var c = _cfg;
        return $$"""
        {
          "bloomCap":        {{F(c?.BloomCap    ?? 0f)}},
          "brightness":      {{F(c?.Brightness  ?? 1f)}},
          "gamma":           {{F(c?.Gamma       ?? 1f)}},
          "contrast":        {{F(c?.Contrast    ?? 1f)}},
          "tint": { "r": {{F(c?.TintR ?? 1f)}}, "g": {{F(c?.TintG ?? 1f)}}, "b": {{F(c?.TintB ?? 1f)}}, "a": {{F(c?.TintA ?? 1f)}} },
          "gradient": {
            "s":     {{F(D3DRenderer.GradientS)}},
            "v":     {{F(D3DRenderer.GradientV)}},
            "speed": {{F(D3DRenderer.GradientSpeed)}}
          },
          "visible":         {{B(c?.Screen.Visible    ?? false)}},
          "alwaysDraw":      {{B(c?.AlwaysDraw        ?? false)}},
          "showBlackBacking":{{B(c?.ShowBlackBacking  ?? false)}}
        }
        """;
    }

    // ── JSON helpers ──────────────────────────────────────────────────────────

    private static string Q(string? s)
        => s == null ? "null" : $"\"{s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r")}\"";

    private static string B(bool v) => v ? "true" : "false";

    private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);

    // ── Section builders ──────────────────────────────────────────────────────

    private string BuildVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
        return $"{{\"version\":\"{v}\",\"loaded\":true}}";
    }

    private string BuildCopyBlit()
    {
        var cb = _cb;
        var c  = _cfg;
        var (vpW, vpH) = cb?.LastViewport ?? (0, 0);
        return $$"""
        {
          "useCopyBlitRenderer": {{B(c?.UseCopyBlitRenderer ?? false)}},
          "isAvailable":         {{B(cb?.IsAvailable ?? false)}},
          "frameCount":          {{cb?.FrameCount ?? 0}},
          "depthCaptureCount":   {{cb?.DepthCaptureCount ?? 0}},
          "blitCount":           {{cb?.BlitCount ?? 0}},
          "lastError":           {{Q(cb?.LastError ?? "")}},
          "lastDepthFmt":        {{Q(cb?.LastDepthFmt ?? "none")}},
          "lastDepthTexPtr":     "0x{{(cb?.LastDepthTexPtr ?? 0):X}}",
          "lastViewport":        { "w": {{vpW}}, "h": {{vpH}} }
        }
        """;
    }

    private string BuildRender()
    {
        var d = _d3d;
        return $$"""
        {
          "initialized": {{B(d?.IsAvailable ?? false)}},
          "hasTexture": {{B(d?.HasTexture ?? false)}},
          "activeSrvSource": {{Q(d?.ActiveSrvSource ?? "null")}},
          "sceneInjectCount": {{d?.SceneInjectCount ?? 0}},
          "ldrInjectCount": {{d?.LdrInjectCount ?? 0}},
          "mainSceneDsvSet": {{B(d?.MainSceneDsvSet ?? false)}},
          "mainSceneRtvEverSeen": {{B(d?.MainSceneRtvEverSeen ?? false)}},
          "backbufferLearned": {{B(d?.BackbufferLearned ?? false)}},
          "cbkFrameCount": {{d?.CbkFrameCount ?? 0}},
          "viewProjDiag": [{{F(d?.StoredViewProj.M11 ?? 0)}}, {{F(d?.StoredViewProj.M22 ?? 0)}}, {{F(d?.StoredViewProj.M33 ?? 0)}}, {{F(d?.StoredViewProj.M44 ?? 0)}}],
          "viewProjFull": [
            {{F(d?.StoredViewProj.M11??0)}},{{F(d?.StoredViewProj.M12??0)}},{{F(d?.StoredViewProj.M13??0)}},{{F(d?.StoredViewProj.M14??0)}},
            {{F(d?.StoredViewProj.M21??0)}},{{F(d?.StoredViewProj.M22??0)}},{{F(d?.StoredViewProj.M23??0)}},{{F(d?.StoredViewProj.M24??0)}},
            {{F(d?.StoredViewProj.M31??0)}},{{F(d?.StoredViewProj.M32??0)}},{{F(d?.StoredViewProj.M33??0)}},{{F(d?.StoredViewProj.M34??0)}},
            {{F(d?.StoredViewProj.M41??0)}},{{F(d?.StoredViewProj.M42??0)}},{{F(d?.StoredViewProj.M43??0)}},{{F(d?.StoredViewProj.M44??0)}}
          ],
          "screenCenter": [{{F(d?.StoredScreen?.Center.X??0)}}, {{F(d?.StoredScreen?.Center.Y??0)}}, {{F(d?.StoredScreen?.Center.Z??0)}}],
          "screenVisible": {{B(d?.StoredScreen?.Visible ?? false)}}
        }
        """;
    }

    private string BuildVideo()
    {
        var vp = _vp;
        return $$"""
        {
          "status": {{Q(vp?.Status)}},
          "currentPath": {{Q(vp?.CurrentPath)}},
          "hasTexture": {{B(vp?.HasTexture ?? false)}},
          "isPlaying": {{B(vp?.IsPlaying ?? false)}},
          "isPaused": {{B(vp?.IsPaused ?? false)}},
          "framesDecoded": {{vp?.FramesDecoded ?? 0}},
          "positionFraction": {{F(vp?.Position ?? 0f)}},
          "timeMs": {{vp?.TimeMs ?? -1}},
          "lengthMs": {{vp?.LengthMs ?? -1}},
          "volume": {{vp?.Volume ?? 0}},
          "muted": {{B(vp?.Muted ?? false)}}
        }
        """;
    }

    private string BuildBrowser()
    {
        var bp = _bp;
        return $$"""
        {
          "status": {{Q(bp?.Status)}},
          "currentUrl": {{Q(bp?.CurrentUrl)}},
          "isInitialized": {{B(bp?.IsInitialized ?? false)}},
          "hasTexture": {{B(bp?.HasTexture ?? false)}}
        }
        """;
    }

    private string BuildSync()
    {
        var s   = _sync;
        var srv = s?.Server;
        var cli = s?.Client;
        return $$"""
        {
          "mode": {{Q(s?.Mode.ToString())}},
          "videoStatus": {{Q(s?.VideoStatus)}},
          "server": {
            "running": {{B(srv?.IsRunning ?? false)}},
            "clientCount": {{srv?.ClientCount ?? 0}},
            "publicIp": {{Q(srv?.PublicIp)}},
            "upnpStatus": {{Q(srv?.UPnPStatus)}},
            "lastError": {{Q(srv?.LastError)}}
          },
          "client": {
            "connected": {{B(cli?.IsConnected ?? false)}},
            "running": {{B(cli?.IsRunning ?? false)}},
            "status": {{Q(cli?.Status)}}
          }
        }
        """;
    }

    private string BuildConfig()
    {
        var c = _cfg;
        return $$"""
        {
          "activeMode": {{Q(c?.ActiveMode.ToString())}},
          "screenVisible": {{B(c?.Screen.Visible ?? false)}},
          "bloomCap": {{F(c?.BloomCap ?? 0f)}},
          "brightness": {{F(c?.Brightness ?? 1f)}},
          "gamma": {{F(c?.Gamma ?? 1f)}},
          "contrast": {{F(c?.Contrast ?? 1f)}},
          "syncMode": {{Q(c?.SyncMode.ToString())}},
          "syncPort": {{c?.SyncPort ?? 0}},
          "videoUrl": {{Q(c?.VideoUrl)}},
          "videoPath": {{Q(c?.VideoPath)}},
          "browserUrl": {{Q(c?.BrowserUrl)}},
          "imagePath": {{Q(c?.ImagePath)}},
          "volume": {{c?.Volume ?? 0}},
          "muted": {{B(c?.Muted ?? false)}},
          "alwaysDraw": {{B(c?.AlwaysDraw ?? false)}},
          "showBlackBacking": {{B(c?.ShowBlackBacking ?? false)}},
          "playlistCount": {{c?.Playlist?.Count ?? 0}},
          "playlistIndex": {{c?.PlaylistIndex ?? -1}}
        }
        """;
    }

    private string BuildRect()
    {
        var d      = _d3d;
        var vp     = _vp;
        var screen = d?.StoredScreen;
        var viewProj = d?.StoredViewProj ?? Matrix4x4.Identity;
        var (vpW, vpH) = d?.DeviceResolution ?? (0, 0);

        // ── Screen-space corner projection ────────────────────────────────────
        // Shader local positions for the unit quad (matches VS_SRC kPos[]):
        //   TL=(-0.5, 0.5, 0)  TR=(0.5, 0.5, 0)
        //   BL=(-0.5,-0.5, 0)  BR=(0.5,-0.5, 0)
        // Pipeline: local → ScreenTransform → world → ViewProj → clip → NDC → screen
        static (float sx, float sy, bool behind) Project(
            Vector3 local, Matrix4x4 screenTransform, Matrix4x4 vp2, int w, int h)
        {
            var world = Vector4.Transform(new Vector4(local, 1f), screenTransform);
            var clip  = Vector4.Transform(world, vp2);
            if (clip.W <= 0f) return (0f, 0f, true);       // behind camera
            float ndcX = clip.X / clip.W;
            float ndcY = clip.Y / clip.W;
            return ((ndcX + 1f) * 0.5f * w, (1f - ndcY) * 0.5f * h, false);
        }

        string CornerJson(string label, Vector3 local, Matrix4x4 st)
        {
            var (sx, sy, behind) = Project(local, st, viewProj, vpW, vpH);
            return $"{{\"label\":{Q(label)},\"x\":{sx.ToString("F1", CultureInfo.InvariantCulture)},\"y\":{sy.ToString("F1", CultureInfo.InvariantCulture)},\"behindCamera\":{B(behind)}}}";
        }

        var cornersJson = "[]";
        if (screen != null && vpW > 0)
        {
            var st = screen.ComputeScreenTransform();
            var tl = CornerJson("topLeft",     new Vector3(-0.5f,  0.5f, 0f), st);
            var tr = CornerJson("topRight",    new Vector3( 0.5f,  0.5f, 0f), st);
            var bl = CornerJson("bottomLeft",  new Vector3(-0.5f, -0.5f, 0f), st);
            var br = CornerJson("bottomRight", new Vector3( 0.5f, -0.5f, 0f), st);
            cornersJson = $"[{tl},{tr},{bl},{br}]";
        }

        // ── Color samples from CPU pixel buffer ───────────────────────────────
        // 9 canonical UV positions: corners (inset), edge midpoints, center.
        // BGRA → reported as RGBA for readability. Hex = #RRGGBB.
        static string SampleJson(string label, float u, float v, VideoPlayer? vpPlayer)
        {
            string Hex2(byte b) => b.ToString("X2");
            if (vpPlayer == null || vpPlayer.FramesDecoded == 0)
                return $"{{\"label\":{Q(label)},\"u\":{u.ToString("F2", CultureInfo.InvariantCulture)},\"v\":{v.ToString("F2", CultureInfo.InvariantCulture)},\"r\":null,\"g\":null,\"b\":null,\"a\":null,\"hex\":null}}";
            var (bVal, gVal, rVal, aVal) = vpPlayer.SamplePixelBgra(u, v);
            return $"{{\"label\":{Q(label)},\"u\":{u.ToString("F2", CultureInfo.InvariantCulture)},\"v\":{v.ToString("F2", CultureInfo.InvariantCulture)},\"r\":{rVal},\"g\":{gVal},\"b\":{bVal},\"a\":{aVal},\"hex\":\"#{Hex2(rVal)}{Hex2(gVal)}{Hex2(bVal)}\"}}";
        }

        var (texW, texH) = vp?.TextureSize ?? (0, 0);
        var activeSrvSource = d?.ActiveSrvSource ?? "null";
        // colorSamples read the CPU pixel buffer (last decoded frame).
        // They show video SOURCE content, not what is currently injected.
        // When video is stopped/null, samples show the last frame before stop.
        // Check activeSrvSource to know what is actually being rendered: "video", "gradient", "image", "browser", "black", "null".
        bool samplesReflectScreen = activeSrvSource == "video" && (vp?.FramesDecoded ?? 0) > 0;
        var samples = string.Join(",", new[]
        {
            SampleJson("center",       0.50f, 0.50f, vp),
            SampleJson("topLeft",      0.05f, 0.05f, vp),
            SampleJson("topCenter",    0.50f, 0.05f, vp),
            SampleJson("topRight",     0.95f, 0.05f, vp),
            SampleJson("midLeft",      0.05f, 0.50f, vp),
            SampleJson("midRight",     0.95f, 0.50f, vp),
            SampleJson("bottomLeft",   0.05f, 0.95f, vp),
            SampleJson("bottomCenter", 0.50f, 0.95f, vp),
            SampleJson("bottomRight",  0.95f, 0.95f, vp),
        });

        return $$"""
        {
          "viewport": {"width": {{vpW}}, "height": {{vpH}}},
          "textureSize": {"width": {{texW}}, "height": {{texH}}},
          "activeSrvSource": {{Q(activeSrvSource)}},
          "samplesReflectScreen": {{B(samplesReflectScreen)}},
          "framesDecoded": {{vp?.FramesDecoded ?? 0}},
          "screenCorners": {{cornersJson}},
          "colorSamples": [{{samples}}]
        }
        """;
    }

    private string BuildState()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
        return $$"""
        {
          "version": "{{v}}",
          "loaded": true,
          "render": {{BuildRender()}},
          "video": {{BuildVideo()}},
          "browser": {{BuildBrowser()}},
          "sync": {{BuildSync()}},
          "config": {{BuildConfig()}}
        }
        """;
    }

    // ── /inject — inject-path diagnostics ────────────────────────────────────
    // Shows which inject path fired last frame, what surface was used, its format/dimensions,
    // and exposes the BbDrawSkip and OmSetRtInjectEnabled control flags.
    //
    // Usage:
    //   curl http://localhost:17777/inject
    //   curl "http://localhost:17777/set/omsetrtenable?v=false"   # disable intermediate inject → forces BB fallback
    //   curl "http://localhost:17777/set/bbdrawskip?n=3"          # skip 3 draws on BB before injecting
    //   curl "http://localhost:17777/set/bbdrawskip?n=0"          # inject on first draw after BB bind (default)
    private string BuildInject()
    {
        var d = _d3d;
        return $$"""
        {
          "lastInjectPath":       {{Q(d?.LastInjectPath   ?? "none")}},
          "lastInjectRtvPtr":     "0x{{(d?.LastInjectRtvPtr ?? 0):X}}",
          "lastInjectFmt":        {{Q(d?.LastInjectFmt    ?? "unknown")}},
          "lastInjectSize":       {"w": {{d?.LastInjectW ?? 0}}, "h": {{d?.LastInjectH ?? 0}}},
          "lastFallbackUsed":     {{B(d?.LastFallbackUsed ?? false)}},
          "lastIntermediateGot":  "0x{{(d?.LastIntermediateGot ?? 0):X}}",
          "lastPrevNoDsvPtr":     "0x{{(d?.LastPrevNoDsvPtr ?? 0):X}}",
          "bbDrawCountLastFrame": {{d?.BbDrawCount ?? 0}},
          "bbRtvCount":           {{d?.BbRtvCount ?? 0}},
          "bbTexCount":           {{d?.BbTexCount ?? 0}},
          "targetInjectPtr":      "0x{{(d?.TargetInjectPtr ?? 0):X}}",
          "lastSeenValidPtr":     "0x{{(d?.LastSeenValidPtr ?? 0):X}}",
          "ldrInjectCount":       {{d?.LdrInjectCount ?? 0}},
          "sceneInjectCount":     {{d?.SceneInjectCount ?? 0}},
          "cfDiCount":            {{d?.CfDiCount ?? 0}},
          "cfDrawCount":          {{d?.CfDrawCount ?? 0}},
          "omSetRtCount":         {{d?.OmSetRtCount ?? 0}},
          "clearRtvInjectCount":  {{d?.ClearRtvInjectCount ?? 0}},
          "clearRtvCallCount":    {{d?.ClearRtvCallCount ?? 0}},
          "clearRtvSceneDrawn":   {{d?.ClearRtvSceneDrawnCount ?? 0}},
          "clearRtvLdrFound":     {{d?.ClearRtvLdrCount ?? 0}},
          "diBbCount":            {{d?.DiBbCount ?? 0}},
          "cfDiMissNullPtr":          {{d?.CfDiMissNullPtr ?? 0}},
          "cfDiMissNotLdr":           {{d?.CfDiMissNotLdr ?? 0}},
          "cfDiMissTargetMismatch":   {{d?.CfDiMissTargetMismatch ?? 0}},
          "omSetRtMissSceneNotDrawn": {{d?.OmSetRtMissSceneNotDrawn ?? 0}},
          "omSetRtMissInUiPassFalse": {{d?.OmSetRtMissInUiPassFalse ?? 0}},
          "omSetRtMissDrawCall":      {{d?.OmSetRtMissDrawCall ?? 0}},
          "omSetRtLdrCount":          {{d?.OmSetRtLdrCount ?? 0}},
          "copyResourceTotal":        {{d?.CopyResourceTotal ?? 0}},
          "copyResourceLdrMatch":     {{d?.CopyResourceLdrMatch ?? 0}},
          "cfCopyCount":              {{d?.CfCopyCount ?? 0}},
          "ldrTexPtr":                "0x{{(d?.LdrTexPtr ?? 0):X}}",
          "dispatchInWindow":            {{d?.DispatchInWindow ?? 0}},
          "dispatchNoUiPass":            {{d?.DispatchNoUiPass ?? 0}},
          "cfDispatchCount":             {{d?.CfDispatchCount ?? 0}},
          "dispatchIndirectInWindow":    {{d?.DispatchIndirectInWindow ?? 0}},
          "cfDispatchIndirectCount":     {{d?.CfDispatchIndirectCount ?? 0}},
          "ldrFilledByNonDraw":          {{B(d?.LdrFilledByNonDraw ?? false)}},
          "controls": {
            "cfDiEnabled":          {{B(D3DRenderer.CfDiEnabled)}},
            "cfDrawEnabled":        {{B(D3DRenderer.CfDrawEnabled)}},
            "clearRtvInjectEnabled":{{B(D3DRenderer.ClearRtvInjectEnabled)}},
            "cfDrawPreInject":      {{B(D3DRenderer.CfDrawPreInject)}},
            "cfDrawHudSkip":        {{D3DRenderer.CfDrawHudSkip}},
            "omSetRtLdrEnabled":    {{B(D3DRenderer.OmSetRtLdrEnabled)}},
            "omSetRtInjectEnabled": {{B(D3DRenderer.OmSetRtInjectEnabled)}},
            "cfDispatchEnabled":    {{B(D3DRenderer.CfDispatchEnabled)}},
            "cfDispatchSkip":       {{D3DRenderer.CfDispatchSkip}},
            "bbDrawSkip":           {{D3DRenderer.BbDrawSkip}}
          }
        }
        """;
    }

    // ── /hud — native FFXIV addon positions vs rect bounds ───────────────────
    // Returns screen-space position/size of every visible native FFXIV HUD addon,
    // computes whether each one overlaps the rect bounding box, and reports a
    // definitive pass/fail: if ANY visible addon overlaps the rect, game UI is
    // confirmed to be in the same screen region. Use with a screenshot to verify
    // whether it renders in FRONT of or BEHIND the rect.
    //
    // Usage:
    //   curl http://localhost:17777/hud
    private unsafe string BuildHud()
    {
        var d = _d3d;
        var gui = _gui;

        // Compute rect screen bounding box from projected corners.
        float rectMinX = float.MaxValue, rectMinY = float.MaxValue;
        float rectMaxX = float.MinValue, rectMaxY = float.MinValue;
        bool rectValid = false;

        var screen   = d?.StoredScreen;
        var viewProj = d?.StoredViewProj ?? Matrix4x4.Identity;
        var (vpW, vpH) = d?.DeviceResolution ?? (0, 0);

        if (screen != null && vpW > 0)
        {
            var st = screen.ComputeScreenTransform();
            Vector3[] locals = {
                new(-0.5f,  0.5f, 0f), new( 0.5f,  0.5f, 0f),
                new(-0.5f, -0.5f, 0f), new( 0.5f, -0.5f, 0f),
            };
            foreach (var loc in locals)
            {
                var world = Vector4.Transform(new Vector4(loc, 1f), st);
                var clip  = Vector4.Transform(world, viewProj);
                if (clip.W <= 0f) continue;
                float sx = (clip.X / clip.W + 1f) * 0.5f * vpW;
                float sy = (1f - clip.Y / clip.W) * 0.5f * vpH;
                if (sx < rectMinX) rectMinX = sx;
                if (sy < rectMinY) rectMinY = sy;
                if (sx > rectMaxX) rectMaxX = sx;
                if (sy > rectMaxY) rectMaxY = sy;
            }
            rectValid = rectMaxX > rectMinX && rectMaxY > rectMinY;
        }

        // Known native FFXIV HUD addon names to check.
        string[] addonNames = {
            "_HotBar", "_HotBar1", "_HotBar2", "_HotBar3", "_HotBar4",
            "_HotBar5", "_HotBar6", "_HotBar7", "_HotBar8", "_HotBar9",
            "_NaviMap", "_ParameterWidget", "_PartyList",
            "_TargetInfo", "_FocusTargetInfo", "_TargetInfoMainTarget",
            "_ChatLog", "_ChatLogPanel_0", "_ChatLogPanel_1",
            "_ExpBar", "_JobHudACN0", "_JobHudGFF0", "_JobHudWHM0",
            "_StatusCustom0316", "_ActionContents",
            // Inventory and common windows
            "Inventory", "InventoryLarge", "InventoryExpansion",
            "Character", "CharacterInspect",
            "Map", "AreaMap",
            "RecipeNote", "Synthesis",
            "SelectYesno", "Talk", "SelectString",
            "SystemMenu", "ConfigCharacter",
            "Buddy", "PvpProfile",
            "MountNoteBook", "FateProgress",
            "ArmouryBoard",
        };

        var entries     = new System.Text.StringBuilder();
        var overlapping = new System.Text.StringBuilder();
        bool anyOverlap = false;
        bool first      = true;

        if (gui != null)
        {
            foreach (var name in addonNames)
            {
                var addonWrap = gui.GetAddonByName(name);
                nint ptr = (nint)addonWrap;
                if (ptr == nint.Zero) continue;

                var addon = (AtkUnitBase*)ptr;
                if (addon == null) continue;

                bool visible  = addon->IsVisible;
                short ax      = addon->X;
                short ay      = addon->Y;
                float scale   = addon->Scale;
                int   aw      = (int)(addon->RootNode == null ? 0 : addon->RootNode->Width  * scale);
                int   ah      = (int)(addon->RootNode == null ? 0 : addon->RootNode->Height * scale);

                bool overlap = false;
                if (rectValid && visible && aw > 0 && ah > 0)
                {
                    float ax2 = ax + aw;
                    float ay2 = ay + ah;
                    overlap = ax < rectMaxX && ax2 > rectMinX && ay < rectMaxY && ay2 > rectMinY;
                }
                if (overlap) anyOverlap = true;

                if (!first) entries.Append(',');
                first = false;
                entries.Append($"{{\"name\":{Q(name)},\"visible\":{B(visible)},\"x\":{ax},\"y\":{ay},\"w\":{aw},\"h\":{ah},\"overlapsRect\":{B(overlap)}}}");

                if (overlap)
                {
                    if (overlapping.Length > 0) overlapping.Append(',');
                    overlapping.Append($"{Q(name)}");
                }
            }
        }

        string F1(float v) => v.ToString("F0", CultureInfo.InvariantCulture);
        return $$"""
        {
          "rectBounds": {
            "valid": {{B(rectValid)}},
            "minX": {{(rectValid ? F1(rectMinX) : "0")}},
            "minY": {{(rectValid ? F1(rectMinY) : "0")}},
            "maxX": {{(rectValid ? F1(rectMaxX) : "0")}},
            "maxY": {{(rectValid ? F1(rectMaxY) : "0")}}
          },
          "anyAddonOverlapsRect": {{B(anyOverlap)}},
          "overlappingAddons": [{{overlapping}}],
          "addons": [{{entries}}]
        }
        """;
    }

    // ── HTTP response ─────────────────────────────────────────────────────────

    private static void Respond(HttpListenerContext ctx, int statusCode, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode      = statusCode;
        ctx.Response.ContentType     = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }
}
