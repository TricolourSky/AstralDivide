using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace AstralDivide.Client;

/// 新做法管的部位（DEV_NOTES 37.31）。None = 头发（和兽耳），照旧走 PMX 物理
internal enum SwayKind { None = -1, Skirt = 0, Coat = 1, Acc = 2, Chest = 3 }

/// <summary>
/// 新做法一个部位的参数，存在每套的 tuning.json 里（skirt / coat / acc / chest 四组），开发版调参台实时改。
/// 默认值照少前 2 动画里量出来的规律定（DEV_NOTES 37.31）：前片裙根跟大腿 0.88~1.0、往下每节晚 30~50 ms。
/// </summary>
internal sealed class SwayPart
{
#pragma warning disable CS0649     // 发布版里只有 tuning.json（Newtonsoft）给它赋值，编译器看不见
    public bool on;                 // 开 = 这一部位不建 PhysX 刚体、走新做法（重新换装 / 重进战局才生效）
#pragma warning restore CS0649
    public float follow;            // 带动比例：链根跟同侧大腿转多少（只对挂在胯 / 脊柱上的链）
    public float hz = 6f;           // 软硬：每节追回原形的弹簧频率（Hz），越大越硬、跟得越紧
    public float zeta = 0.6f;       // 阻尼比：1 = 刚好不冲过头，小于 1 甩一下再停
    public float inertia;           // 甩动：角色速度变化（走跑的颠簸、起步急停、跳起落地）传进来多少；0 = 不传（08-23 口径）。匀速前进、转身都不传
    public float maxAngle = 45f;    // 每节偏离原形的最大角度（度）
    public float gravity;           // 重力（m/s²）；0 = 保持建模形状（少前 2 的衣服不往下坠）
    // 第一节（挂点那节）相对挂点的最大摆角（度）；0 = 跟 maxAngle 一样。带子挂在环上，要开大才能在小臂抬起时照样往下垂（37.31，OTs-14 设 150）
    public float rootAngle;

    public SwayPart() { }

    internal float RootAngle => rootAngle > 0f ? rootAngle : maxAngle;

    internal SwayPart(float follow, float hz, float zeta, float maxAngle)
    {
        this.follow = follow;
        this.hz = hz;
        this.zeta = zeta;
        this.maxAngle = maxAngle;
    }

    internal void Clamp()
    {
        follow = Mathf.Clamp01(follow);
        hz = Mathf.Clamp(hz, 0.5f, 12f);
        zeta = Mathf.Clamp(zeta, 0.05f, 3f);
        inertia = Mathf.Clamp01(inertia);
        maxAngle = Mathf.Clamp(maxAngle, 0f, 90f);
        gravity = Mathf.Clamp(gravity, 0f, 20f);
        rootAngle = Mathf.Clamp(rootAngle, 0f, 170f);   // 再大就接近完全反过来，转动轴定不住、骨头会乱拧
    }

    internal string Describe() =>
        $"带动 {follow:0.##} / 软硬 {hz:0.#}Hz / 阻尼比 {zeta:0.##} / 甩动 {inertia:0.##} / 摆角 {maxAngle:0}°"
        + (rootAngle > 0f ? $"（第一节 {rootAngle:0}°）" : "") + $" / 重力 {gravity:0.#}";
}

/// <summary>
/// 衣服的新做法（DEV_NOTES 37.31）：**不建 PhysX 刚体，逐骨算「身体带动 + 弹簧跟随」**，模仿少前 2 动画师的做法。
///
/// 少前 2 的角色身上没有实时物理，裙摆衣摆的晃动全是做进每个动作里的关键帧；量出来的规律是
/// 前片裙根跟同侧大腿走、往下每节慢半拍、甩一下再停。这里照这个规律实时算，分三层：
///   · **带动**：链根的朝向 = 挂点的朝向，再按链根在腰上的方位叠一部分大腿相对胯的转动（正前方跟同侧腿，侧面递减，后面不跟）。
///   · **跟随**：每节骨一个粒子，用弹簧 + 阻尼追「父骨当前朝向下的原形位置」，根硬梢软；隐式积分，参数拖到多大都不会炸。
///   · **保护**：每节限最大摆角；身体碰撞体（PMX 的骨骼追随刚体，换成胶囊）把粒子推出去；同一圈相邻的链保持间距（不裂缝、不挤成团）。
/// 角色走跑、转身默认整体带着走（「甩动」= 0），只有身体自己的动作（腿、胯扭、弯腰）会让衣服晃 —— 和少前 2 的动作一样是原地的。
///
/// 和 08-23 删掉的自研弹簧骨不是一回事：那个用 4 个编出来的数模仿 MMD 物理、丢了碰撞体和扭转；
/// 这里不模仿 MMD，扭转取自被带动的姿态（每节只把「指向下一节」的方向转过去）。
/// 只依赖 UnityEngine，参数从构造时给的委托现取（调参台拖滑块实时生效）。
/// </summary>
internal sealed class ClothSway
{
    internal const float Radius = 0.01f;            // 粒子半径（米）：裙板贴着骨，只留 1 cm
    private const float SubStep = 1f / 60f;         // 一帧最多拆成 4 步，每步不超过 1/60 秒（隐式积分，大步也稳）
    private const int MaxSub = 4;
    private const float FarCull = 1.0f;             // 静止时离这条链所有骨都超过 1 米的碰撞体（头、手）不参与

    // 内部手感常数（不进调参台；离线自检比过几组，DEV_NOTES 37.31）：
    // 不连相邻链时裙子会被拉开到 2.55 倍、挤到 0.43 倍，连上（0.75~1.35）实测 0.71~1.46；跟腿宽度 1.2 → 2 前片跟腿 0.52 → 0.59、下蹲裙根抬 54° → 60°
    internal static float GapMin = 0.75f, GapMax = 1.35f;   // 相邻两条链同一节的间距只许在静止的这个倍数范围内
    internal static float MaxTurnLag = 600f;                // 转身时每秒最多少带走多少度（甩动 × 转角，封顶）
    internal static float Spread = 2f;                      // 跟腿的权重：横向偏开「大腿离中线距离 × 这个数」就降到 0
    internal static int Iterations = 1;                     // 每一小步里「拉间距 → 推开 + 定长」做几遍（2 遍开销 +50%、穿模只少一点，不值）
    // 碰撞按「整节骨（父 → 这节）」对胶囊算，不只算这一节的端点。自检比过：开销 +30%、穿模没少（阿斯缇亚长外套下蹲 26 → 33 mm），默认关
    internal static bool SegmentHits = false;

    // 头发，和兽耳 / 尾巴（重力一压就塌：贝丝蒂的尾巴静站就被压到摆角上限，第二步之后的自检）→ 都照旧 PMX
    private static readonly Regex HairRx = new Regex(@"hair|bang|髪|もみあげ|アホ毛|ahoge|pony|braid|^ear_|耳|^tail|尻尾|しっぽ", RegexOptions.IgnoreCase);
    private static readonly Regex ChestRx = new Regex(@"(^|_)Chest_[LR]$|^[左右]胸", RegexOptions.IgnoreCase);
    private static readonly Regex SkirtRx = new Regex(@"skirt|スカート|裙", RegexOptions.IgnoreCase);
    // 领子（collar）不算外套：外套改成「会往下垂」以后领子会塌下来，归饰品保持形状（37.31，蕾娜 collar_*）
    private static readonly Regex CoatRx = new Regex(@"jacket|coat|cloth|cape|cloak|top_|sleeve|袖|裾|上着|コート|外套", RegexOptions.IgnoreCase);
    private static readonly Regex ArmRx = new Regex(@"upperarm|forearm|palm|hand|elbow|腕|ひじ|手", RegexOptions.IgnoreCase);

    /// <summary>
    /// 按链的第一根骨名（和挂在哪根骨上）分部位：头发、兽耳 = None（留在 PMX 物理）；胸；挂在手臂 / 手上的 = 饰品
    ///（OTs-14 手臂上的布条名字带 cloth，但它们是会垂会甩的带子，不是外套 —— 第二步实测「不飘了」）；其余按名字分裙、外套衣摆、饰品
    /// </summary>
    internal static SwayKind KindOf(string bone, string attach = null)
    {
        if (string.IsNullOrEmpty(bone) || HairRx.IsMatch(bone))
            return SwayKind.None;
        if (ChestRx.IsMatch(bone))
            return SwayKind.Chest;
        if (!string.IsNullOrEmpty(attach) && ArmRx.IsMatch(attach))
            return SwayKind.Acc;
        if (SkirtRx.IsMatch(bone))
            return SwayKind.Skirt;
        return CoatRx.IsMatch(bone) ? SwayKind.Coat : SwayKind.Acc;
    }

    /// AttachPatch 交过来的一条链（已嫁接到玩家骨架上，骨还在静止姿态）
    internal sealed class Input
    {
        internal int Owner;                         // 哪个部位建的，换装时按它清理
        internal SwayKind Kind;
        internal Transform[] Bones;                 // 从根到梢，前一根是后一根的直接父骨
        internal string[] Names;
        internal Transform Attach;                  // 链根挂在哪根 EFT 骨上
        internal bool Torso;                        // 挂在胯 / 脊柱上 —— 只有这种链才跟腿
        internal Vector3 RestRoot;                  // 链根在服装 prefab 静止姿态下的位置（和 Body 的静止框架同一个空间）
        internal int Group = -1, Mask = 0xFFFF;     // 链上 PMX 刚体的碰撞组 / 掩码；-1 = 链上没刚体，和所有身体碰撞体碰
        internal Vector3[] PmxPos;                  // 每节骨在 PMX 里的位置（PMX 单位）；用来找静止时就贴着碰撞体的那几对
        internal Vector3 Tail;                      // 只有一节骨的链（OTs-14 一边一个球的胸）：虚拟尖端相对这节骨的位置（骨的朝向空间，米）；零 = 没有
    }

    /// 带动要用的身体：运行时的胯和两条大腿，加上静止姿态下量的框架
    internal sealed class Body
    {
        internal Transform Pelvis, ThighL, ThighR, Root;    // Root = 角色根（转身用）
        internal Quaternion RestL = Quaternion.identity, RestR = Quaternion.identity;   // 静止时大腿相对胯的朝向
        internal Vector3 Center, Right = Vector3.right, Forward = Vector3.forward, Up = Vector3.up;   // 静止框架：两大腿中点、右、前、上
        internal float HalfWidth = 0.09f;                   // 静止时大腿离中线多远（米）：正对大腿前面的那几列裙子跟腿
        internal bool Legs => Pelvis != null && ThighL != null && ThighR != null;
    }

    /// 一个身体碰撞体（PMX 骨骼追随刚体）换成胶囊；球 = 半长 0，盒子 = 沿最长边的胶囊
    internal sealed class Col
    {
        internal Transform Bone;
        internal Vector3 LocalPos;                  // 刚体相对骨（米）
        internal Quaternion LocalRot;
        internal Vector3 Axis = Vector3.up;         // 胶囊轴（刚体局部）
        internal float Radius, Half;                // 米
        internal int Group, Mask;
        internal Vector3 PmxCenter, PmxAxis;        // PMX 静止姿态（PMX 单位）
        internal float PmxRadius, PmxHalf;
        internal Vector3 A, B;                      // 本帧世界端点
    }

    private sealed class Chain
    {
        internal Input In;
        internal int N;                             // 骨数
        internal int P;                             // 点数 = 骨数 + 虚拟尖端（有的话）；下面按点存的数组都是 P 长
        internal Quaternion RootLocalRot;           // 链根相对挂点的静止朝向
        internal Vector3[] LocalPos;                // 每节骨静止 localPosition / localRotation（复位用，N 长）
        internal Quaternion[] LocalRot;
        internal Vector3[] Off;                     // Off[i]：第 i 点相对第 i-1 节骨的偏移（第 i-1 节朝向空间，米），i ≥ 1
        internal float[] Len;
        internal float[] Stiff;                     // 每点频率系数：根 2 → 梢 1
        internal Vector3[] X, V;                    // 粒子 = 第 i 点的世界位置 / 速度（i ≥ 1；第 N 点是虚拟尖端）
        internal Vector3[] Prev;                    // 这一小步开始时的位置（步末按「走了多远」重算速度）
        internal Vector3[] Home;                    // 静止时每节相对挂点的位置（挂点朝向空间，米）：相邻链的静止间距从这儿算，和此刻的姿势无关
        internal Quaternion[] Rot;                  // 本帧算好的每节骨世界朝向
        internal float WL, WR;                      // 链根跟左 / 右大腿的权重（还要乘带动比例）
        internal Quaternion Drive;                  // 本帧链根朝向
        internal Vector3 RootPos;
        internal readonly List<int> Cols = new List<int>();
        internal float[][] ColR;                    // [节][第几个碰撞体] 有效半径（米）
        internal int[] Active = new int[0];         // 本帧够得着这条链的碰撞体（Cols 里的下标），每帧 UpdateCols 挑一次
        internal int ActiveCount;
        internal float Reach;                       // 整条链的长度（米）
    }

    private sealed class Pair
    {
        internal Chain A, B;
        internal float[] Gap;                       // 同一节的静止间距
    }

    private readonly Func<SwayKind, SwayPart> _part;
    private readonly List<Chain> _chains = new List<Chain>();
    private readonly List<Pair> _pairs = new List<Pair>();
    private List<Col> _cols = new List<Col>();
    private Body _body;
    private float _upm = 12.5f;
    private bool _carryInit;
    private Vector3 _lastPos;
    private Quaternion _lastYaw = Quaternion.identity;
    private Vector3 _lastVel;                       // 角色上一帧的速度（甩动按「速度变了多少」给冲量）

    internal ClothSway(Func<SwayKind, SwayPart> part) => _part = part;

    internal int Count => _chains.Count;

    internal int CountKind(SwayKind kind)
    {
        int n = 0;
        foreach (Chain c in _chains)
            if (c.In.Kind == kind)
                n++;
        return n;
    }
    internal int PairCount => _pairs.Count;
    internal int ColCount => _cols.Count;
    internal bool LegsReady => _body != null && _body.Legs;

    internal int BoneCount
    {
        get
        {
            int n = 0;
            foreach (Chain c in _chains)
                n += c.N;
            return n;
        }
    }

    internal void SetBody(Body body)
    {
        if (body != null)
            _body = body;
    }

    internal void SetColliders(List<Col> cols) => _cols = cols ?? new List<Col>();

    // ── 建 / 拆 ──────────────────────────────────────────────────────────

    /// 加一条链。骨必须还在静止姿态（刚嫁接完），这里直接从 Transform 上量原形。
    /// 只有一节骨的链要带虚拟尖端（Input.Tail），不然没有「下一节」可追
    internal bool Add(Input input)
    {
        Transform[] b = input.Bones;
        if (b == null || b.Length < 1 || input.Attach == null)
            return false;
        int n = b.Length, p = n + (input.Tail.sqrMagnitude > 1e-6f ? 1 : 0);
        if (p < 2)
            return false;
        var c = new Chain
        {
            In = input, N = n, P = p, RootLocalRot = b[0].localRotation, Drive = b[0].rotation, RootPos = b[0].position,
            LocalPos = new Vector3[n], LocalRot = new Quaternion[n], Rot = new Quaternion[n],
            Off = new Vector3[p], Len = new float[p], Stiff = new float[p], X = new Vector3[p], V = new Vector3[p], Prev = new Vector3[p], Home = new Vector3[p],
        };
        for (int i = 0; i < n; i++)
        {
            if (b[i] == null)
                return false;
            c.LocalPos[i] = b[i].localPosition;
            c.LocalRot[i] = b[i].localRotation;
            c.Rot[i] = b[i].rotation;
        }
        Quaternion toAttach = Quaternion.Inverse(input.Attach.rotation);
        c.Home[0] = toAttach * (b[0].position - input.Attach.position);
        for (int i = 1; i < p; i++)
        {
            Vector3 off = i < n ? Quaternion.Inverse(b[i - 1].rotation) * (b[i].position - b[i - 1].position) : input.Tail;
            if (off.sqrMagnitude < 1e-8f)
                off = Vector3.down * 0.01f;                 // 重合的骨给一个 1 cm 的短段，别除零
            c.Off[i] = off;
            c.Len[i] = off.magnitude;
            c.Reach += c.Len[i];
            // 根硬梢软：第一节 2 倍、梢 1 倍。梢不能比这更软 —— 跑步一步约 1.9 Hz，梢的频率掉到 3 Hz 以下就会跟着步子共振、越甩越大（离线自检实测）
            // 胸部例外：每节都 2 倍，整团一起晃。蕾娜的胸是两节，外节按 1 倍会在中间折、跟着跑步的上下颠共振（离线自检：外节 32°~36°，一节胸的六套 13°~19°）
            c.Stiff[i] = input.Kind == SwayKind.Chest ? 2f : Mathf.Lerp(2f, 1f, (i - 1) / (float)Mathf.Max(1, p - 2));
            c.X[i] = i < n ? b[i].position : b[n - 1].position + b[n - 1].rotation * off;
            c.Home[i] = toAttach * (c.X[i] - input.Attach.position);
        }
        _chains.Add(c);
        return true;
    }

    internal void RemoveOwner(int owner)
    {
        if (_chains.RemoveAll(c => c.In.Owner == owner) > 0)
            Link(_upm);
    }

    /// 加完 / 拆完之后调：算带动权重、每条链和哪些碰撞体碰、相邻的链
    internal void Link(float upm)
    {
        _upm = Mathf.Max(1e-3f, upm);
        foreach (Chain c in _chains)
        {
            Weights(c);
            Pairs(c);
        }
        Neighbors();
    }

    private void Weights(Chain c)
    {
        c.WL = c.WR = 0f;
        if (_body == null || !_body.Legs || !c.In.Torso || c.In.Kind == SwayKind.Chest)
            return;                                         // 胸在身体正前方，但不该跟腿
        // 少前 2 的裙子：正对大腿前面的那片跟这条腿走（0.88~1.0），中间、侧面、后面主要跟胯 ——
        // 所以按「链根横向离哪条大腿近」给权重：正对大腿 = 1，横向偏开 Spread 倍「大腿离中线的距离」就降到 0；后半圈不跟腿（腿往后踢靠碰撞推开）
        Vector3 d = Vector3.ProjectOnPlane(c.In.RestRoot - _body.Center, _body.Up);
        float fwd = Vector3.Dot(d, _body.Forward), mag = d.magnitude;
        if (fwd <= 0f || mag < 1e-4f)
            return;
        float front = Mathf.Clamp01(fwd / mag / 0.5f);      // 前方 ±60° 以内满权重
        float side = Vector3.Dot(d, _body.Right), hw = Mathf.Max(0.02f, _body.HalfWidth);
        c.WL = front * Mathf.Clamp01(1f - Mathf.Abs(side + hw) / (Spread * hw));    // 左大腿在 side = -hw
        c.WR = front * Mathf.Clamp01(1f - Mathf.Abs(side - hw) / (Spread * hw));
    }

    /// 这条链和哪些碰撞体碰（PMX 的规矩：双方掩码都同意才碰），以及每节的有效半径 ——
    /// 静止时就埋在碰撞体里的那一节（PMX 作者常把腿的胶囊做得偏胖），半径收到它静止的位置，不然一开始就被顶歪
    private void Pairs(Chain c)
    {
        c.Cols.Clear();
        float rp = Radius * _upm;
        for (int k = 0; k < _cols.Count; k++)
        {
            Col col = _cols[k];
            bool agree = c.In.Group < 0 || (((c.In.Mask >> col.Group) & 1) != 0 && ((col.Mask >> c.In.Group) & 1) != 0);
            if (agree && Near(c, col))
                c.Cols.Add(k);
        }
        c.Active = new int[c.Cols.Count];
        c.ActiveCount = 0;
        c.ColR = new float[c.P][];
        for (int i = 0; i < c.P; i++)
        {
            c.ColR[i] = new float[c.Cols.Count];
            for (int k = 0; k < c.Cols.Count; k++)
            {
                Col col = _cols[c.Cols[k]];
                float r = col.PmxRadius + rp;
                Vector3 q = c.In.PmxPos != null && i < c.In.PmxPos.Length ? c.In.PmxPos[i] : Vector3.positiveInfinity;
                Vector3 q0 = i > 0 && c.In.PmxPos != null && i - 1 < c.In.PmxPos.Length ? c.In.PmxPos[i - 1] : q;
                if (!float.IsInfinity(q.x) && !float.IsInfinity(q0.x))
                {
                    // 静止时和它的距离：按整节骨算（和 Collide 用同一种量法），不然整节横穿胖胶囊的那几节一开始就被顶歪
                    Vector3 ea = col.PmxCenter + col.PmxAxis * col.PmxHalf, eb = col.PmxCenter - col.PmxAxis * col.PmxHalf;
                    float d = Dist(q, ea, eb);
                    if (SegmentHits && i > 0)
                    {
                        Closest(q0, q, ea, eb, out float _, out Vector3 onSeg, out Vector3 onAxis);
                        d = (onSeg - onAxis).magnitude;
                    }
                    if (d < r)
                        r = Mathf.Max(0f, d - 0.001f * _upm);
                }
                c.ColR[i][k] = r / _upm;
            }
        }
    }

    /// 静止时离这条链所有骨都超过 1 米的碰撞体不要（头、举起来的手），省掉大部分测试
    private bool Near(Chain c, Col col)
    {
        if (c.In.PmxPos == null)
            return true;
        float far = (FarCull + col.Radius) * _upm;
        foreach (Vector3 q in c.In.PmxPos)
            if (!float.IsInfinity(q.x) && Dist(q, col.PmxCenter + col.PmxAxis * col.PmxHalf, col.PmxCenter - col.PmxAxis * col.PmxHalf) < far)
                return true;
        return false;
    }

    private static readonly Regex TailDigits = new Regex(@"\d+$");

    /// 同一部位、挂在同一根骨上、**名字前缀相同**的链（裙子一圈 = Skirt_0_0 ~ Skirt_0_15）按绕身体的方位排序，相邻的两条之间保持间距；
    /// 相距比中位数大很多的（前开口、后开衩）不连。
    /// ⚠️ 只按挂点分组会把蕾娜的领子（collar_*，脖子那么高）和外套下摆（Jacket_0_*，腰那么低）排进同一圈，按方位交错 → 下摆反而连不上（第二步自检前查出来的）
    private void Neighbors()
    {
        _pairs.Clear();
        if (_body == null)
            return;
        var groups = new Dictionary<string, List<Chain>>();
        foreach (Chain c in _chains)
        {
            string prefix = c.In.Names != null && c.In.Names.Length > 0 ? TailDigits.Replace(c.In.Names[0], "") : "";
            string key = (int)c.In.Kind + "|" + c.In.Attach.GetInstanceID() + "|" + prefix;
            if (!groups.TryGetValue(key, out List<Chain> list))
                groups[key] = list = new List<Chain>();
            list.Add(c);
        }
        foreach (List<Chain> ring in groups.Values)
            if (ring.Count >= 3)
                Ring(ring);
    }

    private void Ring(List<Chain> ring)
    {
        ring.Sort((a, b) => Azimuth(a).CompareTo(Azimuth(b)));
        var gaps = new List<float>();
        for (int i = 0; i < ring.Count; i++)
            gaps.Add((ring[i].In.RestRoot - ring[(i + 1) % ring.Count].In.RestRoot).magnitude);
        var sorted = new List<float>(gaps);
        sorted.Sort();
        float limit = sorted[sorted.Count / 2] * 1.8f;
        for (int i = 0; i < ring.Count; i++)
        {
            if (gaps[i] > limit)
                continue;
            Chain a = ring[i], b = ring[(i + 1) % ring.Count];
            int m = Mathf.Min(a.P, b.P);
            var p = new Pair { A = a, B = b, Gap = new float[m] };
            for (int j = 1; j < m; j++)
                p.Gap[j] = (a.Home[j] - b.Home[j]).magnitude;     // 同一个挂点，两边的 Home 在同一个空间
            _pairs.Add(p);
        }
    }

    private float Azimuth(Chain c)
    {
        Vector3 d = Vector3.ProjectOnPlane(c.In.RestRoot - _body.Center, _body.Up);
        return Mathf.Atan2(Vector3.Dot(d, _body.Right), Vector3.Dot(d, _body.Forward));
    }

    /// 复位：骨摆回原形、粒子贴上去、速度清零（建好、瞬移、走近恢复时调）。
    /// 角色此刻的位置 / 朝向也一起记下 —— 不记的话，复位到下一帧之间角色走的那一步会被当成「没带走」，粒子留在原地被腿顶穿
    internal void Reset()
    {
        foreach (Chain c in _chains)
            ResetChain(c);
        _carryInit = false;
        Carry(0f);
    }

    private static void ResetChain(Chain c)
    {
        Transform[] b = c.In.Bones;
        for (int i = 0; i < c.N; i++)
        {
            if (b[i] == null)
                return;
            b[i].localPosition = c.LocalPos[i];
            b[i].localRotation = c.LocalRot[i];
        }
        for (int i = 1; i < c.P; i++)
        {
            c.X[i] = i < c.N ? b[i].position : b[c.N - 1].position + b[c.N - 1].rotation * c.Off[i];
            c.V[i] = Vector3.zero;
        }
    }

    // ── 跑（每帧一次，LateUpdate 里 PhysX 写回之后）─────────────────────

    internal void Tick(float dt)
    {
        if (_chains.Count == 0 || dt <= 0f)
            return;
        float total = Mathf.Min(dt, SubStep * MaxSub);
        int steps = Mathf.Clamp(Mathf.CeilToInt(total / SubStep - 1e-3f), 1, MaxSub);
        float h = total / steps;
        Carry(dt);
        Drive();
        UpdateCols();
        for (int s = 0; s < steps; s++)
        {
            foreach (Chain c in _chains)
                Step(c, h);
            // 推开放在最后：先拉相邻间距、再统一推出身体 + 定长。推开放在前面的话，拉间距会把被腿推出去的那列连同邻居拽回腿里
            //（第二步自检：阿斯缇亚下蹲穿 3.5~5.2 cm）
            for (int it = 0; it < Iterations; it++)
            {
                foreach (Pair p in _pairs)
                    Keep(p);
                foreach (Chain c in _chains)
                    Settle(c, h);
            }
        }
        foreach (Chain c in _chains)
            Pose(c);
    }

    /// <summary>
    /// 角色整体的移动和转身：粒子先整个跟着带走（位置平移 + 绕胯转），再按「甩动」给一个**速度变化**的反冲：
    /// 惯性只来自速度变化 —— 起步急停、走跑的上下颠、跳起落地会让胸部 / 挂件晃，匀速前进不会。
    /// ⚠️ 首版是「每帧把移动的一部分留下不带走」，等于一直有股按速度往后拽的拖力：匀速跑时胸、挂件被压在摆角上限（自检实测）。
    /// 转身按「甩动」少带走一部分（晚一点跟上、再弹回来）：以前转身一律整体带走，主菜单里拖着人物转，除了头发什么都不动（Tech Leader 实测）。
    /// 每秒最多少带 MaxTurnLag 度：塔科夫里转身常常是鼠标一甩，不封顶就一下甩到摆角上限。
    /// </summary>
    private void Carry(float dt)
    {
        if (_chains.Count == 0)
            return;
        Transform anchor = _body != null && _body.Pelvis != null ? _body.Pelvis : _chains[0].In.Attach;
        if (anchor == null)
            return;
        Vector3 pos = anchor.position;
        Quaternion yaw = _body != null && _body.Root != null ? _body.Root.rotation : Quaternion.identity;
        if (!_carryInit || dt <= 0f)
        {
            _lastPos = pos;
            _lastYaw = yaw;
            _lastVel = Vector3.zero;
            _carryInit = true;
            return;
        }
        Vector3 move = pos - _lastPos, vel = move / dt, dv = vel - _lastVel;
        if (dv.sqrMagnitude > 100f)
            dv = Vector3.zero;                              // 一帧里速度变化超过 10 m/s = 瞬移 / 卡了一下，不当成惯性
        Quaternion turn = yaw * Quaternion.Inverse(_lastYaw);
        turn.ToAngleAxis(out float ang, out Vector3 axis);
        if (ang > 180f)
            ang -= 360f;
        _lastPos = pos;
        _lastYaw = yaw;
        _lastVel = vel;
        foreach (Chain c in _chains)
        {
            float k = _part(c.In.Kind).inertia;
            float lag = Mathf.Clamp(ang * k, -MaxTurnLag * dt, MaxTurnLag * dt);
            Quaternion carry = lag == 0f ? turn : Quaternion.AngleAxis(ang - lag, axis);
            for (int i = 1; i < c.P; i++)
            {
                c.X[i] = pos + carry * (c.X[i] + move - pos);
                c.V[i] = carry * c.V[i] - dv * k;
            }
        }
    }

    private void UpdateCols()
    {
        foreach (Col col in _cols)
        {
            if (col.Bone == null)
            {
                col.A = col.B = Vector3.positiveInfinity;
                continue;
            }
            Quaternion r = col.Bone.rotation;
            Vector3 center = col.Bone.position + r * col.LocalPos;
            Vector3 axis = r * col.LocalRot * col.Axis * col.Half;
            col.A = center + axis;
            col.B = center - axis;
        }
        // 每条链只留这一帧够得着的碰撞体（链根到胶囊的距离 < 链长 + 胶囊半径 + 5 cm）：裙子碰不到手臂、头，外套多数时候碰不到小腿
        foreach (Chain c in _chains)
        {
            c.ActiveCount = 0;
            for (int k = 0; k < c.Cols.Count; k++)
            {
                Col col = _cols[c.Cols[k]];
                if (!float.IsInfinity(col.A.x) && Dist(c.RootPos, col.A, col.B) < c.Reach + col.Radius + Radius + 0.05f)
                    c.Active[c.ActiveCount++] = k;
            }
        }
    }

    /// 带动层：链根朝向 = 挂点朝向，叠上大腿相对胯的转动（按权重 × 带动比例）
    private void Drive()
    {
        bool legs = _body != null && _body.Legs;
        Quaternion dl = Quaternion.identity, dr = Quaternion.identity;
        if (legs)
        {
            Quaternion pelvis = _body.Pelvis.rotation;
            dl = _body.ThighL.rotation * Quaternion.Inverse(pelvis * _body.RestL);
            dr = _body.ThighR.rotation * Quaternion.Inverse(pelvis * _body.RestR);
        }
        foreach (Chain c in _chains)
        {
            Transform at = c.In.Attach;
            if (at == null || c.In.Bones[0] == null)
                continue;
            c.RootPos = c.In.Bones[0].position;
            Quaternion rest = at.rotation * c.RootLocalRot;
            float f = _part(c.In.Kind).follow;
            if (legs && f > 0f && (c.WL > 0f || c.WR > 0f))
                rest = Quaternion.Slerp(Quaternion.identity, dl, c.WL * f) * Quaternion.Slerp(Quaternion.identity, dr, c.WR * f) * rest;
            c.Drive = rest;
        }
    }

    /// 跟随层 + 保护层：一节一节往下，弹簧追「父骨朝向下的原形位置」，再限角、推开、定长
    private void Step(Chain c, float h)
    {
        if (c.In.Attach == null || c.In.Bones[0] == null)
            return;                                         // 骨已经跟着换装拆掉了，等 RemovePart 把这条链摘掉
        SwayPart p = _part(c.In.Kind);
        float w0 = 2f * Mathf.PI * p.hz, cosMax = Mathf.Cos(p.maxAngle * Mathf.Deg2Rad), maxRad = p.maxAngle * Mathf.Deg2Rad;
        float rootRad = p.RootAngle * Mathf.Deg2Rad, cosRoot = Mathf.Cos(rootRad);
        Vector3 g = Vector3.down * p.gravity;
        Vector3 pPos = c.RootPos;
        Quaternion pBase = c.Drive;
        for (int i = 1; i < c.P; i++)
        {
            Vector3 target = pPos + pBase * c.Off[i];
            float w = w0 * c.Stiff[i], len = c.Len[i];
            Vector3 x = c.X[i];
            c.Prev[i] = x;
            // 隐式欧拉（弹簧 + 阻尼一起隐式）：步长、频率拖到多大都不会发散
            Vector3 v = (c.V[i] + (w * w * (target - x) + g) * h) / (1f + 2f * p.zeta * w * h + w * w * h * h);
            Vector3 tdir = (target - pPos) / len;
            Vector3 dir = Dir(x + v * h - pPos, tdir);
            dir = i == 1 ? Cone(dir, tdir, cosRoot, rootRad) : Cone(dir, tdir, cosMax, maxRad);
            Vector3 xn = pPos + dir * len;                  // 推出身体在步末的 Settle 里统一做（只做一遍，省一半碰撞测试）
            c.V[i] = (xn - x) / h;
            c.X[i] = xn;
            Quaternion fin = Quaternion.FromToRotation(tdir, dir) * pBase;
            c.Rot[i - 1] = fin;
            if (i < c.N)
                pBase = fin * c.LocalRot[i];                // 虚拟尖端后面没有骨了
            pPos = xn;
        }
        if (c.P == c.N)
            c.Rot[c.N - 1] = pBase;                         // 最后一根骨（梢）没有下一节可指：跟父骨的原形走
    }

    /// <summary>
    /// 一小步的收尾：从根往下逐节「限角 → 推出身体 → 定长」，顺手定好每节骨的朝向，再按这一步实际走了多远重算速度
    ///（拉间距、推开带来的位移也算进去，下一步不会弹回去）。
    /// 限角放在推开前面：拉间距会把一节拽到很歪的地方，不先限角，推开时就可能把它推到大腿另一边、整节翻折
    ///（第二步自检：阿斯缇亚的长外套下蹲时一节翻了 161°）
    /// </summary>
    private void Settle(Chain c, float h)
    {
        if (c.In.Attach == null || c.In.Bones[0] == null)
            return;
        SwayPart p = _part(c.In.Kind);
        float cosMax = Mathf.Cos(p.maxAngle * Mathf.Deg2Rad), maxRad = p.maxAngle * Mathf.Deg2Rad;
        float rootRad = p.RootAngle * Mathf.Deg2Rad, cosRoot = Mathf.Cos(rootRad);
        Vector3 pPos = c.RootPos;
        Quaternion pBase = c.Drive;
        for (int i = 1; i < c.P; i++)
        {
            float len = c.Len[i];
            Vector3 tdir = pBase * c.Off[i] / len;
            Vector3 dir = Dir(c.X[i] - pPos, tdir);
            dir = i == 1 ? Cone(dir, tdir, cosRoot, rootRad) : Cone(dir, tdir, cosMax, maxRad);
            Vector3 x = Collide(c, i, pPos, pPos + dir * len);
            dir = Dir(x - pPos, tdir);
            x = pPos + dir * len;
            c.X[i] = x;
            c.V[i] = (x - c.Prev[i]) / h;
            Quaternion fin = Quaternion.FromToRotation(tdir, dir) * pBase;
            c.Rot[i - 1] = fin;
            if (i < c.N)
                pBase = fin * c.LocalRot[i];
            pPos = x;
        }
        if (c.P == c.N)
            c.Rot[c.N - 1] = pBase;
    }

    /// 限角：dir 偏离原形方向 tdir 超过 maxRad 就拉回到边上。第一节相对挂点用 rootAngle（带子挂在环上，37.31），后面每节相对上一节用 maxAngle
    private static Vector3 Cone(Vector3 dir, Vector3 tdir, float cosMax, float maxRad) =>
        Vector3.Dot(dir, tdir) < cosMax ? Vector3.RotateTowards(tdir, dir, maxRad, 0f) : dir;

    /// 把第 i 节推出身体胶囊。SegmentHits 开：按整节骨（父 → 这节）和胶囊轴的最近点算，只能挪这一节的端点，
    /// 所以推的量按最近点在骨上的位置放大（最近点离父骨越近要挪得越多，最多 4 倍）
    private Vector3 Collide(Chain c, int i, Vector3 from, Vector3 x)
    {
        float[] radii = c.ColR[i];
        for (int a = 0; a < c.ActiveCount; a++)
        {
            int k = c.Active[a];
            Col col = _cols[c.Cols[k]];
            float r = radii[k];
            if (r <= 0f || float.IsInfinity(col.A.x))
                continue;
            float s = 1f;
            Vector3 cp, off;
            if (SegmentHits)
            {
                Closest(from, x, col.A, col.B, out s, out Vector3 onSeg, out cp);
                off = onSeg - cp;
            }
            else
            {
                Vector3 ab = col.B - col.A;
                cp = col.A + ab * Mathf.Clamp01(Vector3.Dot(x - col.A, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude));
                off = x - cp;
            }
            float dd = off.sqrMagnitude;
            if (dd >= r * r || dd < 1e-12f)
                continue;
            float d = Mathf.Sqrt(dd);
            x += off * ((r - d) / d / Mathf.Max(0.25f, s));
        }
        return x;
    }

    /// 两条线段的最近点（Ericson《实时碰撞检测》5.1.9）：骨 p0→p1 上参数 s 处的 onSeg，和胶囊轴 a→b 上的 onAxis
    private static void Closest(Vector3 p0, Vector3 p1, Vector3 a, Vector3 b, out float s, out Vector3 onSeg, out Vector3 onAxis)
    {
        Vector3 u = p1 - p0, v = b - a, r = p0 - a;
        float uu = Vector3.Dot(u, u), vv = Vector3.Dot(v, v), vr = Vector3.Dot(v, r);
        float t;
        if (uu <= 1e-10f)
        {
            s = 0f;
            t = vv <= 1e-10f ? 0f : Mathf.Clamp01(vr / vv);
        }
        else
        {
            float ur = Vector3.Dot(u, r);
            if (vv <= 1e-10f)
            {
                t = 0f;
                s = Mathf.Clamp01(-ur / uu);
            }
            else
            {
                float uv = Vector3.Dot(u, v), denom = uu * vv - uv * uv;
                s = denom > 1e-10f ? Mathf.Clamp01((uv * vr - ur * vv) / denom) : 0f;
                t = (uv * s + vr) / vv;
                if (t < 0f) { t = 0f; s = Mathf.Clamp01(-ur / uu); }
                else if (t > 1f) { t = 1f; s = Mathf.Clamp01((uv - ur) / uu); }
            }
        }
        onSeg = p0 + u * s;
        onAxis = a + v * t;
    }

    /// 相邻两条链同一节的间距保持在静止的 GapMin~GapMax 倍（两边各让一半）
    private static void Keep(Pair p)
    {
        for (int j = 1; j < p.Gap.Length; j++)
        {
            Vector3 d = p.B.X[j] - p.A.X[j];
            float m = d.magnitude, gap = p.Gap[j];
            float want = Mathf.Clamp(m, gap * GapMin, gap * GapMax);
            if (m < 1e-6f || Mathf.Approximately(m, want))
                continue;
            Vector3 corr = d * ((m - want) / m * 0.5f);
            p.A.X[j] += corr;
            p.B.X[j] -= corr;
        }
    }

    /// 写回骨：朝向在最后一小步的 Settle 里已经定好（只转「指向下一节」的方向，扭转取自父骨）
    private static void Pose(Chain c)
    {
        Transform[] b = c.In.Bones;
        if (b[0] == null)
            return;
        for (int i = 1; i < c.P; i++)
            if (float.IsNaN(c.X[i].x) || float.IsInfinity(c.X[i].x) || float.IsNaN(c.Rot[i - 1].w))
            {
                ResetChain(c);                              // 万一算出了 NaN：整条链复位，绝不把坏值写进骨
                return;
            }
        for (int i = 0; i < c.N; i++)
            if (b[i] != null)
                b[i].rotation = c.Rot[i];
    }

    private static Vector3 Dir(Vector3 v, Vector3 fallback)
    {
        float m = v.magnitude;
        return m > 1e-6f ? v / m : fallback;
    }

    private static float Dist(Vector3 q, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float t = Mathf.Clamp01(Vector3.Dot(q - a, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude));
        return (q - (a + ab * t)).magnitude;
    }
}
