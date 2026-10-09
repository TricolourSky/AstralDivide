using System.Collections.Generic;
using UnityEngine;

namespace AstralDivide.Client;

/// <summary>
/// 一个角色的一整套 MMD 物理：**每个 PMX 刚体建一个真刚体，每条 PMX joint 建一个真关节**。
///
/// 和以前那套「四个参数的弹簧骨」的区别，一句话：以前是我们编了四个数去模仿 MMD，
/// 现在是把 MMD 写的每一个数原样搬进 PhysX。
///
/// 三类刚体照 MMD 的规矩各走各的（`physicsType`）：
///   · **骨骼追随**（Static）—— kinematic，每步由骨推着走。头、胸、大腿那些挡布料的碰撞体就是它。
///   · **物理**（Dynamic）—— 骨完全跟着刚体走（位置+旋转都写回）。头发、裙子、外套。
///   · **物理+骨位置**（DynamicAndBonePosition）—— 位置跟骨、旋转跟物理。
///
/// ⚠️ 尺度：物理跑在「PMX 单位」里（1 单位 ≈ 8 cm），**不是米**。
/// 物理不是缩放不变的 —— 只有在 PMX 尺度里用 MMD 的重力（≈ 98，界面上的 9.8 内部 ×10，DEV_NOTES 37.31 更正），摆动的周期和幅度才和 MMD 一致。
///
/// 衣服可以按部位改走新做法（<see cref="ClothSway"/>，DEV_NOTES 37.31）：那些骨不建刚体，身体碰撞体照旧建（头发要用，新做法也拿它们当胶囊）。
/// 好处是质量/尺寸/阻尼/弹簧全都能原样抄，一个换算系数都不用编。世界坐标 ↔ 布料坐标的换算见
/// <see cref="ToCloth"/>；每个角色再错开一个大偏移，两个人贴着站也不会互相打架。
/// </summary>
internal class MmdRig
{
    private class Seg
    {
        internal int Body;
        internal int Owner;                     // 哪个部位（bundle）建的，换装时按它清理
        internal Rigidbody Rb;
        internal Transform Bone;
        internal Vector3 LocalPos;              // 刚体相对骨的固定偏移（米）
        internal Quaternion LocalRot;
        internal MmdBodyKind Kind;
        internal MmdBodyKind TrueKind;          // PMX 里写的原始类型；Anchor 兜底时 Kind 会被临时改掉
        internal Vector3 RestLocalPos;          // 骨自己的静止局部位姿，复位用
        internal Quaternion RestLocalRot;
        internal int Depth;
        internal float BaseDrag;                // PMX 换算出来的阻尼原值，滑块在它上面乘
        internal float BaseAngDrag;
        internal bool Proxy;                    // 衣服新做法的碰撞代理（37.31）：布料刚体改成跟骨走（kinematic），只给头发碰，不当新做法的身体碰撞体
        // 插值（37.31）：最近两次物理写回后骨的局部位姿，每帧按「下一步走了多少」在两者之间插，60Hz 物理在高帧率下也逐帧顺滑
        internal Vector3 PrevLocalPos, CurLocalPos;
        internal Quaternion PrevLocalRot, CurLocalRot;
        internal bool HasPose;
    }

    private static int[] _layerOf;                           // PMX 组 → 层：身体碰撞体永远在这儿，布料平时也在这儿
    private static int[] _clothLayerOf;                      // PMX 组 → 「布料之间互不碰撞」打开时布料待的层
    private static int _clothMask;                           // 上面那 16 个布料层的位掩码

    private readonly MmdModel _m;
    private readonly Dictionary<int, Seg> _segs = new Dictionary<int, Seg>();
    private readonly List<Seg> _order = new List<Seg>();     // 按骨骼层级深度排好的写回顺序
    private readonly Dictionary<int, ConfigurableJoint> _joints = new Dictionary<int, ConfigurableJoint>();
    private readonly HashSet<int> _welded = new HashSet<int>();      // 六自由度全锁死的链关节（见 Build），放开角度跟调参台走
    private readonly HashSet<int> _weldedChest = new HashSet<int>(); // 同上，但是胸部关节：跟调参台的「胸部」几项走（37.30）
    private readonly Dictionary<int, ChestBase> _sprungChest = new Dictionary<int, ChestBase>();   // 原模型带弹簧的胸部关节（蕾娜）：原值，滑块按倍数缩放

    /// 原模型带弹簧的胸部关节的原值（角度单位度、已按非对称补偿算好中位），调参台的倍数乘在它上面
    private sealed class ChestBase
    {
        internal Vector3 Lo, Hi, Mid, LinLo, LinHi, LinSpring, AngSpring;
    }
    private readonly Dictionary<long, PhysicMaterial> _mats = new Dictionary<long, PhysicMaterial>();
    private readonly int _slotIndex;
    private readonly Vector3 _slot;
    private float _upm = 12.5f;                              // 1 米 = 多少 PMX 单位
    private Transform _ref;
    private Vector3 _lastRef;
    private bool _awake;
    private int _complaints;
    private int _asymBuilt;                                  // 本次 Link 里走了「非对称限位补偿」的关节数，只用来汇总日志

    /// <summary>
    /// 布料物理跑在**这个参考系**里：原点跟着角色走，**但坐标轴始终与世界对齐**。
    ///
    /// **为什么跟位置**（2026-08-23 Tech Leader 一句话点破）：角色跑起来时衣服会**往前冲**、
    /// 裙子跑到人身前。那不是惯性 —— 惯性会让裙子甩在**身后**。是**被身体推的**：
    /// 身体碰撞体走 `MovePosition`，PhysX 会给它算出真实速度，角色 5 m/s → 布料世界里 47 单位/秒，
    /// 每帧一巴掌把布料拍出去。把原点跟上角色后，碰撞体在这个系里几乎不动，那一巴掌从根上消失。
    ///
    /// ⚠️ **坐标轴绝不能跟着角色转。** 2026-08-23 试过连朝向一起跟，结果头发裙子炸成碎片：
    /// 关节的静止朝向是在**世界对齐**的坐标系里算出来的，把刚体旋转着从关节框架底下抽走，
    /// 链条就会被按错误的零点拧过去。日志里两个部件报同一个 `关节零点偏差 119.88°`
    /// （平时 0.00°），那个角度就是角色胯骨的世界朝向。**转身的力就让它传进来，那本来就是真物理。**
    ///
    /// ⚠️ 每个角色一个，不能共用 —— 全局共用时，阵营页面上相距 1886 m 的两个角色里会有一个
    /// 坐标飙到 17540 单位，float 精度不够，整块沿 X 漂移（实机实锤）。
    /// </summary>
    private Vector3 _framePos;
    private bool _frameSet;

    private Vector3 _lastRefPos;
    private bool _trackInit;

    private ClothSway _sway;                                 // 衣服新做法（37.31）：交给它的链不建刚体；没有就是 null

    internal bool Alive => _segs.Count > 0 || (_sway != null && _sway.Count > 0);
    internal string Model => _m.Name;
    internal Transform Reference => _ref;

    internal MmdRig(MmdModel model)
    {
        _m = model;
        // 角色之间在布料世界里错开，互不打扰。200 单位 ≈ 21 m，比任何一套布料都大得多；
        // 车位号是**回收**的，所以本地玩家基本永远是 0 号，坐标贴着原点，精度最好。
        _slotIndex = PhysWorld.TakeSlot();
        _slot = new Vector3(_slotIndex * 200f, 0f, 0f);
    }

    private Vector3 ToCloth(Vector3 world) => (world - _framePos) * _upm + _slot;

    private Vector3 FromCloth(Vector3 cloth) => (cloth - _slot) / _upm + _framePos;

    // ── 建 ────────────────────────────────────────────────────────────────

    /// 把一个部位（bundle）的刚体加进来。`owner` 用于换装时定点清理。
    /// `sway` = 这个部位里交给新做法的链（37.31，可以为 null）：它们的骨不建动态刚体，建完 PhysX 那部分再交给 <see cref="ClothSway"/>。
    internal int AddPart(int owner, MmdSurvey survey, HashSet<string> partBones, Transform reference, string bundleKey,
                         List<ClothSway.Input> sway = null, ClothSway.Body legs = null)
    {
        sway?.RemoveAll(c => c.Bones.Length < 2 && !FillTail(c, survey));   // 一节骨的胸：补不出尖端就照旧 PMX
        var handed = new HashSet<string>();
        if (sway != null)
            foreach (ClothSway.Input c in sway)
                foreach (string n in c.Names)
                    handed.Add(n);
        int skippedSway = 0;
        EnsureLayers();
        _upm = 1f / Mathf.Max(1e-4f, survey.MetersPerUnit);
        if (_ref == null)
            _ref = reference;
        if (_segs.Count == 0 && _ref != null && !_frameSet)
        {
            _framePos = _ref.position;                       // 还没建刚体，参考系直接对齐到角色身上
            _frameSet = true;
        }

        int made = 0, skipped = 0, proxies = 0;
        string firstWhy = null;
        foreach (MmdBody body in _m.bodies)
        {
            if (_segs.ContainsKey(body.i))
                continue;                                    // 别的部位已经建过（碰撞体是全身共用的）
            string bn = _m.BoneName(body.bone);
            bool mine = bn != null && partBones.Contains(bn);
            bool collider = body.Kind == MmdBodyKind.BoneFollow;
            if (!mine && !collider)
                continue;                                    // 动态刚体只归它自己那个部位
            bool proxy = false;
            if (!collider && handed.Contains(bn))
            {
                // 交给新做法的骨：不建会动的 PhysX 刚体。头发会碰到它的话，留一个跟着骨走的碰撞代理（kinematic），
                // 不然长头发（贝丝蒂的后发、阿斯缇亚的头发）会直接穿过外套 / 披风沉到身体碰撞体上（第二步加的，37.31）
                if (!NeedsProxy(body))
                {
                    skippedSway++;
                    continue;
                }
                proxy = true;
            }
            if (!survey.Resolve(body, out Transform bone, out Vector3 lp, out Quaternion lr, out string why))
            {
                skipped++;
                if (firstWhy == null)
                    firstWhy = why;
                continue;
            }
            _segs[body.i] = Make(owner, body, bone, lp, lr, proxy);
            made++;
            if (proxy)
                proxies++;
        }

        // ⚠️ 顺序不能换：Unity 的关节限位零点 = **AddComponent 那一瞬间两块刚体的相对姿态**。
        // 所以必须先把骨复位到静止姿态、刚体贴上去，再建关节，零点才等于 MMD 的静止姿态。
        Reorder();
        Reset();
        int joints = Link(bundleKey);
        Plugin.Log.LogInfo($"[mmd] {bundleKey}: 建刚体 {made}（跳过 {skipped}）、关节 {joints}；本角色累计 {_segs.Count} 刚体 / {_joints.Count} 关节"
                           + (_welded.Count > 0 ? $"；其中 {_welded.Count} 条六自由度全锁死的链关节已放开 ±{_m.Tuning.freeAngle:0}°" : "")
                           + (_weldedChest.Count > 0 ? $"；胸部 {_weldedChest.Count} 条锁死关节放开 ±{_m.Tuning.chestAngle:0}°" : "")
                           + (_sprungChest.Count > 0 ? $"；胸部 {_sprungChest.Count} 条原模型弹簧关节" : ""));
        ReportGroups(bundleKey);
        ReportCollisionPairs(bundleKey);
        string limbs = survey.LimbReport();
        if (limbs != null)
            Plugin.Log.LogInfo($"[mmd] {bundleKey}: {limbs}");
        Anchor(bundleKey);
        float far = 0f;
        foreach (Seg s in _segs.Values)
            if (s.Rb != null)
                far = Mathf.Max(far, s.Rb.position.magnitude);
        Plugin.Log.LogInfo($"[mmd] {bundleKey}: 布料世界里最远的刚体在 {far:F0} 单位处"
                           + (far > 3000f ? "  ← ⚠ 太远了，float 精度不够，关节会漂" : "（越小精度越好）"));
        if (skipped > 0 && firstWhy != null)
            Plugin.Log.LogWarning($"[mmd] {bundleKey}: 跳过的第一条原因 —— {firstWhy}");
        if (sway != null && sway.Count > 0)
            AddSway(sway, legs, skippedSway, proxies, bundleKey);
        return made;
    }

    /// 交给新做法的布料刚体要不要留碰撞代理：PMX 规矩里它和某个还在 PhysX 里的刚体（头发 / 胸）碰得上才留；
    /// 「布料之间互不碰撞」开着时头发本来就不碰布料（蕾娜），不留
    private bool NeedsProxy(MmdBody cloth)
    {
        if (_m.Tuning.clothNoSelf)
            return false;
        foreach (MmdBody b in _m.bodies)
        {
            if (b.Kind == MmdBodyKind.BoneFollow || ClothSway.KindOf(_m.BoneName(b.bone)) != SwayKind.None)
                continue;
            if (((cloth.collidesWith >> Mathf.Clamp(b.group, 0, 15)) & 1) != 0 && ((b.collidesWith >> Mathf.Clamp(cloth.group, 0, 15)) & 1) != 0)
                return true;
        }
        return false;
    }

    // ── 衣服新做法（DEV_NOTES 37.31）────────────────────────────────────────

    /// 把交给新做法的链加进 <see cref="ClothSway"/>：身体碰撞体（骨骼追随刚体）换成胶囊给它用，每条链补上 PMX 的碰撞组和静止位置
    private void AddSway(List<ClothSway.Input> inputs, ClothSway.Body legs, int skippedBodies, int proxies, string bundleKey)
    {
        if (_sway == null)
            _sway = new ClothSway(k => _m.Tuning.Part(k));
        _sway.SetBody(legs);
        _sway.SetColliders(SwayColliders());
        int added = 0;
        var kinds = new Dictionary<SwayKind, int>();
        foreach (ClothSway.Input c in inputs)
        {
            FillPmx(c);
            if (!_sway.Add(c))
                continue;
            added++;
            kinds.TryGetValue(c.Kind, out int k);
            kinds[c.Kind] = k + 1;
        }
        _sway.Link(_upm);
        _sway.Reset();
        var what = new List<string>();
        foreach (KeyValuePair<SwayKind, int> kv in kinds)
            what.Add((kv.Key == SwayKind.Skirt ? "裙 " : kv.Key == SwayKind.Coat ? "外套衣摆 " : kv.Key == SwayKind.Chest ? "胸部 " : "饰品 ") + kv.Value);
        Plugin.Log.LogInfo($"[mmd] {bundleKey}: 新做法接管 {added} 条链（{string.Join("、", what.ToArray())}），PhysX 少建 {skippedBodies} 个刚体"
                           + (proxies > 0 ? $"、{proxies} 个改成跟骨走的碰撞代理（给头发碰）；" : "；")
                           + $"本角色新做法共 {_sway.Count} 条链 / {_sway.BoneCount} 根骨，身体胶囊 {_sway.ColCount} 个，相邻链 {_sway.PairCount} 对，"
                           + (_sway.LegsReady ? "腿带动就绪" : "⚠ 找不到胯 / 大腿，只跟挂点走")
                           + $"，转身参照 {(legs?.Root != null ? legs.Root.name : "无")}");
    }

    /// 身体碰撞体 → 胶囊（球 = 半长 0；盒子 = 沿最长边、半径取第二长的半边长）。只要真·骨骼追随的（Anchor 兜底转过来的布料不算）
    private List<ClothSway.Col> SwayColliders()
    {
        var list = new List<ClothSway.Col>();
        foreach (Seg s in _segs.Values)
        {
            if (s.TrueKind != MmdBodyKind.BoneFollow || s.Proxy || s.Bone == null)
                continue;                                    // 碰撞代理是衣服自己，不能拿来挡衣服
            MmdBody b = _m.bodies[s.Body];
            Vector3 size = b.Size, axis = Vector3.up;
            float r = Mathf.Max(1e-3f, size.x), half = 0f;
            if (b.shape == "Capsule")
                half = Mathf.Max(0f, size.y) * 0.5f;
            else if (b.shape == "Box")
                BoxAsCapsule(size, out axis, out r, out half);
            list.Add(new ClothSway.Col
            {
                Bone = s.Bone, LocalPos = s.LocalPos, LocalRot = s.LocalRot, Axis = axis,
                Radius = r / _upm, Half = half / _upm, Group = Mathf.Clamp(b.group, 0, 15), Mask = b.collidesWith,
                PmxCenter = b.Pos, PmxAxis = b.Rot * axis, PmxRadius = r, PmxHalf = half,
            });
        }
        return list;
    }

    private static void BoxAsCapsule(Vector3 size, out Vector3 axis, out float radius, out float half)
    {
        float[] e = { Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z) };
        int big = e[0] >= e[1] && e[0] >= e[2] ? 0 : e[1] >= e[2] ? 1 : 2;
        axis = big == 0 ? Vector3.right : big == 1 ? Vector3.up : Vector3.forward;
        float mid = Mathf.Max(e[(big + 1) % 3], e[(big + 2) % 3]);
        radius = Mathf.Max(1e-3f, mid);
        half = Mathf.Max(0f, e[big] - radius);
    }

    /// 链的碰撞组（第一个 PMX 动态刚体的）/ 掩码（链上所有动态刚体的并集），和每节骨在 PMX 里的静止位置（找不到的记成无穷远 = 不参与「静止就贴着」的判断）；
    /// 有虚拟尖端的（一节骨的胸）再补上尖端 = 那个刚体的中心。
    /// 掩码取并集（37.31）：PMX 里常常只有第一块刚体不碰挂点那块（OTs-14 的手臂带子：第一块不碰手臂、后面都碰），
    /// 以前整条按第一块算 → 带子整条不碰手臂，改软往下垂以后会穿过手臂
    private void FillPmx(ClothSway.Input c)
    {
        MmdBody first = FirstBody(c);
        if (first != null)
        {
            c.Group = Mathf.Clamp(first.group, 0, 15);
            c.Mask = MaskUnion(c);
        }
        bool tail = c.Tail.sqrMagnitude > 1e-6f && first != null;
        c.PmxPos = new Vector3[c.Names.Length + (tail ? 1 : 0)];
        for (int i = 0; i < c.Names.Length; i++)
            c.PmxPos[i] = _m.BoneByName.TryGetValue(c.Names[i], out int bi) ? _m.BonePos(bi) : Vector3.positiveInfinity;
        if (tail)
            c.PmxPos[c.Names.Length] = first.Pos;
    }

    private MmdBody FirstBody(ClothSway.Input c)
    {
        var names = new HashSet<string>(c.Names);
        foreach (MmdBody b in _m.bodies)
        {
            string bn = _m.BoneName(b.bone);
            if (b.Kind != MmdBodyKind.BoneFollow && bn != null && names.Contains(bn))
                return b;
        }
        return null;
    }

    private int MaskUnion(ClothSway.Input c)
    {
        var names = new HashSet<string>(c.Names);
        int mask = 0;
        foreach (MmdBody b in _m.bodies)
        {
            string bn = _m.BoneName(b.bone);
            if (b.Kind != MmdBodyKind.BoneFollow && bn != null && names.Contains(bn))
                mask |= b.collidesWith;
        }
        return mask;
    }

    /// <summary>
    /// 一节骨的链（六套少前 2 解包的胸：一边一个 Chest_L/R 球刚体）补虚拟尖端：尖端 = PMX 里那个刚体的中心，按点云拟合落到这根骨上（米、骨的朝向空间）。
    /// 刚体就在骨原点上（< 5 mm）的定不出方向，返回 false，这条链照旧 PMX
    /// </summary>
    private bool FillTail(ClothSway.Input c, MmdSurvey survey)
    {
        MmdBody b = FirstBody(c);
        if (b == null || !survey.Resolve(b, out Transform bone, out Vector3 lp, out Quaternion _, out string _) || bone != c.Bones[0])
            return false;
        if (lp.sqrMagnitude < 0.005f * 0.005f)
            return false;
        c.Tail = lp;
        return true;
    }

    internal int SwayCount(SwayKind kind) => _sway != null ? _sway.CountKind(kind) : 0;

    /// 每帧一次（PhysWorld.Tick 里 PhysX 写回之后）。睡着（距离剔除）的角色不算
    internal void SwayTick(float dt)
    {
        if (_awake && _sway != null)
            _sway.Tick(dt);
    }

    /// <summary>
    /// PMX 的 16 个碰撞组要落到 Unity 的层上，但**游戏自己的层碰撞矩阵**把一大堆层对关掉了
    /// （贝丝蒂实机：0×1 0×2 0×4 0×5 2×4 2×5 2×8 4×8 5×8 全被挡）。
    ///
    /// 首版把组号直接当层号、指望 `includeLayers` 去强行覆盖矩阵 —— 那是在赌一个不确定的行为。
    /// 现在改成**先扫出一组「彼此之间矩阵全开」的层**再映射，
    /// 于是只需要用 `excludeLayers`（方向明确、优先级最高）去实现 PMX 的「不碰」规则，不用赌了。
    /// </summary>
    private static void EnsureLayers()
    {
        if (_layerOf != null)
            return;
        var pool = new List<int>();
        for (int layer = 0; layer < 32 && pool.Count < 16; layer++)
        {
            if (Physics.GetIgnoreLayerCollision(layer, layer))
                continue;                                    // 连自己都不碰的层，不能用
            bool ok = true;
            foreach (int p in pool)
                if (Physics.GetIgnoreLayerCollision(layer, p))
                {
                    ok = false;
                    break;
                }
            if (ok)
                pool.Add(layer);
        }
        // ⚠️ **16 个组必须各占各的层，一个都不许撞车。**
        // 首版 `pool` 装不下的组直接拿组号当层号（`_layerOf[g] = g`），而 pool 挑出来的层号
        // 恰恰也在 0~15 里 —— 阿斯缇亚实机：pool = [0,3,6,7,8]，于是
        //   组2 → pool[2] = 层6，组6 → fallback 层6   ← 两个组挤进同一层
        //   组3 → pool[3] = 层7，组7 → fallback 层7   ← 又一次
        // exclude 是按**层**下的：组6 排掉层6，连带把组2 一起排掉了。
        // 组2 里装的正是 `下半身SkirtBlock` —— 挡裙子穿胯的那个碰撞体，于是腿直接穿过裙子。
        // 躯干/腿的组7 撞上组3 同理，袖子也一样。所以 fallback 要**避开 pool 已占的层**。
        _layerOf = new int[16];
        var taken = new HashSet<int>(pool);
        for (int g = 0; g < 16; g++)
        {
            if (g < pool.Count)
            {
                _layerOf[g] = pool[g];
                continue;
            }
            // 先要「自己能碰自己」的层；EFT 里满足这条的只有 8 个（实测 0,3,6,7,8,12,18,23），
            // 用光了就**退而求其次拿任何没被占的层**——绝不能退回「组号当层号」，那正是撞车的来源。
            // 同层只会出现在同组之间，碰不碰由 include/exclude 说了算（实测 include 能压过矩阵）。
            int layer = FreeLayer(taken, true);
            if (layer < 0)
                layer = FreeLayer(taken, false);
            _layerOf[g] = layer < 0 ? g : layer;              // 32 个层全占满了才认命
            taken.Add(_layerOf[g]);
        }
        AssignClothLayers(taken);
        // Info 不是 Warning：EFT 的层矩阵永远只扫得出 5 个全开层，这行每局必打，是常态不是故障（Tech Leader 2026-10-01 定）。
        if (pool.Count < 16)
            Plugin.Log.LogInfo($"[mmd] 只找到 {pool.Count} 个彼此全开的层（要 16 个），"
                               + "多出来的碰撞组分到了矩阵未必全开的层，靠 includeLayers 强开");
        int distinct = new HashSet<int>(_layerOf).Count;
        Plugin.Log.LogInfo($"[mmd] 碰撞组 → 层映射：{string.Join(",", System.Array.ConvertAll(_layerOf, x => x.ToString()))}"
                           + $"（前 {pool.Count} 组的层两两全开；16 组占了 {distinct} 个不同的层）；"
                           + $"互不碰撞模式的布料层：{string.Join(",", System.Array.ConvertAll(_clothLayerOf, x => x.ToString()))}");
        if (distinct < 16)
            Plugin.Log.LogError("[mmd] ⚠ 有碰撞组共用了同一个层 —— 一个组的 exclude 会连累另一个组，"
                                + "布料会莫名穿模（2026-08-26 腿穿裙子就是这么来的）");
    }

    /// <summary>
    /// 给「布料之间互不碰撞」模式准备布料层：**每个 PMX 组的布料各占一个自己的层**，用的是组层挑剩下的那 16 个。
    ///
    /// ⚠️ 首版把所有布料挤进同一个层（借第 15 组的）。布料自己那一侧没问题，但**身体碰撞体那一侧**的 PMX 规矩是
    /// 按「布料原来在哪个组」写的 —— 布料都挤到一起之后，身体只能看自己掩码的第 15 位，等于换了一条规矩：
    /// 可露凯两个专门挡头发的 `上半身2Block` / `下半身Block`（掩码 0x0040 = 只和第 6 组碰）从此和头发不碰（丢 200 对），
    /// 蕾娜的手臂反而开始推外套和裙子（多 1518 对）。2026-10-01 审查时拿 raw 逐对算出来的。
    /// 布料按组分层之后，身体照旧按组表态，两边都同意才碰 —— 和关着这个开关时是同一条 PMX 规矩，只少了布料互撞。
    /// </summary>
    private static void AssignClothLayers(HashSet<int> taken)
    {
        var spare = new List<int>();
        for (int layer = 0; layer < 32; layer++)
            if (!taken.Contains(layer))
                spare.Add(layer);
        _clothLayerOf = new int[16];
        _clothMask = 0;
        for (int g = 0; g < 16; g++)
        {
            // 组层最多占 16 个，所以剩下的至少有 16 个；真不够（32 层全满，理论上到不了）就退回组层，等于这组不分家。
            _clothLayerOf[g] = g < spare.Count ? spare[g] : _layerOf[g];
            if (g < spare.Count)
                _clothMask |= 1 << spare[g];
        }
    }

    /// 找一个还没被占的层。<paramref name="selfCollide"/> = 只要「自己能碰自己」的层。找不到返回 -1。
    private static int FreeLayer(HashSet<int> taken, bool selfCollide)
    {
        for (int layer = 0; layer < 32; layer++)
            if (!taken.Contains(layer) && (!selfCollide || !Physics.GetIgnoreLayerCollision(layer, layer)))
                return layer;
        return -1;
    }

    private void ReportGroups(string bundleKey)
    {
        var used = new List<int>();
        foreach (Seg s in _segs.Values)
        {
            int g = Mathf.Clamp(_m.bodies[s.Body].group, 0, 15);
            if (!used.Contains(g))
                used.Add(g);
        }
        used.Sort();
        Plugin.Log.LogInfo($"[mmd] {bundleKey}: 用到的碰撞组 [{string.Join(",", used.ConvertAll(x => x.ToString()).ToArray())}]");
    }

    /// <summary>
    /// 诊断：把**实际建出来的**布料层和身体层逐对查一遍，看它们到底碰不碰得上。
    ///
    /// 2026-08-26 阿斯缇亚实机：腿穿裙子、手臂穿袖子，可拟合残差是 0.00 mm、刚体一个没跳过 ——
    /// 数据侧干净，怀疑是碰撞组落到了被游戏层矩阵挡住的层上（它只扫得出 5 个全开层，阿斯缇亚要 8 组）。
    /// 与其继续猜，不如把矩阵/include/exclude 三个因子逐对打出来。
    /// </summary>
    private void ReportCollisionPairs(string bundleKey)
    {
        var cloth = new Dictionary<int, Collider>();
        var body = new Dictionary<int, Collider>();
        foreach (Seg s in _segs.Values)
        {
            Collider col = s.Rb == null ? null : s.Rb.GetComponent<Collider>();
            if (col == null)
                continue;
            var map = s.TrueKind == MmdBodyKind.BoneFollow ? body : cloth;
            if (!map.ContainsKey(col.gameObject.layer))
                map[col.gameObject.layer] = col;
        }
        int ok = 0;
        var lines = new List<string>();
        foreach (KeyValuePair<int, Collider> c in cloth)
            foreach (KeyValuePair<int, Collider> b in body)
            {
                int cBit = 1 << b.Key, bBit = 1 << c.Key;
                bool matrix = !Physics.GetIgnoreLayerCollision(c.Key, b.Key);
                bool inc = (c.Value.includeLayers.value & cBit) != 0
                           || (b.Value.includeLayers.value & bBit) != 0;
                bool exc = (c.Value.excludeLayers.value & cBit) != 0
                           || (b.Value.excludeLayers.value & bBit) != 0;
                bool hit = (matrix || inc) && !exc;
                if (hit)
                {
                    ok++;
                    continue;                    // 能碰的不逐条报 —— 汇总行里的数字就够了
                }
                lines.Add($"布料层{c.Key}×身体层{b.Key} 挡住"
                          + $"（矩阵{(matrix ? "开" : "关")} include{(inc ? "有" : "无")} exclude{(exc ? "有" : "无")}）");
            }
        Plugin.Log.LogInfo($"[mmd] {bundleKey}: 布料层 {cloth.Count} 个 × 身体层 {body.Count} 个 —— "
                           + $"{ok} 对能碰 / {lines.Count} 对被挡");
        foreach (string l in lines)
            Plugin.Log.LogInfo("[mmd]   " + l);
    }

    /// <summary>
    /// **没有任何关节连着的动态刚体，一律改成骨骼追随。**
    ///
    /// 一个动态刚体如果一条 joint 都没接上，它就只剩重力 —— 直接自由落体，
    /// 把蒙在它上面的顶点拉成一条几十米的黑刺（2026-08-23 贝丝蒂的蝴蝶结实锤）。
    /// 这种情况的根因通常是**它的固定端刚体没建起来**，而那往往是数据/映射问题；
    /// 这里做的是兜底：宁可这几根骨不动，也绝不让它们飞出去。
    /// </summary>
    private void Anchor(string bundleKey)
    {
        var linked = new HashSet<int>();
        foreach (MmdJoint j in _m.joints)
            if (_joints.ContainsKey(j.i))
            {
                linked.Add(j.bodyA);
                linked.Add(j.bodyB);
            }
        var loose = new List<string>();
        foreach (Seg s in _segs.Values)
        {
            if (s.TrueKind == MmdBodyKind.BoneFollow || s.Rb == null)
                continue;
            // ⚠️ 必须可逆：head 先建时 upper 的刚体还不存在，同一根骨可能这一轮"孤立"、
            // 下一轮就接上关节了。钉死不还原的话就永久不动了。
            if (linked.Contains(s.Body))
            {
                if (s.Kind == s.TrueKind)
                    continue;
                s.Kind = s.TrueKind;
                s.Rb.isKinematic = false;
                s.Rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                continue;
            }
            if (s.Kind == MmdBodyKind.BoneFollow)
                continue;
            s.Kind = MmdBodyKind.BoneFollow;
            s.Rb.isKinematic = true;
            s.Rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            loose.Add(_m.bodies[s.Body].name);
        }
        if (loose.Count > 0)
            Plugin.Log.LogWarning($"[mmd] {bundleKey}: {loose.Count} 个动态刚体一条关节都没接上，"
                                  + $"已改成骨骼追随防止自由落体 —— {string.Join(" ", loose.ToArray())}。"
                                  + "多半是它们的固定端刚体没建起来，去查那一头");
    }

    /// `proxy` = 衣服新做法的碰撞代理（37.31）：当骨骼追随体建（kinematic、每步贴到骨上、不写回骨），层和碰撞规矩照它原来的布料身份
    private Seg Make(int owner, MmdBody body, Transform bone, Vector3 localPos, Quaternion localRot, bool proxy = false)
    {
        var seg = new Seg
        {
            Body = body.i,
            Owner = owner,
            Bone = bone,
            LocalPos = localPos,
            LocalRot = localRot,
            Kind = proxy ? MmdBodyKind.BoneFollow : body.Kind,
            TrueKind = proxy ? MmdBodyKind.BoneFollow : body.Kind,
            Proxy = proxy,
            RestLocalPos = bone.localPosition,
            RestLocalRot = bone.localRotation,
            Depth = Depth(bone),
        };

        var go = new GameObject("bds_" + (body.name ?? body.i.ToString()));
        go.layer = _layerOf[Mathf.Clamp(body.group, 0, 15)];
        go.transform.SetPositionAndRotation(ToCloth(WorldPos(seg)), WorldRot(seg));
        PhysWorld.Adopt(go);
        AddCollider(go, body);

        var rb = go.AddComponent<Rigidbody>();
        rb.useGravity = false;                               // 重力我们自己加，见 PhysWorld（Unity 的是全局的，动不得）
        rb.isKinematic = seg.Kind == MmdBodyKind.BoneFollow;
        rb.mass = MassOf(body);
        seg.BaseDrag = ToDrag(body.linearDamping);
        seg.BaseAngDrag = ToDrag(body.angularDamping);
        rb.drag = seg.BaseDrag * _m.Tuning.damping * DragScale(body.i);
        rb.angularDrag = seg.BaseAngDrag * _m.Tuning.damping * DragScale(body.i);
        rb.interpolation = RigidbodyInterpolation.None;
        // 裙板只有 8~10 mm 厚，Discrete 在角色跑动时会被直接穿过去（这就是"穿模"）。
        // Speculative 连续检测对薄物体便宜又有效；骨骼追随体是 kinematic，保持 Discrete。
        rb.collisionDetectionMode = rb.isKinematic
            ? CollisionDetectionMode.Discrete
            : CollisionDetectionMode.ContinuousSpeculative;
        rb.sleepThreshold = 0f;                              // MMD 的布料永远不睡
        rb.maxAngularVelocity = _m.Tuning.maxSpin;
        // ⚠️ 「顶飞」的真凶：两块布料压在一起时 PhysX 会强行把它们推开，
        // Unity 默认上限 10 单位/s ≈ 1 m/s。蹲下时外套和裙子被躯干挤到一起，穿透一深就弹飞。
        rb.maxDepenetrationVelocity = _m.Tuning.separate;
        rb.solverIterations = _m.Tuning.solverIters;
        rb.solverVelocityIterations = Mathf.Max(1, _m.Tuning.solverIters / 3);
        seg.Rb = rb;
        return seg;
    }

    /// <summary>
    /// PMX 的碰撞组 → Unity 的层。第 n 位 = 1 表示「和第 n 组碰撞」。
    ///
    /// 层是 <see cref="EnsureLayers"/> 挑出来的「矩阵里两两全开」的一组，所以只要把不该碰的排掉就行。
    /// exclude 是**逐碰撞体**的，两边各排一次，正好等于 PMX 那条「双方都同意才碰」的规矩。
    /// ⚠️ 两道保险都要：EFT 的碰撞矩阵很严，实机只扫得出 **5 个**彼此全开的层（要 16 个），
    /// 剩下的组只能退回「组号当层号」，那些层多半被矩阵挡着 —— 所以 include（强开）不能撤，
    /// exclude（强关）才是 PMX 规矩的执行者。
    ///
    /// </summary>
    private void Layers(GameObject go, Collider col, MmdBody body)
    {
        bool cloth = body.Kind != MmdBodyKind.BoneFollow;
        int include = 0, exclude = 0;
        for (int g = 0; g < 16; g++)
        {
            // 第 g 组的东西可能在两个层上：组层，或者（开了互不碰撞的布料）布料层。
            // 身体碰撞体两个都要表态，同一个答案；布料只会遇到组层上的身体，布料层另算（见下）。
            int bit = 1 << _layerOf[g];
            if (!cloth)
                bit |= 1 << _clothLayerOf[g];
            if (((body.collidesWith >> g) & 1) != 0)
                include |= bit;
            else
                exclude |= bit;
        }
        // 「布料之间互不碰撞」：动态刚体挪到**它那一组自己的布料层**，并把全部布料层排掉 —— 于是它们彼此穿过，
        // 而和身体（骨骼追随体，还在各自的组层上）之间仍是 PMX 那条「双方都同意才碰」，一对都不多、一对都不少。
        // 这就是真·布料模拟解决"外套和裙子打架"的做法，只是我们保留了 PMX 的限位和 Box 碰撞体。
        bool noSelf = _m.Tuning.clothNoSelf && cloth;
        int group = Mathf.Clamp(body.group, 0, 15);
        go.layer = noSelf ? _clothLayerOf[group] : _layerOf[group];
        if (noSelf)
            exclude |= _clothMask;
        col.includeLayers = include;
        col.excludeLayers = exclude;
    }

    /// PMX 形状 → Unity 碰撞体。尺寸直接就是 PMX 数值 —— 布料世界的单位就是 PMX 单位。
    private void AddCollider(GameObject go, MmdBody body)
    {
        Vector3 s = body.Size;
        Collider col;
        if (body.shape == "Box")
        {
            var box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(Mathf.Max(1e-3f, s.x), Mathf.Max(1e-3f, s.y), Mathf.Max(1e-3f, s.z)) * 2f;
            col = box;                                       // PMX 的 Box 尺寸是**半边长**
        }
        else if (body.shape == "Capsule")
        {
            var cap = go.AddComponent<CapsuleCollider>();
            cap.radius = Mathf.Max(1e-3f, s.x);
            cap.height = Mathf.Max(1e-3f, s.y) + cap.radius * 2f;   // PMX 的 y 是圆柱段长，不含两头的半球
            cap.direction = 1;                                      // MMD 的胶囊沿 Y 轴
            col = cap;
        }
        else
        {
            var sp = go.AddComponent<SphereCollider>();
            sp.radius = Mathf.Max(1e-3f, s.x);
            col = sp;
        }

        col.material = Material(body);

        Layers(go, col, body);
        // ⚠️ **千万别调小 contactOffset**：动态刚体用的是 `ContinuousSpeculative`，
        // 而 Unity 里 contactOffset 就是**投机 CCD 的预测余量** —— 调小等于把防穿隧关掉。
        // 2026-08-23 我把它设成 0.004 想减少"虚假排斥"，结果穿隧回来、裙板互相穿过卡住，
        // 加上同时关掉了关节防拉散，长链被扯成扇形碎片。保持 Unity 默认。
    }

    /// 摩擦/反发力一样的刚体共用一份材质 —— 贝丝蒂 417 个刚体其实只有两三种组合。
    private PhysicMaterial Material(MmdBody body)
    {
        long key = ((long)Mathf.RoundToInt(body.friction * 1000f) << 32) | (uint)Mathf.RoundToInt(body.restitution * 1000f);
        if (_mats.TryGetValue(key, out PhysicMaterial m))
            return m;
        _mats[key] = m = new PhysicMaterial("bds")
        {
            dynamicFriction = body.friction,
            staticFriction = body.friction,
            bounciness = Mathf.Clamp01(body.restitution),
            frictionCombine = PhysicMaterialCombine.Multiply,        // Bullet 是两边相乘
            bounceCombine = PhysicMaterialCombine.Multiply,
        };
        return m;
    }

    /// Bullet 的阻尼是「每秒衰减到 (1-d) 倍」，PhysX 的 drag 是另一套公式。这里换算成等效值。
    private static float ToDrag(float damping)
    {
        float d = Mathf.Clamp(damping, 0f, 0.9999f);
        if (d <= 1e-5f)
            return 0f;
        float dt = PhysWorld.Step;
        return Mathf.Clamp((Mathf.Pow(1f - d, -dt) - 1f) / dt, 0f, 500f);
    }

    private static int Depth(Transform t)
    {
        int d = 0;
        while (t != null && d < 128) { t = t.parent; d++; }
        return d;
    }

    private void Reorder()
    {
        _order.Clear();
        foreach (Seg s in _segs.Values)
            _order.Add(s);
        _order.Sort((a, b) => a.Depth.CompareTo(b.Depth));   // 写回必须父在前、子在后
    }

    // ── 关节 ──────────────────────────────────────────────────────────────

    private int Link(string bundleKey)
    {
        int n = 0;
        float worst = 0f;
        string worstName = null;
        _asymBuilt = 0;
        foreach (MmdJoint j in _m.joints)
        {
            if (_joints.ContainsKey(j.i))
                continue;
            if (!_segs.TryGetValue(j.bodyA, out Seg a) || !_segs.TryGetValue(j.bodyB, out Seg b))
                continue;                                    // 另一头还没建（别的部位没加载），等它来了再补
            if (a.Rb == null || b.Rb == null)
                continue;
            if ((a.Proxy || b.Proxy) && a.TrueKind == MmdBodyKind.BoneFollow && b.TrueKind == MmdBodyKind.BoneFollow)
                continue;                                    // 两头都跟骨走（新做法的碰撞代理），关节没用（37.31）

            // 对账：这两块刚体在游戏里的相对姿态，和它们在 PMX 里的相对姿态差多少。
            // 差得多说明落位或拟合有问题 —— 关节的零点就是在这一刻被锁死的，这里错后面全错。
            Quaternion want = Quaternion.Inverse(_m.bodies[j.bodyA].Rot) * _m.bodies[j.bodyB].Rot;
            Quaternion have = Quaternion.Inverse(a.Rb.rotation) * b.Rb.rotation;
            float off = Quaternion.Angle(want, have);
            if (off > worst) { worst = off; worstName = j.name; }

            _joints[j.i] = Build(j, a.Rb, b.Rb);
            n++;
        }
        if (n > 0)
            Plugin.Log.LogInfo($"[mmd] {bundleKey}: 关节零点对账，最大偏差 {worst:F2}°（{worstName}）"
                               + (worst > 5f ? "  ← ⚠ 偏大，检查拟合残差" : "")
                               + (_asymBuilt > 0 ? $"；{_asymBuilt} 条非对称限位已用中位建关节补偿" : ""));
        return n;
    }

    private ConfigurableJoint Build(MmdJoint j, Rigidbody a, Rigidbody b)
    {
        // joint 的坐标系换算成「相对子刚体 B 的局部帧」—— 这一步纯在 PMX 空间做，
        // 和拟合无关，所以 A、B 就算来自不同的拟合组也不会歪。
        MmdBody pb = _m.bodies[j.bodyB];
        Quaternion invB = Quaternion.Inverse(pb.Rot);
        Vector3 anchor = invB * (j.Pos - pb.Pos);            // PMX 单位 = 布料世界单位，直接用
        Quaternion frame = invB * j.Rot;

        Vector3 lo = Vec.Of(j.angMin) * Mathf.Rad2Deg;
        Vector3 hi = Vec.Of(j.angMax) * Mathf.Rad2Deg;
        // ⚠️ 六个自由度全锁死（平移 0/0、角度 0/0、无弹簧）= 焊死，物理链上不可能是作者本意。
        //    可露凯 111 条 joint 里 102 条是这样：它在 MMD 里能甩，全靠 1e16 的质量把 Bullet 的约束压软——
        //    靠的是求解器误差。贝丝蒂的裙子关节角度也全锁，但平移有 ±1 单位，靠平移在摆，所以没事。
        //    这里放开成 ±freeAngle（调参台「锁死关节放开角度」，默认 45°），阻尼照 SetSprings 走。
        //    胸部关节（37.30）用调参台「胸部摆动角度」，不跟头发的放开角度走
        bool chest = _m.ChestJoints.Contains(j.i);
        float release = chest ? _m.Tuning.chestAngle : _m.Tuning.freeAngle;
        bool welded = release > 0f && !b.isKinematic && j.Welded;
        if (welded)
        {
            lo = -Vector3.one * release;
            hi = Vector3.one * release;
            (chest ? _weldedChest : _welded).Add(j.i);
        }
        Vector3 mid = Mid(lo, hi);

        // ── 非对称限位补偿 ──
        // Unity 的 Y/Z 只能写「±一个数」，写不了「外 30 内 10」。而 Unity 的限位零点 =
        // **AddComponent 那一瞬间两块刚体的相对姿态**。所以先把子刚体摆到限位区间的中点上，
        // 建完关节再摆回去，零点就被挪到中点了，±半宽正好等于原来的不对称区间。
        // ⚠️ 贝丝蒂 643 条 joint 全是对称的，这条分支在她身上一次都不会走 —— 等 OTs-14 实机验证。
        Quaternion save = b.transform.rotation;
        bool shifted = mid.sqrMagnitude > 1e-6f;
        if (shifted)
        {
            b.transform.rotation = save * (frame * Quaternion.Euler(mid) * Quaternion.Inverse(frame));
            _asymBuilt++;                                    // 不逐条报了（ots14 一穿就是 26 行黄字），Link 汇总成一句
        }

        var cj = b.gameObject.AddComponent<ConfigurableJoint>();
        cj.connectedBody = a;
        cj.autoConfigureConnectedAnchor = false;
        cj.anchor = anchor;
        cj.axis = frame * Vector3.right;
        cj.secondaryAxis = frame * Vector3.up;
        cj.enableCollision = false;                          // MMD 的 joint 两端本来就不互撞
        cj.enablePreprocessing = false;
        cj.projectionMode = _m.Tuning.projection ? JointProjectionMode.PositionAndRotation : JointProjectionMode.None;
        cj.projectionDistance = 0.2f;
        cj.projectionAngle = 25f;

        if (chest && !welded)
        {
            var c = new ChestBase
            {
                Lo = lo, Hi = hi, Mid = mid, LinLo = Vec.Of(j.linMin), LinHi = Vec.Of(j.linMax),
                LinSpring = Vec.Of(j.linSpring), AngSpring = Vec.Of(j.angSpring),
            };
            _sprungChest[j.i] = c;
            ApplySprungChest(cj, c);
        }
        else
        {
            SetLinear(cj, Vec.Of(j.linMin), Vec.Of(j.linMax));
            SetAngular(cj, lo, hi, mid);
            SetSprings(cj, Vec.Of(j.linSpring), Vec.Of(j.angSpring), _m.Tuning.springDamper);
        }
        if (welded)
            WeldSpring(cj, b.mass, chest);

        if (shifted)
            b.transform.rotation = save;
        cj.connectedAnchor = a.transform.InverseTransformPoint(b.transform.TransformPoint(anchor));
        return cj;
    }

    /// <summary>
    /// 放开的锁死关节要有「回到发型」的力，否则三束头发只剩重力、全落进同一块空间互相穿插打结
    /// （作者关了头发之间的碰撞，2026-08-27 实机）。作者在 MMD 里靠锁死关节保发型、靠求解器误差给一点晃，
    /// 这里用角弹簧复现：目标 = 静止姿态（Unity 关节零点就是建关节那一刻 = 静止姿态），
    /// 刚度按子刚体质量配（链上质量逐节减半，按质量配才能整条链一个频率），阻尼比走「弹簧阻尼」滑块（I ≈ 0.1·m，球半径 0.5 单位）。
    /// </summary>
    private void WeldSpring(ConfigurableJoint cj, float mass, bool chest)
    {
        MmdTuning t = _m.Tuning;
        float k = (chest ? t.chestSpring : t.weldSpring) * mass;
        // 阻尼 = 阻尼比 × 临界阻尼（2√(k·I)，I ≈ 0.1·m）。阻尼比用「弹簧阻尼」滑块（胸部用「胸部阻尼比」）：
        // 1 = 临界（刚好不冲过头），2.2（默认）= 过阻尼，转头时头发慢慢跟上、不甩。
        // ⚠️ 首版取 0.7（欠阻尼）：视角一转，弹簧把头发猛拽到新位置、梢部冲过头再弹回，看着像加速甩（2026-08-27 实机）。
        float c = Mathf.Max(0.1f, chest ? t.chestDamper : t.springDamper) * 2f * Mathf.Sqrt(k * 0.1f * mass);
        cj.rotationDriveMode = RotationDriveMode.XYAndZ;
        cj.targetRotation = Quaternion.identity;
        cj.angularXDrive = new JointDrive { positionSpring = k, positionDamper = c, maximumForce = float.MaxValue };
        cj.angularYZDrive = new JointDrive { positionSpring = k, positionDamper = c, maximumForce = float.MaxValue };
    }

    /// 原模型带弹簧的胸部关节（蕾娜，37.30）：限位绕中位按「胸部摆动幅度」缩放（0 = 锁住不动），弹簧乘「胸部回弹」，阻尼乘「胸部阻尼」。
    /// 建关节和调参台实时刷新都走这里，两条路算出同一个物理
    private void ApplySprungChest(ConfigurableJoint cj, ChestBase c)
    {
        MmdTuning t = _m.Tuning;
        float r = t.chestRange;
        SetLinear(cj, c.LinLo * r, c.LinHi * r);
        SetAngular(cj, c.Mid + (c.Lo - c.Mid) * r, c.Mid + (c.Hi - c.Mid) * r, c.Mid);
        SetSprings(cj, c.LinSpring * t.chestSpringScale, c.AngSpring * t.chestSpringScale, t.springDamper * t.chestDampScale);
    }

    /// 刚体阻尼的额外倍数：原模型带弹簧的胸部刚体乘「胸部阻尼」，别的 1
    private float DragScale(int body) => _m.ChestSprung && _m.ChestBodies.Contains(body) ? _m.Tuning.chestDampScale : 1f;

    private static Vector3 Mid(Vector3 lo, Vector3 hi)
    {
        return new Vector3(
            Free(lo.x, hi.x) || Locked(lo.x, hi.x) ? 0f : (lo.x + hi.x) * 0.5f,
            Free(lo.y, hi.y) || Locked(lo.y, hi.y) ? 0f : (lo.y + hi.y) * 0.5f,
            Free(lo.z, hi.z) || Locked(lo.z, hi.z) ? 0f : (lo.z + hi.z) * 0.5f);
    }

    // Bullet 的规矩：下限 > 上限 = 自由，下限 == 上限 = 锁死，否则限位。
    // ⚠️ 老代码把「全 0」当成自由，那是反的 —— 贝丝蒂 643 条里有 260 条旋转全 0（= 该锁死），
    // 当成自由就等于整条链散架，这是以前布料乱飞的主因之一。
    private static bool Free(float lo, float hi) => lo > hi;
    private static bool Locked(float lo, float hi) => Mathf.Abs(lo - hi) < 1e-6f;

    private static ConfigurableJointMotion Motion(float lo, float hi) =>
        Free(lo, hi) ? ConfigurableJointMotion.Free
        : Locked(lo, hi) ? ConfigurableJointMotion.Locked
        : ConfigurableJointMotion.Limited;

    private static void SetLinear(ConfigurableJoint cj, Vector3 lo, Vector3 hi)
    {
        cj.xMotion = Motion(lo.x, hi.x);
        cj.yMotion = Motion(lo.y, hi.y);
        cj.zMotion = Motion(lo.z, hi.z);
        // Unity 的平移限位只有**一个共用幅值**，写不了三轴各不同。贝丝蒂 255 条带平移限位的
        // joint 里 253 条本来就是三轴对称同幅值（±1 单位），所以取最大值几乎无损。
        float m = 0f;
        if (cj.xMotion == ConfigurableJointMotion.Limited) m = Mathf.Max(m, Mathf.Max(Mathf.Abs(lo.x), Mathf.Abs(hi.x)));
        if (cj.yMotion == ConfigurableJointMotion.Limited) m = Mathf.Max(m, Mathf.Max(Mathf.Abs(lo.y), Mathf.Abs(hi.y)));
        if (cj.zMotion == ConfigurableJointMotion.Limited) m = Mathf.Max(m, Mathf.Max(Mathf.Abs(lo.z), Mathf.Abs(hi.z)));
        if (m > 0f)
            cj.linearLimit = new SoftJointLimit { limit = m, bounciness = 0f, contactDistance = 0f };
    }

    private static void SetAngular(ConfigurableJoint cj, Vector3 lo, Vector3 hi, Vector3 mid)
    {
        cj.angularXMotion = Motion(lo.x, hi.x);
        cj.angularYMotion = Motion(lo.y, hi.y);
        cj.angularZMotion = Motion(lo.z, hi.z);
        if (cj.angularXMotion == ConfigurableJointMotion.Limited)
        {
            cj.lowAngularXLimit = new SoftJointLimit { limit = lo.x - mid.x };
            cj.highAngularXLimit = new SoftJointLimit { limit = hi.x - mid.x };
        }
        if (cj.angularYMotion == ConfigurableJointMotion.Limited)
            cj.angularYLimit = new SoftJointLimit { limit = (hi.y - lo.y) * 0.5f };
        if (cj.angularZMotion == ConfigurableJointMotion.Limited)
            cj.angularZLimit = new SoftJointLimit { limit = (hi.z - lo.z) * 0.5f };
    }

    /// <summary>
    /// PMX 的弹簧 = 「往静止姿态拉回去」的刚度。Unity 这边用 drive 实现（目标就是零点=静止姿态）。
    ///
    /// ⚠️ **damper 给所有关节，不管 PMX 写没写弹簧**（2026-08-26 定案，别再翻回去）：
    /// `spring=0 + damper>0` 是纯阻尼器。全部实机验收（贝丝蒂 08-23、阿斯缇亚 08-26）都是在
    /// 调参台的实时路径下调的，而那条路一直就是全关节上阻尼 —— **验收过的手感才是基准**。
    /// 首版建链只给有弹簧的关节上阻尼，结果「拖滑块的手感」和「重开之后的手感」是两回事：
    /// Tech Leader 调完 ots14 觉得对，重开就松了，值明明还在 json 里。
    /// 08-26 当天先往「忠于 PMX」的方向对齐过一次（实时也不给无弹簧关节阻尼），头发当场变样 ——
    /// 头发链大半没写弹簧，等于把调好的阻尼整段抽走。两条路必须一致，而基准是实时那条。
    /// </summary>
    private static void SetSprings(ConfigurableJoint cj, Vector3 lin, Vector3 ang, float damper)
    {
        cj.rotationDriveMode = RotationDriveMode.XYAndZ;
        cj.targetRotation = Quaternion.identity;
        cj.angularXDrive = new JointDrive { positionSpring = ang.x, positionDamper = damper, maximumForce = float.MaxValue };
        cj.angularYZDrive = new JointDrive { positionSpring = Mathf.Max(ang.y, ang.z),       // Unity 的 Y、Z 共用一个 drive
                                             positionDamper = damper, maximumForce = float.MaxValue };
        cj.targetPosition = Vector3.zero;
        cj.xDrive = new JointDrive { positionSpring = lin.x, positionDamper = damper, maximumForce = float.MaxValue };
        cj.yDrive = new JointDrive { positionSpring = lin.y, positionDamper = damper, maximumForce = float.MaxValue };
        cj.zDrive = new JointDrive { positionSpring = lin.z, positionDamper = damper, maximumForce = float.MaxValue };
    }

    // ── 跑 ────────────────────────────────────────────────────────────────

    private Vector3 WorldPos(Seg s) => s.Bone.position + s.Bone.rotation * s.LocalPos;

    private Quaternion WorldRot(Seg s) => s.Bone.rotation * s.LocalRot;

    /// 推物理之前：骨骼追随刚体贴到骨上；动态刚体加重力。
    internal void PreStep()
    {
        if (!_awake)
            return;
        Track();
        Vector3 gravity = new Vector3(0f, -_m.Tuning.gravity, 0f);
        for (int i = 0; i < _order.Count; i++)
        {
            Seg s = _order[i];
            if (s.Rb == null || s.Bone == null)
                continue;
            if (s.Kind == MmdBodyKind.BoneFollow)
            {
                s.Rb.MovePosition(ToCloth(WorldPos(s)));
                s.Rb.MoveRotation(WorldRot(s));
                continue;
            }
            if (s.Kind == MmdBodyKind.DynamicBonePos)
                s.Rb.position = ToCloth(WorldPos(s));
            s.Rb.AddForce(gravity, ForceMode.Acceleration);
        }
    }

    /// <summary>
    /// 让参考系跟上角色。跟得越紧（「跟随强度」越大），角色移动能传给布料的力就越少。
    ///
    /// ⚠️ **这里只动参考系，一个刚体都不碰** —— 那正是它比上一版「瞬移布料」正确的地方：
    /// 瞬移只挪了布料的位置，身体碰撞体照样带着满速度扫过来把布料拍飞；
    /// 挪参考系则让碰撞体在这个系里**根本没动**，速度自然就是 0。
    /// </summary>
    private void Track()
    {
        if (_ref == null)
            return;
        Vector3 now = _ref.position;
        if (!_trackInit)
        {
            _lastRefPos = now;
            _trackInit = true;
            return;
        }
        // 水平和竖直分开跟：水平是**玩家操作**产生的力（前进后退平移），剥掉；转身**不**剥，坐标轴必须与世界对齐；
        // 竖直是走路周期的**上下颠簸**，那正是少前2 里头发弹动的来源，留着才好看。
        float k = _m.Tuning.follow;
        float ky = _m.Tuning.followUp;
        Vector3 d = now - _lastRefPos;
        _framePos += new Vector3(d.x * k, d.y * ky, d.z * k);
        _lastRefPos = now;
    }



    /// <summary>
    /// 把本模型 tuning 里的每一个数重新刷到已经建好的刚体和关节上 —— **调参台拖滑块时实时生效**。
    ///
    /// 能这么做的前提，也正是「每套模型独立」能成立的原因：这些参数全都写在**刚体/关节**上，
    /// 运行时改一下就生效。写在**物理场景**上的（步频）才做不到，所以那个留在全局。
    /// </summary>
    internal void ApplyTuning()
    {
        MmdTuning t = _m.Tuning;
        foreach (Seg s in _segs.Values)
        {
            if (s.Rb == null)
                continue;
            s.Rb.maxAngularVelocity = t.maxSpin;
            s.Rb.maxDepenetrationVelocity = t.separate;
            s.Rb.solverIterations = t.solverIters;
            s.Rb.solverVelocityIterations = Mathf.Max(1, t.solverIters / 3);
            if (s.Kind != MmdBodyKind.BoneFollow)            // 按「是不是布料」判，别按 isKinematic —— 被距离剔除冻住的布料也是 kinematic
            {
                s.Rb.mass = MassOf(_m.bodies[s.Body]);
                s.Rb.drag = s.BaseDrag * t.damping * DragScale(s.Body);
                s.Rb.angularDrag = s.BaseAngDrag * t.damping * DragScale(s.Body);
            }
            Collider col = s.Rb.GetComponent<Collider>();
            if (col != null)
                Layers(s.Rb.gameObject, col, _m.bodies[s.Body]);
        }
        foreach (KeyValuePair<int, ConfigurableJoint> kv in _joints)
        {
            ConfigurableJoint j = kv.Value;
            if (j == null)
                continue;
            bool chestWeld = _weldedChest.Contains(kv.Key);
            if (chestWeld || _welded.Contains(kv.Key))
            {
                Release(j, chestWeld ? t.chestAngle : t.freeAngle, chestWeld);
                continue;                                    // 下面那段 Damped() 会把回弹刚度冲掉，焊死关节不走它
            }
            j.projectionMode = t.projection ? JointProjectionMode.PositionAndRotation : JointProjectionMode.None;
            if (_sprungChest.TryGetValue(kv.Key, out ChestBase c))
            {
                ApplySprungChest(j, c);                      // 胸部（蕾娜）：限位、弹簧、阻尼都按调参台的倍数重算
                continue;
            }
            // 弹簧刚度是 PMX 原值、不该动，这里只换 damper —— 读回来改一个字段就行，不用重算。
            j.angularXDrive = Damped(j.angularXDrive, t.springDamper);
            j.angularYZDrive = Damped(j.angularYZDrive, t.springDamper);
            j.xDrive = Damped(j.xDrive, t.springDamper);
            j.yDrive = Damped(j.yDrive, t.springDamper);
            j.zDrive = Damped(j.zDrive, t.springDamper);
        }
    }

    /// 放开的锁死关节：限位 ±angle、回弹按质量配（头发跟「锁死关节放开角度 / 回弹」，胸部跟「胸部摆动角度 / 回弹」，37.30）
    private void Release(ConfigurableJoint j, float angle, bool chest)
    {
        float a = Mathf.Max(0.01f, angle);
        j.angularXMotion = j.angularYMotion = j.angularZMotion = ConfigurableJointMotion.Limited;
        j.lowAngularXLimit = new SoftJointLimit { limit = -a };
        j.highAngularXLimit = new SoftJointLimit { limit = a };
        j.angularYLimit = new SoftJointLimit { limit = a };
        j.angularZLimit = new SoftJointLimit { limit = a };
        Rigidbody rb = j.GetComponent<Rigidbody>();
        if (rb != null)
            WeldSpring(j, rb.mass, chest);
    }

    /// 只换 damper、不动 PMX 原值的刚度。全关节无差别上阻尼 —— 和 <see cref="SetSprings"/> 一字一样，
    /// 两条路必须算出同一个物理（为什么全关节都给，见 SetSprings 上那段账）。
    private static JointDrive Damped(JointDrive d, float damper) => new JointDrive
    {
        positionSpring = d.positionSpring,
        positionDamper = damper,
        maximumForce = d.maximumForce,
    };

    /// 刚体质量 = PMX 值 × 调参台「质量倍率」，夹进 [0.001, MassCap]（离谱的在 MmdModel.Index 里已转成骨骼追随）。
    private float MassOf(MmdBody body) =>
        Mathf.Clamp(body.mass * _m.Tuning.massScale, 0.001f, _m.MassCap);

    /// 推完物理：把刚体的位姿写回骨。父骨先写、子骨后写。
    internal void PostStep()
    {
        if (!_awake)
            return;
        for (int i = 0; i < _order.Count; i++)
        {
            Seg s = _order[i];
            if (s.Rb == null || s.Bone == null || s.Kind == MmdBodyKind.BoneFollow)
                continue;
            Quaternion rot = s.Rb.rotation * Quaternion.Inverse(s.LocalRot);
            if (s.Kind != MmdBodyKind.Dynamic)
            {
                s.Bone.rotation = rot;                        // 位置跟骨、只有旋转来自物理
                continue;
            }
            Vector3 want = FromCloth(s.Rb.position) - rot * s.LocalPos;
            if (Sane(s, want))
                s.Bone.SetPositionAndRotation(want, rot);
        }
        // 记下这次写回后的局部位姿（父骨都写完了再读，局部才对），给 Interp 用
        for (int i = 0; i < _order.Count; i++)
        {
            Seg s = _order[i];
            if (s.Rb == null || s.Bone == null || s.Kind == MmdBodyKind.BoneFollow)
                continue;
            s.PrevLocalPos = s.HasPose ? s.CurLocalPos : s.Bone.localPosition;
            s.PrevLocalRot = s.HasPose ? s.CurLocalRot : s.Bone.localRotation;
            s.CurLocalPos = s.Bone.localPosition;
            s.CurLocalRot = s.Bone.localRotation;
            s.HasPose = true;
        }
    }

    /// <summary>
    /// 每帧调（37.31）：骨摆在「上一次写回」和「这一次写回」之间，按累积到下一步的比例插。
    /// 物理改 60Hz 以后，高帧率下头发 / 胸部每秒只变 60 次、看着一顿一顿（Tech Leader：「不顺畅」「说不上来的怪」）；
    /// 插在局部空间：角色在两步之间走的那段由父骨带着走，不会拖尾。代价是画面晚一步（60Hz 时 ≤ 17 ms）
    /// </summary>
    internal void Interp(float alpha)
    {
        if (!_awake)
            return;
        for (int i = 0; i < _order.Count; i++)
        {
            Seg s = _order[i];
            if (!s.HasPose || s.Rb == null || s.Bone == null || s.Kind == MmdBodyKind.BoneFollow)
                continue;
            if (s.Kind == MmdBodyKind.Dynamic)
                s.Bone.localPosition = Vector3.LerpUnclamped(s.PrevLocalPos, s.CurLocalPos, alpha);
            s.Bone.localRotation = Quaternion.Slerp(s.PrevLocalRot, s.CurLocalRot, alpha);
        }
    }

    /// <summary>
    /// 写回前的安全闸：一根布料骨**绝不可能**离它父骨两米远。
    ///
    /// 一根骨飞出去，蒙在它上面的顶点就会被从正确位置一路拉到那儿，在画面上是一条又长又细的**黑刺**
    /// （DEV_NOTES 十三/十五都记过这个现象）。这里拦下来 + 报出**是哪根骨**，
    /// 免得只能靠猜。NaN 也一起拦 —— 物理一旦炸出 NaN，写进 Transform 会污染整条骨骼链。
    /// </summary>
    private bool Sane(Seg s, Vector3 want)
    {
        if (float.IsNaN(want.x) || float.IsInfinity(want.x))
        {
            Complain(s, want, -1f);
            return false;
        }
        Transform par = s.Bone.parent;
        if (par == null)
            return true;
        float d = (want - par.position).magnitude;
        if (d <= 2f)
            return true;
        Complain(s, want, d);
        return false;
    }

    private void Complain(Seg s, Vector3 want, float dist)
    {
        if (_complaints >= 10)
            return;
        _complaints++;
        string name = _m.BoneName(_m.bodies[s.Body].bone) ?? "?";
        string how = dist < 0f ? "算出了 NaN/无穷" : $"离父骨 {dist:F1} m";
        Plugin.Log.LogError($"[mmd] 骨 {name}（刚体 {_m.bodies[s.Body].name}）{how}，已拦下不写回。"
                            + $" 目标 {want}，父骨 {s.Bone.parent.name} @ {s.Bone.parent.position}"
                            + (_complaints == 10 ? "（后续同类不再刷屏）" : ""));
    }

    /// 角色瞬移（换地图 / 复活 / 传送）时整套复位，否则布料会被"拽"出一条几百米的甩尾。
    internal bool CheckTeleport()
    {
        if (_ref == null)
            return false;
        Vector3 p = _ref.position;
        bool jumped = _awake && (p - _lastRef).sqrMagnitude > 4f;
        _lastRef = p;
        if (jumped)
            Reset();
        return jumped;
    }

    internal void Reset()
    {
        if (!_awake)
            Freeze(false);          // 被距离剔除冻住的刚体先放开，下面才能给它们摆位、清速度
        _awake = true;
        _trackInit = false;         // ⚠️ 不清的话，瞬移后 Track() 会把那一大步当成本帧位移
        if (_ref != null)
        {
            _framePos = _ref.position;      // 参考系直接对齐，复位后不留任何残余相对运动
        }
        for (int i = 0; i < _order.Count; i++)
        {
            Seg s = _order[i];
            s.HasPose = false;                               // 插值从头记，别从复位前的姿势插过来
            if (s.Bone == null || s.Kind == MmdBodyKind.BoneFollow)
                continue;
            s.Bone.localPosition = s.RestLocalPos;            // 先把骨摆回静止姿态，再让刚体贴上去
            s.Bone.localRotation = s.RestLocalRot;
        }
        for (int i = 0; i < _order.Count; i++)
        {
            Seg s = _order[i];
            if (s.Rb == null || s.Bone == null)
                continue;
            s.Rb.position = ToCloth(WorldPos(s));
            s.Rb.rotation = WorldRot(s);
            if (!s.Rb.isKinematic)
            {
                s.Rb.velocity = Vector3.zero;
                s.Rb.angularVelocity = Vector3.zero;
            }
        }
        _sway?.Reset();                                      // 新做法的骨同样摆回原形、粒子清零
        if (_ref != null)
            _lastRef = _ref.position;
    }

    /// 浮动原点：角色跑远了就把原点挪过来、所有刚体整体平移一次。整体平移不改变任何物理量。
    internal void KeepNear()
    {
        if (_ref == null || (_ref.position - _framePos).sqrMagnitude < 50f * 50f)
            return;
        Vector3 delta = _ref.position - _framePos;
        _framePos = _ref.position;
        Vector3 d = delta * _upm;
        foreach (Seg s in _segs.Values)
            if (s.Rb != null)
                s.Rb.position -= d;
    }

    internal bool IsAwake => _awake;

    internal void SetAwake(bool on)
    {
        if (_awake == on)
            return;
        if (on)
        {
            Reset();                                         // Reset 里先解冻、再把 _awake 置真
            return;
        }
        _awake = false;
        Freeze(true);
    }

    /// <summary>
    /// 距离剔除的真正开关：睡着时把动态刚体转成 kinematic，醒来再放开。
    ///
    /// ⚠️ 首版睡着只是跳过 PreStep / PostStep —— 骨不写了，但刚体还活在场景里，
    /// `Simulate()` 每一步照样给它们解关节、算碰撞（而且没人加重力，布料在原地飘着），PhysX 的开销一分没省。
    /// 不动的 kinematic 刚体在 PhysX 里基本零成本：不积分、不进求解器，两头都是 kinematic 的关节直接不算。
    /// 不用 SetActive(false)：物体一停用关节就被拆掉，重新启用时零点按**那一刻**的姿态重记，
    /// 非对称限位补偿（Build 里先摆到中位再建关节）会跟着丢。改 isKinematic 不碰关节，<see cref="Anchor"/> 一直就是这么用的。
    /// </summary>
    private void Freeze(bool frozen)
    {
        foreach (Seg s in _segs.Values)
        {
            if (s.Rb == null || s.Kind == MmdBodyKind.BoneFollow)
                continue;                                    // 骨骼追随体（含 Anchor 兜底转过来的）本来就是 kinematic
            s.Rb.isKinematic = frozen;
            s.Rb.collisionDetectionMode = frozen
                ? CollisionDetectionMode.Discrete
                : CollisionDetectionMode.ContinuousSpeculative;
        }
    }

    // ── 拆 ────────────────────────────────────────────────────────────────

    /// 换装：只拆掉这个部位建的刚体，别人的留着。
    internal void RemovePart(int owner)
    {
        var gone = new List<int>();
        foreach (KeyValuePair<int, Seg> kv in _segs)
            if (kv.Value.Owner == owner)
                gone.Add(kv.Key);
        var dead = new List<int>();
        foreach (MmdJoint j in _m.joints)
            if (_joints.ContainsKey(j.i) && (gone.Contains(j.bodyA) || gone.Contains(j.bodyB)))
                dead.Add(j.i);
        foreach (int i in dead)
        {
            if (_joints[i] != null)
                Object.Destroy(_joints[i]);
            _joints.Remove(i);                               // 对端重新加载时会自动重建
        }
        foreach (int i in gone)
        {
            Destroy(_segs[i]);
            _segs.Remove(i);
        }
        Reorder();
        if (_sway != null)
        {
            _sway.SetColliders(SwayColliders());             // 这个部位的身体碰撞体也可能拆了
            _sway.RemoveOwner(owner);
            _sway.Link(_upm);
        }
    }

    internal void DestroyAll()
    {
        foreach (ConfigurableJoint cj in _joints.Values)
            if (cj != null)
                Object.Destroy(cj);
        _joints.Clear();
        foreach (Seg s in _segs.Values)
            Destroy(s);
        _segs.Clear();
        _order.Clear();
        _sway = null;                                        // 骨由 InjectionRegistry 跟着链根一起拆
        PhysWorld.FreeSlot(_slotIndex);
    }

    private static void Destroy(Seg s)
    {
        if (s.Rb != null)
            Object.Destroy(s.Rb.gameObject);                 // 挂在它身上的关节跟着一起没
        s.Rb = null;
    }
}
