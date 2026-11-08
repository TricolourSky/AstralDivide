using System.Collections.Generic;
using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Routers;
using Path = System.IO.Path;

namespace AstralDivide;

/// <summary>
/// 把 SORA 塞进商人表：base.json + assort.json（货架）+ 邮件文案 + 头像路由。
/// </summary>
[Injectable]
public class TraderRegistrar(
    ISptLogger<TraderRegistrar> log,
    TradersTable traders,
    ImageRouter images,
    ModHelper mod)
{
    public TraderBase Register(Assembly assembly, Dictionary<string, List<string>?> dialogue)
    {
        var root = mod.GetAbsolutePathToModFolder(assembly);
        var traderBase = mod.GetJsonDataFromFile<TraderBase>(root, "db/base.json");

        var assort = mod.GetJsonDataFromFile<TraderAssort>(root, "db/assort.json");
        EnsureSellItemsPerLevel(traderBase);
        traders[traderBase.Id] = new Trader
        {
            Base = traderBase,
            Assort = assort,
            QuestAssort = new() { ["Started"] = new(), ["Success"] = new(), ["Fail"] = new() },
            Dialogue = dialogue
        };

        RegisterAvatar(traderBase, root);
        return traderBase;
    }

    /// <summary>
    /// <c>items_sell</c> 必须**每一档忠诚都有一条**（哪怕是空的）。
    /// 客户端 <c>EditBuildManipulation.GetItemAvailability</c> 打开武器预设编辑器时对每个商人做
    /// <c>for (lvl = 你的忠诚; lvl &gt; 0; lvl--) Settings.SellItems[lvl]</c> —— 直接索引、没有 TryGetValue。
    /// 首版只写了 "1"，1 级号没事，69 级测试号对 SORA 是 4 档 → <c>SellItems[4]</c> → KeyNotFoundException，
    /// 整个预设编辑器一片空白（2026-08-26 实机，Player.log 有完整堆栈）。原版 12 个商人全部是 1~4 齐全的。
    /// 这里兜底：少哪档补哪档，并把它喊出来 —— 别让下一次编辑 base.json 再静默栽进去。
    /// </summary>
    void EnsureSellItemsPerLevel(TraderBase traderBase)
    {
        var levels = traderBase.LoyaltyLevels?.Count ?? 0;
        traderBase.ItemsSell ??= new Dictionary<string, ItemSellData>();
        var missing = new List<string>();
        for (var i = 1; i <= levels; i++)
        {
            var key = i.ToString();
            if (traderBase.ItemsSell.ContainsKey(key)) continue;
            traderBase.ItemsSell[key] = new ItemSellData { Category = [], IdList = [] };
            missing.Add(key);
        }
        if (missing.Count > 0)
            log.Warning($"[AstralDivide] base.json items_sell is missing loyalty level {string.Join("/", missing)}; added empty ones. "
                + "Without them the weapon preset editor goes blank on high-loyalty profiles - add them to base.json");
    }

    /// <summary>
    /// 头像路由键**不能带扩展名**：SPT 收到请求也会先把扩展名切掉再查表，
    /// 带着 .png 注册的话那条路由永远匹配不上，商人头像就是一片空白。
    /// </summary>
    void RegisterAvatar(TraderBase traderBase, string root)
    {
        var route = (traderBase.Avatar ?? string.Empty).Replace(".png", string.Empty);
        images.AddRoute(route, Path.Combine(root, "res", "SORA.png"));
    }
}
