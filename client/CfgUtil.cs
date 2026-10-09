using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace AstralDivide.Client;

/// F12 窗口（SPT 带的 ConfigurationManager 18.4）认的附加属性：它按类型名找、按字段名抄，不用引用它的 DLL；没设的字段（null）它不管
internal sealed class ConfigurationManagerAttributes
{
    public int? Order;                                  // 同一组里数字大的排前面
    public string DispName;                             // 显示的名字（cfg 里的键不变）
    public bool? IsAdvanced;                            // true = 勾了窗口上方的「Advanced settings」才显示（调试组）
#pragma warning disable CS0649     // 下面几项只有开发版的菜单会设
    public bool? Browsable;                             // false = 不显示（本套微调：你身上没穿的那套藏起来）
    public bool? HideDefaultButton;                     // true = 不显示「Reset」（按钮、说明行）
    public Action<ConfigEntryBase> CustomDrawer;        // 自己画这一项（按钮、说明行）
#pragma warning restore CS0649
}

/// <summary>
/// F12 的分组和小工具（DEV_NOTES 37.29）。SPT 带的 F12 窗口**不按组名排序，按每组第一次绑定的先后排**
///（37.29 实机：绑定顺序 1 → 5 → 2 → 4 → 6 → 3 就显示成这样），所以 Plugin.Awake 按下面的顺序绑。
/// 发布包只有「布料物理」；其余几组只在开发版（编译开关 SORA_DEV，Tech Leader 本机 dotnet build 部署的那份）里有
/// </summary>
internal static class CfgUtil
{
    internal const string SecPhysics = "布料物理";      // 和 0.1.0 同名：玩家原来的设置直接接上
    internal const string SecTune = "布料调参台";
    internal const string SecOutfit = "本套微调";
    internal const string SecLight = "光照换算";
    internal const string SecMenu = "主菜单展示";
    internal const string SecDebug = "调试";

    /// 37.29 第一版的组名（带序号），旧值从这里搬
    internal static readonly string[] Numbered = { "1. 布料物理", "2. 布料调参台", "3. 本套微调", "4. 塔科夫光照换算", "5. 主菜单展示", "6. 调试" };

    private static readonly PropertyInfo OrphansProp = AccessTools.Property(typeof(ConfigFile), "OrphanedEntries");

    /// 说明 + 在组里的位置（数字大的排前面）+ 取值范围 + 显示名（不给 = 用键名）+ 是不是高级项
    internal static ConfigDescription Desc(string text, int order, AcceptableValueBase range = null, string name = null, bool advanced = false) =>
        new ConfigDescription(text, range, new ConfigurationManagerAttributes { Order = order, DispName = name, IsAdvanced = advanced ? true : (bool?)null });

    /// 键名里的半角括号换成全角（显示用；键名本身不改，免得 cfg 里的值对不上）
    internal static string Pretty(string key) => key.Replace("(", "（").Replace(")", "）");

    /// 换了组 / 名字的项：cfg 里旧位置还留着值（BepInEx 叫孤儿项）就搬过来，旧的删掉
    internal static void Move(ConfigFile cfg, string oldSection, string oldKey, ConfigEntryBase entry)
    {
        Dictionary<ConfigDefinition, string> orphans = Orphans(cfg);
        var old = new ConfigDefinition(oldSection, oldKey);
        if (orphans == null || !orphans.TryGetValue(old, out string value))
            return;
        orphans.Remove(old);
        entry.SetSerializedValue(value);
        Plugin.Log.LogInfo($"[配置] 「{oldSection} / {oldKey}」搬到「{entry.Definition.Section} / {entry.Definition.Key}」= {value}");
    }

    /// 删掉 cfg 里不再用的旧项（孤儿项）；key 为 null 时整组删
    internal static void Drop(ConfigFile cfg, string section, string key = null)
    {
        Dictionary<ConfigDefinition, string> orphans = Orphans(cfg);
        if (orphans == null)
            return;
        foreach (ConfigDefinition d in new List<ConfigDefinition>(orphans.Keys))
            if (d.Section == section && (key == null || d.Key == key))
                orphans.Remove(d);
    }

    private static Dictionary<ConfigDefinition, string> Orphans(ConfigFile cfg) =>
        OrphansProp?.GetValue(cfg, null) as Dictionary<ConfigDefinition, string>;

#if SORA_DEV
    private static object _cm;
    private static PropertyInfo _cmWindow;

    /// F12 窗口开着没有（反射读 ConfigurationManager 的 DisplayingWindow，不加引用；没装 = 永远 false）。
    /// 调参台、本套微调只认窗口开着时的变更：Fika / MenuOverhaul 会调 ConfigFile.Reload()，把 cfg 里的旧值回灌一遍（2026-08-27 踩过）
    internal static bool F12Open
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
                return _cmWindow != null && _cmWindow.GetValue(_cm, null) is bool b && b;
            }
            catch
            {
                return false;
            }
        }
    }
#endif
}
