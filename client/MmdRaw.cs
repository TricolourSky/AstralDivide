using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

// 这些 DTO 的字段全由 Newtonsoft 反序列化赋值，编译器看不见 -> 关掉 "从未赋值" 告警
#pragma warning disable 649

namespace AstralDivide.Client;

/// <summary>
/// 读 `&lt;模型&gt;_raw.json` —— PMX 物理的**原样**导出（`PmxPhysics --raw` 产的）。
///
/// 这份数据里一个数都没被换算过：长度是 PMX 单位、角度是弧度、质量阻尼摩擦全是 MMD 原值。
/// 所有换算都在客户端做，好处是「MMD 里写的是什么」和「游戏里跑的是什么」可以逐字对账。
///
/// ⚠️ **`rot` 的欧拉顺序已用数据定死**：就是 Unity 的 <c>Quaternion.Euler(x,y,z)</c>。
/// 判据 = 贝丝蒂 137 个「胶囊 + 单子骨」样本里，胶囊长轴与骨段方向的对齐度 0.9992，
/// 且胶囊中心与骨段中点的距离中位 0.0 mm（六种欧拉顺序里只有这一种能同时满足）。
/// 顺带说明 PMX 和 Unity 一样是**左手系**，所以坐标不用翻轴。
/// </summary>
internal static class MmdRaw
{
    private static readonly Dictionary<string, MmdModel> _byModel =
        new Dictionary<string, MmdModel>(StringComparer.OrdinalIgnoreCase);

    internal static int ModelCount => _byModel.Count;



    /// 调参台的下拉用。排序只为了每次启动顺序一致。
    internal static List<string> ModelNames
    {
        get
        {
            var names = new List<string>(_byModel.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
    }

    internal static MmdTuning TuningOf(string model) =>
        model != null && _byModel.TryGetValue(model, out MmdModel m) ? m.Tuning : null;

    internal static MmdModel Get(string model) =>
        model != null && _byModel.TryGetValue(model, out MmdModel m) ? m : null;

    internal static void LoadAll(string dir)
    {
        _byModel.Clear();
        if (!Directory.Exists(dir))
            return;
        foreach (string file in Directory.GetFiles(dir, "*_raw.json"))
        {
            try
            {
                MmdModel m = JsonConvert.DeserializeObject<MmdModel>(File.ReadAllText(file));
                if (m == null)
                    continue;
                string name = Path.GetFileNameWithoutExtension(file);
                name = name.Substring(0, name.Length - "_raw".Length);
                m.Name = name;
                m.Index();
                m.Tuning = MmdTuning.Load(dir, name);         // 每套模型自带一份手感参数，缺文件就用默认值
                bool hasFile = File.Exists(MmdTuning.PathFor(dir, name));
                Plugin.Log.LogInfo($"[mmd] {name}: 手感 {m.Tuning.Describe()}{(hasFile ? "" : "（没有 tuning.json）")}");
                _byModel[name] = m;
                Plugin.Log.LogInfo($"[mmd] {name}: 刚体 {m.bodies.Count}（动态 {m.DynamicCount}）/ joint {m.joints.Count} / 骨 {m.bones.Count} 已读入；" +
                                   (m.ChestJoints.Count == 0 ? "没有胸部物理" : $"胸部关节 {m.ChestJoints.Count} 条（{(m.ChestSprung ? "原模型带弹簧" : "六轴全锁，靠放开才动")}）"));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[mmd] raw 解析失败 {file}: {e.Message}");
            }
        }
    }

    /// bundle 名（如 `basti_upper`）→ 模型（`basti`）。查不到返回 null，调用方直接不建物理。
    internal static MmdModel For(string bundleKey)
    {
        if (string.IsNullOrEmpty(bundleKey))
            return null;
        int cut = bundleKey.LastIndexOf('_');
        string model = cut > 0 ? bundleKey.Substring(0, cut) : bundleKey;
        return _byModel.TryGetValue(model, out MmdModel m) ? m : null;
    }
}

internal class MmdModel
{
    public double scale = 0.08;                       // 仅参考值，真值靠点云拟合
    public List<MmdBone> bones = new List<MmdBone>();
    public List<MmdBody> bodies = new List<MmdBody>();
    public List<MmdJoint> joints = new List<MmdJoint>();
    public List<MmdBoneMap> boneMap = new List<MmdBoneMap>();

    [JsonIgnore] internal readonly Dictionary<string, int> BoneByName = new Dictionary<string, int>(StringComparer.Ordinal);
    [JsonIgnore] internal readonly Dictionary<int, MmdBoneMap> EftOfBone = new Dictionary<int, MmdBoneMap>();
    [JsonIgnore] internal readonly Dictionary<string, List<int>> RegionBones = new Dictionary<string, List<int>>(StringComparer.Ordinal);
    [JsonIgnore] internal int DynamicCount;
    [JsonIgnore] internal string Name;
    [JsonIgnore] internal MmdTuning Tuning = new MmdTuning();

    internal void Index()
    {
        for (int i = 0; i < bones.Count; i++)
            if (!string.IsNullOrEmpty(bones[i].name))
                BoneByName[bones[i].name] = i;
        foreach (MmdBoneMap bm in boneMap)
        {
            EftOfBone[bm.bone] = bm;
            if (bm.regions == null)
                continue;
            foreach (string r in bm.regions)
            {
                if (!RegionBones.TryGetValue(r, out List<int> list))
                    RegionBones[r] = list = new List<int>();
                list.Add(bm.bone);
            }
        }
        DynamicCount = 0;
        var masses = new List<float>();
        foreach (MmdBody b in bodies)
            if (b.Kind != MmdBodyKind.BoneFollow)
            {
                DynamicCount++;
                masses.Add(b.mass);
            }
        // ⚠️ 质量上限 = 中位数 × 1000（只拦 1e16 这种明显填错的；可露凯的 raw 已按镜像链修过，正常数据不触发）。可露凯的 PMX 把头发根部刚体质量写到 1e16（中位 205、34 个 >1e6），
        //    相邻两节质量比 1e11，PhysX 的关节解算在这种比例下根本收不住 —— 主菜单里 HairC2-B1 直接掉到头下 2 米
        //    （2026-08-26 实机，`离父骨 2.0 m 已拦下`）。PhysX 官方建议相邻刚体质量比 ≤ 10。
        //    MMD 里质量只有相对意义，别的五套最大/中位 ≤ 15，这个上限对它们一个字都不改。
        masses.Sort();
        MassCap = masses.Count > 0 ? Mathf.Max(1e5f, masses[masses.Count / 2] * 1000f) : float.MaxValue;
        // ⚠️ 质量离谱（> 中位×1000 且 > 1e5）的刚体 = 作者拿它当**锚点**：可露凯 `HairC` 整条 1e16~1e10，
        //    两条辫子的根关节都挂在它的第二节上。它在 MMD 里等于不动；把它缩成正常质量试过（2026-08-27）：
        //    它 5cm 的球埋在头部碰撞体里，一变成活的就每帧被往外顶，整棵头发树（A、B 都挂在它下面）跟着抖、发僵，
        //    而改之前 B 链是好的。所以尊重作者：转成骨骼追随（kinematic、跟骨走、不写回），别缩、别拉脱。
        int anchored = 0;
        foreach (MmdBody b in bodies)
            if (b.Kind != MmdBodyKind.BoneFollow && b.mass > MassCap)
            {
                b.physicsType = "Static";
                anchored++;
            }
        // ⚠️ 阻尼 1.0 = Bullet 里每步把速度清零，刚体只能被关节拖着走、自己不会落（头发下落慢得不自然）。
        //    可露凯 103/111 个刚体是 1.0，其他五套中位 0.94~0.95、满格的只有零星几个（那些是有意的，别动）。
        //    所以按模型判：超过一半的动态刚体都满格 = 作者拿极端值凑效果，把满格的压到 0.95（别家的中位数）。
        int saturated = 0;
        foreach (MmdBody b in bodies)
            if (b.Kind != MmdBodyKind.BoneFollow && (b.linearDamping >= 0.999f || b.angularDamping >= 0.999f))
                saturated++;
        if (DynamicCount > 0 && saturated * 2 > DynamicCount)
        {
            foreach (MmdBody b in bodies)
                if (b.Kind != MmdBodyKind.BoneFollow)
                {
                    if (b.linearDamping >= 0.999f) b.linearDamping = 0.95f;
                    if (b.angularDamping >= 0.999f) b.angularDamping = 0.95f;
                }
            Plugin.Log.LogInfo($"[mmd] {Name ?? "?"}: {saturated}/{DynamicCount} 个刚体阻尼满格 1.0（速度每步清零），已压到 0.95");
        }
        if (anchored > 0)
        {
            DynamicCount -= anchored;
            Plugin.Log.LogInfo($"[mmd] {Name ?? "?"}: {anchored} 个刚体质量 > {MassCap:G3}（作者当锚点用），已转成骨骼追随");
        }
        IndexChest();
    }

    [JsonIgnore] internal float MassCap = float.MaxValue;

    // ── 胸部（DEV_NOTES 37.30）：调参台的「胸部」几项只管这些，和头发分开 ──
    // 刚体名 Chest_L / Chest_R（少前 2 解包的六套，可露凯前面带 boneXXXX_）或 左胸1 / 右胸2 这种（蕾娜的原模型）；
    // 子刚体是胸部的关节 = 胸部关节。六套的胸部关节六轴全锁（焊死，靠放开才动）；蕾娜的是原模型带弹簧和限位的（ChestSprung）
    private static readonly System.Text.RegularExpressions.Regex ChestName =
        new System.Text.RegularExpressions.Regex(@"(^|_)Chest_[LR]$|^[左右]胸\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    [JsonIgnore] internal readonly HashSet<int> ChestBodies = new HashSet<int>();
    [JsonIgnore] internal readonly HashSet<int> ChestJoints = new HashSet<int>();
    [JsonIgnore] internal bool ChestSprung;

    /// 由 Index 调：认出胸部刚体和胸部关节
    private void IndexChest()
    {
        foreach (MmdBody b in bodies)
            if (ChestName.IsMatch(b.name ?? ""))
                ChestBodies.Add(b.i);
        foreach (MmdJoint j in joints)
            if (ChestBodies.Contains(j.bodyB))
            {
                ChestJoints.Add(j.i);
                ChestSprung |= !j.Welded;
            }
    }

    internal string BoneName(int i) => i >= 0 && i < bones.Count ? bones[i].name : null;

    internal Vector3 BonePos(int i) => i >= 0 && i < bones.Count ? bones[i].Pos : Vector3.zero;
}

internal class MmdBone
{
    public int i;
    public string name;
    public int parent = -1;
    public float[] pos;

    [JsonIgnore] internal Vector3 Pos => Vec.Of(pos);
}

internal enum MmdBodyKind { BoneFollow = 0, Dynamic = 1, DynamicBonePos = 2 }

internal class MmdBody
{
    public int i;
    public string name;
    public int bone = -1;
    public int group;
    public int collidesWith = 0xFFFF;      // 第 n 位为 1 = 和第 n 组碰撞
    public string shape;                   // Sphere / Box / Capsule
    public float[] size;
    public float[] pos;
    public float[] rot;
    public float mass = 1f;
    public float linearDamping;
    public float angularDamping;
    public float restitution;
    public float friction;
    public string physicsType;             // Static / Dynamic / DynamicAndBonePosition

    [JsonIgnore] internal Vector3 Pos => Vec.Of(pos);
    [JsonIgnore] internal Vector3 Size => Vec.Of(size);
    [JsonIgnore] internal Quaternion Rot => Vec.Euler(rot);

    [JsonIgnore]
    internal MmdBodyKind Kind =>
        physicsType == "Static" ? MmdBodyKind.BoneFollow
        : physicsType == "DynamicAndBonePosition" ? MmdBodyKind.DynamicBonePos
        : MmdBodyKind.Dynamic;
}

internal class MmdJoint
{
    public int i;
    public string name;
    public int bodyA = -1;                 // PMX 里 A 是父、B 是子
    public int bodyB = -1;
    public float[] pos;
    public float[] rot;
    public float[] linMin;
    public float[] linMax;
    public float[] angMin;
    public float[] angMax;
    public float[] linSpring;
    public float[] angSpring;

    [JsonIgnore] internal Vector3 Pos => Vec.Of(pos);
    [JsonIgnore] internal Quaternion Rot => Vec.Euler(rot);

    /// 六个自由度全锁死、也没弹簧（平移 0/0、角度 0/0）= 焊死；物理链上这样的关节客户端会放开（见 MmdRig.Build）
    [JsonIgnore]
    internal bool Welded => Zero(linMin) && Zero(linMax) && Zero(angMin) && Zero(angMax) && Zero(linSpring) && Zero(angSpring);

    private static bool Zero(float[] v)
    {
        if (v == null)
            return true;
        foreach (float x in v)
            if (Mathf.Abs(x) > 1e-6f)
                return false;
        return true;
    }
}

internal class MmdBoneMap
{
    public int bone;
    public string eft;
    public string[] regions;
}

internal static class Vec
{
    internal static Vector3 Of(float[] a) =>
        a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : Vector3.zero;

    /// PMX 的弧度 XYZ → Unity 四元数。顺序已实测确认与 Unity 原生一致，别改成手搓的 Rz*Ry*Rx。
    internal static Quaternion Euler(float[] a) =>
        a != null && a.Length >= 3
            ? Quaternion.Euler(a[0] * Mathf.Rad2Deg, a[1] * Mathf.Rad2Deg, a[2] * Mathf.Rad2Deg)
            : Quaternion.identity;
}
