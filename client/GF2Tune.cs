using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using EFT;
using EFT.Visual;
using Newtonsoft.Json;
using UnityEngine;

namespace AstralDivide.Client;

/// <summary>
/// 每套服装自己的「塔科夫微调」（DEV_NOTES 37.29）：渲染器（GF2/* shader）所有套装共用、参数照少前 2 原版，
/// 塔科夫里个别地方要偏离原版的，只写进这一套自己的表，别的套装不受影响。
/// 表 = plugins\AstralDivide\gf2\&lt;模型&gt;.json：{ "材质名": { "参数名": 值 } }，只记和原版不一样的；
/// 换上部位时套到这个部位的材质上（材质是包里的，同一套衣服所有人共用）—— 发布版也这样。
/// 能调什么看材质自己（Knobs）：开了黑丝分支的 → 黑丝透肉 / 黑丝背光亮度；标了胸前塑料的 → 胸前塑料透明度。
/// 开发版（SORA_DEV）在 F12「3. 本套微调」列出你身上穿的那几套能调的项（没穿的藏起来），拖动实时生效，停 1.5 秒自动存回表。
/// 模型名 = 部位包名去掉最后一段（lenna_upper → lenna），和布料物理的一样
/// </summary>
internal static class GF2Tune
{
    // 模型 → 材质名 → 参数 → 值
    private static readonly Dictionary<string, Dictionary<string, Dictionary<string, float>>> Tables =
        new Dictionary<string, Dictionary<string, Dictionary<string, float>>>(StringComparer.OrdinalIgnoreCase);
    private static string _dir;

    internal static void Init(ConfigFile cfg, string pluginDir)
    {
        _dir = Path.Combine(pluginDir, "gf2");
#if SORA_DEV
        BindDev(cfg);
#endif
    }

    /// 换上一个部位（GF2SkinPatch）：把这套的表套到这个部位的 GF2 材质上；开发版再登记 F12 的项、按「是不是你身上的」决定显示哪几套
    internal static void OnSkin(PlayerBody body, KeyValuePair<EBodyModelPart, ResourceKey> part, LoddedSkin lodded)
    {
        string model = ModelOf(part.Value?.path);
        List<Material> mats = GF2Materials(lodded);
        if (mats.Count == 0)
            model = null;                           // 这个部位不是少前 2 渲染的
        if (model != null)
            foreach (Material m in mats)
                ApplyTable(model, m);
#if SORA_DEV
        if (model != null)
            foreach (Material m in mats)
                Register(model, m);
        if (PlayerGate.IsLocal(body) && !PlayerGate.IconBodies.Contains(body))
            Wear(part.Key, model);
#endif
    }

    /// 部位包名 → 模型名（lenna_upper → lenna）
    private static string ModelOf(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        string key = Path.GetFileNameWithoutExtension(path);
        int cut = key.LastIndexOf('_');
        return cut > 0 ? key.Substring(0, cut) : key;
    }

    private static List<Material> GF2Materials(LoddedSkin lodded) =>
        lodded.GetComponentsInChildren<SkinnedMeshRenderer>(true).SelectMany(r => r.sharedMaterials)
            .Where(m => m != null && m.shader != null && m.shader.name.StartsWith("GF2/")).Distinct().ToList();

    private static string NameOf(Material m) => m.name.Replace(" (Instance)", "");

    /// 表里这个材质写了的参数 → 材质
    private static void ApplyTable(string model, Material m)
    {
        if (!Table(model).TryGetValue(NameOf(m), out Dictionary<string, float> props))
            return;
        foreach (KeyValuePair<string, float> kv in props)
            m.SetFloat(kv.Key, kv.Value);
    }

    /// 这一套的表（第一次用时从 gf2\<模型>.json 读；没有文件 = 空表 = 全是原版）
    private static Dictionary<string, Dictionary<string, float>> Table(string model)
    {
        if (Tables.TryGetValue(model, out Dictionary<string, Dictionary<string, float>> table))
            return table;
        table = new Dictionary<string, Dictionary<string, float>>();
        string path = Path.Combine(_dir, model + ".json");
        try
        {
            if (File.Exists(path))
                table = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, float>>>(File.ReadAllText(path)) ?? table;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[GF2 微调] 读 {path} 失败，这套按原版: {e.Message}");
        }
        Tables[model] = table;
        return table;
    }

#if SORA_DEV
    /// 一种能调的参数：材质有这个分支 / 标记才列出来
    private sealed class Knob
    {
        internal string Prop, Label, Help;
        internal float Def;                         // 原版的值（表里没写就是它）
        internal Func<Material, bool> Has;
    }

    private static readonly Knob[] Knobs =
    {
        new Knob { Prop = "_GF2StockingSkin", Label = "黑丝透肉", Def = 1f, Has = IsStocking,
                   Help = "正对镜头那块透出多少肤色。1 = 少前 2 原版，0 = 不透肉、整条都是边缘那种深色" },
        new Knob { Prop = "_GF2StockingBack", Label = "黑丝背光亮度", Def = 1f, Has = IsStocking,
                   Help = "背光那面、太阳阴影里还吃多少太阳。1 = 少前 2 原版（蕾娜是六成多，别的套一到三成），0 = 不吃、和塔科夫自己的东西一样" },
        new Knob { Prop = "_GF2PlasticClear", Label = "胸前塑料透明度", Def = 0f, Has = IsPlastic,
                   Help = "0 = 原版（中间那片大约六成不透明），往上拖越清透，1 时大约只剩一成半；完全不透明的地方（背后火焰、黑线、白点）不变" },
    };

    private static bool IsStocking(Material m) => m.IsKeywordEnabled("_USE_STOCKING");

    private static bool IsPlastic(Material m) => m.HasProperty("_GF2PlasticKnob") && m.GetFloat("_GF2PlasticKnob") > 0.5f;

    // F12 里显示的套装名（加新服装时在这里加一行；没加就显示模型名）
    private static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["asteria_w"] = "阿斯缇亚 W", ["asteria_b"] = "阿斯缇亚 B", ["basti"] = "贝丝蒂", ["ots14"] = "OTs-14",
        ["sop"] = "索普", ["clukay"] = "可露凯", ["lenna"] = "蕾娜",
    };

    private static string Display(string model) => Names.TryGetValue(model, out string n) ? n : model;

    /// F12 的一项：哪套、哪个材质、调什么；改了要套到哪些材质上（同一套衣服在主菜单、战局里可能是不同的实例）
    private sealed class Item
    {
        internal string Model, Mat;
        internal Knob Knob;
        internal ConfigEntry<float> Entry;
        internal ConfigurationManagerAttributes Attr;
        internal readonly List<Material> Targets = new List<Material>();
    }

    private static ConfigFile _cfg;
    private static ConfigEntry<string> _wornInfo;   // 「你身上的套装」：一行说明；启动时就绑，这一组在 F12 里才排在第 3（F12 按每组第一次绑定的先后排）
    private static readonly Dictionary<string, Item> Items = new Dictionary<string, Item>();                     // F12 的键 → 项
    private static readonly Dictionary<EBodyModelPart, string> Worn = new Dictionary<EBodyModelPart, string>();  // 你身上每个部位是哪套
    private static readonly HashSet<string> Dirty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);       // 改过、还没存的模型
    private static float _dirtyAt;
    private static bool _sync;                      // Pull 写 F12 的值时挡住自己触发自己

    private static void BindDev(ConfigFile cfg)
    {
        _cfg = cfg;
        _wornInfo = cfg.Bind(CfgUtil.SecOutfit, "你身上的套装", "（还没换上少前 2 的套装）", new ConfigDescription(
            "下面列的是这几套能调的项；换了衣服要重新打开 F12 才会更新", null,
            new ConfigurationManagerAttributes { Order = 100, HideDefaultButton = true, CustomDrawer = Label }));
        cfg.SettingChanged += (_, a) =>
        {
            if (_sync || a.ChangedSetting.Definition.Section != CfgUtil.SecOutfit ||
                !Items.TryGetValue(a.ChangedSetting.Definition.Key, out Item it))
                return;
            if (CfgUtil.F12Open)
                Push(it);
            else
                Pull(it);                           // cfg 重载回灌的旧值（Fika / MenuOverhaul）：拉回表里的真值
        };
    }

    /// 「你身上的套装」画成一行字（不是输入框）
    private static void Label(ConfigEntryBase e) => GUILayout.Label(e.BoxedValue as string ?? "", GUILayout.ExpandWidth(true));

    /// 这套这个材质能调的项登记进 F12（第一次见到时绑定；值 = 表里的，没写 = 原版）。键名不变，显示名见 RefreshNames
    private static void Register(string model, Material m)
    {
        bool added = false;
        foreach (Knob k in Knobs.Where(k => k.Has(m)))
        {
            string key = $"{model} · {NameOf(m)}：{k.Label}";
            if (!Items.TryGetValue(key, out Item it))
            {
                it = new Item { Model = model, Mat = NameOf(m), Knob = k, Attr = new ConfigurationManagerAttributes { Browsable = false } };
                it.Entry = _cfg.Bind(CfgUtil.SecOutfit, key, k.Def, new ConfigDescription(
                    $"{k.Help}。只改{Display(model)}这一套，拖动实时生效，停 1.5 秒自动存进 gf2\\{model}.json", new AcceptableValueRange<float>(0f, 1f), it.Attr));
                Items[key] = it;
                added = true;
            }
            if (!it.Targets.Contains(m))
                it.Targets.Add(m);
            Pull(it);
        }
        if (added)
            RefreshNames();
    }

    /// 显示名「蕾娜 · 黑丝透肉」；同一套有好几个材质能调同一项（比如阿斯缇亚）时才带上材质名
    private static void RefreshNames()
    {
        foreach (Item it in Items.Values)
        {
            bool many = Items.Values.Count(o => o.Model == it.Model && o.Knob == it.Knob) > 1;
            it.Attr.DispName = $"{Display(it.Model)} · {(many ? it.Mat + " · " : "")}{it.Knob.Label}";
        }
    }

    /// 你身上这个部位现在是哪套（不是少前 2 渲染的 = null）；F12 只显示你身上这几套的项（下次打开 F12 时生效）
    private static void Wear(EBodyModelPart part, string model)
    {
        Worn[part] = model;
        foreach (Item it in Items.Values)
            it.Attr.Browsable = Worn.ContainsValue(it.Model);
        string worn = string.Join("、", Worn.Values.Where(m => m != null).Distinct().Select(Display));
        _wornInfo.Value = worn.Length > 0 ? worn : "（没穿少前 2 的套装）";
    }

    private static float Value(Item it) =>
        Table(it.Model).TryGetValue(it.Mat, out Dictionary<string, float> props) && props.TryGetValue(it.Knob.Prop, out float v) ? v : it.Knob.Def;

    /// 表 → F12（不触发 Push）
    private static void Pull(Item it)
    {
        float v = Value(it);
        if (it.Entry.Value == v)
            return;
        _sync = true;
        it.Entry.Value = v;
        _sync = false;
    }

    /// F12 → 表 → 材质，记下待存（回到原版的值不记进表）
    private static void Push(Item it)
    {
        Dictionary<string, Dictionary<string, float>> table = Table(it.Model);
        if (!table.TryGetValue(it.Mat, out Dictionary<string, float> props))
            table[it.Mat] = props = new Dictionary<string, float>();
        float v = it.Entry.Value;
        if (Mathf.Approximately(v, it.Knob.Def))
            props.Remove(it.Knob.Prop);
        else
            props[it.Knob.Prop] = v;
        if (props.Count == 0)
            table.Remove(it.Mat);
        it.Targets.RemoveAll(m => m == null);
        foreach (Material m in it.Targets)
            m.SetFloat(it.Knob.Prop, v);
        Dirty.Add(it.Model);
        _dirtyAt = Time.realtimeSinceStartup;
    }

    /// 由 Plugin 每帧调：改动停下 1.5 秒就把改过的表写盘（防抖：拖滑块时每帧都在变）
    internal static void Tick()
    {
        if (Dirty.Count == 0 || Time.realtimeSinceStartup - _dirtyAt < 1.5f)
            return;
        foreach (string model in Dirty)
            Save(model);
        Dirty.Clear();
    }

    private static void Save(string model)
    {
        string path = Path.Combine(_dir, model + ".json");
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(path, JsonConvert.SerializeObject(Table(model), Formatting.Indented));
            Plugin.Log.LogInfo($"[GF2 微调] 已存 {model}.json：{JsonConvert.SerializeObject(Table(model))}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[GF2 微调] 写 {path} 失败: {e.Message}");
        }
    }
#endif
}
