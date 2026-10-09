#if SORA_DEV
using System;
using EFT.CameraControl;
using EFT.EnvironmentEffect;
using UnityEngine;

namespace AstralDivide.Client;

/// <summary>
/// F12「6. 调试」里「主光」开着时的日志（DEV_NOTES 37.28；37.29 起只在开发版里有）：37.27 实测街区大楼大堂里 Unity 没给她主光（调试显示纯蓝），
/// 离线核对过 Unity 不会因为屋里灯多就把太阳挤掉 → 是那一刻太阳被关了 / 强度 0 / 层不对。这里每秒写一行：
/// 战局主视角相机剔除前（onPreCull）和开画前（onPreRender）两个时刻太阳的状态、塔科夫的室内 / 室外、像素灯数，
/// 她每个渲染器的层，头骨位置的光照探针和塔科夫天空球谐（GF2Render.DescribeForDiag）
/// </summary>
internal static class GF2Diag
{
    private static bool _hooked, _errorLogged;
    private static float _nextAt;
    private static string _atCull = "（没记到）";

    /// 每帧调：开关变了就挂上 / 摘掉相机回调（关着时一点开销都没有）
    internal static void Hook(bool on)
    {
        if (on == _hooked)
            return;
        _hooked = on;
        if (on)
        {
            Camera.onPreCull += AtCull;
            Camera.onPreRender += AtRender;
            _nextAt = 0f;
            return;
        }
        Camera.onPreCull -= AtCull;
        Camera.onPreRender -= AtRender;
    }

    private static bool MainCamera(Camera cam) => CameraManager.Exist && cam == CameraManager.Instance.Camera;

    private static void AtCull(Camera cam)
    {
        try
        {
            if (MainCamera(cam) && Time.unscaledTime >= _nextAt)
                _atCull = SunState();
        }
        catch (Exception e)
        {
            LogError(e);
        }
    }

    private static void AtRender(Camera cam)
    {
        try
        {
            if (!MainCamera(cam) || Time.unscaledTime < _nextAt)
                return;
            _nextAt = Time.unscaledTime + 1f;
            string env = EnvironmentManager.Instance != null ? EnvironmentManager.Instance.Environment.ToString() : "没有";
            Plugin.Log.LogInfo($"[GF2 渲染] 主光调试：{cam.name} | 剔除前 太阳 {_atCull} | 开画前 太阳 {SunState()} | " +
                               $"塔科夫环境 {env} | 像素灯数 {QualitySettings.pixelLightCount} | {GF2Render.DescribeForDiag()}");
        }
        catch (Exception e)
        {
            LogError(e);
        }
    }

    /// 天空系统的太阳（没有就 RenderSettings.sun）：启用、激活、强度、颜色、层掩码、渲染模式、阴影、烘焙方式；和 RenderSettings.sun 是不是同一盏
    private static string SunState()
    {
        Light sun = TODSkyProvider.IsAvailable && TODSkyProvider.Instance != null ? TODSkyProvider.Instance.LightObject : RenderSettings.sun;
        if (sun == null)
            return "没有";
        LightBakingOutput b = sun.bakingOutput;
        Color c = sun.color;
        return $"{sun.name}（启用 {sun.enabled}，激活 {sun.gameObject.activeInHierarchy}，强度 {sun.intensity:0.00}，颜色 ({c.r:0.00},{c.g:0.00},{c.b:0.00})，" +
               $"层 0x{sun.cullingMask:X8}，{sun.renderMode}，阴影 {sun.shadows}，烘焙 {b.lightmapBakeType}/{b.isBaked}，" +
               $"是 RenderSettings.sun {(RenderSettings.sun == sun ? "是" : "否")}）";
    }

    private static void LogError(Exception e)
    {
        if (!_errorLogged)
            Plugin.Log.LogError($"GF2Diag 异常（只报一次）: {e}");
        _errorLogged = true;
    }
}
#endif
