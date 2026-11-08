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
/// 碰撞组 → 层的全局表在 <see cref="MmdLayers"/>。
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

    private readonly MmdModel _m;
    private readonly Dictionary<int, Seg> _segs = new Dictionary<int, Seg>();
    private readonly List<Seg> _order = new List<Seg>();     // 按骨骼层级深度排好的写回顺序
    private readonly Dictionary<int, ConfigurableJoint> _joints = new Dictionary<int, ConfigurableJoint>();
    private readonly HashSet<int> _welded = new HashSet<int>();      // 六自由度全锁死的链关节（见 Build），放开角度跟调参台走
    private readonly Dictionary<long, PhysicMaterial> _mats = new Dictionary<long, PhysicMaterial>();
    private readonly int _slotIndex;
    private readonly Vector3 _slot;
    private float _upm = 12.5f;                              // 1 米 = 多少 PMX 单位
    private Transform _ref;
    private Vector3 _lastRef;
    private bool _awake;
    private int _complaints;

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
    internal void AddPart(int owner, MmdSurvey survey, HashSet<string> partBones, Transform reference, string bundleKey,
                          List<ClothSway.Input> sway = null, ClothSway.Body legs = null)
    {
        sway?.RemoveAll(c => c.Bones.Length < 2 && !FillTail(c, survey));   // 一节骨的胸：补不出尖端就照旧 PMX
        var handed = new HashSet<string>();
        if (sway != null)
            foreach (ClothSway.Input c in sway)
                foreach (string n in c.Names)
                    handed.Add(n);
        MmdLayers.Ensure();
        _upm = 1f / Mathf.Max(1e-4f, survey.MetersPerUnit);
        if (_ref == null)
            _ref = reference;
        if (_segs.Count == 0 && _ref != null && !_frameSet)
        {
            _framePos = _ref.position;                       // 还没建刚体，参考系直接对齐到角色身上
            _frameSet = true;
        }
        BuildBodies(owner, survey, partBones, handed, bundleKey);
        // ⚠️ 顺序不能换：Unity 的关节限位零点 = **AddComponent 那一瞬间两块刚体的相对姿态**。
        // 所以必须先把骨复位到静止姿态、刚体贴上去，再建关节，零点才等于 MMD 的静止姿态。
        Reorder();
        Reset();
        Link(bundleKey);
        Anchor(bundleKey);
        WarnFar(bundleKey);
        if (sway != null && sway.Count > 0)
            AddSway(sway, legs);
    }

    /// <summary>
    /// 这个部位的刚体：自己的动态刚体 + 全身共用的碰撞体（别的部位已经建过的跳过）。
    /// 交给新做法的骨：不建会动的 PhysX 刚体。头发会碰到它的话，留一个跟着骨走的碰撞代理（kinematic），
    /// 不然长头发（贝丝蒂的后发、阿斯缇亚的头发）会直接穿过外套 / 披风沉到身体碰撞体上（第二步加的，37.31）。
    /// 建不起来的刚体跳过，报一次第一条原因
    /// </summary>
    private void BuildBodies(int owner, MmdSurvey survey, HashSet<string> partBones, HashSet<string> handed, string bundleKey)
    {
        int skipped = 0;
        string firstWhy = null;
        foreach (MmdBody body in _m.bodies)
        {
            if (_segs.ContainsKey(body.i))
                continue;                                    // 别的部位已经建过（碰撞体是全身共用的）
            string bn = _m.BoneName(body.bone);
            bool collider = body.Kind == MmdBodyKind.BoneFollow;
            if (!collider && (bn == null || !partBones.Contains(bn)))
                continue;                                    // 动态刚体只归它自己那个部位
            bool proxy = !collider && handed.Contains(bn);
            if (proxy && !NeedsProxy(body))
                continue;
            if (!survey.Resolve(body, out Transform bone, out Vector3 lp, out Quaternion lr, out string why))
            {
                skipped++;
                firstWhy = firstWhy ?? why;
                continue;
            }
            _segs[body.i] = Make(owner, body, bone, lp, lr, proxy);
        }
        if (skipped > 0)
            LogOnce.Warn($"[mmd] {bundleKey}: {skipped} rigid bodies skipped, first reason: {firstWhy}");
    }

    /// 布料世界里离原点太远（> 3000 单位）的刚体：float 精度不够、关节会漂。车位号没回收的时候出过（87 号车位，17540 单位）
    private void WarnFar(string bundleKey)
    {
        float far = 0f;
        foreach (Seg s in _segs.Values)
            if (s.Rb != null)
                far = Mathf.Max(far, s.Rb.position.magnitude);
        if (far > 3000f)
            LogOnce.Warn($"[mmd] {bundleKey}: a rigid body is {far:F0} units from the cloth world origin, too far for float precision (joints will drift)",
                         bundleKey + "/far");
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
    private void AddSway(List<ClothSway.Input> inputs, ClothSway.Body legs)
    {
        if (_sway == null)
            _sway = new ClothSway(k => _m.Tuning.Part(k));
        _sway.SetBody(legs);
        _sway.SetColliders(SwayColliders());
        foreach (ClothSway.Input c in inputs)
        {
            FillPmx(c);
            _sway.Add(c);
        }
        _sway.Link(_upm);
        _sway.Reset();
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
            Vector3 pmxAxis = b.Rot * axis;
            list.Add(new ClothSway.Col
            {
                Bone = s.Bone, LocalPos = s.LocalPos, LocalRot = s.LocalRot, Axis = axis,
                Radius = r / _upm, Half = half / _upm, Group = Mathf.Clamp(b.group, 0, 15), Mask = b.collidesWith,
                PmxA = b.Pos + pmxAxis * half, PmxB = b.Pos - pmxAxis * half, PmxRadius = r,
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
        MmdBody first = ChainBodies(c, out int mask);
        if (first != null)
        {
            c.Group = Mathf.Clamp(first.group, 0, 15);
            c.Mask = mask;
        }
        bool tail = c.Tail.sqrMagnitude > 1e-6f && first != null;
        c.PmxPos = new Vector3[c.Names.Length + (tail ? 1 : 0)];
        for (int i = 0; i < c.Names.Length; i++)
            c.PmxPos[i] = _m.BoneByName.TryGetValue(c.Names[i], out int bi) ? _m.BonePos(bi) : Vector3.positiveInfinity;
        if (tail)
            c.PmxPos[c.Names.Length] = first.Pos;
    }

    /// 链上的 PMX 动态刚体：返回第一个（没有 = null），mask = 它们碰撞掩码的并集（一次遍历拿到两样）
    private MmdBody ChainBodies(ClothSway.Input c, out int mask)
    {
        var names = new HashSet<string>(c.Names);
        MmdBody first = null;
        mask = 0;
        foreach (MmdBody b in _m.bodies)
        {
            string bn = _m.BoneName(b.bone);
            if (b.Kind == MmdBodyKind.BoneFollow || bn == null || !names.Contains(bn))
                continue;
            first = first ?? b;
            mask |= b.collidesWith;
        }
        return first;
    }

    /// <summary>
    /// 一节骨的链（六套少前 2 解包的胸：一边一个 Chest_L/R 球刚体）补虚拟尖端：尖端 = PMX 里那个刚体的中心，按点云拟合落到这根骨上（米、骨的朝向空间）。
    /// 刚体就在骨原点上（< 5 mm）的定不出方向，返回 false，这条链照旧 PMX
    /// </summary>
    private bool FillTail(ClothSway.Input c, MmdSurvey survey)
    {
        MmdBody b = ChainBodies(c, out int _);
        if (b == null || !survey.Resolve(b, out Transform bone, out Vector3 lp, out Quaternion _, out string _) || bone != c.Bones[0])
            return false;
        if (lp.sqrMagnitude < 0.005f * 0.005f)
            return false;
        c.Tail = lp;
        return true;
    }

    /// 每帧一次（PhysWorld.Tick 里 PhysX 写回之后）。睡着（距离剔除）的角色不算
    internal void SwayTick(float dt)
    {
        if (_awake && _sway != null)
            _sway.Tick(dt);
    }

    /// <summary>
    /// **没有任何关节连着的动态刚体，一律改成骨骼追随。**
    ///
    /// 一个动态刚体如果一条 joint 都没接上，它就只剩重力 —— 直接自由落体，
    /// 把蒙在它上面的顶点拉成一条几十米的黑刺（2026-08-23 贝丝蒂的蝴蝶结实锤）。
    /// 这种情况的根因通常是**它的固定端刚体没建起来**，而那往往是数据/映射问题；
    /// 这里做的是兜底：宁可这几根骨不动，也绝不让它们飞出去。
    /// ⚠️ 必须可逆：head 先建时 upper 的刚体还不存在，同一根骨可能这一轮"孤立"、
    /// 下一轮就接上关节了。钉死不还原的话就永久不动了。
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
            bool pin = !linked.Contains(s.Body);
            if (pin == (s.Kind == MmdBodyKind.BoneFollow))
                continue;                                    // 已经是该有的样子
            s.Kind = pin ? MmdBodyKind.BoneFollow : s.TrueKind;
            SetKinematic(s.Rb, pin);
            if (pin)
                loose.Add(_m.bodies[s.Body].name);
        }
        if (loose.Count > 0)
            LogOnce.Warn($"[mmd] {bundleKey}: {loose.Count} dynamic rigid bodies have no joint and were pinned to their bones so they don't fall: "
                         + string.Join(" ", loose.ToArray()) + ". Usually the body they hang from was not built - check that end");
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
            BaseDrag = ToDrag(body.linearDamping),
            BaseAngDrag = ToDrag(body.angularDamping),
        };
        var go = new GameObject("bds_" + (body.name ?? body.i.ToString()));
        go.transform.SetPositionAndRotation(ToCloth(WorldPos(seg)), WorldRot(seg));
        PhysWorld.Adopt(go);
        AddCollider(go, body);                               // 层也在这里定（MmdLayers.Apply）
        var rb = go.AddComponent<Rigidbody>();
        rb.useGravity = false;                               // 重力我们自己加，见 PhysWorld（Unity 的是全局的，动不得）
        rb.interpolation = RigidbodyInterpolation.None;
        rb.sleepThreshold = 0f;                              // MMD 的布料永远不睡
        SetKinematic(rb, seg.Kind == MmdBodyKind.BoneFollow);
        seg.Rb = rb;
        ApplyBody(seg);
        return seg;
    }

    /// <summary>
    /// 刚体上跟手感参数有关的几项：建的时候设一次，开发版调参台拖滑块时再刷一次（两条路共用这一份，2026-11-09 并的）。
    /// 质量 / 阻尼只给布料设 —— 按「是不是布料」判，别按 isKinematic：被距离剔除冻住的布料也是 kinematic；骨骼追随体是 kinematic，PhysX 不看它的质量
    /// </summary>
    private void ApplyBody(Seg s)
    {
        MmdTuning t = _m.Tuning;
        Rigidbody rb = s.Rb;
        rb.maxAngularVelocity = t.maxSpin;
        // ⚠️ 「顶飞」的真凶：两块布料压在一起时 PhysX 会强行把它们推开，
        // Unity 默认上限 10 单位/s ≈ 1 m/s。蹲下时外套和裙子被躯干挤到一起，穿透一深就弹飞。
        rb.maxDepenetrationVelocity = t.separate;
        rb.solverIterations = t.solverIters;
        rb.solverVelocityIterations = Mathf.Max(1, t.solverIters / 3);
        if (s.Kind == MmdBodyKind.BoneFollow)
            return;
        rb.mass = MassOf(_m.bodies[s.Body]);
        rb.drag = s.BaseDrag * t.damping;
        rb.angularDrag = s.BaseAngDrag * t.damping;
    }

    /// <summary>
    /// kinematic 和碰撞检测方式成对改（建、Anchor 兜底、距离剔除冻住 / 解冻都走这里）：
    /// 裙板只有 8~10 mm 厚，Discrete 在角色跑动时会被直接穿过去（这就是"穿模"）。
    /// Speculative 连续检测对薄物体便宜又有效；kinematic 的（骨骼追随体、冻住的布料）用 Discrete。
    /// </summary>
    private static void SetKinematic(Rigidbody rb, bool on)
    {
        rb.isKinematic = on;
        rb.collisionDetectionMode = on ? CollisionDetectionMode.Discrete : CollisionDetectionMode.ContinuousSpeculative;
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

        MmdLayers.Apply(go, col, body, _m.Tuning.clothNoSelf);
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

    /// 两头刚体都在了的关节建起来（另一头还没建的等它的部位加载了再补）。
    /// 对账：这两块刚体在游戏里的相对姿态和 PMX 里的差多少 —— 关节的零点就是在这一刻被锁死的，差 5° 以上说明落位或拟合有问题，报一次
    private void Link(string bundleKey)
    {
        float worst = 0f;
        string worstName = null;
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
            Quaternion want = Quaternion.Inverse(_m.bodies[j.bodyA].Rot) * _m.bodies[j.bodyB].Rot;
            Quaternion have = Quaternion.Inverse(a.Rb.rotation) * b.Rb.rotation;
            float off = Quaternion.Angle(want, have);
            if (off > worst) { worst = off; worstName = j.name; }
            _joints[j.i] = Build(j, a.Rb, b.Rb);
        }
        if (worst > 5f)
            LogOnce.Warn($"[mmd] {bundleKey}: joint rest pose is off by up to {worst:F2}° ({worstName}), check the fit residuals",
                         bundleKey + "/joints");
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
        bool welded = Unweld(j, b, ref lo, ref hi);
        Vector3 mid = Mid(lo, hi);

        // ── 非对称限位补偿 ──
        // Unity 的 Y/Z 只能写「±一个数」，写不了「外 30 内 10」。而 Unity 的限位零点 =
        // **AddComponent 那一瞬间两块刚体的相对姿态**。所以先把子刚体摆到限位区间的中点上，
        // 建完关节再摆回去，零点就被挪到中点了，±半宽正好等于原来的不对称区间。
        Quaternion save = b.transform.rotation;
        bool shifted = mid.sqrMagnitude > 1e-6f;
        if (shifted)
            b.transform.rotation = save * (frame * Quaternion.Euler(mid) * Quaternion.Inverse(frame));

        ConfigurableJoint cj = NewJoint(a, b, anchor, frame);
        SetLinear(cj, Vec.Of(j.linMin), Vec.Of(j.linMax));
        SetAngular(cj, lo, hi, mid);
        SetSprings(cj, Vec.Of(j.linSpring), Vec.Of(j.angSpring), _m.Tuning.springDamper);
        if (welded)
            WeldSpring(cj, b.mass);

        if (shifted)
            b.transform.rotation = save;
        cj.connectedAnchor = a.transform.InverseTransformPoint(b.transform.TransformPoint(anchor));
        return cj;
    }

    /// <summary>
    /// ⚠️ 六个自由度全锁死（平移 0/0、角度 0/0、无弹簧）= 焊死，物理链上不可能是作者本意。
    /// 可露凯 111 条 joint 里 102 条是这样：它在 MMD 里能甩，全靠 1e16 的质量把 Bullet 的约束压软——
    /// 靠的是求解器误差。贝丝蒂的裙子关节角度也全锁，但平移有 ±1 单位，靠平移在摆，所以没事。
    /// 这里放开成 ±freeAngle（调参台「锁死关节放开角度」，默认 45°），阻尼照 SetSprings 走。放开了返回 true
    /// </summary>
    private bool Unweld(MmdJoint j, Rigidbody b, ref Vector3 lo, ref Vector3 hi)
    {
        float release = _m.Tuning.freeAngle;
        if (release <= 0f || b.isKinematic || !j.Welded)
            return false;
        lo = -Vector3.one * release;
        hi = Vector3.one * release;
        _welded.Add(j.i);
        return true;
    }

    /// 关节本体：挂在子刚体 B 上、连父刚体 A，锚点 / 轴按 PMX 关节的坐标系；两端不互撞；防拉散照 tuning
    private ConfigurableJoint NewJoint(Rigidbody a, Rigidbody b, Vector3 anchor, Quaternion frame)
    {
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
        return cj;
    }

    /// <summary>
    /// 放开的锁死关节要有「回到发型」的力，否则三束头发只剩重力、全落进同一块空间互相穿插打结
    /// （作者关了头发之间的碰撞，2026-08-27 实机）。作者在 MMD 里靠锁死关节保发型、靠求解器误差给一点晃，
    /// 这里用角弹簧复现：目标 = 静止姿态（Unity 关节零点就是建关节那一刻 = 静止姿态），
    /// 刚度按子刚体质量配（链上质量逐节减半，按质量配才能整条链一个频率），阻尼比走「弹簧阻尼」滑块（I ≈ 0.1·m，球半径 0.5 单位）。
    /// </summary>
    private void WeldSpring(ConfigurableJoint cj, float mass)
    {
        MmdTuning t = _m.Tuning;
        float k = t.weldSpring * mass;
        // 阻尼 = 阻尼比 × 临界阻尼（2√(k·I)，I ≈ 0.1·m）。阻尼比用「弹簧阻尼」滑块：
        // 1 = 临界（刚好不冲过头），2.2（默认）= 过阻尼，转头时头发慢慢跟上、不甩。
        // ⚠️ 首版取 0.7（欠阻尼）：视角一转，弹簧把头发猛拽到新位置、梢部冲过头再弹回，看着像加速甩（2026-08-27 实机）。
        float c = Mathf.Max(0.1f, t.springDamper) * 2f * Mathf.Sqrt(k * 0.1f * mass);
        cj.rotationDriveMode = RotationDriveMode.XYAndZ;
        cj.targetRotation = Quaternion.identity;
        cj.angularXDrive = new JointDrive { positionSpring = k, positionDamper = c, maximumForce = float.MaxValue };
        cj.angularYZDrive = new JointDrive { positionSpring = k, positionDamper = c, maximumForce = float.MaxValue };
    }

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

#if SORA_DEV
    internal string Model => _m.Name;

    /// 调参台那行「这套身上有几条」
    internal int SwayCount(SwayKind kind) => _sway != null ? _sway.CountKind(kind) : 0;

    /// <summary>
    /// 把本模型 tuning 里的每一个数重新刷到已经建好的刚体和关节上 —— **调参台拖滑块时实时生效**（开发版）。
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
            ApplyBody(s);
            Collider col = s.Rb.GetComponent<Collider>();
            if (col != null)
                MmdLayers.Apply(s.Rb.gameObject, col, _m.bodies[s.Body], t.clothNoSelf);
        }
        foreach (KeyValuePair<int, ConfigurableJoint> kv in _joints)
        {
            ConfigurableJoint j = kv.Value;
            if (j == null)
                continue;
            if (_welded.Contains(kv.Key))
            {
                Release(j, t.freeAngle);
                continue;                                    // 下面那段 Damped() 会把回弹刚度冲掉，焊死关节不走它
            }
            j.projectionMode = t.projection ? JointProjectionMode.PositionAndRotation : JointProjectionMode.None;
            // 弹簧刚度是 PMX 原值、不该动，这里只换 damper —— 读回来改一个字段就行，不用重算。
            j.angularXDrive = Damped(j.angularXDrive, t.springDamper);
            j.angularYZDrive = Damped(j.angularYZDrive, t.springDamper);
            j.xDrive = Damped(j.xDrive, t.springDamper);
            j.yDrive = Damped(j.yDrive, t.springDamper);
            j.zDrive = Damped(j.zDrive, t.springDamper);
        }
    }

    /// 放开的锁死关节：限位 ±angle、回弹按质量配（跟「锁死关节放开角度 / 回弹」）
    private void Release(ConfigurableJoint j, float angle)
    {
        float a = Mathf.Max(0.01f, angle);
        j.angularXMotion = j.angularYMotion = j.angularZMotion = ConfigurableJointMotion.Limited;
        j.lowAngularXLimit = new SoftJointLimit { limit = -a };
        j.highAngularXLimit = new SoftJointLimit { limit = a };
        j.angularYLimit = new SoftJointLimit { limit = a };
        j.angularZLimit = new SoftJointLimit { limit = a };
        Rigidbody rb = j.GetComponent<Rigidbody>();
        if (rb != null)
            WeldSpring(j, rb.mass);
    }

    /// 只换 damper、不动 PMX 原值的刚度。全关节无差别上阻尼 —— 和 <see cref="SetSprings"/> 一字一样，
    /// 两条路必须算出同一个物理（为什么全关节都给，见 SetSprings 上那段账）。
    private static JointDrive Damped(JointDrive d, float damper) => new JointDrive
    {
        positionSpring = d.positionSpring,
        positionDamper = damper,
        maximumForce = d.maximumForce,
    };
#endif

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
        string how = dist < 0f ? "became NaN/Infinity" : $"is {dist:F1} m from its parent";
        Transform par = s.Bone.parent;
        Plugin.Log.LogError($"[mmd] Bone {name} (rigid body {_m.bodies[s.Body].name}) {how}; not written back."
                            + $" Target {want}, parent {(par != null ? par.name : "none")} @ {(par != null ? par.position : Vector3.zero)}"
                            + (_complaints == 10 ? " (further ones are not logged)" : ""));
    }

    /// 角色瞬移（换地图 / 复活 / 传送）时整套复位，否则布料会被"拽"出一条几百米的甩尾。
    internal void CheckTeleport()
    {
        if (_ref == null)
            return;
        Vector3 p = _ref.position;
        bool jumped = _awake && (p - _lastRef).sqrMagnitude > 4f;
        _lastRef = p;
        if (jumped)
            Reset();
    }

    internal void Reset()
    {
        if (!_awake)
            Freeze(false);          // 被距离剔除冻住的刚体先放开，下面才能给它们摆位、清速度
        _awake = true;
        _trackInit = false;         // ⚠️ 不清的话，瞬移后 Track() 会把那一大步当成本帧位移
        if (_ref != null)
            _framePos = _ref.position;      // 参考系直接对齐，复位后不留任何残余相对运动
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
            if (s.Rb != null && s.Kind != MmdBodyKind.BoneFollow)
                SetKinematic(s.Rb, frozen);                  // 骨骼追随体（含 Anchor 兜底转过来的）本来就是 kinematic
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
