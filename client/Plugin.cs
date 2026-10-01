using System;
using System.Collections.Generic;
using System.IO;
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
[BepInPlugin("com.tricoloursky.astraldivide", "TricolourSky-AstralDivide", "0.1.0")]
public class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;
    internal static string PluginDir;
    internal static readonly Dictionary<string, PhysicsConfig> Configs = new Dictionary<string, PhysicsConfig>(StringComparer.OrdinalIgnoreCase);

    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<float> CullRange;
    internal static ConfigEntry<int> StepHz;
    internal static ConfigEntry<float> MenuFollow;

    private void Awake()
    {
        Log = Logger;
        Enabled = Config.Bind("布料物理", "启用", true,
            "按 PMX 原始数据复现 MMD 物理（每根物理骨一个真刚体 + 一条真关节）。关掉只嫁接骨、不跑物理。改完要重进战局或重新换装才生效");
        CullRange = Config.Bind("布料物理", "计算距离(米)", 30f, new ConfigDescription(
            "超出这个距离的角色暂停布料计算，靠近了自动恢复", new AcceptableValueRange<float>(5f, 100f)));
        // 步频只能全局：所有角色共用一个 PhysicsScene、一次 Simulate()。MMD 原生 60，裙板薄、120 明显减少穿透。
        StepHz = Config.Bind("布料性能", "物理步频(Hz)", 120, new ConfigDescription(
            "MMD 原生 60；裙板很薄，60 下角色跑动会被穿透，120 明显改善。越高越费 CPU",
            new AcceptableValueList<int>(60, 120, 240)));
        MenuFollow = Config.Bind("渲染", "主菜单跟随场景光", 0.65f, new ConfigDescription(
            "只影响主菜单，战局不受影响。0=纯自发光(最亮最平) 1=完全跟随环境(最暗)。在主菜单里拖着看，实时生效、不用重打包",
            new AcceptableValueRange<float>(0f, 1f)));

        PluginDir = Path.GetDirectoryName(Info.Location);
        string physDir = Path.Combine(PluginDir, "physics");
        LoadConfigs(physDir);
        MmdRaw.LoadAll(physDir);
        TuneUI.Bind(Config, physDir, MmdRaw.ModelNames);
        new Harmony("com.tricoloursky.astraldivide").PatchAll();
        Log.LogInfo($"插件初始化完成: MMD 物理 {MmdRaw.ModelCount} 个模型 / 热成像兼容 / 服装缩略图");
    }

    // 主菜单单独用一个「跟随场景光」强度：战局里跟随环境是对的，主菜单需要更适合展示的固定值。
    // ⚠️ 别直接归零 —— 那是 100% 自发光，画面会平、而且比原来亮一截（08-22 实测）。
    private static readonly int MenuLightId = Shader.PropertyToID("_GF2MenuLight");
    private static readonly int MenuFollowId = Shader.PropertyToID("_GF2MenuFollow");

    private void Update()
    {
        bool inRaid = Singleton<GameWorld>.Instantiated;         // 没有 GameWorld = 在主菜单
        Shader.SetGlobalFloat(MenuLightId, inRaid ? 0f : 1f);
        Shader.SetGlobalFloat(MenuFollowId, MenuFollow.Value);
    }

    /// ⚠️ 必须是 LateUpdate：动画在 Update 之后才把骨摆到本帧的姿势，
    /// 在 Update 里推物理读到的是**上一帧**的骨（老代码就是这么写的）。
    private void LateUpdate()
    {
        if (Enabled.Value)
            PhysWorld.Tick(Time.deltaTime);
        TuneUI.Tick();                                           // 调参台改完 1.5 秒自动写盘
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
