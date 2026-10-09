using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace AstralDivide.Client;

// ⚠️ GUID 同时是 F12 配置文件名（BepInEx\config\<GUID>.cfg）：一改，原来的设置就留在旧文件里读不到了。
//    Forge 的规定（2026-10-01 照它改）：GUID 必须和服务端 ModGuid、上传时填的 GUID 三者相同；
//    插件名必须是「用户名-模组名」，只许字母、数字和一个横杠。下面 Harmony 的 id 跟 GUID 保持一致。
//    用过的旧值：com.binarydimension.store.client → com.tricoloursky.astraldivide.client（都不合规）。
[BepInPlugin("com.tricoloursky.astraldivide", "TricolourSky-AstralDivide", "0.2.0")]
public class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;
    internal static string PluginDir;
    internal static readonly Dictionary<string, PhysicsConfig> Configs = new Dictionary<string, PhysicsConfig>(StringComparer.OrdinalIgnoreCase);

    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> OnlySelf;
    internal static ConfigEntry<float> CullRange;
    internal static ConfigEntry<int> StepHz;
#pragma warning disable CS0649     // 开发版才有（F12「5. 主菜单展示」）；发布版一直是 null，用下面的默认值
    private static ConfigEntry<float> _menuFollow;
    private static ConfigEntry<float> _idleWait;
#pragma warning restore CS0649

    // 发布版没有「5. 主菜单展示」，用这两个值（0.2.0 = Tech Leader 2026-10-10 F12 里调好的：跟随场景光 1、挂机 15 秒）
    private const float MenuFollowDefault = 1f, IdleWaitDefault = 15f;

    /// 主菜单跟随场景光（GF2Render ⑥：主菜单里「材质本色 / 少前 2 光照」怎么混）
    internal static float MenuFollow => _menuFollow != null ? _menuFollow.Value : MenuFollowDefault;

    /// 主菜单挂机多少秒播 OTs-14 的挂机表情
    internal static float IdleWait => _idleWait != null ? _idleWait.Value : IdleWaitDefault;

    private void Awake()
    {
        Log = Logger;
        BindPhysics();
        PluginDir = Path.GetDirectoryName(Info.Location);
        string physDir = Path.Combine(PluginDir, "physics");
        LoadConfigs(physDir);
        MmdRaw.LoadAll(physDir);
        // ⚠️ F12 窗口按每组第一次绑定的先后排组（不按组名），所以按菜单顺序绑（DEV_NOTES 37.29）
#if SORA_DEV
        TuneUI.Bind(Config, physDir, MmdRaw.ModelNames);       // 布料调参台
#endif
        GF2Tune.Init(Config, PluginDir);                        // 本套微调（开发版）
        GF2Render.BindConfig(Config);                           // 光照换算 / 主菜单展示 / 调试（开发版）
#if SORA_DEV
        BindMenuExtras();                                       // 主菜单展示里插件这边的两项
#endif
        DropOldEntries();
        new Harmony("com.tricoloursky.astraldivide").PatchAll();
        Log.LogInfo($"插件初始化完成: MMD 物理 {MmdRaw.ModelCount} 个模型 / 热成像兼容 / 服装缩略图");
    }

    /// F12「布料物理」：发布包里玩家只看得到这一组（DEV_NOTES 37.29）。组名和 0.1.0 一样（玩家原来的设置直接接上）；
    /// 物理步频以前在「布料性能」，37.29 第一版在「1. 布料物理」，值搬过来
    private void BindPhysics()
    {
        const string sec = CfgUtil.SecPhysics;
        Enabled = Config.Bind(sec, "启用", true, CfgUtil.Desc(
            "头发、裙子、外套按 MMD 原模型的物理晃动。关 = 不晃。改完要重进战局或重新换装才生效", 4));
        OnlySelf = Config.Bind(sec, "联机时只算自己", false, CfgUtil.Desc(
            "联机时只给你自己身上的衣服算物理，队友身上的不晃，省 CPU。改完要重进战局或重新换装才生效", 3));
        CullRange = Config.Bind(sec, "计算距离(米)", 30f, CfgUtil.Desc(
            "离你多远以内的角色才算布料物理，远了自动暂停、走近恢复", 2, new AcceptableValueRange<float>(5f, 100f), "计算距离（米）"));
        // 步频只能全局：所有角色共用一个 PhysicsScene、一次 Simulate()。MMD 原生 60。
        // 37.31 第二步衣服改走新做法后默认一度改成 60；但各套头发的数值都是在 120 下调的，60 时贝丝蒂 / 索普的头发乱飞（Tech Leader 实测）→ 默认回 120
        StepHz = Config.Bind(sec, "物理步频(Hz)", 120, CfgUtil.Desc(
            "每秒算几次头发的物理（还有没交给新做法的绳子之类）。120（默认）= 头发数值调的时候用的；60 更省 CPU 但头发会变飘；240 更细（衣服、胸部不受这项影响）", 1,
            new AcceptableValueList<int>(60, 120, 240), "物理步频（Hz）"));
        foreach (ConfigEntryBase e in new ConfigEntryBase[] { Enabled, OnlySelf, CullRange, StepHz })
            CfgUtil.Move(Config, CfgUtil.Numbered[0], e.Definition.Key, e);
        CfgUtil.Move(Config, "布料性能", "物理步频(Hz)", StepHz);
    }

#if SORA_DEV
    /// 开发版 F12「主菜单展示」里插件这边的两项（另外三项在 GF2Render.BindMenu）；37.29 第一版在「5. 主菜单展示」，值搬过来
    private void BindMenuExtras()
    {
        _menuFollow = Config.Bind(CfgUtil.SecMenu, "主菜单跟随场景光", MenuFollowDefault, CfgUtil.Desc(
            "主菜单里材质本色和灯光怎么混：0 = 纯本色（最亮最平），1 = 完全跟灯光（最暗）。只影响主菜单", 2,
            new AcceptableValueRange<float>(0f, 1f), "跟随场景光"));
        _idleWait = Config.Bind(CfgUtil.SecMenu, "主菜单挂机多少秒触发", IdleWaitDefault, CfgUtil.Desc(
            "主菜单里不动鼠标、不按键这么久，OTs-14 播一遍挂机表情（试验，只有 OTs-14；动一下才会再播）", 1,
            new AcceptableValueRange<float>(5f, 600f), "挂机表情等待（秒）"));
        CfgUtil.Move(Config, CfgUtil.Numbered[4], "主菜单跟随场景光", _menuFollow);
        CfgUtil.Move(Config, CfgUtil.Numbered[4], "主菜单挂机多少秒触发", _idleWait);
    }
#endif

    /// 改过名的旧组：要留的值上面已经搬走，剩下的（不再用的旧项、整组搬走的组）从 cfg 里删掉，再存一次
    private void DropOldEntries()
    {
        foreach (string old in CfgUtil.Numbered.Concat(new[] { "布料性能", "渲染", "表情", "GF2 渲染", "布料手感(调参台)" }))
            CfgUtil.Drop(Config, old);
        Config.Save();
    }

    // 主菜单单独用一个「跟随场景光」强度：战局里跟随环境是对的，主菜单需要更适合展示的固定值。
    // ⚠️ 别直接归零 —— 那是 100% 自发光，画面会平、而且比原来亮一截（08-22 实测）。
    private static readonly int MenuLightId = Shader.PropertyToID("_GF2MenuLight");
    private static readonly int MenuFollowId = Shader.PropertyToID("_GF2MenuFollow");

    private void Update()
    {
        bool inRaid = Singleton<GameWorld>.Instantiated;         // 没有 GameWorld = 在主菜单
        Shader.SetGlobalFloat(MenuLightId, inRaid ? 0f : 1f);
        Shader.SetGlobalFloat(MenuFollowId, MenuFollow);
        MenuIdle.Tick();                                         // 主菜单挂机计时（OTs-14 挂机表情用）
    }

    /// ⚠️ 必须是 LateUpdate：动画在 Update 之后才把骨摆到本帧的姿势，
    /// 在 Update 里推物理读到的是**上一帧**的骨（老代码就是这么写的）。
    private void LateUpdate()
    {
        MenuIdleFace.LateTickAll();                              // 挂机表情的歪头：必须在头发物理之前叠到骨头上
        if (Enabled.Value)
            PhysWorld.Tick(Time.deltaTime);
#if SORA_DEV
        TuneUI.Tick();                                           // 调参台改完 1.5 秒自动写盘
        GF2Tune.Tick();                                          // 本套微调同上
#endif
    }

    private static void LoadConfigs(string dir)
    {
        if (!Directory.Exists(dir))
        {
            Log.LogInfo($"physics 配置目录不存在, 布料物理停用: {dir}");
            return;
        }
        foreach (string file in Directory.GetFiles(dir, "*.json"))
        {
            if (file.EndsWith("_raw.json", StringComparison.OrdinalIgnoreCase))
                continue;                       // PMX 原样导出，由 MmdRaw 读，不是链配置
            if (file.EndsWith(".tuning.json", StringComparison.OrdinalIgnoreCase))
                continue;                       // 每套模型的手感参数，由 MmdTuning 读
            PhysicsConfig cfg = PhysicsConfig.Load(file);
            if (cfg == null)
                continue;
            string key = Path.GetFileNameWithoutExtension(file);
            if (key.EndsWith(".physics", StringComparison.OrdinalIgnoreCase))
                key = key.Substring(0, key.Length - ".physics".Length);
            Configs[key] = cfg;
        }
        Log.LogInfo($"已加载 {Configs.Count} 份链配置");
    }

    // ── FIKA 无头客户端探测 ────────────────────────────────────────────────
    //
    // 无头（专用服务端）客户端上没人看画面，跑布料纯属白烧 CPU。
    // **用反射探，不加引用** —— 没装 FIKA 的人一点影响都没有，FIKA 改版本也编译得过。
    // 懒查：插件的加载顺序可能在 FIKA 之前，Awake 那会儿它还没加载。
    private static PropertyInfo _headlessProp;
    private static bool _headlessProbed;

    internal static bool HeadlessDetected
    {
        get
        {
            try
            {
                if (!_headlessProbed)
                    Probe();
                return _headlessProp != null && _headlessProp.GetValue(null) is bool b && b;
            }
            catch
            {
                return false;
            }
        }
    }

    private static void Probe()
    {
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.GetName().Name != "Fika.Core")
                continue;
            _headlessProbed = true;
            Type t = asm.GetType("Fika.Core.Main.Utils.FikaBackendUtils") ??
                     asm.GetType("Fika.Core.Utils.FikaBackendUtils");
            _headlessProp = t?.GetProperty("IsHeadless", BindingFlags.Public | BindingFlags.Static);
            Log.LogInfo(_headlessProp != null
                ? "[mmd] 检测到 FIKA，已接上无头客户端判定"
                : "[mmd] 检测到 FIKA 但找不到 IsHeadless，按「有人在看」处理");
            return;
        }
    }
}
