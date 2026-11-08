using System.Collections.Generic;
using UnityEngine;

namespace AstralDivide.Client;

/// <summary>
/// PMX 的 16 个碰撞组 → Unity 的层。全局一张表、和单个角色无关（2026-11-09 从 MmdRig 挪出来，审查 S1）。
///
/// PMX 的 16 个碰撞组要落到 Unity 的层上，但**游戏自己的层碰撞矩阵**把一大堆层对关掉了
/// （贝丝蒂实机：0×1 0×2 0×4 0×5 2×4 2×5 2×8 4×8 5×8 全被挡）。
///
/// 首版把组号直接当层号、指望 `includeLayers` 去强行覆盖矩阵 —— 那是在赌一个不确定的行为。
/// 现在改成**先扫出一组「彼此之间矩阵全开」的层**再映射，
/// 于是只需要用 `excludeLayers`（方向明确、优先级最高）去实现 PMX 的「不碰」规则，不用赌了。
/// </summary>
internal static class MmdLayers
{
    private static int[] _layerOf;                           // PMX 组 → 层：身体碰撞体永远在这儿，布料平时也在这儿
    private static int[] _clothLayerOf;                      // PMX 组 → 「布料之间互不碰撞」打开时布料待的层
    private static int _clothMask;                           // 上面那 16 个布料层的位掩码

    /// 第一次建刚体之前扫一次（层矩阵是游戏定的，一局里不变）
    internal static void Ensure()
    {
        if (_layerOf != null)
            return;
        List<int> pool = FullyOpenLayers();
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
        // EFT 的层矩阵永远只扫得出 5 个全开层，多出来的组分到矩阵未必全开的层、靠 includeLayers 强开 —— 常态不打日志（10-17 Tech Leader 定）
        if (new HashSet<int>(_layerOf).Count < 16)
            Plugin.Log.LogError("[mmd] Some collision groups share a layer: one group's exclude also hits the other, "
                                + "cloth will clip at random (this is how legs went through the skirt on 2026-08-26)");
    }

    /// 彼此之间（和自己）在游戏的层矩阵里全开的层，最多 16 个
    private static List<int> FullyOpenLayers()
    {
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
        return pool;
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

    /// <summary>
    /// 一个刚体的层和碰撞规矩。第 n 位 = 1 表示「和第 n 组碰撞」。
    ///
    /// 层是 <see cref="Ensure"/> 挑出来的「矩阵里两两全开」的一组，所以只要把不该碰的排掉就行。
    /// exclude 是**逐碰撞体**的，两边各排一次，正好等于 PMX 那条「双方都同意才碰」的规矩。
    /// ⚠️ 两道保险都要：EFT 的碰撞矩阵很严，实机只扫得出 **5 个**彼此全开的层（要 16 个），
    /// 剩下的组只能退到矩阵未必全开的层 —— 所以 include（强开）不能撤，exclude（强关）才是 PMX 规矩的执行者。
    /// </summary>
    internal static void Apply(GameObject go, Collider col, MmdBody body, bool clothNoSelf)
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
        bool noSelf = clothNoSelf && cloth;
        int group = Mathf.Clamp(body.group, 0, 15);
        go.layer = noSelf ? _clothLayerOf[group] : _layerOf[group];
        if (noSelf)
            exclude |= _clothMask;
        col.includeLayers = include;
        col.excludeLayers = exclude;
    }
}
