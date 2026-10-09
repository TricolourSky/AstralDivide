using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.UI;
using EFT.UI.Screens;
using EFT.Visual;
using UnityEngine;

namespace AstralDivide.Client;

/// <summary>
/// 主菜单挂机计时（DEV_NOTES 三十六）。「有操作」的判法照抄游戏自己的 AFKMonitor：按下任意键 / 鼠标键，或者鼠标动了。
/// 不在主菜单（别的界面、战局里）一律当作有操作，所以只算「待在主菜单一动不动」的时间。
/// </summary>
internal static class MenuIdle
{
    private static float _since;

    /// 每有一次操作就换一个号：同一段挂机只播一次表情，动一下才能再播。
    internal static int Epoch { get; private set; }

    internal static float Seconds => Time.time - _since;

    internal static void Tick()
    {
        if (OnMainMenu() && !Input.anyKeyDown && Input.GetAxis("Mouse X") == 0f && Input.GetAxis("Mouse Y") == 0f)
            return;
        _since = Time.time;
        Epoch++;
    }

    /// ⚠️ 读 `_instance`，别用 `EftScreenManager.Instance` —— 那个属性没有就 new 一个，开局太早调用会抢先造出界面管理器。
    private static bool OnMainMenu() =>
        !Singleton<GameWorld>.Instantiated &&
        EftScreenManager._instance?.CurrentScreenController?.ScreenType == EEftScreenType.MainMenu;
}

/// <summary>
/// OTs-14 主菜单挂机表情（试验，Tech Leader 定的戏）：
/// ① 疑惑「怎么没动静了？」→ ② 扬眉、眼睛睁大一点「原来在挂机」（不眨眼）→ ③ 眯眼微笑表示安心 → 恢复；头跟着歪 / 低头 / 抬下巴。
/// 动作用**二阶弹簧**驱动（Tech Leader：「动作过快了」「真正的动画师会把这几个帧的过程细化」）：
/// 每个通道一个弹簧，事件表只说「几秒往哪个目标走、走多快」，加速 / 减速 / 轻微冲过再回稳都由弹簧算出来；
/// 脸先动、头后跟，脖子慢头快，停住时还有极轻的漂移。和 docs\表情图鉴\ots14_挂机动作对比.mp4 右边（v2）是同一套算法和参数。
/// 播到一半有人动了鼠标 / 按了键：所有目标归零、弹簧加快一倍收回（约 0.5 秒）。播放期间 MmdBlink 的随机眨眼暂停。
/// 形态键在 Update 里写；颈 / 头的角度由 Plugin.LateUpdate 在动画之后、头发物理之前叠到骨头上（<see cref="LateTickAll"/>）。
/// </summary>
internal sealed class MenuIdleFace
{
    internal const string Model = "ots14";          // 试验：先只给 OTs-14
    private const float End = 9.8f;                  // 最后一个事件在 7.1 秒，之后留给弹簧回稳
    private const float SubStep = 1f / 240f;         // 弹簧按小步积分：帧率高低都一样顺
    private const float CancelSpeed = 2f;            // 被打断：弹簧加快一倍收回
    private const float NeckShare = 0.35f, HeadShare = 0.65f;

    // 通道：0~4 形态键（权重 0~100）；5 颈歪、6 头歪、7 低头（度。歪头正 = 往她的右边，低头正 = 下巴往下）
    private static readonly string[] Shapes = { "じと目", "びっくり", "上", "笑い", "口角上げ" };
    private const int JiTo = 0, Bikkuri = 1, Brow = 2, Smile = 3, Mouth = 4, Neck = 5, Head = 6, Nod = 7, Tilt = -1;
    private static readonly float[] Freq = { 0.9f, 1.6f, 1.6f, 0.8f, 0.7f, 0.45f, 0.6f, 0.6f };   // Hz：越低越慢
    private static readonly float[] Damp = { 1f, 0.9f, 0.9f, 1f, 1f, 0.75f, 0.65f, 0.7f };        // <1：到位时轻微冲过再回稳

    private readonly struct Ev
    {
        internal readonly float T, Value, Speed;
        internal readonly int Ch;                    // Tilt = 颈 + 头一起
        internal Ev(float t, int ch, float value, float speed = 1f) { T = t; Ch = ch; Value = value; Speed = speed; }
    }

    private static readonly Ev[] Events =
    {
        new Ev(0.00f, JiTo, 18),                                                  // ① 眼神先动：微微眯起，打量
        new Ev(0.20f, Tilt, 10), new Ev(0.20f, Nod, 3),                           //    头跟上：歪 + 一点低头（探过来看）
        new Ev(0.45f, JiTo, 30),
        new Ev(1.60f, JiTo, 34, 0.35f),                                           //    停住时眼睛再慢慢多眯一点
        new Ev(2.60f, JiTo, 0), new Ev(2.60f, Brow, 100), new Ev(2.60f, Bikkuri, 30),   // ② 意识到：脸先反应
        new Ev(2.70f, Tilt, 0, 1.35f), new Ev(2.70f, Nod, -3, 1.3f),              //    头摆正稍快 + 下巴轻轻一抬
        new Ev(3.30f, Nod, 0),
        new Ev(3.70f, Brow, 0), new Ev(3.70f, Bikkuri, 0),
        new Ev(3.75f, Mouth, 100),                                                // ③ 笑从嘴角开始
        new Ev(3.90f, Smile, 100),                                                //    眼睛跟上
        new Ev(4.10f, Tilt, -5), new Ev(4.10f, Nod, 2),
        new Ev(5.30f, Tilt, -6, 0.4f),                                            //    停住时头再慢慢多歪一点
        new Ev(6.90f, Smile, 0),                                                  // 恢复：眼先睁开
        new Ev(7.05f, Mouth, 0),                                                  //    嘴角再放松
        new Ev(7.10f, Tilt, 0), new Ev(7.10f, Nod, 0),                            //    头最后回正
    };

    // 自然眨眼（Tech Leader：「加吧」）：疑惑停住中间一次、恢复时眼睛睁开后一次（微笑时眼睛本来就闭着，眨了看不见）。
    // 曲线：闭眼 u² 加速合上、停一下、睁眼 (1-v)² 减速睁开 —— 比匀速开合自然。意识到那一下不眨（Tech Leader 定的）。
    private static readonly float[] Blinks = { 1.95f, 8.15f };
    private const float BlinkClose = 0.08f, BlinkHold = 0.04f, BlinkOpen = 0.16f;

    /// 正在播（要动头）的：Plugin.LateUpdate 每帧过一遍。
    private static readonly HashSet<MenuIdleFace> Playing = new HashSet<MenuIdleFace>();
    private static readonly List<MenuIdleFace> Finished = new List<MenuIdleFace>();

    private readonly List<KeyValuePair<SkinnedMeshRenderer, int>>[] _targets;
    private readonly FaceSpring[] _springs = new FaceSpring[Freq.Length];   // 弹簧在 FaceKit.cs，和战局表情共用
    private readonly float[] _applied = new float[Shapes.Length];
    private float _t = -1f;          // 播到第几秒；-1 = 没在播
    private int _next;               // 下一个要触发的事件
    private bool _cancel;            // 被打断，正在收回
    private float _cancelT;          // 被打断那一刻（之后的眨眼不再开始，已经在眨的眨完）
    private float _blink;            // 当前眨眼 0~1
    private int _played = -1;        // 上次播是在哪一段挂机（MenuIdle.Epoch）
    private bool _menuBody;
    private HeadTilt _tilt;

    internal static MenuIdleFace Create(LoddedSkin lodded, string model) =>
        model == Model ? new MenuIdleFace(lodded) : null;

    private MenuIdleFace(LoddedSkin lodded)
    {
        for (int c = 0; c < _springs.Length; c++)
            _springs[c] = new FaceSpring(Freq[c], Damp[c]);
        _targets = new List<KeyValuePair<SkinnedMeshRenderer, int>>[Shapes.Length];
        for (int c = 0; c < Shapes.Length; c++)
            _targets[c] = new List<KeyValuePair<SkinnedMeshRenderer, int>>();
        foreach (SkinnedMeshRenderer smr in lodded.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            Mesh mesh = smr.sharedMesh;
            for (int c = 0; mesh != null && c < Shapes.Length; c++)
            {
                int index = mesh.GetBlendShapeIndex(Shapes[c]);
                if (index >= 0)
                    _targets[c].Add(new KeyValuePair<SkinnedMeshRenderer, int>(smr, index));
            }
        }
        for (int c = 0; c < Shapes.Length; c++)
            if (_targets[c].Count == 0)
                Plugin.Log.LogWarning($"[表情] {Model} 头部找不到「{Shapes[c]}」，挂机表情少这一路");
    }

    /// 每帧由 MmdBlink 调。在播返回「まばたき」该是多少（0~100，只有安排好的那两次眨眼不是 0；随机眨眼暂停）；没在播返回 -1，随机眨眼照常。
    internal float Tick(Component owner)
    {
        if (_t < 0f && !Begin(owner))
            return -1f;
        if (!_cancel && MenuIdle.Epoch != _played)
            Cancel();                                       // 播到一半有人动了：目标全部归零、加快收回
        Advance(Time.deltaTime);
        _blink = BlinkAt(_t, _cancel ? _cancelT : float.MaxValue);
        Apply();
        if (Done())
            Stop();
        return _blink * 100f;
    }

    /// 0~1。只算开始时间早于 notAfter 的眨眼（被打断后不再开新的）。
    private static float BlinkAt(float t, float notAfter)
    {
        float b = 0f;
        foreach (float s in Blinks)
        {
            float u = t - s;
            if (s > notAfter || u < 0f)
                continue;
            if (u < BlinkClose)
                b = Mathf.Max(b, (u / BlinkClose) * (u / BlinkClose));
            else if (u < BlinkClose + BlinkHold)
                b = 1f;
            else if (u < BlinkClose + BlinkHold + BlinkOpen)
                b = Mathf.Max(b, Mathf.Pow(1f - (u - BlinkClose - BlinkHold) / BlinkOpen, 2f));
        }
        return b;
    }

    private bool Begin(Component owner)
    {
        if (!ShouldStart(owner))
            return false;
        _t = 0f;
        _next = 0;
        _cancel = false;
        _played = MenuIdle.Epoch;
        foreach (FaceSpring s in _springs)
            s.Reset();
        _tilt = _tilt ?? HeadTilt.Find(owner);
        if (_tilt != null)
            Playing.Add(this);
        return true;
    }

    /// 开播条件：主菜单里挂满设定秒数、这一段挂机还没播过、而且是主菜单那个人物（挂在 PlayerModelView 下）。
    /// 主菜单的人物是 MenuOverhaul 复制背包页的 PlayerModelView 做的；头在 SetSkin 之后才挂过去，所以开播时再查。
    private bool ShouldStart(Component owner)
    {
        if (MenuIdle.Seconds < Plugin.IdleWait || MenuIdle.Epoch == _played)
            return false;
        _menuBody = _menuBody || owner.GetComponentInParent<PlayerModelView>() != null;
        if (!_menuBody)
            _played = MenuIdle.Epoch;                       // 不是主菜单那个人物：这一段挂机不再查
        return _menuBody;
    }

    /// 按 SubStep 小步往前推：先触发到点的事件，再让每个弹簧朝「目标 + 微动」走一步。
    private void Advance(float dt)
    {
        for (float left = dt; left > 1e-6f; left -= SubStep)
        {
            float h = Mathf.Min(SubStep, left);
            while (!_cancel && _next < Events.Length && Events[_next].T <= _t)
                Fire(Events[_next++]);
            Wobble(_cancel ? -1f : _t, out float roll, out float nod);
            for (int c = 0; c < _springs.Length; c++)
                _springs[c].Step(h, c == Neck || c == Head ? roll : c == Nod ? nod : 0f);
            _t += h;
        }
    }

    private void Fire(Ev e)
    {
        if (e.Ch != Tilt)
        {
            _springs[e.Ch].Aim(e.Value, e.Speed);
            return;
        }
        _springs[Neck].Aim(e.Value, e.Speed);
        _springs[Head].Aim(e.Value, e.Speed);
    }

    /// 停住时的极轻漂移（两个不成倍数的慢正弦：歪头约 ±0.8°、低头约 ±0.35°），开头 0.8 秒淡入、7.2~9 秒淡出；t &lt; 0 = 不要。
    private static void Wobble(float t, out float roll, out float nod)
    {
        float env = t < 0f ? 0f : Smooth(0f, 0.8f, t) * (1f - Smooth(7.2f, 9f, t));
        roll = env * (0.5f * Mathf.Sin(2f * Mathf.PI * 0.23f * t + 1.3f) + 0.3f * Mathf.Sin(2f * Mathf.PI * 0.41f * t + 0.2f));
        nod = env * 0.35f * Mathf.Sin(2f * Mathf.PI * 0.17f * t + 2.1f);
    }

    private static float Smooth(float a, float b, float t)
    {
        float u = Mathf.Clamp01((t - a) / (b - a));
        return u * u * (3f - 2f * u);
    }

    private void Cancel()
    {
        _cancel = true;
        _cancelT = _t;
        foreach (FaceSpring s in _springs)
            s.Aim(0f, CancelSpeed);
    }

    /// 播完 = 过了 End（或被打断）且所有通道都回到 0、几乎不动了；万一回不稳，多等 3 秒也收。
    private bool Done()
    {
        if (!_cancel && _t < End)
            return false;
        if (_t > End + 3f)
            return true;
        return Array.TrueForAll(_springs, s => s.Resting);
    }

    private void Stop()
    {
        foreach (FaceSpring s in _springs)
            s.Reset();
        _blink = 0f;
        Apply();                                            // 形态键归 0
        _t = -1f;
    }

    /// 形态键：数值变了才写（颈 / 头 / 低头不在这，归 LateTick）。
    /// 眨眼时把眯眼（じと目）和笑眼（笑い）按比例让出来 —— 和「まばたき」叠满会眼皮穿插、闭过头。
    private void Apply()
    {
        for (int c = 0; c < Shapes.Length; c++)
        {
            float w = Mathf.Clamp(_springs[c].Y, 0f, 100f);
            if (c == JiTo || c == Smile)
                w *= 1f - _blink;
            if (Mathf.Abs(w - _applied[c]) < 0.01f)
                continue;
            _applied[c] = w;
            foreach (KeyValuePair<SkinnedMeshRenderer, int> t in _targets[c])
                if (t.Key != null)
                    t.Key.SetBlendShapeWeight(t.Value, w);
        }
    }

    /// Plugin.LateUpdate 调，**必须在 PhysWorld.Tick 之前**：动画已经把骨头摆好了，叠上歪头再让头发物理去追，头发不会慢一帧发抖。
    internal static void LateTickAll()
    {
        if (Playing.Count == 0)
            return;
        foreach (MenuIdleFace f in Playing)
            if (!f.LateTick())
                Finished.Add(f);
        foreach (MenuIdleFace f in Finished)
            Playing.Remove(f);
        Finished.Clear();
    }

    /// 返回 false = 不用再动头了（播完 / 收回完 / 人物被销毁），骨头已还原。
    private bool LateTick()
    {
        if (_t < 0f || !_tilt.Alive)
        {
            _tilt.Release();
            return false;
        }
        _tilt.Apply(_springs[Neck].Y * NeckShare, _springs[Head].Y * HeadShare, _springs[Nod].Y);
        return true;
    }
}

/// <summary>
/// 动头：给脖子 / 头叠歪头（绕人物正面方向）和低头（绕人物右边方向）。
/// 转轴用人物根节点的方向（世界坐标），不依赖 EFT 骨头本地轴；支点是骨头自己的根部。
/// 动画每帧会重新摆骨头；万一这帧没摆（待机动画关了），先还原成上一帧叠之前的样子再叠，不会越歪越多。
/// </summary>
internal sealed class HeadTilt
{
    private readonly Transform _root;
    private readonly Transform _neck;                // 可能为 null（父骨头名字对不上时整份给头）
    private readonly Transform _head;
    private readonly Transform[] _bones;
    private readonly Quaternion[] _base;
    private readonly Quaternion[] _set;
    private bool _applied;

    private HeadTilt(Transform root, Transform neck, Transform head)
    {
        _root = root;
        _neck = neck;
        _head = head;
        _bones = neck != null ? new[] { neck, head } : new[] { head };
        _base = new Quaternion[_bones.Length];
        _set = new Quaternion[_bones.Length];
    }

    internal bool Alive => _root != null && Array.TrueForAll(_bones, b => b != null);

    /// 头骨用 EFT 自己记的 PlayerBones.Head；脖子 = 它的父骨头（名字对得上才用，不然整份给头）。
    internal static HeadTilt Find(Component owner)
    {
        PlayerBody body = owner.GetComponentInParent<PlayerBody>();
        Transform head = body != null && body.PlayerBones != null && body.PlayerBones.Head != null ? body.PlayerBones.Head.Original : null;
        if (head == null)
        {
            Plugin.Log.LogWarning("[表情] 找不到头骨（PlayerBones.Head），挂机表情不动头");
            return null;
        }
        Transform neck = head.parent;
        if (neck != null && neck.name == "Base HumanNeck")
            return new HeadTilt(body.transform, neck, head);
        Plugin.Log.LogWarning($"[表情] 头骨的父骨头是「{neck?.name}」不是 Base HumanNeck，只转头");
        return new HeadTilt(body.transform, null, head);
    }

    /// neckRoll / headRoll：歪头（度，正 = 往她的右边）；pitch：低头（度，正 = 下巴往下）。
    /// Unity 里绕「正面方向」转正角度是往她的左边，所以歪头取负；绕「她的右边」转正角度 = 低头。
    internal void Apply(float neckRoll, float headRoll, float pitch)
    {
        for (int i = 0; i < _bones.Length; i++)
        {
            if (_applied && _bones[i].localRotation == _set[i])
                _bones[i].localRotation = _base[i];            // 这帧动画没重摆它：先还原，免得越叠越歪
            _base[i] = _bones[i].localRotation;
        }
        Vector3 forward = _root.forward, right = _root.right;
        if (_neck != null)
            _neck.rotation = Quaternion.AngleAxis(-neckRoll, forward) * _neck.rotation;
        else
            headRoll += neckRoll;
        _head.rotation = Quaternion.AngleAxis(pitch, right) * Quaternion.AngleAxis(-headRoll, forward) * _head.rotation;
        for (int i = 0; i < _bones.Length; i++)
            _set[i] = _bones[i].localRotation;
        _applied = true;
    }

    /// 播完：骨头若还停在我们叠过的样子（动画没重摆），还原回去。
    internal void Release()
    {
        for (int i = 0; _applied && i < _bones.Length; i++)
            if (_bones[i] != null && _bones[i].localRotation == _set[i])
                _bones[i].localRotation = _base[i];
        _applied = false;
    }
}
