using System.Collections.Generic;
using UnityEngine;

namespace AstralDivide.Client;

/// <summary>
/// 「量尺寸」的那一步：在**服装 prefab 的静止姿态**下，把 PMX 里的刚体坐标换算成
/// 「相对某根骨的固定偏移」。量完之后就和姿势无关了 —— 角色怎么动，刚体都能跟着骨走。
///
/// 为什么必须在静止姿态量：`SetSkin` 跑的时候玩家正摆着动画姿势，直接拿玩家骨去量会把
/// 那一瞬间的姿势永久烘进去（DEV_NOTES 十「挂链姿态」那条坑就是这么踩的）。
/// 服装 prefab 自己带一份完整的骨架副本，它永远是静止的，所以一切测量都对它做。
///
/// 三条路，各走各的：
///   1. **物理骨上的刚体**（头发/裙子/外套）→ 按 `attachTo` 分组做点云拟合。
///      这批骨是从 MMD 原样搬进 blend 的，所以拟合是精确的（贝丝蒂实测残差 0.00~0.30 mm）。
///   2. **躯干上的骨骼追随碰撞体**（头/脖/胸/胯）→ 用**合并后的躯干链组**拟合。
///      躯干在对齐时没被转过，头发骨和躯干碰撞体在 PMX 里属于同一个刚性块，所以通用。
///   3. **四肢上的骨骼追随碰撞体**（上臂/前臂/手掌/大腿/小腿/脚）→ **按 EFT 骨自身的几何重建**。
///
/// ⚠️ 第 3 条为什么不能也用拟合：**MMD 骨架和 EFT 骨架是两副独立设计的骨架**，
/// 同名骨的原点本来就不重合（DEV_NOTES 十八：MMD 下半身 z=1.16 vs EFT Pelvis z=0.955）。
/// 拿「MMD 骨位置 ↔ EFT 骨位置」去做刚体拟合，前提根本不成立 ——
/// 2026-08-23 首次实机实测残差 torso 77 mm / arm 80 mm / leg 42 mm，就是这么来的。
/// 四肢碰撞体全是**胶囊**，绕自身轴的旋转不影响碰撞，所以只要把它放在
/// 「EFT 骨段上的同一个相对位置」就够了。贝丝蒂实测：手臂/前臂/锁骨/脖子的
/// 垂直骨轴分量是 0.0~0.9 mm，最大的手掌也只有 23 mm。
/// </summary>
internal class MmdSurvey
{
    private class Rest
    {
        internal Transform Tf;              // 运行时那根骨（物理骨=prefab 自己的；EFT 骨=玩家骨架的）
        internal Transform RestTf;          // prefab 里那份静止副本（EFT 骨用，四肢重建要读它的层级）
        internal Vector3 Pos;               // prefab 静止姿态下的世界位置
        internal Quaternion Rot;
    }

    /// 躯干刚性块：对齐时这一整块没被转过，所以挂在这些骨上的链可以合并成一个拟合。
    private static readonly string[] TorsoBones =
        { "Pelvis", "Spine", "Ribcage", "Neck", "Head", "Collarbone" };

    internal const string TorsoKey = "#torso";

    private static readonly HashSet<string> _reported = new HashSet<string>();

    private readonly MmdModel _m;
    private readonly Dictionary<string, Rest> _phys = new Dictionary<string, Rest>();
    private readonly Dictionary<string, Rest> _eft = new Dictionary<string, Rest>();
    private readonly Dictionary<string, string> _boneGroup = new Dictionary<string, string>();
    private readonly Dictionary<string, List<string>> _groupBones = new Dictionary<string, List<string>>();
    private readonly Dictionary<string, MmdFit> _fits = new Dictionary<string, MmdFit>();

    private readonly List<string> _limbTurn = new List<string>();
    private float _scaleFix = 1f;

    /// 1 个 PMX 单位 = 多少米（玩家空间）。以前硬编码 0.08，现在是从骨骼点云量出来的。
    internal float MetersPerUnit { get; private set; } = 0.08f;

    internal MmdSurvey(MmdModel model) => _m = model;

    internal static bool IsTorso(string eftBone)
    {
        if (string.IsNullOrEmpty(eftBone))
            return false;
        foreach (string t in TorsoBones)
            if (eftBone.IndexOf(t, System.StringComparison.Ordinal) >= 0)
                return true;
        return false;
    }

    /// 登记一条链：`groupKey` 用链的 attachTo（挂同一根 EFT 骨的链共享一个拟合）。
    internal void AddChain(string groupKey, IList<string> names, Transform[] tfs)
    {
        for (int i = 0; i < names.Count && i < tfs.Length; i++)
        {
            if (tfs[i] == null || _phys.ContainsKey(names[i]))
                continue;
            _phys[names[i]] = new Rest { Tf = tfs[i], Pos = tfs[i].position, Rot = tfs[i].rotation };
            _boneGroup[names[i]] = groupKey;
            Add(groupKey, names[i]);
            // 躯干块的链额外并进一个大拟合：点更多、更稳，而且给躯干碰撞体用
            if (IsTorso(groupKey))
                Add(TorsoKey, names[i]);
        }
    }

    private void Add(string key, string bone)
    {
        if (!_groupBones.TryGetValue(key, out List<string> list))
            _groupBones[key] = list = new List<string>();
        list.Add(bone);
    }

    /// 登记一根 EFT 骨：`runtime` 是玩家骨架上的，`rest` 是 prefab 里那份静止副本。
    internal void AddEft(string name, Transform runtime, Transform rest)
    {
        if (runtime == null || rest == null || _eft.ContainsKey(name))
            return;
        _eft[name] = new Rest { Tf = runtime, RestTf = rest, Pos = rest.position, Rot = rest.rotation };
        float a = Mathf.Abs(rest.lossyScale.x), b = Mathf.Abs(runtime.lossyScale.x);
        if (a > 1e-4f && b > 1e-4f)
            _scaleFix = b / a;
    }

    /// 把所有拟合解出来。返回 false = 一组都解不出来，调用方直接放弃建物理。
    internal bool Solve(string bundleKey)
    {
        bool loud = _reported.Add(bundleKey);       // 同一个 bundle 只详细报告一次，换装时别刷屏
        foreach (KeyValuePair<string, List<string>> g in _groupBones)
        {
            var src = new List<Vector3>();
            var dst = new List<Vector3>();
            foreach (string n in g.Value)
                if (_m.BoneByName.TryGetValue(n, out int bi) && _phys.TryGetValue(n, out Rest r))
                {
                    src.Add(_m.BonePos(bi));
                    dst.Add(r.Pos);
                }
            _fits[g.Key] = MmdFit.Solve(src, dst);
            Report(bundleKey, g.Key, _fits[g.Key], loud);
        }

        // 尺度只认**链组**拟合 —— 它们的点是同一批骨原样搬过去的，残差 0 才有资格定尺度。
        var scales = new List<float>();
        foreach (MmdFit f in _fits.Values)
            if (f.Usable && f.Rms < 0.01f)
                scales.Add(f.Scale);
        if (scales.Count == 0)
        {
            Plugin.Log.LogError($"[mmd] {bundleKey}: 没有一个可信的链组拟合，本部位不建物理");
            return false;
        }
        scales.Sort();
        MetersPerUnit = scales[scales.Count / 2] * _scaleFix;
        if (Mathf.Abs(_scaleFix - 1f) > 1e-3f)
            Plugin.Log.LogWarning($"[mmd] {bundleKey}: prefab 与玩家骨架缩放不一致（×{_scaleFix:F4}），已补偿");
        if (loud)
            Plugin.Log.LogInfo($"[mmd] {bundleKey}: 1 PMX 单位 = {MetersPerUnit * 1000f:F2} mm"
                               + $"（{scales.Count} 个可信链组量出来的，PMX 参考值 {_m.scale * 1000.0:F0} mm）");
        return true;
    }

    private void Report(string key, string label, MmdFit fit, bool loud)
    {
        if (!fit.Usable)
        {
            // 单链的骨天生共线（贝丝蒂的尾巴就是），这是**正确判断**不是故障：
            // 挂在躯干上的链会自动退到 #torso 大拟合，照样建得起来。
            bool rescued = IsTorso(label);
            string tail = rescued ? "—— 已退到躯干大拟合，不影响" : "—— 该组刚体会被跳过";
            string line = $"[mmd] {key}: 拟合组 {label} 单独不可用（{fit.Count} 点，共线度 {fit.Linearity:F3}）{tail}";
            if (rescued)
            {
                if (loud) Plugin.Log.LogInfo(line);
            }
            else
                Plugin.Log.LogWarning(line);
            return;
        }
        string msg = $"[mmd] {key}: 拟合组 {label} {fit.Count} 点，残差 {fit.Rms * 1000f:F2} mm，"
                     + $"尺度 {fit.Scale * 1000f:F2} mm/单位，共线度 {fit.Linearity:F3}";
        if (fit.Rms > 0.01f)
            Plugin.Log.LogWarning(msg + "  ← ⚠ 残差偏大，这组不参与定尺度");
        else if (loud)
            Plugin.Log.LogInfo(msg);
    }

    /// 一个 PMX 刚体 → (跟哪根骨、相对那根骨的固定位置和朝向)。返回 false = 这个刚体建不了。
    internal bool Resolve(MmdBody body, out Transform bone, out Vector3 localPos, out Quaternion localRot, out string why)
    {
        bone = null;
        localPos = Vector3.zero;
        localRot = Quaternion.identity;
        why = null;

        string bn = _m.BoneName(body.bone);
        if (string.IsNullOrEmpty(bn))
        {
            why = "刚体没有归属骨";
            return false;
        }

        // ① 物理骨（头发/裙子/外套）：用它自己那条链的拟合，不行就退到躯干大拟合
        if (_phys.TryGetValue(bn, out Rest rest))
        {
            MmdFit fit = null;
            string gk = null;
            if (_boneGroup.TryGetValue(bn, out gk))
                _fits.TryGetValue(gk, out fit);
            // 只有**挂在躯干上**的链才准退到躯干大拟合。挂在手臂/腿上的链绝不能退 ——
            // 那两块在对齐时被转过，用躯干的变换会把袖布甩到几十厘米外，还不如不建。
            if ((fit == null || !fit.Usable) && IsTorso(gk))
                _fits.TryGetValue(TorsoKey, out fit);
            if (fit == null || !fit.Usable)
            {
                why = $"物理骨 {bn}（链组 {gk}）的拟合不可用" + (IsTorso(gk) ? "，躯干拟合也不可用" : "，且它不在躯干上、不许借用躯干拟合");
                return false;
            }
            return Place(fit, body, rest, out bone, out localPos, out localRot);
        }

        // ② / ③ / ④ 的落脚点：先按自己找，找不到就**沿 PMX 骨层级往上**找第一个 EFT 映射骨。
        //
        // ⚠️ 上溯这一步是 2026-08-23 实机补的，不是锦上添花：贝丝蒂的 `Bow` 是个**骨骼追随刚体**，
        // 4 条蝴蝶结链全靠 joint 吊在它上面，但它自己不在任何链里（提链工具把它当成"挂点"了）。
        // 它建不起来 → 那 4 条 joint 的固定端不存在 → 8 个刚体一根绳都没有 → **自由落体**，
        // 骨被拉到两米外，画面上就是一条几十米长的黑刺。
        MmdBoneMap bm = null;
        int hop = body.bone;
        for (int n = 0; n < 16 && hop >= 0 && rest == null; n++)
        {
            if (_m.EftOfBone.TryGetValue(hop, out bm))
                _eft.TryGetValue(bm.eft, out rest);
            hop = hop < _m.bones.Count ? _m.bones[hop].parent : -1;
        }
        if (rest == null || bm == null)
        {
            why = $"骨 {bn} 既不在本部位的链里，往上也找不到任何 EFT 映射骨";
            return false;
        }

        // ② 躯干碰撞体：躯干和头发同属一个刚性块，直接用躯干大拟合
        if (IsTorso(bm.eft))
        {
            if (!_fits.TryGetValue(TorsoKey, out MmdFit tf) || !tf.Usable)
            {
                why = $"骨 {bn} → {bm.eft}：躯干拟合不可用";
                return false;
            }
            return Place(tf, body, rest, out bone, out localPos, out localRot);
        }

        // ③ 四肢碰撞体：按 EFT 骨自身几何重建
        return Limb(body, bm, rest, out bone, out localPos, out localRot, out why);
    }

    private bool Place(MmdFit fit, MmdBody body, Rest rest, out Transform bone, out Vector3 localPos, out Quaternion localRot)
    {
        Vector3 worldPos = fit.Apply(body.Pos);
        Quaternion worldRot = fit.ApplyRot(body.Rot);
        localPos = Quaternion.Inverse(rest.Rot) * (worldPos - rest.Pos) * _scaleFix;
        localRot = Quaternion.Inverse(rest.Rot) * worldRot;
        bone = rest.Tf;
        return bone != null;
    }

    /// <summary>
    /// 四肢碰撞体：把「刚体相对 MMD 骨的整个偏移」原样搬到 EFT 骨上，只补一个把
    /// **MMD 骨轴转到 EFT 骨轴**的旋转（swing）。缺的那个「绕骨轴的扭转」不影响 ——
    /// 四肢碰撞体全是胶囊，绕自身轴对称。
    ///
    /// ⚠️ **MMD 侧的骨轴取自「胶囊自己的长轴」，不去查 MMD 的骨层级。**
    /// 2026-08-23 首版用「最远的直接子骨」定骨轴，实机报出 `LUpperarm 1.59 / LForearm1 0.41 /
    /// LThigh1 0.50` 这种离谱比值 —— 因为 MMD 骨层级里最远的直接子骨经常**跨过好几节**
    /// （「足D」的最远子骨是「足首D」，跨过了膝盖），还混着 IK 骨 / 捩骨 / D 骨系。
    /// 用胶囊长轴则干净得多，而且实测和子骨方向在 12 根里有 10 根**完全一致（≤0.5°）**，
    /// 贝丝蒂的手臂/前臂垂直分量正好 **0.0 mm**、大腿/小腿 13.9/17.4 mm。
    /// （胶囊长轴 = 骨方向这件事本身也是实测过的：137 个样本对齐度 0.9992。）
    /// </summary>
    private bool Limb(MmdBody body, MmdBoneMap bm, Rest rest,
                      out Transform bone, out Vector3 localPos, out Quaternion localRot, out string why)
    {
        bone = null;
        localPos = Vector3.zero;
        localRot = Quaternion.identity;
        why = null;

        Transform restChild = EftChild(rest.RestTf);
        if (restChild == null)
        {
            why = $"四肢骨 {bm.eft} 在 prefab 里找不到下一节骨，定不出骨轴";
            return false;
        }
        Vector3 eAxis = restChild.position - rest.Pos;
        if (eAxis.sqrMagnitude < 1e-10f)
        {
            why = $"四肢骨 {bm.eft} 与下一节重合，定不出骨轴";
            return false;
        }
        eAxis.Normalize();

        Vector3 d = body.Pos - _m.BonePos(body.bone);
        Vector3 mAxis = body.Rot * Vector3.up;                  // 胶囊长轴
        if (Vector3.Dot(d, mAxis) < 0f)
            mAxis = -mAxis;                                     // 让它朝"向外"那头，和 eAxis 同侧
        Quaternion swing = Quaternion.FromToRotation(mAxis, eAxis);

        // 偏移整体旋转过去 —— 垂直骨轴的那部分**大小保留**（只是绕轴的方位定不出来），比丢掉强
        Vector3 offset = swing * d * (MetersPerUnit / Mathf.Max(1e-4f, _scaleFix));
        localPos = Quaternion.Inverse(rest.Rot) * offset * _scaleFix;
        localRot = Quaternion.Inverse(rest.Rot) * (swing * body.Rot);
        bone = rest.Tf;

        // 报告时要扣掉「PMX 模型空间 → prefab 空间」那个基准旋转，否则量的是两个坐标系的差、
        // 不是这根四肢骨自己被转了多少 —— 首版就是没扣，报出一片 85~155° 的假警报。
        if (_fits.TryGetValue(TorsoKey, out MmdFit tf) && tf.Usable)
            _limbTurn.Add($"{bm.eft.Replace("Base Human", "")} {Vector3.Angle(tf.Rot * mAxis, eAxis):F0}°");
        return bone != null;
    }

    /// 四肢碰撞体的对账：每根四肢骨相对躯干块被转了多少。腿应该很小，手臂大是正常的
    /// （MMD 是斜下垂臂、EFT 是 T-pose）。由 <see cref="MmdRig"/> 建完之后调一次。
    internal string LimbReport()
    {
        if (_limbTurn.Count == 0)
            return null;
        string line = "四肢骨相对躯干的转角：" + string.Join(" / ", _limbTurn.ToArray());
        _limbTurn.Clear();
        return line;
    }

    /// prefab 层级里这根 EFT 骨的下一节（名字同样以 `Base Human` 开头的第一个子节点）。
    /// 只拿它定**方向**，不拿它的长度 —— EFT 把前臂切成三节而 MMD 是一整条，长度没有可比性。
    private static Transform EftChild(Transform t)
    {
        if (t == null)
            return null;
        for (int i = 0; i < t.childCount; i++)
        {
            Transform c = t.GetChild(i);
            if (c.name.StartsWith("Base Human", System.StringComparison.Ordinal))
                return c;
        }
        return null;
    }
}
