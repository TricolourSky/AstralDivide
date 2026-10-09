using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using EFT.UI;
using UnityEngine;
using UnityEngine.Rendering;

namespace AstralDivide.Client;

/// <summary>
/// GF2Render 里按相机分的事（DEV_NOTES 37.9）：
/// ① 哪台相机用战局灯光：只有战局里的主视角相机和瞄具相机（塔科夫自己的 ShadowMaskExtractor 也只认这两台）。
///    其它相机（主菜单、角色资料、选人、加载画面、战局里打开背包的预览）都用展示灯光 —— 加载地图时 GameWorld 已经建好了，按它判断会提前切成战局灯光。
/// ② 哪台是塔科夫的角色预览面板的相机（在 PlayerModelView 下面）：只用于日志。37.10 查明菜单里画她的全是这种相机（包括主菜单），
///    37.9 加的「预览面板亮度」就在 37.11 去掉了。
/// ③ 太阳的屏幕阴影（给半透明件，37.8）：照塔科夫 ShadowMaskExtractor 的做法 —— 命令缓冲挂在天空系统的太阳（TODSkyProvider）上，
///    每台战局相机画之前重录成「当场拷进这台相机自己的图 → 设成全局 → 标记 1」，别的相机录成空的。
///    F12「调试：深度对照」开着时同一时刻再拍一份深度快照（37.14 诊断：太阳阴影算的时候深度图里有没有她）。
/// ④ 每台第一次画她的相机往日志写一行（类型、渲染方式、目标、挂了哪些组件），排查不同界面为什么看着不一样。
/// </summary>
internal static class GF2Cameras
{
    private static readonly int SunMaskId = Shader.PropertyToID("_GF2SunMask");
    private static readonly int SunMaskOnId = Shader.PropertyToID("_GF2SunMaskOn");
    private static readonly int DepthSnapId = Shader.PropertyToID("_GF2DepthSnap");
    private static readonly int DepthSnapOnId = Shader.PropertyToID("_GF2DepthSnapOn");
    private static readonly Dictionary<Camera, bool> PreviewCache = new Dictionary<Camera, bool>();
    private static readonly Dictionary<Camera, RenderTexture> MaskRTs = new Dictionary<Camera, RenderTexture>();
    private static readonly Dictionary<Camera, RenderTexture> DepthRTs = new Dictionary<Camera, RenderTexture>();
    private static CommandBuffer _maskCb;
    private static Light _maskLight;

    /// 这台相机画的是战局（战局里的主视角 / 瞄具相机）
    internal static bool RaidWorld(Camera cam)
    {
        if (!Singleton<GameWorld>.Instantiated || !CameraManager.Exist)
            return false;
        CameraManager cm = CameraManager.Instance;
        return cam == cm.Camera || (cm.OpticCameraManager != null && cam == cm.OpticCameraManager.Camera);
    }

    /// 塔科夫角色预览面板（选人、角色资料、加载画面、背包）的相机：在 PlayerModelView 下面。每台相机只查一次
    internal static bool Preview(Camera cam)
    {
        if (!PreviewCache.TryGetValue(cam, out bool p))
            PreviewCache[cam] = p = cam.GetComponentInParent<PlayerModelView>(true) != null;
        return p;
    }

    /// 太阳阴影的命令缓冲挂到天空系统的太阳上（在战局里才挂；换了灯就从旧的上摘下来）。挂上时往日志写一行
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
        string others = string.Join("、", sun.GetCommandBuffers(LightEvent.AfterScreenspaceMask).Select(c => c.name));
        Plugin.Log.LogInfo($"[GF2 渲染] 太阳阴影：挂到 {PathOf(sun.transform)}（阴影 {sun.shadows}，强度 {sun.intensity:0.00}；这个时机上的命令缓冲：{others}）");
    }

    /// 每台相机画之前：先把标记清 0；战局相机把命令缓冲录成「当场拷进这台相机自己的图、设成全局、标记 1」，别的相机录成空的。
    /// snap 不是空的时候（F12「调试：深度对照」开着，37.14）同一时刻再用它的 snapPass 把深度图拷一份（_GF2DepthSnap）
    internal static void RecordSun(Camera cam, bool raidCam, Material snap, int snapPass)
    {
        Shader.SetGlobalFloat(SunMaskOnId, 0f);
        Shader.SetGlobalFloat(DepthSnapOnId, 0f);
        if (_maskCb == null)
            return;
        _maskCb.Clear();
        if (!raidCam)
            return;
        RenderTextureFormat r8 = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R8) ? RenderTextureFormat.R8 : RenderTextureFormat.ARGB32;
        RenderTexture rt = ScreenRT(MaskRTs, cam, r8, "太阳阴影");
        _maskCb.Blit(BuiltinRenderTextureType.CurrentActive, rt);
        _maskCb.SetGlobalTexture(SunMaskId, rt);
        _maskCb.SetGlobalFloat(SunMaskOnId, 1f);
        if (snap == null)
            return;
        RenderTexture d = ScreenRT(DepthRTs, cam, RenderTextureFormat.RFloat, "深度快照");
        _maskCb.Blit((Texture)null, d, snap, snapPass);
        _maskCb.SetGlobalTexture(DepthSnapId, d);
        _maskCb.SetGlobalFloat(DepthSnapOnId, 1f);
    }

    /// 这台相机自己的一张屏幕大小的图（阴影图 / 深度快照；大小变了就重建）
    private static RenderTexture ScreenRT(Dictionary<Camera, RenderTexture> cache, Camera cam, RenderTextureFormat fmt, string label)
    {
        int w = Mathf.Max(cam.pixelWidth, 1), h = Mathf.Max(cam.pixelHeight, 1);
        if (cache.TryGetValue(cam, out RenderTexture rt) && rt != null && rt.width == w && rt.height == h)
            return rt;
        if (rt != null)
            Object.Destroy(rt);
        rt = new RenderTexture(w, h, 0, fmt) { name = $"AstralDivide GF2 {label} {cam.name}" };
        cache[cam] = rt;
        return rt;
    }

    /// 一台相机第一次画她：写一行它是什么相机
    internal static void Log(Camera cam, bool raidCam, bool preview)
    {
        string kind = raidCam ? "战局" : preview ? "预览面板" : "其它（展示灯光）";
        RenderTexture t = cam.targetTexture;
        string target = t != null ? $"{t.name} {t.width}×{t.height} {t.format}" : $"屏幕 {cam.pixelWidth}×{cam.pixelHeight}";
        string comps = string.Join("、", cam.GetComponents<Behaviour>().Where(b => b != null && b.enabled && !(b is Camera)).Select(b => b.GetType().Name));
        Plugin.Log.LogInfo($"[GF2 渲染] 新相机 {PathOf(cam.transform)} | {kind} | {cam.actualRenderingPath} HDR {cam.allowHDR} | 目标 {target} | " +
                           $"视口 {cam.rect} | 层 0x{cam.cullingMask:X8} | 组件 {comps}");
    }

    /// 清掉已经销毁的相机（阴影图、深度快照一起释放）
    internal static void Cleanup()
    {
        foreach (Camera dead in PreviewCache.Keys.Where(c => c == null).ToList())
            PreviewCache.Remove(dead);
        foreach (Dictionary<Camera, RenderTexture> cache in new[] { MaskRTs, DepthRTs })
            foreach (Camera dead in cache.Keys.Where(c => c == null).ToList())
            {
                if (cache[dead] != null)
                    Object.Destroy(cache[dead]);
                cache.Remove(dead);
            }
    }

    /// 物体在场景里的路径（最多往上 6 层）
    private static string PathOf(Transform t)
    {
        var names = new List<string>();
        for (int i = 0; t != null && i < 6; i++, t = t.parent)
            names.Insert(0, t.name);
        return string.Join("/", names);
    }
}
