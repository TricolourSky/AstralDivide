#if SORA_DEV
using System.Collections.Generic;
using BepInEx.Configuration;

namespace AstralDivide.Client;

/// <summary>
/// F12 里的**调参台**：选一套模型，拖滑块实时看效果，满意了按一下写回该模型的
/// `physics/&lt;模型&gt;.tuning.json`。
///
/// 这些滑块**不是全局设置** —— 它们永远只作用在「当前模型」那一栏选中的那套衣服上，
/// 换一栏就切到另一套的数值。真正的设置存在各自的 JSON 里，这里只是个编辑器。
///
/// 之所以要它：三套模型每套都得实机调一遍，改 JSON → 重进战局的循环太慢。
/// 37.29 起只在开发版里有（F12「2. 布料调参台」），发布包不带；发布版照样读 tuning.json。
/// </summary>
internal static class TuneUI
{
    private const string Sec = CfgUtil.SecTune;
    private static int _order = 100;                // F12 里按绑定的先后排（数字大的在前）
    private static string _dir;
    private static bool _sync;                      // Pull 写滑块时挡住 Push，免得自己触发自己
    private static float _pulledAt = -10f;          // 最近一次 Pull 的时刻：之后 0.5 秒内的「变更」不是人拖的，一律不写
    private static string _dirty;                   // 待写盘的模型名；null = 没有改动
    private static float _dirtyAt;                  // 最后一次改动的时刻，用来做防抖

    private static ConfigEntry<string> _model;
    private static ConfigEntry<float> _gravity, _follow, _followUp, _damping, _separate, _maxSpin, _springDamper, _massScale, _freeAngle, _weldSpring;
    private static ConfigEntry<float> _chestAngle, _chestSpring, _chestDamper, _chestRange, _chestSpringScale, _chestDampScale;   // 胸部（37.30）
    private static ConfigEntry<bool> _clothNoSelf, _projection, _save;
    private static ConfigEntry<int> _solverIters;
    // 衣服新做法（37.31）：先选部位，下面几项调的就是这套衣服的这个部位
    private static ConfigEntry<string> _swayPart;
    private static ConfigEntry<bool> _swayOn;
    private static ConfigEntry<float> _swayFollow, _swayHz, _swayZeta, _swayInertia, _swayAngle, _swayRootAngle, _swayGravity;
    private static readonly string[] SwayParts = { "裙", "外套衣摆", "饰品", "胸部" };
    // 胸部两套滑块按当前模型的胸部是哪种显示（Pull 里切）：六轴全锁的（六套）用前三个，原模型带弹簧的（蕾娜）用后三个倍数
    private static readonly List<ConfigurationManagerAttributes> ChestWeldAttrs = new List<ConfigurationManagerAttributes>();
    private static readonly List<ConfigurationManagerAttributes> ChestSprungAttrs = new List<ConfigurationManagerAttributes>();

    internal static void Bind(ConfigFile cfg, string dir, List<string> models)
    {
        if (models.Count == 0)
            return;
        _dir = dir;
        _model = cfg.Bind(Sec, "当前模型", models[0], CfgUtil.Desc(
            "下面的滑块只作用在这一套衣服上。换装 / 进战局时自动切到你身上穿的那套，也可以手动选；切换会读出那一套自己的数值", _order--,
            new AcceptableValueList<string>(models.ToArray())));
        BindSway(cfg);

        // ⚠️ 下面这组（到「关节防拉散」为止）只管头发 / 兽耳尾巴 / 没交给新做法的一节骨绳子这些还走 PhysX 的；
        //    衣服、胸部、饰品走新做法，在「新做法」那组调（37.31：Tech Leader 反映「很多滑块调了跟没调一样」）
        _follow = Num(cfg, "跟随强度(水平)", 1f, 0f, 1f,
            "只管头发。剥掉玩家操作传给头发的力：前进后退、左右平移。1 = 完全剥掉（跑起来头发不往前冲）；0 = 保留物理惯性。"
            + "转身不剥 —— 坐标轴必须与世界对齐，否则关节零点被拧歪、链条炸成碎片");
        _followUp = Num(cfg, "跟随强度(上下起伏)", 1f, 0f, 1f,
            "只管头发。单独管竖直。走路时胯骨一步一颠，那个颠簸正是少前2 里头发弹动的来源 —— "
            + "**想让头发弹起来就往下调**（0.3~0.6）。代价是跳跃/下蹲的冲击也跟着放回来");
        _damping = Num(cfg, "阻尼倍率", 1.75f, 0.2f, 5f, "只管头发。乘在 PMX 阻尼上，调大 = 甩起来更快停下");
        _separate = Num(cfg, "分离速度上限", 10f, 0.5f, 20f,
            "只管头发。头发压进身体 / 别的头发时被推开的最大速度（单位/秒）。Unity 默认 10。调小 = 慢慢分开而不是弹开");
        _gravity = Num(cfg, "重力", 9.8f, 0f, 200f,
            "只管头发。PMX 单位/秒²：1 单位 ≈ 8~10cm，地球重力 ≈ 100（MMD 界面上的 9.8 内部要 ×10）。太小头发垂不下来、像硬棍；蕾娜 / OTs-14 / 可露凯用 200");
        _maxSpin = Num(cfg, "最大角速度", 50f, 7f, 300f, "只管头发。单个刚体的自转上限（弧度/秒）");
        _springDamper = Num(cfg, "弹簧阻尼", 0.5f, 0f, 20f, "只管头发。PMX 只写了刚度没写阻尼，这里补。太小会抖，太大会发木。对放开的锁死关节它是阻尼比：1 = 临界，2 以上 = 转头时头发慢慢跟上不甩");
        _massScale = Num(cfg, "质量倍率", 1f, 0.01f, 100f,
            "只管头发。头发刚体的质量一起乘这个数（PMX 原值 = 1）。质量大 = 惯性大、甩起来慢停、关节阻尼/碰撞推不太动；"
            + "质量小 = 轻飘、阻尼吃得重。实时生效");
        _freeAngle = Num(cfg, "锁死关节放开角度", 45f, 0f, 120f,
            "只管头发。PMX 里六个自由度全锁死的链关节（等于焊死，可露凯的头发全是）放开成 ±这个角度。0 = 照 PMX 焊死（头发变铁棍）。实时生效");
        _weldSpring = Num(cfg, "锁死关节回弹", 10f, 0f, 200f,
            "只管头发。放开的锁死关节回到发型的力（按质量配）。0 = 不回弹、只剩重力（头发全塌到一起打结）；越大越像作者的原始造型、晃得越小。实时生效");
        BindChest(cfg);
        _solverIters = cfg.Bind(Sec, "求解器迭代数", 12,
            CfgUtil.Desc("只管头发。越高链条越不容易被拉散，越费 CPU", _order--, new AcceptableValueRange<int>(1, 60), "头发 · 求解器迭代数"));
        _clothNoSelf = cfg.Bind(Sec, "布料之间互不碰撞", false, CfgUtil.Desc(
            "只管头发。开 = 头发不碰衣服（穿过外套 / 围巾，只和身体碰），衣服那边也不用留给头发碰的代理刚体；"
            + "关 = 按 PMX 的碰撞组走。⚠️ 换装 / 重进战局才生效", _order--, null, "头发 · 不碰衣服"));
        _projection = cfg.Bind(Sec, "关节防拉散", true, CfgUtil.Desc(
            "只管头发。把被拉开的关节强行拽回。⚠️ 关掉的话头发被碰一下就可能散开、翘着回不去（索普 37.31）。**别关**", _order--, null, "头发 · 关节防拉散"));
        _save = cfg.Bind(Sec, "立即保存", false, new ConfigDescription(
            "平时不用按：拖完滑块 1.5 秒自动存进 physics\\<模型>.tuning.json；想马上存就按这个", null,
            new ConfigurationManagerAttributes { Order = _order--, HideDefaultButton = true, CustomDrawer = SaveButton }));

        // 订一次就够 —— `ConfigFile.SettingChanged` 会为本文件里每一项变更触发。
        cfg.SettingChanged += (_, a) =>
        {
            ConfigEntryBase e = a.ChangedSetting;
            if (e.Definition.Section != Sec || _sync)
                return;
            if (e == _model || e == _swayPart)
            {
                Pull();
                return;
            }
            // ⚠️ 只认 F12 窗口开着时的变更。Fika.Core / MenuOverhaul 会调 ConfigFile.Reload()，
            //    BepInEx 重载时对每一项都触发 SettingChanged，把 cfg 里的旧值回灌进滑块 ——
            //    我们一直把它当成「人拖了」写进 json 并实时应用（projection 被灌成 true，六条头发全僵，2026-08-27）。
            if (!CfgUtil.F12Open)
            {
                Pull();                                 // 把滑块拉回 json 的真值，别让回灌的值留在 UI 上
                return;
            }
            if (e == _save)  DoSave();
            else             Push();
        };
        Pull();
    }

    /// <summary>
    /// 把「当前模型」切到本地玩家身上穿的那套。
    ///
    /// `当前模型` 是存进 `.cfg` 的，会跨存档记住 —— 于是很容易出现「身上穿 A、下拉框停在 B」，
    /// 拖滑块调的是 B、看到的是 A，**一点提示都没有**。换装/进战局时自动切过去就没这个坑了。
    /// 只认本地玩家和主菜单预览人物；FIKA 队友身上的衣服不许抢这个下拉框。
    /// </summary>
    internal static void Follow(string model)
    {
        if (_model == null || string.IsNullOrEmpty(model) || _model.Value == model)
            return;
        if (MmdRaw.TuningOf(model) == null)
            return;
        _model.Value = model;                       // 触发 SettingChanged → Pull()，滑块跟着换成这套的值
        Plugin.Log.LogInfo($"[mmd] 调参台已切到你身上穿的这套：{model}");
    }

    // 只给头发那组（PhysX）用：显示名前面加「头发 · 」，键名不变（cfg 里的值对得上）
    private static ConfigEntry<float> Num(ConfigFile cfg, string key, float def, float lo, float hi, string help) =>
        cfg.Bind(Sec, key, def, CfgUtil.Desc(help, _order--, new AcceptableValueRange<float>(lo, hi), "头发 · " + CfgUtil.Pretty(key)));

    /// 胸部几项（DEV_NOTES 37.30）：只管胸部关节、和头发分开。初始值 = 原生数值（六套 = 头发那三项现在的值，蕾娜 = 原模型 ×1）
    private static void BindChest(ConfigFile cfg)
    {
        const string tail = "只管胸部、和头发分开，实时生效";
        _chestAngle = ChestNum(cfg, "胸部摆动角度", "胸部摆动角度（度）", 45f, 120f, ChestWeldAttrs, "胸部能晃多大角度（±度）；0 = 不动。" + tail);
        _chestSpring = ChestNum(cfg, "胸部回弹", "胸部回弹", 10f, 200f, ChestWeldAttrs, "胸部回到原位的力（按质量配）；越大晃得越小、回得越快，0 = 只剩重力。" + tail);
        _chestDamper = ChestNum(cfg, "胸部阻尼比", "胸部阻尼比", 0.5f, 20f, ChestWeldAttrs, "晃动停下来的快慢：1 = 刚好不冲过头，越大越黏、晃一下就停。" + tail);
        _chestRange = ChestNum(cfg, "胸部摆动幅度倍数", "胸部摆动幅度（倍）", 1f, 2f, ChestSprungAttrs, "原模型胸部的摆动范围 × 这个数；1 = 原模型，0 = 不动。" + tail);
        _chestSpringScale = ChestNum(cfg, "胸部回弹倍数", "胸部回弹（倍）", 1f, 5f, ChestSprungAttrs, "原模型胸部的弹簧 × 这个数；1 = 原模型，越大越硬、晃得越小。" + tail);
        _chestDampScale = ChestNum(cfg, "胸部阻尼倍数", "胸部阻尼（倍）", 1f, 5f, ChestSprungAttrs, "原模型胸部的阻尼 × 这个数；1 = 原模型，越大停得越快。" + tail);
    }

    private static ConfigEntry<float> ChestNum(ConfigFile cfg, string key, string name, float def, float hi, List<ConfigurationManagerAttributes> group, string help)
    {
        var attr = new ConfigurationManagerAttributes { Order = _order--, DispName = name, Browsable = false };
        group.Add(attr);
        return cfg.Bind(Sec, key, def, new ConfigDescription(help, new AcceptableValueRange<float>(0f, hi), attr));
    }

    /// <summary>
    /// 衣服新做法（DEV_NOTES 37.31）：先选部位（裙 / 外套衣摆 / 饰品 / 胸部），下面几项就是这套衣服这个部位的参数。
    /// 开关要重新换装 / 重进战局才生效（得重建刚体），其余几项实时生效。存进这套的 tuning.json（skirt / coat / acc / chest）。
    /// </summary>
    private static void BindSway(ConfigFile cfg)
    {
        _swayPart = cfg.Bind(Sec, "衣服新做法：部位", SwayParts[0], CfgUtil.Desc(
            "下面「新做法」几项调的是这套衣服的哪个部位。新做法 = 不建 PhysX 刚体，改成跟着身体走 + 弹簧跟随（少前 2 动画的规律）；胸部 = 软弹簧 + 跟着身体颠",
            _order--, new AcceptableValueList<string>(SwayParts), "新做法 · 部位"));
        // 「现在调的是哪个部位、这套有没有」—— 画成一行字，每帧现读，切部位 / 换模型马上变（37.31：「调了跟没调一样」多半是调错了部位）
        cfg.Bind(Sec, "衣服新做法：正在调", "", new ConfigDescription("", null,
            new ConfigurationManagerAttributes { Order = _order--, HideDefaultButton = true, CustomDrawer = SwayNowLabel }));
        _swayOn = cfg.Bind(Sec, "衣服新做法：开启", false, CfgUtil.Desc(
            "开 = 这个部位改用新做法；关 = 照旧 PMX 物理。⚠️ 要重新换装或重进战局才生效（下面几项拖了马上生效）", _order--, null, "新做法 · 开启"));
        _swayFollow = SwayNum(cfg, "带动比例", 0.9f, 1f, "链根跟同侧大腿转多少：1 = 完全跟腿，0 = 只跟挂点（胯 / 胸）。少前 2 的裙子前片约 0.9，外套衣摆约 0.15");
        _swayHz = SwayNum(cfg, "软硬", 6f, 12f, "每节追回原形的快慢（Hz）：越大越硬、跟得越紧；越小越飘、甩得越慢（低于 4 跑步时梢部容易跟着步子共振；挂着往下垂的带子例外，0.5 = 让重力说了算）", "新做法 · 软硬（Hz）");
        _swayZeta = SwayNum(cfg, "阻尼比", 0.6f, 3f, "1 = 刚好不冲过头；小于 1 甩一下再停；大于 1 慢慢跟上、不甩");
        _swayInertia = SwayNum(cfg, "甩动", 0f, 1f, "角色走跑的颠簸、起步急停、跳起落地传进来多少：0 = 不传（只跟身体自己的动作，像少前 2），1 = 全传。转身一律不传");
        _swayAngle = SwayNum(cfg, "最大摆角", 45f, 90f, "每节最多偏离原形多少度：调小 = 再怎么动也只晃一点", "新做法 · 最大摆角（度）");
        _swayRootAngle = SwayNum(cfg, "第一节摆角", 0f, 170f,
            "挂点那一节最多偏离原形多少度：0 = 跟「最大摆角」一样。挂在环上的带子要开大（OTs-14 是 150），手臂抬起来时才能照样往下垂", "新做法 · 第一节摆角（度）");
        _swayGravity = SwayNum(cfg, "重力", 0f, 20f, "米/秒²：0 = 保持建模的形状（少前 2 的衣服不往下坠），9.8 = 地球重力", "新做法 · 重力（m/s²）");
    }

    private static ConfigEntry<float> SwayNum(ConfigFile cfg, string key, float def, float hi, string help, string name = null) =>
        cfg.Bind(Sec, "衣服新做法：" + key, def, CfgUtil.Desc(help, _order--, new AcceptableValueRange<float>(0f, hi), name ?? "新做法 · " + key));

    private static void SwayNowLabel(ConfigEntryBase e)
    {
        string part = _swayPart != null ? _swayPart.Value : "";
        int n = _model != null ? PhysWorld.SwayCount(_model.Value, SwayKindOf(part)) : 0;
        UnityEngine.GUILayout.Label(n > 0
                ? $"▼ 下面几项调的是【{part}】：这套身上有 {n} 条"
                : $"▼ 下面几项调的是【{part}】：这套没有（或没开 / 没穿上），拖了不会变 —— 先在上面换部位",
            UnityEngine.GUILayout.ExpandWidth(true));
    }

    private static SwayKind SwayKindOf(string part) =>
        part == SwayParts[1] ? SwayKind.Coat : part == SwayParts[2] ? SwayKind.Acc : part == SwayParts[3] ? SwayKind.Chest : SwayKind.Skirt;

    private static void PullSway(MmdTuning t)
    {
        SwayPart p = t.Part(SwayKindOf(_swayPart.Value));
        _swayOn.Value = p.on;
        _swayFollow.Value = p.follow;
        _swayHz.Value = p.hz;
        _swayZeta.Value = p.zeta;
        _swayInertia.Value = p.inertia;
        _swayAngle.Value = p.maxAngle;
        _swayRootAngle.Value = p.rootAngle;
        _swayGravity.Value = p.gravity;
    }

    private static bool PushSway(MmdTuning t)
    {
        SwayPart p = t.Part(SwayKindOf(_swayPart.Value));
        bool changed = Set(ref p.on, _swayOn.Value);
        changed |= Set(ref p.follow, _swayFollow.Value);
        changed |= Set(ref p.hz, _swayHz.Value);
        changed |= Set(ref p.zeta, _swayZeta.Value);
        changed |= Set(ref p.inertia, _swayInertia.Value);
        changed |= Set(ref p.maxAngle, _swayAngle.Value);
        changed |= Set(ref p.rootAngle, _swayRootAngle.Value);
        changed |= Set(ref p.gravity, _swayGravity.Value);
        if (changed)
            p.Clamp();
        return changed;
    }

    /// 胸部滑块显示哪套：看这套模型的胸部关节是哪种（没有胸部物理就都不显示；下次打开 F12 生效）
    private static void ShowChest(string model)
    {
        MmdModel m = MmdRaw.Get(model);
        // 胸部走新做法（37.31）时这几个 PhysX 胸部滑块没用了，藏起来（胸部在「新做法 · 部位 = 胸部」里调）
        bool any = m != null && m.ChestJoints.Count > 0 && !m.Tuning.chest.on, sprung = any && m.ChestSprung;
        foreach (ConfigurationManagerAttributes a in ChestWeldAttrs)
            a.Browsable = any && !sprung;
        foreach (ConfigurationManagerAttributes a in ChestSprungAttrs)
            a.Browsable = sprung;
    }

    /// 「立即保存」画成按钮：按下 = 值设成 true → SettingChanged → DoSave（存完自己弹回 false）
    private static void SaveButton(ConfigEntryBase e)
    {
        if (UnityEngine.GUILayout.Button("保存这一套", UnityEngine.GUILayout.ExpandWidth(true)))
            e.BoxedValue = true;
    }

    /// 选中模型的参数 → 滑块
    private static void Pull()
    {
        MmdTuning t = MmdRaw.TuningOf(_model.Value);
        if (t == null)
            return;
        _sync = true;
        _gravity.Value = t.gravity;
        _follow.Value = t.follow;
        _followUp.Value = t.followUp;
        _damping.Value = t.damping;
        _separate.Value = t.separate;
        _maxSpin.Value = t.maxSpin;
        _springDamper.Value = t.springDamper;
        _massScale.Value = t.massScale;
        _freeAngle.Value = t.freeAngle;
        _weldSpring.Value = t.weldSpring;
        _chestAngle.Value = t.chestAngle;
        _chestSpring.Value = t.chestSpring;
        _chestDamper.Value = t.chestDamper;
        _chestRange.Value = t.chestRange;
        _chestSpringScale.Value = t.chestSpringScale;
        _chestDampScale.Value = t.chestDampScale;
        _solverIters.Value = t.solverIters;
        _clothNoSelf.Value = t.clothNoSelf;
        _projection.Value = t.projection;
        PullSway(t);
        _sync = false;
        _pulledAt = UnityEngine.Time.realtimeSinceStartup;
        ShowChest(_model.Value);
    }

    /// 滑块 → 选中模型的参数，然后立刻推给场上所有穿着这套衣服的角色
    private static void Push()
    {
        MmdTuning t = MmdRaw.TuningOf(_model.Value);
        if (_sync || t == null)
            return;
        // ⚠️ 两道闸（2026-08-27 实机：可露凯 tuning 被灌成默认值，六条头发全僵）：
        //  ① Pull 后 2 秒内的 SettingChanged 不是人拖的（cfg 里存的上一套模型/上一次会话的滑块值在启动、
        //     换模型时会回灌进来，日志里「自动写入」两次、第一次少字段就是它），一律不写；
        //  ② 只把**和当前值不同**的字段写进去，滑块没动的字段绝不覆盖 json。
        if (UnityEngine.Time.realtimeSinceStartup - _pulledAt < 0.5f)   // 回灌是同一帧里的事；2 秒太长，切完部位马上拖会被吞掉（37.31）
            return;
        bool changed = false;
        changed |= Set(ref t.gravity, _gravity.Value);
        changed |= Set(ref t.follow, _follow.Value);
        changed |= Set(ref t.followUp, _followUp.Value);
        changed |= Set(ref t.damping, _damping.Value);
        changed |= Set(ref t.separate, _separate.Value);
        changed |= Set(ref t.maxSpin, _maxSpin.Value);
        changed |= Set(ref t.springDamper, _springDamper.Value);
        changed |= Set(ref t.massScale, _massScale.Value);
        changed |= Set(ref t.freeAngle, _freeAngle.Value);
        changed |= Set(ref t.weldSpring, _weldSpring.Value);
        changed |= Set(ref t.chestAngle, _chestAngle.Value);
        changed |= Set(ref t.chestSpring, _chestSpring.Value);
        changed |= Set(ref t.chestDamper, _chestDamper.Value);
        changed |= Set(ref t.chestRange, _chestRange.Value);
        changed |= Set(ref t.chestSpringScale, _chestSpringScale.Value);
        changed |= Set(ref t.chestDampScale, _chestDampScale.Value);
        changed |= Set(ref t.solverIters, _solverIters.Value);
        changed |= Set(ref t.clothNoSelf, _clothNoSelf.Value);
        changed |= Set(ref t.projection, _projection.Value);
        changed |= PushSway(t);
        if (!changed)
            return;
        PhysWorld.ApplyTuning(_model.Value);
        _dirty = _model.Value;                      // 标记待写盘，真正落盘交给 Tick 防抖
        _dirtyAt = UnityEngine.Time.realtimeSinceStartup;
    }

    /// <summary>
    /// 由 <see cref="Plugin"/> 每帧调一次：改动停下 1.5 秒就自动写盘。
    ///
    /// 以前只有「打勾保存」这一条路，拖完滑块忘了打勾就白调了，而且**每套模型要各打一次勾** ——
    /// 切了「当前模型」再打勾存的是新那套，旧那套的改动无声丢失。自动保存把这个坑填掉。
    /// 防抖是必须的：拖滑块时 SettingChanged 每帧都触发，不防抖就是每秒几十次写文件。
    /// </summary>
    internal static void Tick()
    {
        if (_dirty == null || UnityEngine.Time.realtimeSinceStartup - _dirtyAt < 1.5f)
            return;
        string model = _dirty;
        _dirty = null;
        MmdTuning t = MmdRaw.TuningOf(model);
        if (t != null && t.Save(_dir, model))
            Plugin.Log.LogInfo($"[mmd] 手感已自动写入 {model}.tuning.json（{t.Describe()}）");
    }

    private static bool Set<T>(ref T field, T value)
    {
        if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        return true;
    }

    private static void DoSave()
    {
        if (!_save.Value)
            return;
        _save.Value = false;                        // 当按钮用，按完弹回
        _dirty = null;                              // 手动存过就别让防抖再存一次
        MmdTuning t = MmdRaw.TuningOf(_model.Value);
        if (t != null && t.Save(_dir, _model.Value))
            Plugin.Log.LogInfo($"[mmd] 已写入 {_model.Value}.tuning.json（{t.Describe()}）");
    }
}
#endif