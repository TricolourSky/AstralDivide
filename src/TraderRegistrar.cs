using System.Collections.Generic;
using System.Linq;
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
        log.Debug($"[AstralDivide] 商人 SORA 已注册 ({traderBase.Id}) — "
            + $"{traderBase.LoyaltyLevels?.Count ?? 0} 档忠诚, 收购 {traderBase.ItemsBuy?.Category.Count ?? 0} 类, "
            + $"修理={traderBase.Repair?.Availability}, 保险={traderBase.Insurance?.Availability}, PVE={traderBase.IsAvailableInPVE}, "
            + $"邮件文案 {dialogue.Count} 类, "
            // 前三个数必须相等：在架商品 / 售价 / 解锁等级。少一个都会变成"看得见买不了"。
            // 子件 = 容器里的东西（弹药盒里的子弹），漏了的话买到手是空盒
            + $"货架 {RootCount(assort)} 件·{assort.BarterScheme?.Count ?? 0} 价·{assort.LoyalLevelItems?.Count ?? 0} 级"
            + $" + {(assort.Items?.Count ?? 0) - RootCount(assort)} 子件");
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
            log.Warning($"[AstralDivide] base.json 的 items_sell 缺第 {string.Join("/", missing)} 档，已补空档 —— "
                + "缺档会让高忠诚存档打开武器预设编辑器时崩成空白，请补进 base.json");
    }

    /// 货架上"一格商品"的数量。slotId=hideout 才是根，其余是装在容器里的子件。
    static int RootCount(TraderAssort assort) =>
        assort.Items?.Count(i => i.SlotId == "hideout") ?? 0;

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
