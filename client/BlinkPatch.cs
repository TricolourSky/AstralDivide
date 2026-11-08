using System;
using System.Collections.Generic;
using EFT;
using EFT.HealthSystem;
using EFT.Visual;
using HarmonyLib;
using UnityEngine;

namespace AstralDivide.Client;

/// <summary>
/// 我们的头一穿上就挂眨眼（DEV_NOTES 三十六）。游戏本体只有灯塔商人会眨眼（LightKeeperEyesBlinking），
/// 玩家头部没有；我们的头部包里带着 MMD 表情，这里自己驱动「まばたき」（阿斯缇亚用「ウィンク」，见 MmdBlink.BlinkShape）。
/// 贝丝蒂 / 索普的头部包没有形态键，找不到就什么都不挂。图标身体不挂（拍到半闭眼的卡片）。
/// </summary>
[HarmonyPatch(typeof(PlayerBody), nameof(PlayerBody.SetSkin))]
internal static class BlinkPatch
{
    [HarmonyPostfix]
    private static void Postfix(PlayerBody __instance, KeyValuePair<EBodyModelPart, ResourceKey> part)
    {
        try
        {
            if (part.Key != EBodyModelPart.Head || Plugin.HeadlessDetected || PlayerGate.IconBodies.Contains(__instance))
                return;
            // 只给我们自己的头（有 <模型>_raw.json 的那几套）：原版的头每刷一个 bot 都会走到这里，不给它们挂组件
            if (!SkinPart.TryGet(__instance, part, out string key, out LoddedSkin lodded))
                return;
            MmdModel model = MmdRaw.For(key);
            if (model != null)
                MmdBlink.Attach(lodded, model.Name);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"BlinkPatch error: {e}");
        }
    }
}

/// <summary>
/// 眨眼 + 死后闭眼：随机隔 2.5~6 秒眨一次（闭 0.06 秒 → 停 0.04 秒 → 睁 0.1 秒）；
/// 人一死 0.3 秒内闭上、不再睁开。主菜单 / 藏身处的预览人物没有 Player，只眨眼。
/// OTs-14 另带主菜单挂机表情（<see cref="MenuIdleFace"/>）：播的时候随机眨眼停掉。
/// 战局表情（<see cref="RaidFace"/>，37.32 起有形态键的头都挂）：找到 Player 之后，伤情表情、眨眼、死后闭眼都归它（这里的随机眨眼 / 0.3 秒闭眼不再用）。
/// </summary>
internal sealed class MmdBlink : MonoBehaviour
{
    private const float Close = 0.06f, Hold = 0.04f, Open = 0.10f;
    private const float DeathClose = 0.3f;

    private FaceMorphs _lid;             // 眨眼那一个形态键
    private Player _player;
    private float _nextLook;
    private float _nextBlink;
    private float _phase = -1f;          // 这一次眨眼进行到第几秒；-1 = 没在眨
    private float _weight;
    private bool _dead;
    private MenuIdleFace _idle;
    private RaidFace _raid;

    /// 眨眼用哪个形态键：一般是「まばたき」。阿斯缇亚（W / B）的模型里两个名字对调了（DEV_NOTES 37.27）：
    /// 「まばたき」只动左眼（392 个顶点，其实是左眼的笑眼单眨）、「ウィンク」才是双眼眨眼（左右各 370 个）
    private static string BlinkShape(string model) => model != null && model.StartsWith("asteria", StringComparison.Ordinal) ? "ウィンク" : "まばたき";

    internal static void Attach(LoddedSkin lodded, string model)
    {
        MmdBlink blink = lodded.gameObject.GetComponent<MmdBlink>();
        if (blink == null)
            blink = lodded.gameObject.AddComponent<MmdBlink>();
        var lid = new FaceMorphs(lodded, new[] { BlinkShape(model) }, model, warnMissing: false);
        if (!lid.Has(0))
        {
            Destroy(blink);
            return;
        }
        blink._lid = lid;
        blink._nextBlink = Time.time + UnityEngine.Random.Range(FaceBlink.GapMin, FaceBlink.GapMax);
        blink._idle = MenuIdleFace.Create(lodded, model);
        blink._raid?.Unbind();                               // 同一个头再挂一次：旧的先退订受伤事件
        blink._raid = RaidFace.Create(lodded, model);
    }

    /// 头被销毁（死后清场、换头）：战局表情退订 Player 的受伤事件，别让游戏那边一直挂着我们。
    private void OnDestroy()
    {
        _raid?.Unbind();
    }

    private void Update()
    {
        if (_lid == null)
            return;                         // 刚加上就因为头上没有眨眼键被销毁的那一帧
        if (!_dead && IsDead())
            _dead = true;
        float scripted = -1f;
        IHealthController health = _player != null ? _player.HealthController : null;
        if (_raid != null && health != null)
            scripted = _raid.Tick(health, _dead);           // 战局：伤情表情 + 眨眼 + 死后闭眼都归它
        else if (_idle != null && !_dead)
            scripted = _idle.Tick(this);
        if (scripted >= 0f)
            _weight = Follow(scripted);
        else if (_dead)
            _weight = Mathf.MoveTowards(_weight, 100f, 100f / DeathClose * Time.deltaTime);
        else
            _weight = BlinkWeight(Time.deltaTime);
        _lid.Set(0, _weight);
    }

    /// 挂机表情在播：「まばたき」用它给的值（它自己安排的两次眨眼，其余时间 0），随机眨眼的计时往后推（播完不会马上补眨一下）。
    private float Follow(float weight)
    {
        _phase = -1f;
        _nextBlink = Time.time + UnityEngine.Random.Range(FaceBlink.GapMin, FaceBlink.GapMax);
        return weight;
    }

    private float BlinkWeight(float dt)
    {
        if (_phase < 0f)
        {
            if (Time.time < _nextBlink)
                return 0f;
            _phase = 0f;
        }
        _phase += dt;
        if (_phase < Close)
            return 100f * _phase / Close;
        if (_phase < Close + Hold)
            return 100f;
        if (_phase < Close + Hold + Open)
            return 100f * (1f - (_phase - Close - Hold) / Open);
        _phase = -1f;
        _nextBlink = Time.time + UnityEngine.Random.Range(FaceBlink.GapMin, FaceBlink.GapMax);
        return 0f;
    }

    /// 头部挂到玩家身上之前找不到 Player（SetSkin 那一刻还在服装 prefab 里），所以每秒找一次直到找到。
    private bool IsDead()
    {
        if (_player == null)
        {
            if (Time.time < _nextLook)
                return false;
            _nextLook = Time.time + 1f;
            _player = GetComponentInParent<Player>();
            if (_player == null)
                return false;
        }
        IHealthController health = _player.HealthController;
        return health != null && !health.IsAlive;
    }
}
