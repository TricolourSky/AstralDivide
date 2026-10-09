using System;
using System.Collections.Generic;
using EFT;
using EFT.Ballistics;
using EFT.HealthSystem;
using EFT.Visual;
using UnityEngine;

namespace AstralDivide.Client;

/// <summary>
/// 战局表情第 5 版（DEV_NOTES 三十六；37.32 起有形态键的头都挂，BOT 穿着也生效）：
/// 第 4 版「少女前线中破风」—— Tech Leader 给了 UMP45 受损立绘（「大概是这种风格」）：一只眼痛得闭上、另一只锐利地盯着、嘴张着喘。
/// 第 5 版只改濒死（实机后 Tech Leader：「濒死的那个呼吸的嘴改一下，感觉看起来挺开心的」「然后右眼半闭」→ 选「C搭配眼1」）：
/// 大张嘴 + 拉宽配上闭着的一只眼像眨眼大笑 → 改成小圆嘴喘、嘴角往下，原来睁着的那只也半闭。
/// 轻微 = 眯一只眼 + 嘴一抿；重度 = 一只眼快闭上 + 张嘴急喘；濒死 = 一只眼闭紧 + 另一只半闭 + 小圆嘴喘。
/// 中弹：闭着那只挤紧、睁着那只一眯、嘴张开「呃！」，0.2~0.3 秒回到原来的状态。死亡：慢慢闭眼，嘴闭上。
/// 伤势 = 总血量、头、胸口三者里掉得最多的比例；15% / 45% / 70% 进下一级，回落要再低 5%，不来回闪。
/// 中弹 = 游戏的受伤事件，规则照游戏自己的痛哼（Player.OnAudioHealthApplyDamage）：流血 / 脱水这类自身掉血不算，吃了止痛药 4 点以下不算。
/// 和 docs\表情图鉴\ots14_战局表情预览.mp4（raid_preview5.py）同一套参数和式子；
/// 预览里伤情 / 中弹按时间表、眨眼时间固定，这里按血量、受伤事件、随机眨眼。
/// </summary>
internal sealed class RaidFace
{
    // 37.32 起不再只给 OTs-14：头上有形态键的都挂（贝丝蒂 / 索普的头部网格 0 个键，Create 返回 null）。
    // 表情组合是按 OTs-14 的受损立绘调的（三十六）；别的脸缺的键（ｷﾘｯ / 口横縮げ / 瞳小右）那一路空着，样子淡一点但不穿帮。
    private static readonly string[] Shapes = { "ウィンク２", "ｷﾘｯ", "口横縮げ", "口角下げ", "あ", "あ２", "瞳小右", "ウィンク２右", "お" };
    private const int Wink = 0, WinkR = 7, Lid = 9, Channels = 10;  // ウィンク２ / ウィンク２右 / まばたき（第 10 路只算，由 MmdBlink 去写）
    private static readonly float[][] Levels =
    {
        //    ウィンク２ ｷﾘｯ 口横縮げ 口角下げ  あ   あ２ 瞳小右 ウィンク２右 お まばたき
        new[] {   0f,  0f,   0f,    0f,   0f,  0f,   0f,   0f,     0f,   0f },   // 0 健康
        new[] {  25f, 20f,  20f,    0f,   0f,  0f,   0f,   0f,     0f,   0f },   // 1 轻微：眯一只眼、嘴一抿
        new[] {  60f, 30f,   0f,   30f,   0f,  0f,   0f,   0f,     0f,   0f },   // 2 重度：一只眼痛得快闭上（嘴由喘气管）
        new[] {  90f, 35f,   0f,   40f,   0f,  0f,  15f,  50f,     0f,   0f },   // 3 濒死：一只眼闭紧、另一只半闭、嘴角下（嘴由喘气管）
    };
    private static readonly float[] Dead =  {   0f,  0f,   0f,    0f,   0f,  0f,   0f,   0f,     0f, 100f };   // 慢慢闭眼，嘴闭上
    private static readonly float[] Wince = { 100f, 60f,   0f,   30f,   0f, 40f,   0f,   0f,     0f,  35f };   // 中弹那一下
    // 喘气（只有第 2、3 级）：哪个嘴型、底、幅度、Hz —— 重度 あ 28±12 急喘，濒死 お 40±10 小圆嘴喘；换级时两种嘴型交叉淡入淡出
    private static readonly int[] BreathLevel = { 2, 3 }, BreathCh = { 4, 8 };
    private static readonly float[] BreathBase = { 28f, 40f }, BreathAmp = { 12f, 10f }, BreathHz = { 0.6f, 0.5f };
    private static readonly float[] Up = { 0.15f, 0.45f, 0.70f };   // 掉血比例到这就进下一级
    private const float Margin = 0.05f, Poll = 0.25f, SubStep = 1f / 240f;
    private const float FaceHz = 0.8f, BreathFadeHz = 0.5f, IdleBreathHz = 0.5f;   // 换级 ≈0.7 秒；喘气幅度淡入淡出
    private const float WinceHz = 2.5f, WinceDamp = 0.9f;           // 中弹：绷紧时快一倍，回状态正常速度
    private const float HitFull = 40f, PainkillerIgnore = 4f;       // 掉 40 以上缩满，以下按比例（至少一半）；止痛药下 4 点以下不缩

    private readonly FaceSpring[] _face = new FaceSpring[Channels];
    private readonly FaceSpring[] _breath = new FaceSpring[BreathLevel.Length];
    private readonly FaceSpring _wince = new FaceSpring(WinceHz, WinceDamp);
    private readonly float[] _y = new float[Channels];             // 这一帧合成出来的各路数值
    private readonly FaceMorphs _morphs;
    private IHealthController _bound;                               // 订阅了谁的受伤事件
    private int _level;
    private float _nextPoll, _phase;
    private float _hitStrength, _hitUntil = -10f;                   // 中弹力度、绷到什么时候（Time.time）
    private float _blinkAt, _blinkStart = -10f;
    private bool _blinkSlow;

    internal static RaidFace Create(LoddedSkin lodded, string model)
    {
        var morphs = new FaceMorphs(lodded, Shapes, model + " 战局表情", warnMissing: false);
        // 两只眼（ウィンク２ / ウィンク２右）和两个喘气嘴型（あ / お）必须都有，缺任何一个 = 这张脸撑不起这套表情
        if (!morphs.Has(Wink) || !morphs.Has(WinkR) || !morphs.Has(BreathCh[0]) || !morphs.Has(BreathCh[1]))
            return null;
        var missing = new List<string>();
        for (int c = 0; c < Shapes.Length; c++)
            if (!morphs.Has(c))
                missing.Add(Shapes[c]);
        Plugin.Log.LogInfo($"[表情] {model} 挂上战局表情" + (missing.Count > 0 ? $"（头上没有 {string.Join("、", missing.ToArray())}，这几路空着）" : ""));
        return new RaidFace(morphs);
    }

    private RaidFace(FaceMorphs morphs)
    {
        for (int c = 0; c < Channels; c++)
            _face[c] = new FaceSpring(FaceHz, 1f);
        for (int i = 0; i < _breath.Length; i++)
            _breath[i] = new FaceSpring(BreathFadeHz, 1f);
        _morphs = morphs;
        _blinkAt = Time.time + UnityEngine.Random.Range(2.5f, 6f);
    }

    /// 每帧由 MmdBlink 调（找到 Player 之后）。返回「まばたき」该是多少（0~100）。
    internal float Tick(IHealthController health, bool dead)
    {
        Bind(health);
        float now = Time.time;
        if (!dead && now >= _nextPoll)
        {
            _nextPoll = now + Poll;
            _level = Level(Severity(health), _level);
        }
        Aim(dead, now);
        float hz = dead ? IdleBreathHz : BreathHzFor(_level);
        for (float left = Time.deltaTime; left > 1e-6f; left -= SubStep)
        {
            float h = Mathf.Min(SubStep, left);
            foreach (FaceSpring s in _face)
                s.Step(h, 0f);
            foreach (FaceSpring s in _breath)
                s.Step(h, 0f);
            _wince.Step(h, 0f);
            _phase = Mathf.Repeat(_phase + 2f * Mathf.PI * hz * h, 2f * Mathf.PI);
        }
        return Compose(dead, now);
    }

    /// 各路弹簧的目标：底脸跟等级（死了 = Dead）；喘气幅度只开当前等级那一路；中弹窗口里绷紧（快一倍），过了窗口回来。
    private void Aim(bool dead, float now)
    {
        float[] target = dead ? Dead : Levels[_level];
        for (int c = 0; c < Channels; c++)
            _face[c].Seek(target[c]);
        for (int i = 0; i < _breath.Length; i++)
            _breath[i].Seek(!dead && BreathLevel[i] == _level ? 1f : 0f);
        float w = !dead && now < _hitUntil ? _hitStrength : 0f;
        _wince.Aim(w, w > _wince.Y ? 2f : 1f);
    }

    /// 合成：底脸 + 喘气 → 按中弹程度往「中弹脸」混 → 眨眼（中弹绷着、死了都不眨）→ ウィンク２ / ウィンク２右 都不超过 100 − まばたき
    /// （同一只眼的单眼闭和双眼闭叠起来眼皮会穿插）。写 9 路形态键，返回まばたき。
    private float Compose(bool dead, float now)
    {
        float w = Mathf.Clamp01(_wince.Y);
        for (int c = 0; c < Channels; c++)
            _y[c] = _face[c].Y;
        float wave = Mathf.Sin(_phase);
        for (int i = 0; i < _breath.Length; i++)
            _y[BreathCh[i]] += _breath[i].Y * (BreathBase[i] + BreathAmp[i] * wave);
        for (int c = 0; c < Channels; c++)
            _y[c] = (1f - w) * _y[c] + w * Wince[c];
        float blink = Blink(now, dead);
        if (dead || w > 0.2f)
            blink = 0f;
        _y[Lid] += (100f - _y[Lid]) * blink;
        _y[Wink] = Mathf.Min(_y[Wink], 100f - _y[Lid]);
        _y[WinkR] = Mathf.Min(_y[WinkR], 100f - _y[Lid]);
        for (int c = 0; c < Lid; c++)
            _morphs.Set(c, _y[c]);
        return Mathf.Clamp(_y[Lid], 0f, 100f);
    }

    private static float BreathHzFor(int level)
    {
        for (int i = 0; i < BreathLevel.Length; i++)
            if (BreathLevel[i] == level)
                return BreathHz[i];
        return IdleBreathHz;
    }

    /// 找到 Player 之后订阅它的受伤事件；换了人先退订旧的。MmdBlink 销毁 / 换头时调 Unbind。
    private void Bind(IHealthController health)
    {
        if (ReferenceEquals(health, _bound))
            return;
        Unbind();
        _bound = health;
        _bound.ApplyDamageEvent += OnDamage;
    }

    internal void Unbind()
    {
        if (_bound != null)
            _bound.ApplyDamageEvent -= OnDamage;
        _bound = null;
    }

    /// 中弹：规则照游戏自己的痛哼 —— 流血 / 脱水 / 中毒这类自身掉血不算（另外排掉 Undefined 和用药回血），吃了止痛药 4 点以下不算。
    /// 力度 = 伤害 / 40，至少 0.5、最多 1；绷 0.08 + 0.12×力度 秒。同一帧几处受伤（霰弹、肢体打烂溢出）取最大、窗口往后延。
    /// ⚠️ 这是游戏的事件，这里抛异常会打断游戏自己的伤害流程 —— 整个包在 try 里。
    private void OnDamage(EBodyPart part, float damage, DamageInfo info)
    {
        try
        {
            EDamageType type = info.DamageType;
            if (damage <= 0f || type.IsSelfInflicted() || type == EDamageType.Undefined || type == EDamageType.Medicine)
                return;
            if (damage <= PainkillerIgnore && _bound != null && _bound.FindActiveEffect<IPainKiller>() != null)
                return;
            float now = Time.time, s = Mathf.Clamp(damage / HitFull, 0.5f, 1f);
            _hitStrength = now < _hitUntil ? Mathf.Max(_hitStrength, s) : s;
            _hitUntil = Mathf.Max(_hitUntil, now + 0.08f + 0.12f * s);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[表情] 受伤事件处理异常: {e}");
        }
    }

    /// 伤势 0~1：总血量、头、胸口三者里掉得最多的比例（头 / 胸口归零就死，所以单独算）。
    private static float Severity(IHealthController health)
    {
        float s = Lost(health, EBodyPart.Common);
        s = Mathf.Max(s, Lost(health, EBodyPart.Head));
        return Mathf.Max(s, Lost(health, EBodyPart.Chest));
    }

    private static float Lost(IHealthController health, EBodyPart part)
    {
        ValueStruct v = health.GetBodyPartHealth(part);
        return v.Maximum > 0f ? Mathf.Clamp01(1f - v.Current / v.Maximum) : 0f;
    }

    /// 进下一级要到 Up[i]；已经在这一级，要掉回 Up[i] - Margin 以下才退回去（不来回闪）。
    private static int Level(float severity, int current)
    {
        int level = 0;
        for (int i = 0; i < Up.Length; i++)
            if (severity >= Up[i] - (current > i ? Margin : 0f))
                level = i + 1;
        return level;
    }

    /// 0~1。到点开始一次（死了不开新的）；濒死眨得慢而沉、间隔更长（越专注眨得越少）。
    private float Blink(float now, bool dead)
    {
        if (!dead && now >= _blinkAt)
        {
            _blinkStart = now;
            _blinkSlow = _level == 3;
            _blinkAt = now + (_blinkSlow ? UnityEngine.Random.Range(4f, 8f) : UnityEngine.Random.Range(2.5f, 6f));
        }
        return BlinkCurve(now - _blinkStart, _blinkSlow);
    }

    /// 0~1：闭眼 u² 加速合上 → 停一下 → 睁眼 (1-v)² 减速睁开。濒死眨得更慢更重。
    private static float BlinkCurve(float u, bool slow)
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
