using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using PyonPix.Config;
using PyonPix.Services.Core;
using PyonPix.Shared.Structs.Pix;
using PyonPix.Shared.Structs.Pix.Properties;
using PyonPix.Shared.Utility;
using PyonPix.Structs.Light;

namespace PyonPix.Services.Game;

// Reference: Ktisis.Scene.Modules.Lights.LightSpawner

public class LightService(Configuration config, IServiceContext services) : BaseService(config, services) {
    private PixService PixService => Services.Get<PixService>();

    [Signature("E8 ?? ?? ?? ?? 48 89 84 FB ?? ?? ?? ?? 48 85 C0 0F 84 ?? ?? ?? ?? 48 8B C8")]
    private SceneLightCtorDelegate _sceneLightCtor = null!;
    private unsafe delegate SceneLight* SceneLightCtorDelegate(SceneLight* self);

    [Signature("E8 ?? ?? ?? ?? 48 8B 94 FB ?? ?? ?? ?? 48 8B 0D ?? ?? ?? ??")]
    private SceneLightInitializeDelegate _sceneLightInit = null!;
    private unsafe delegate bool SceneLightInitializeDelegate(SceneLight* self);

    [Signature("F6 41 38 01")]
    private SceneLightSetupDelegate _sceneLightSpawn = null!;
    private unsafe delegate nint SceneLightSetupDelegate(SceneLight* self);

    private unsafe delegate void CleanupRenderDelegate(SceneLight* light);
    private unsafe delegate void DestructorDelegate(SceneLight* light, bool a2);

    private readonly Dictionary<string, Light> Lights = [];

    private const int MaxHistorySamples = 64; // 60 / 1000 * 64 = 1066 samples/sec

    public override Task Initialize() {
        Services.GameInteropProvider.InitializeFromAttributes(this);

        PixService.PixSpawned += OnPixSpawned;
        PixService.PixUpdated += OnPixUpdated;
        PixService.PixDespawned += OnPixDespawned;
        PixService.AllPixDespawned += OnAllPixDespawned;
        return Task.CompletedTask;
    }

    private void OnPixSpawned(IPix pix, bool isUserAction) => Spawn(pix);
    private void OnPixUpdated(PixUpdate u) {
        if(u.Pix == null || !PixService.IsSpawned(u.Pix)) return;
        bool lightSpawned = Lights.ContainsKey(u.Pix.Id);
        bool lightEnabled = u.Pix.Light.Enabled;

        switch(u.Type) {
            case PixUpdateType.All or PixUpdateType.LightTransform or PixUpdateType.LightProperties or PixUpdateType.RendererTransform:
                if(lightEnabled && !lightSpawned) {
                    Spawn(u.Pix);
                } else if(!lightEnabled && lightSpawned) {
                    Despawn(u.Pix);
                } else if(lightSpawned) {
                    Update(u.Pix);
                }
                break;
        }
    }
    private void OnPixDespawned(IPix pix, bool isUserAction) => Despawn(pix);
    private void OnAllPixDespawned() { } // don't need

    private unsafe void Spawn(IPix p) {
        if(!p.Light.Enabled) return;
        if(Lights.TryGetValue(p.Id, out var l) && l.Address != nint.Zero) return;

        var light = (SceneLight*)IMemorySpace.GetDefaultSpace()->Malloc<SceneLight>();
        this._sceneLightCtor(light);
        this._sceneLightInit(light);
        this._sceneLightSpawn(light);

        *(ulong*)((nint)light + 56) |= 2u;
        Lights[p.Id] = new() {
            Address = (nint)light,
            ScreenAverage = null,
            History = new Vector3[MaxHistorySamples],
            HistoryTicks = new long[MaxHistorySamples],
            HistoryCount = 0,
            HistoryIndex = 0,
            LastTimestamp = 0
        };

        Update(p);
    }

    public void UpdateById(string pixId, Vector3? screenAvg = null) {
        if(!PixService.SpawnedPixs.TryGetValue(pixId, out IPix? p)) return;
        Update(p, screenAvg);
    }

    public unsafe void Update(IPix? p, Vector3? screenAvg = null) {
        if(p == null || !p.Light.Enabled) return;
        if(!Lights.TryGetValue(p.Id, out var l) || l.Address == nint.Zero) return;

        var light = (SceneLight*)l.Address;
        var lightProps = p.Light;
        
        if(screenAvg != null) {
            Lights[p.Id] = ComputeTemporalAccumulation(l, screenAvg.Value);
        }

        var rendererPos = p.Renderer.Position;
        var rendererRot = p.Renderer.Rotation;
        var worldPos = Vector3.Transform(lightProps.Position, rendererRot) + rendererPos;
        var worldRot = Quaternion.Normalize(Quaternion.Multiply(rendererRot, lightProps.Rotation));

        light->Transform.Position = worldPos;
        light->Transform.Rotation = worldRot;

        var render = light->RenderLight;
        if(render != null) {
            render->Flags = lightProps.Flags;
            render->LightType = lightProps.LightType;
            render->Transform = &light->Transform;

            render->Color = CalculateColour(lightProps, l.ScreenAverage);
            render->Range = lightProps.Range;

            render->LightAngle = lightProps.LightAngle;

            render->FalloffType = lightProps.FalloffType;
            render->FalloffAngle = lightProps.FalloffAngle;
            render->Falloff = lightProps.FalloffPower;

            render->CharaShadowRange = lightProps.ShadowRange;
            render->ShadowNear = lightProps.ShadowNear;
            render->ShadowFar = lightProps.ShadowFar;
        }
    }

    private Light ComputeTemporalAccumulation(Light l, Vector3 sample) {
        var globalLightProps = Config.Global.Light;

        var smoothing = Math.Clamp(globalLightProps.InfluenceSmoothing, 0f, 1f);
        var maxWindowSeconds = MathF.Max(0.01f, globalLightProps.InfluenceSmoothingDuration);
        var windowSeconds = MathUtil.Lerp(0f, maxWindowSeconds, smoothing);
        var now = Stopwatch.GetTimestamp();
        var windowTicks = (long)(windowSeconds * Stopwatch.Frequency);

        var writeIdx = l.HistoryIndex % MaxHistorySamples;
        l.History[writeIdx] = sample;
        l.HistoryTicks[writeIdx] = now;
        l.HistoryIndex = (writeIdx + 1) % MaxHistorySamples;
        if(l.HistoryCount < MaxHistorySamples) l.HistoryCount++;

        Vector3 windowAverage;
        if(smoothing <= 0f || windowSeconds <= 0f) {
            windowAverage = sample;
        } else {
            var accum = Vector3.Zero;
            var used = 0;
            var cutoff = now - windowTicks;
            for(var i = 0; i < l.HistoryCount; i++) {
                var idx = (writeIdx - 1 - i + MaxHistorySamples) % MaxHistorySamples;
                var ts = l.HistoryTicks[idx];
                if(ts == 0) continue;
                if(ts < cutoff) break;
                accum += l.History[idx];
                used++;
            }
            windowAverage = used == 0 ? sample : accum / used;
        }

        var dt = l.LastTimestamp == 0 ? 0f : MathUtil.TicksToSeconds(now - l.LastTimestamp);
        dt = MathF.Min(dt, 0.2f);
        var maxTau = MathF.Max(0.1f, globalLightProps.InfluenceSmoothingDuration);
        var tau = MathUtil.Lerp(0.01f, maxTau, smoothing);

        Vector3 smoothed;
        if(dt <= 0f || smoothing <= 0f) {
            smoothed = windowAverage;
        } else {
            var alpha = 1f - MathF.Exp(-dt / tau);
            alpha = MathF.Max(alpha, 0.0001f);
            var prev = l.ScreenAverage ?? windowAverage;
            smoothed = Vector3.Lerp(prev, windowAverage, alpha);
        }

        l.ScreenAverage = smoothed;
        l.LastTimestamp = now;
        return l;
    }

    private ColorHDR CalculateColour(LightPixProperties props, Vector3? screenAverageLinear = null) {
        Vector3 finalRgb;
        var finalIntensity = props.Intensity;

        if(props.ScreenColourInfluence > 0f && screenAverageLinear.HasValue) {
            var influence = Math.Clamp(props.ScreenColourInfluence, 0f, 1f);
            var pixRgb = new Vector3(props.Colour.X, props.Colour.Y, props.Colour.Z);

            var screenRgb = screenAverageLinear.Value;

            screenRgb = new Vector3(
                MathF.Pow(screenRgb.X, props.InfluenceGammaCurve),
                MathF.Pow(screenRgb.Y, props.InfluenceGammaCurve),
                MathF.Pow(screenRgb.Z, props.InfluenceGammaCurve)
            );

            screenRgb *= props.InfluenceColourIntensity;
            screenRgb = Vector3.Min(screenRgb, Vector3.One * 4f);

            finalRgb = Vector3.Lerp(pixRgb, screenRgb, influence);

            finalIntensity *= props.InfluenceBrightnessIntensity;
        } else {
            finalRgb = new Vector3(props.Colour.X, props.Colour.Y, props.Colour.Z);
        }

        return new ColorHDR(new Vector4(finalRgb, props.Colour.W), finalIntensity);
    }

    private unsafe void Despawn(IPix p) {
        if(!Lights.TryGetValue(p.Id, out var l) || l.Address == nint.Zero) return;

        Lights.Remove(p.Id);
        Services.Framework.RunOnFrameworkThread(() => {
            InvokeDtor((SceneLight*)l.Address);
        });
    }

    private unsafe void DespawnAll() {
        if(Services.Framework.IsFrameworkUnloading) return;
        Services.Framework.RunOnFrameworkThread(() => {
            foreach(var l in Lights) {
                if(l.Value.Address == nint.Zero) continue;
                InvokeDtor((SceneLight*)l.Value.Address);
            }
            Lights.Clear();
        });
    }

    private unsafe void InvokeDtor(SceneLight* light) {
        GetVirtualFunc<CleanupRenderDelegate>(light, 1)(light);
        GetVirtualFunc<DestructorDelegate>(light, 0)(light, false);
    }

    private unsafe static T GetVirtualFunc<T>(SceneLight* light, int index) => Marshal.GetDelegateForFunctionPointer<T>(light->_vf[index]);

    public override Task Dispose() {
        PixService.PixSpawned -= OnPixSpawned;
        PixService.PixUpdated -= OnPixUpdated;
        PixService.PixDespawned -= OnPixDespawned;
        PixService.AllPixDespawned -= OnAllPixDespawned;

        DespawnAll();
        return Task.CompletedTask;
    }
}
