using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using UnityEngine;
using UnityEngine.Rendering;

namespace AstralDivide.Client;

/// <summary>
/// GF2Render 里按相机分的事（DEV_NOTES 37.9）：
/// ① 哪台相机用战局灯光：只有战局里的主视角相机和瞄具相机（塔科夫自己的 ShadowMaskExtractor 也只认这两台）。
///    其它相机（主菜单、角色资料、选人、加载画面、战局里打开背包的预览）都用展示灯光 —— 加载地图时 GameWorld 已经建好了，按它判断会提前切成战局灯光。
/// ② 太阳的屏幕阴影（给半透明件，37.8）：照塔科夫 ShadowMaskExtractor 的做法 —— 命令缓冲挂在天空系统的太阳（TODSkyProvider）上，
///    每台战局相机画之前重录成「当场拷进这台相机自己的图 → 设成全局 → 标记 1」，别的相机录成空的。
/// （37.10 / 37.14 排查用的「新相机」日志、深度快照 2026-11-09 删了：前者的结论已经有了，后者 37.15 实机全绿）
/// </summary>
internal static class GF2Cameras
{
    private static readonly int SunMaskId = Shader.PropertyToID("_GF2SunMask");
    private static readonly int SunMaskOnId = Shader.PropertyToID("_GF2SunMaskOn");
    private static readonly Dictionary<Camera, RenderTexture> MaskRTs = new Dictionary<Camera, RenderTexture>();
    private static CommandBuffer _maskCb;
    private static Light _maskLight;
    private static bool _formatKnown;
    private static RenderTextureFormat _maskFormat;      // 阴影图的格式：显卡支持 R8 就用 R8（第一次用时问一次，以前每台相机每帧问一次）

    /// 这台相机画的是战局（战局里的主视角 / 瞄具相机）
    internal static bool RaidWorld(Camera cam)
    {
        if (!Singleton<GameWorld>.Instantiated || !CameraManager.Exist)
            return false;
        CameraManager cm = CameraManager.Instance;
        return cam == cm.Camera || (cm.OpticCameraManager != null && cam == cm.OpticCameraManager.Camera);
    }

    /// 太阳阴影的命令缓冲挂到天空系统的太阳上（在战局里才挂；换了灯就从旧的上摘下来）
    internal static void HookSun(bool inWorld)
    {
        Light sun = inWorld && TODSkyProvider.IsAvailable && TODSkyProvider.Instance != null ? TODSkyProvider.Instance.LightObject : null;
        if (sun == _maskLight)
            return;
        if (_maskLight != null)
            _maskLight.RemoveCommandBuffer(LightEvent.AfterScreenspaceMask, _maskCb);
        _maskLight = sun;
        if (sun == null)
            return;
        _maskCb ??= new CommandBuffer { name = "AstralDivide GF2 太阳阴影" };
        sun.AddCommandBuffer(LightEvent.AfterScreenspaceMask, _maskCb);
    }

    /// 每台相机画之前：先把标记清 0；战局相机把命令缓冲录成「当场拷进这台相机自己的图、设成全局、标记 1」，别的相机录成空的
    internal static void RecordSun(Camera cam, bool raidCam)
    {
        Shader.SetGlobalFloat(SunMaskOnId, 0f);
        if (_maskCb == null)
            return;
        _maskCb.Clear();
        if (!raidCam)
            return;
        RenderTexture rt = ScreenRT(cam);
        _maskCb.Blit(BuiltinRenderTextureType.CurrentActive, rt);
        _maskCb.SetGlobalTexture(SunMaskId, rt);
        _maskCb.SetGlobalFloat(SunMaskOnId, 1f);
    }

    /// 这台相机自己的一张屏幕大小的阴影图（大小变了就重建）
    private static RenderTexture ScreenRT(Camera cam)
    {
        if (!_formatKnown)
        {
            _maskFormat = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R8) ? RenderTextureFormat.R8 : RenderTextureFormat.ARGB32;
            _formatKnown = true;
        }
        int w = Mathf.Max(cam.pixelWidth, 1), h = Mathf.Max(cam.pixelHeight, 1);
        if (MaskRTs.TryGetValue(cam, out RenderTexture rt) && rt != null && rt.width == w && rt.height == h)
            return rt;
        if (rt != null)
            Object.Destroy(rt);
        rt = new RenderTexture(w, h, 0, _maskFormat) { name = $"AstralDivide GF2 太阳阴影 {cam.name}" };
        MaskRTs[cam] = rt;
        return rt;
    }

    /// 清掉已经销毁的相机（阴影图一起释放）
    internal static void Cleanup() => GF2Render.Prune(MaskRTs, Release);

    /// 场上没有少前 2 的渲染器了（GF2Render.Idle）：太阳上的命令缓冲摘掉、各相机的阴影图释放、标记清 0
    internal static void Idle()
    {
        HookSun(false);
        foreach (RenderTexture rt in MaskRTs.Values.ToList())
            Release(rt);
        MaskRTs.Clear();
        Shader.SetGlobalFloat(SunMaskOnId, 0f);
    }

    private static void Release(RenderTexture rt)
    {
        if (rt != null)
            Object.Destroy(rt);
    }
}
