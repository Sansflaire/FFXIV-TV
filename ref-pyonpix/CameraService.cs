using System.Numerics;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using SceneCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera;
using RenderCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Camera;

namespace PyonPix.Services.Game;

public static class CameraService {
    public unsafe static Camera* GetGameCamera() {
        var manager = CameraManager.Instance();
        return manager != null ? manager->GetActiveCamera() : null;
    }

    public unsafe static SceneCamera* GetSceneCamera() {
        var cam = GetGameCamera();
        return cam != null ? &cam->CameraBase.SceneCamera : null;
    }

    public unsafe static RenderCamera* GetRenderCamera() {
        var cam = GetSceneCamera();
        return cam != null ? cam->RenderCamera : null;
    }

    public unsafe static Matrix4x4 GetProjectionMatrixForGizmo() {
        var camera = GetRenderCamera();
        if(camera == null)
            return Matrix4x4.Identity;

        var p = camera->ProjectionMatrix * 1;

        var far = camera->FarPlane;
        var near = camera->NearPlane;
        var clip = far / (far - near);
        p.M43 = -(clip * near);
        p.M33 = -((far + near) / (far - near));

        return p;
    }

    public unsafe static Matrix4x4 GetProjectionMatrix() {
        var camera = GetRenderCamera();

        return camera == null ? Matrix4x4.Identity : (Matrix4x4)camera->ProjectionMatrix;
    }

    public unsafe static Matrix4x4 GetViewMatrix() {
        var camera = GetSceneCamera();
        if(camera == null) return Matrix4x4.Identity;
        var view = camera->ViewMatrix;
        view = view with { M44 = 1.0f };
        return view;
    }
}
