using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace AstralDivide.Client;

/// <summary>
/// 一套模型自己的物理手感参数 —— `physics/&lt;模型&gt;.tuning.json`。
///
/// **为什么每套模型一份**（2026-08-23 Tech Leader 定）：FIKA 里玩家 A 穿贝丝蒂、B 穿 OTs-14、
/// C 穿索普，一个全局滑块能把另外两套调废。和渲染那套一样，**每套模型自带一份配好的参数**，
/// 全局只留「开不开」和两个跟这台电脑性能有关的。
///
/// 判断标准很简单：**参数写在刚体/关节上的就能独立，写在物理场景上的就不能**。
/// 这里这 10 个全是前者。唯一做不到的是「物理步频」—— 所有角色共用一个 `PhysicsScene`、
/// 一次 `Simulate()`，没法一部分跑 120Hz 一部分跑 60Hz，所以它留在全局。
///
/// 每个字段都可以省略，省略就用这里的默认值 —— **漏放文件不会崩**，只是回到默认。
/// 默认值 = 贝丝蒂 2026-08-23 实机验收过的那一组。
/// </summary>
internal class MmdTuning
{
    public float gravity = 9.8f;          // PMX 单位/秒²。MMD 原生 ≈ 98（= 地球重力；MMD 界面上的 9.8 内部要 ×10，DEV_NOTES 37.31 更正）
    public float follow = 1f;             // 剥掉玩家操作的水平力（前进后退平移）
    public float followUp = 1f;           // 单独管竖直：调小 = 走路颠簸能弹起头发
    public float damping = 1.75f;         // 乘在 PMX 阻尼上
    public float separate = 10f;          // 刚体被推开的速度上限（单位/秒），Unity 默认 10
    public bool clothNoSelf = false;      // 布料之间互不碰撞（只和身体碰）；只由 json 赋值
    public float maxSpin = 50f;           // 单刚体自转上限（弧度/秒）
    public float springDamper = 0.5f;     // PMX 只写刚度没写阻尼，这里补
    public bool projection = true;        // 关节防拉散。⚠️ 关掉会把 20 段长链扯成碎片
    public int solverIters = 12;
    public float massScale = 1f;          // 全部动态刚体质量的倍率（调参台实验用；1 = 照 PMX）
    public float freeAngle = 45f;         // 六自由度全锁死的链关节放开成 ±这个角度（度）；0 = 照 PMX 焊死
    public float weldSpring = 10f;        // 放开的锁死关节回到静止姿态的角弹簧刚度（每单位质量）；0 = 不回弹，纯靠重力

    // 衣服的新做法（DEV_NOTES 37.31）：每个部位一组，on = 这部位不建 PhysX 刚体、改走 ClothSway。默认全关 = 照旧 PMX 物理。
    // 默认值照少前 2 动画里量的规律：裙子前片跟腿 0.9，外套衣摆根部只跟腿一点，饰品不跟腿；
    // 软硬 6Hz / 阻尼比 0.6 是离线自检比出来的（往下每节摆幅 ×1.3、晚 33~50 ms，和少前 2 的 ×1.5~1.9、30~50 ms 最接近；4Hz 时梢部跟着跑步共振）
    public SwayPart skirt = Skirt();
    public SwayPart coat = Coat();
    public SwayPart acc = Acc();
    public SwayPart chest = Chest();

    // 甩动 0.2：转身、急停时裙子 / 外套也轻轻晃一下（主菜单拖着人物转要看得到动，Tech Leader 实测）
    private static SwayPart Skirt() => new SwayPart(0.9f, 6f, 0.6f, 45f) { inertia = 0.2f };
    // 外套（含围巾）= 会往下垂：弹簧很软（1Hz）+ 地球重力，转身时像布一样甩出去再垂回来（Tech Leader：「该垂的都很硬、没有自然垂落」）。
    // 以前 5Hz / 8Hz 是用很硬的弹簧顶住建模形状：转身时围着原形弹 = 果冻；手臂张开建模的外套（贝丝蒂）一直撑开、前面缝很大。
    // 第一节 150°：索普的围巾在建模姿势里是往外翘的，要转很大角度才垂得下来（60° / 120° 都会顶住）
    private static SwayPart Coat() => new SwayPart(0.15f, 1f, 1f, 60f) { inertia = 0.3f, gravity = 9.8f, rootAngle = 150f };
    // 饰品（飘带、围巾尾、挂件、蝴蝶结）：稍微垂一点、会晃但不乱飞。3Hz / 0.35 / 重力 9.8 时发带、围巾乱飞；会垂会甩的手臂带子在 ots14.tuning.json 里单独设
    private static SwayPart Acc() => new SwayPart(0f, 5f, 0.7f, 50f) { inertia = 0.3f, gravity = 5f };
    // 胸部：围着原形的软弹簧、不往下坠、阻尼小（回弹几下）、走跑跳的颠簸传进来（「水水的」，同上）
    private static SwayPart Chest() => new SwayPart(0f, 3f, 0.25f, 25f) { inertia = 0.7f };

    internal SwayPart Part(SwayKind kind) =>
        kind == SwayKind.Skirt ? skirt : kind == SwayKind.Coat ? coat : kind == SwayKind.Chest ? chest : acc;

    internal static string PathFor(string dir, string model) =>
        Path.Combine(dir, model + ".tuning.json");

    internal static MmdTuning Load(string dir, string model)
    {
        string file = PathFor(dir, model);
        MmdTuning t = new MmdTuning();
        try
        {
            if (File.Exists(file))
                t = JsonConvert.DeserializeObject<MmdTuning>(File.ReadAllText(file)) ?? new MmdTuning();
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[mmd] Failed to read {model}.tuning.json, using defaults: {e.Message}");
            t = new MmdTuning();
        }
        t.Clamp();
        return t;
    }

#if SORA_DEV
    internal bool Save(string dir, string model)
    {
        try
        {
            File.WriteAllText(PathFor(dir, model), JsonConvert.SerializeObject(this, Formatting.Indented));
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[mmd] {model}.tuning.json 写入失败: {e.Message}");
            return false;
        }
    }

    /// 一行摘要，只列**和默认值不同**的项 —— 调参台存盘时打出来，一眼就能看出写进去的是什么（开发版）
    internal string Describe()
    {
        var d = new MmdTuning();
        var parts = new System.Collections.Generic.List<string>();
        if (!Mathf.Approximately(gravity, d.gravity)) parts.Add("重力 " + gravity);
        if (!Mathf.Approximately(follow, d.follow)) parts.Add("跟随 " + follow);
        if (!Mathf.Approximately(followUp, d.followUp)) parts.Add("跟随上下 " + followUp);
        if (!Mathf.Approximately(damping, d.damping)) parts.Add("阻尼 " + damping);
        if (!Mathf.Approximately(separate, d.separate)) parts.Add("分离 " + separate);
        if (!Mathf.Approximately(maxSpin, d.maxSpin)) parts.Add("角速度 " + maxSpin);
        if (!Mathf.Approximately(springDamper, d.springDamper)) parts.Add("弹簧阻尼 " + springDamper);
        if (solverIters != d.solverIters) parts.Add("迭代 " + solverIters);
        if (!Mathf.Approximately(massScale, d.massScale)) parts.Add("质量倍率 " + massScale);
        if (!Mathf.Approximately(freeAngle, d.freeAngle)) parts.Add("锁死关节放开 " + freeAngle + "°");
        if (!Mathf.Approximately(weldSpring, d.weldSpring)) parts.Add("锁死关节回弹 " + weldSpring);
        if (clothNoSelf != d.clothNoSelf) parts.Add("布料互不碰撞 " + clothNoSelf);
        if (projection != d.projection) parts.Add("关节防拉散 " + projection);
        if (skirt.on) parts.Add("裙子新做法（" + skirt.Describe() + "）");
        if (coat.on) parts.Add("外套衣摆新做法（" + coat.Describe() + "）");
        if (acc.on) parts.Add("饰品新做法（" + acc.Describe() + "）");
        if (chest.on) parts.Add("胸部新做法（" + chest.Describe() + "）");
        return parts.Count == 0 ? "全是默认值" : string.Join(" / ", parts.ToArray());
    }
#endif

    /// 手写的 JSON 难免有超范围的值，夹一下 —— 尤其 solverIters=0 会让 PhysX 直接不解算。
    internal void Clamp()
    {
        gravity = Mathf.Clamp(gravity, 0f, 200f);
        follow = Mathf.Clamp01(follow);
        followUp = Mathf.Clamp01(followUp);
        damping = Mathf.Clamp(damping, 0.2f, 5f);
        separate = Mathf.Clamp(separate, 0.5f, 20f);
        maxSpin = Mathf.Clamp(maxSpin, 7f, 300f);
        springDamper = Mathf.Clamp(springDamper, 0f, 20f);
        solverIters = Mathf.Clamp(solverIters, 1, 60);
        massScale = Mathf.Clamp(massScale, 0.01f, 100f);
        freeAngle = Mathf.Clamp(freeAngle, 0f, 120f);
        weldSpring = Mathf.Clamp(weldSpring, 0f, 200f);
        // 手写的 json 里写成 null 也别崩：回到默认（= 关）
        if (skirt == null) skirt = Skirt();
        if (coat == null) coat = Coat();
        if (acc == null) acc = Acc();
        if (chest == null) chest = Chest();
        skirt.Clamp();
        coat.Clamp();
        acc.Clamp();
        chest.Clamp();
    }
}
