using System.Collections.Generic;
using EFT.Visual;
using UnityEngine;

namespace AstralDivide.Client;

/// <summary>
/// 二阶弹簧（y'' 跟着目标走；和 scratchpad 里出预览视频的那份 Python 同一个式子）：f 越低越慢；阻尼 1 = 刚好不冲过头，&lt;1 = 轻微冲过再回稳。
/// 主菜单挂机表情（<see cref="MenuIdleFace"/>）和战局表情（<see cref="RaidFace"/>）共用。
/// Aim 换目标时可以顺便换速度，但保留当前位置和速度，所以中途换挡不会跳；Seek 只换目标。
/// </summary>
internal sealed class FaceSpring
{
    private readonly float _f, _z;
    private float _k1, _k2;
    private float _target;
    private float _v;

    internal float Y { get; private set; }

    internal FaceSpring(float f, float z)
    {
        _f = f;
        _z = z;
        Aim(0f, 1f);
    }

    internal bool Resting => _target == 0f && Mathf.Abs(Y) < 0.05f && Mathf.Abs(_v) < 0.05f;

    internal void Seek(float target) => _target = target;

    internal void Aim(float target, float speed)
    {
        _target = target;
        float w = 2f * Mathf.PI * _f * speed;
        _k1 = 2f * _z / w;
        _k2 = 1f / (w * w);
    }

    internal void Reset()
    {
        Aim(0f, 1f);
        Y = _v = 0f;
    }

    /// offset = 叠在目标上的微动。k2 下限是半隐式欧拉的稳定条件（步长再大也不会炸）。
    internal void Step(float dt, float offset)
    {
        float k2 = Mathf.Max(_k2, Mathf.Max(dt * dt / 2f + dt * _k1 / 2f, dt * _k1));
        Y += dt * _v;
        _v += dt * (_target + offset - Y - _k1 * _v) / k2;
    }
}

/// <summary>
/// 眨眼的间隔和曲线：MmdBlink 的随机眨眼、主菜单挂机表情、战局表情共用（以前两条曲线、三处间隔各写各的，2026-11-09 并成一份）。
/// MmdBlink 自己那条匀速开合的曲线（主菜单 / 藏身处的随机眨眼）不在这里，没改。
/// </summary>
internal static class FaceBlink
{
    internal const float GapMin = 2.5f, GapMax = 6f;     // 随机隔 2.5~6 秒眨一次

    /// 0~1：闭眼 u² 加速合上 → 停一下 → 睁眼 (1-v)² 减速睁开；u = 这次眨眼开始后过了几秒。slow = 濒死眨得更慢更重（战局表情）
    internal static float Curve(float u, bool slow = false)
    {
        float close = slow ? 0.12f : 0.08f, hold = slow ? 0.06f : 0.04f, open = slow ? 0.32f : 0.16f;
        if (u < 0f || u >= close + hold + open)
            return 0f;
        if (u < close)
            return (u / close) * (u / close);
        if (u < close + hold)
            return 1f;
        float v = 1f - (u - close - hold) / open;
        return v * v;
    }
}

/// <summary>
/// 一组形态键（按名字在头部所有 LOD 的网格里找）；Set 数值变了才写（第一次一定写）。找不到的名字打一条警告，那一路就空着。
/// 眨眼、主菜单挂机表情、战局表情都用它（以前挂机表情和眨眼各手抄了一份，2026-11-09 并成一份）。
/// </summary>
internal sealed class FaceMorphs
{
    private readonly List<KeyValuePair<SkinnedMeshRenderer, int>>[] _targets;
    private readonly float[] _applied;

    internal FaceMorphs(LoddedSkin lodded, string[] names, string who, bool warnMissing = true)
    {
        _targets = new List<KeyValuePair<SkinnedMeshRenderer, int>>[names.Length];
        _applied = new float[names.Length];
        for (int c = 0; c < names.Length; c++)
        {
            _targets[c] = new List<KeyValuePair<SkinnedMeshRenderer, int>>();
            _applied[c] = -1f;                              // 还没写过：第一次 Set 一定写（同一个头重新挂上时，别信它上一次的值）
        }
        foreach (SkinnedMeshRenderer smr in lodded.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            Mesh mesh = smr.sharedMesh;
            for (int c = 0; mesh != null && c < names.Length; c++)
            {
                int index = mesh.GetBlendShapeIndex(names[c]);
                if (index >= 0)
                    _targets[c].Add(new KeyValuePair<SkinnedMeshRenderer, int>(smr, index));
            }
        }
        for (int c = 0; c < names.Length; c++)
            if (_targets[c].Count == 0 && warnMissing)
                LogOnce.Warn($"[face] {who}: shape key \"{names[c]}\" not found, that channel stays empty");
    }

    /// 这一路在头上找到形态键没有（37.32：战局表情按「头上有哪些键」决定挂不挂）
    internal bool Has(int c) => _targets[c].Count > 0;

    internal void Set(int c, float weight)
    {
        float w = Mathf.Clamp(weight, 0f, 100f);
        if (Mathf.Abs(w - _applied[c]) < 0.01f)
            return;
        _applied[c] = w;
        foreach (KeyValuePair<SkinnedMeshRenderer, int> t in _targets[c])
            if (t.Key != null)
                t.Key.SetBlendShapeWeight(t.Value, w);
    }
}
