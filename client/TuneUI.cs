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
/// </summary>
internal static class TuneUI
{
    private const string Sec = "布料手感(调参台)";
    private static string _dir;
    private static bool _sync;                      // Pull 写滑块时挡住 Push，免得自己触发自己
    private static float _pulledAt = -10f;          // 最近一次 Pull 的时刻：之后 2 秒内的「变更」不是人拖的，一律不写
    private static string _dirty;                   // 待写盘的模型名；null = 没有改动
    private static float _dirtyAt;                  // 最后一次改动的时刻，用来做防抖

    private static ConfigEntry<string> _model;
    private static ConfigEntry<float> _gravity, _follow, _followUp, _damping, _separate, _maxSpin, _springDamper, _massScale, _freeAngle, _weldSpring;
    private static ConfigEntry<bool> _clothNoSelf, _projection, _save;
    private static ConfigEntry<int> _solverIters;

    internal static void Bind(ConfigFile cfg, string dir, List<string> models)
    {
        if (models.Count == 0)
            return;
        _dir = dir;
        _model = cfg.Bind(Sec, "当前模型", models[0], new ConfigDescription(
            "下面的滑块只作用在这一套衣服上。切换会读出那一套自己的数值",
            new AcceptableValueList<string>(models.ToArray())));

        _follow = Num(cfg, "跟随强度(水平)", 1f, 0f, 1f,
            "剥掉玩家操作传给布料的力：前进后退、左右平移。1 = 完全剥掉（跑起来衣服不往前冲）；0 = 保留物理惯性。"
            + "转身不剥 —— 坐标轴必须与世界对齐，否则关节零点被拧歪、链条炸成碎片");
        _followUp = Num(cfg, "跟随强度(上下起伏)", 1f, 0f, 1f,
            "单独管竖直。走路时胯骨一步一颠，那个颠簸正是少前2 里头发弹动的来源 —— "
            + "**想让头发弹起来就往下调**（0.3~0.6）。代价是跳跃/下蹲的冲击也跟着放回来");
        _damping = Num(cfg, "阻尼倍率", 1.75f, 0.2f, 5f, "乘在 PMX 阻尼上，调大 = 甩起来更快停下");
        _separate = Num(cfg, "分离速度上限", 10f, 0.5f, 20f,
            "两块布料压在一起时被推开的最大速度（单位/秒）。Unity 默认 10。调小 = 慢慢分开而不是弹开");
        _gravity = Num(cfg, "重力", 9.8f, 0f, 200f, "PMX 单位/秒²。1 单位 ≈ 8~10cm，所以地球重力 ≈ 100；MMD 默认 9.8 是刻意飘的");
        _maxSpin = Num(cfg, "最大角速度", 50f, 7f, 300f, "单个刚体的自转上限（弧度/秒）");
        _springDamper = Num(cfg, "弹簧阻尼", 0.5f, 0f, 20f, "PMX 只写了刚度没写阻尼，这里补。太小会抖，太大会发木。对放开的锁死关节它是阻尼比：1 = 临界，2 以上 = 转头时头发慢慢跟上不甩");
        _massScale = Num(cfg, "质量倍率", 1f, 0.01f, 100f,
            "全部布料刚体的质量一起乘这个数（PMX 原值 = 1）。质量大 = 惯性大、甩起来慢停、关节阻尼/碰撞推不太动；"
            + "质量小 = 轻飘、阻尼吃得重。拖着看头发硬不硬，实时生效");
        _freeAngle = Num(cfg, "锁死关节放开角度", 45f, 0f, 120f,
            "PMX 里六个自由度全锁死的链关节（等于焊死，可露凯的头发全是）放开成 ±这个角度。0 = 照 PMX 焊死（头发变铁棍）。实时生效");
        _weldSpring = Num(cfg, "锁死关节回弹", 10f, 0f, 200f,
            "放开的锁死关节回到发型的力（按质量配）。0 = 不回弹、只剩重力（头发全塌到一起打结）；越大越像作者的原始造型、晃得越小。实时生效");
        _solverIters = cfg.Bind(Sec, "求解器迭代数", 12,
            new ConfigDescription("越高链条越不容易被拉散，越费 CPU", new AcceptableValueRange<int>(1, 60)));
        _clothNoSelf = cfg.Bind(Sec, "布料之间互不碰撞", false,
            "开 = 头发/外套/裙子/挎包彼此穿过去，只和身体碰（VRChat 的骨骼物理就是这么干的）。"
            + "关 = 按 PMX 的碰撞组走。**它们互相顶来顶去就开这个**");
        _projection = cfg.Bind(Sec, "关节防拉散", true,
            "把被拉开的关节强行拽回。⚠️ 关掉过一次，裙子外套的 20 段长链当场被扯成扇形碎片。**别关**");
        _save = cfg.Bind(Sec, "立即保存", false,
            "平时不用点 —— 拖完滑块 1.5 秒后会自动写进 physics/<模型>.tuning.json。"
            + "这个只是想马上落盘时用，打勾后自动弹回");

        // 订一次就够 —— `ConfigFile.SettingChanged` 会为本文件里每一项变更触发。
        cfg.SettingChanged += (_, a) =>
        {
            ConfigEntryBase e = a.ChangedSetting;
            if (e.Definition.Section != Sec || _sync)
                return;
            if (e == _model)
            {
                Pull();
                return;
            }
            // ⚠️ 只认 F12 窗口开着时的变更。Fika.Core / MenuOverhaul 会调 ConfigFile.Reload()，
            //    BepInEx 重载时对每一项都触发 SettingChanged，把 cfg 里的旧值回灌进滑块 ——
            //    我们一直把它当成「人拖了」写进 json 并实时应用（projection 被灌成 true，六条头发全僵，2026-08-27）。
            if (!UserEditing)
            {
                Pull();                                 // 把滑块拉回 json 的真值，别让回灌的值留在 UI 上
                return;
            }
            if (e == _save)  DoSave();
            else             Push();
        };
        Pull();
    }

    /// ConfigurationManager 的窗口是否开着（反射读 `DisplayingWindow`，不加引用；没装就永远 false = 调参台只读）。
    private static bool UserEditing
    {
        get
        {
            try
            {
                if (_cm == null)
                {
                    if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("com.bepis.bepinex.configurationmanager", out var info) || info.Instance == null)
                        return false;
                    _cm = info.Instance;
                    _cmWindow = _cm.GetType().GetProperty("DisplayingWindow");
                }
                return _cmWindow != null && _cmWindow.GetValue(_cm) is bool b && b;
            }
            catch { return false; }
        }
    }
    private static object _cm;
    private static System.Reflection.PropertyInfo _cmWindow;

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

    private static ConfigEntry<float> Num(ConfigFile cfg, string key, float def, float lo, float hi, string help) =>
        cfg.Bind(Sec, key, def, new ConfigDescription(help, new AcceptableValueRange<float>(lo, hi)));

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
        _solverIters.Value = t.solverIters;
        _clothNoSelf.Value = t.clothNoSelf;
        _projection.Value = t.projection;
        _sync = false;
        _pulledAt = UnityEngine.Time.realtimeSinceStartup;
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
        if (UnityEngine.Time.realtimeSinceStartup - _pulledAt < 2f)
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
        changed |= Set(ref t.solverIters, _solverIters.Value);
        changed |= Set(ref t.clothNoSelf, _clothNoSelf.Value);
        changed |= Set(ref t.projection, _projection.Value);
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