using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace AstralDivide.Client;

/// <summary>
/// 她的环境光球谐（DEV_NOTES 37.28 / 37.28c），插件每帧按角色在头骨位置算一份，按 unity_SH* 的打包写进渲染器的属性块 _GF2ProbeSH*、_GF2ProbeOn = 1，
/// shader 在「塔科夫」环境光下用它（+ 塔科夫天空球谐 × 室内压暗）。各部位、各遍（含命令缓冲画的脸重画、透过刘海看眼睛）用同一份。
/// ① 光照探针：塔科夫自己的角色在延迟 Pass 里用 Unity 按物体插值的光照探针当环境光（街区这张图取出来是 0）。
/// ② 「不重要」的灯（renderMode = ForceVertex 的点光 / 聚光）：塔科夫自己的东西走延迟渲染，照样被它们逐像素照亮；前向的她，
///    Unity 只给最近 4 盏当逐顶点灯（我们的 shader 不算）、其余折进她的 unity_SH*（命令缓冲画的那几遍又读不到）→ 屋里的灯对她基本全丢。
///    这里把它们全部折进球谐，按塔科夫的东西被同一盏灯逐像素照亮的样子（37.28d）：
///    衰减 = Unity 逐像素的（PixelAtten / SpotCone，离线量过），在 gamma 里乘到灯色上再转线性；球谐权重 0.25（AddDirectionalLight 在正对灯的方向上
///    给的是权重的 2 倍）→ 她正对着灯的那面，乘上 F12 环境光强度 2、转回 gamma 以后 = 塔科夫的物体被这盏灯照的亮度（底色 × 灯色 × 强度 × 衰减）。
///    37.28c 照抄的是 Unity 给前向物体折球谐的算法（聚光锥里全亮、锥外立刻 0，点光到范围边上一下就没，衰减在线性里乘）→
///    走出灯锥一下变黑、离灯中等距离亮好几倍发白
/// </summary>
internal static class GF2Probe
{
    private static readonly int[] ShaIds =
        { Shader.PropertyToID("_GF2ProbeSHAr"), Shader.PropertyToID("_GF2ProbeSHAg"), Shader.PropertyToID("_GF2ProbeSHAb") };
    private static readonly int[] ShbIds =
        { Shader.PropertyToID("_GF2ProbeSHBr"), Shader.PropertyToID("_GF2ProbeSHBg"), Shader.PropertyToID("_GF2ProbeSHBb") };
    private static readonly int ShcId = Shader.PropertyToID("_GF2ProbeSHC");
    private static readonly int OnId = Shader.PropertyToID("_GF2ProbeOn");
    private static Light[] _lamps = new Light[0];      // 场景里「不重要」的点光 / 聚光（每 2 秒重列一次）
    private static float _nextScan;
    private static bool _errorLogged;

    /// 每 2 秒重新列一遍场景里的「不重要」点光 / 聚光（开关、亮度每帧现读，塔科夫的灯具会按距离调亮度）
    internal static void Rescan()
    {
        if (Time.unscaledTime < _nextScan)
            return;
        _nextScan = Time.unscaledTime + 2f;
        _lamps = Object.FindObjectsOfType<Light>()
            .Where(l => l.renderMode == LightRenderMode.ForceVertex && (l.type == LightType.Point || l.type == LightType.Spot)).ToArray();
    }

    /// 这个位置的环境光球谐：光照探针 + 附近的「不重要」灯（layer = 她渲染器的层，灯的层掩码要包含它）。返回加了几盏灯，出错返回 -1（只报一次）
    internal static int Sample(Vector3 pos, Renderer r, int layer, out SphericalHarmonicsL2 sh)
    {
        try
        {
            LightProbes.GetInterpolatedProbe(pos, r, out sh);
            return AddLamps(ref sh, pos, layer);
        }
        catch (Exception e)
        {
            if (!_errorLogged)
                Plugin.Log.LogError($"[GF2 渲染] 算环境光球谐失败（只报一次）: {e}");
            _errorLogged = true;
            sh = default;
            return -1;
        }
    }

    private static int AddLamps(ref SphericalHarmonicsL2 sh, Vector3 pos, int layer)
    {
        int n = 0;
        foreach (Light l in _lamps)
        {
            if (!Affects(l, pos, layer, out Vector3 to, out float k))
                continue;
            float a = PixelAtten(k) * (l.type == LightType.Spot ? SpotCone(l, -to) : 1f);
            if (a <= 0f)
                continue;
            sh.AddDirectionalLight(to.normalized, (l.color * (l.intensity * a)).linear, 0.25f);
            n++;
        }
        return n;
    }

    /// 这盏灯可能照得到这个位置：开着、有亮度、层对、在范围里（聚光的锥由 SpotCone 管）。to = 指向灯，k = (距离 / 范围)²
    private static bool Affects(Light l, Vector3 pos, int layer, out Vector3 to, out float k)
    {
        to = default;
        k = 0f;
        if (l == null || !l.isActiveAndEnabled || l.intensity <= 0f || (l.cullingMask & (1 << layer)) == 0)
            return false;
        to = l.transform.position - pos;
        k = to.sqrMagnitude / (l.range * l.range);
        return k < 1f && to.sqrMagnitude >= 1e-6f;
    }

    /// Unity 逐像素点光 / 聚光的距离衰减（离线量过，差 < 1%）：1 / (1 + 25k)，k = (距离 / 范围)²；从范围的 80%（k = 0.64）起线性淡到范围边上为 0
    private static float PixelAtten(float k)
    {
        float atten = 1f / (1f + 25f * k);
        return k > 0.64f ? atten * (1f - (k - 0.64f) / 0.36f) : atten;
    }

    /// Unity 默认聚光的锥边（离线量过）：r = tan(偏离角) / tan(半锥角)，r ≤ 0.65 全亮，往外平滑淡到锥边（r = 1）为 0；fromLight = 灯指向她
    private static float SpotCone(Light l, Vector3 fromLight)
    {
        float angle = Vector3.Angle(l.transform.forward, fromLight);
        if (angle >= 89f)
            return 0f;
        float r = Mathf.Tan(angle * Mathf.Deg2Rad) / Mathf.Tan(l.spotAngle * 0.5f * Mathf.Deg2Rad);
        float t = Mathf.Clamp01((1f - r) / 0.35f);
        return t * t * (3f - 2f * t);
    }

    /// 写进属性块：打包和 unity_SH* 一样（37.5 离线核对过，同一组球谐走这条和走 Unity 的逐像素一样）
    internal static void Write(MaterialPropertyBlock block, SphericalHarmonicsL2 sh)
    {
        for (int c = 0; c < 3; c++)
        {
            block.SetVector(ShaIds[c], new Vector4(sh[c, 3], sh[c, 1], sh[c, 2], sh[c, 0] - sh[c, 6]));
            block.SetVector(ShbIds[c], new Vector4(sh[c, 4], sh[c, 5], sh[c, 6] * 3f, sh[c, 7]));
        }
        block.SetVector(ShcId, new Vector4(sh[0, 8], sh[1, 8], sh[2, 8], 1f));
        block.SetFloat(OnId, 1f);
    }

    /// 球谐在方向 n 上的值（线性，日志用）
    internal static Vector3 Eval(SphericalHarmonicsL2 sh, Vector3 n)
    {
        var c = new Color[1];
        sh.Evaluate(new[] { n }, c);
        return new Vector3(c[0].r, c[0].g, c[0].b);
    }

    /// 主光调试日志：这个位置附近（范围内）所有开着的点光 / 聚光，近的在前，最多 10 盏：名字、类型、渲染模式、强度、范围、距离
    internal static string DescribeLights(Vector3 pos)
    {
        IEnumerable<string> rows = Object.FindObjectsOfType<Light>()
            .Where(l => l.isActiveAndEnabled && l.type != LightType.Directional && (l.transform.position - pos).sqrMagnitude < l.range * l.range)
            .OrderBy(l => (l.transform.position - pos).sqrMagnitude).Take(10)
            .Select(l => $"{l.name}（{l.type} {l.renderMode} 强度 {l.intensity:0.00} 范围 {l.range:0.0} 距离 {(l.transform.position - pos).magnitude:0.0}）");
        return string.Join("、", rows);
    }
}
