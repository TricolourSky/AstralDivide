using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;

namespace AstralDivide;

/// <summary>
/// LL1 货架像 Fence 一样随机：每次 SORA 到点补货（NextResupply 变了）就把 1 级商品全部扔掉重抽。
/// 货源直接用 Fence 启动时建好的全物品池，但只要**整枪预设**，且只从价格高的那一半（好枪）里抽；耐久 95%~100%。
/// 2~4 级商品是 assort.json 里写死的，不动。
/// </summary>
[Injectable(InjectionType.Singleton)]
public class RandomShelf(TradersTable traders, ItemHelper itemHelper, RandomUtil random, ICloner cloner) : IOnUpdate
{
    const int ShelfSize = 6;
    static readonly MongoId SoraId = new("90726f6a656374536f726132");
    int lastResupply = -1;

    public Task<bool> OnUpdateAsync(long _, CancellationToken __)
    {
        if (!traders.TryGetValue(SoraId, out var sora) || !traders.TryGetValue(Traders.FENCE, out var fence)) return Task.FromResult(false);
        if (sora.Base.NextResupply == lastResupply) return Task.FromResult(true);
        lastResupply = sora.Base.NextResupply ?? 0;
        var assort = sora.Assort;
        var oldRoots = assort.LoyalLevelItems.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();
        var doomed = oldRoots.SelectMany(r => assort.Items.GetItemWithChildren(r)).ToHashSet();
        assort.Items.RemoveAll(doomed.Contains);
        foreach (var root in oldRoots) { assort.BarterScheme.Remove(root); assort.LoyalLevelItems.Remove(root); }
        var pool = fence.Assort.Items.Where(i => i.SlotId == "hideout" && i.Upd?.SptPresetId != null
            && itemHelper.IsOfBaseclass(i.Template, BaseClasses.WEAPON))
            .OrderByDescending(i => fence.Assort.BarterScheme[i.Id][0][0].Count).ToList();
        pool = pool.Take(pool.Count / 2).ToList();
        for (var n = 0; n < ShelfSize && pool.Count > 0; n++)
        {
            var pick = random.GetArrayValue(pool);
            pool.Remove(pick);
            var items = cloner.Clone(fence.Assort.Items.GetItemWithChildren(pick.Id)).ReplaceIDs().ToList();
            items.RemapRootItemId();
            var max = itemHelper.GetItem(pick.Template).Value.Properties?.MaxDurability ?? 100;
            items[0].Upd.Repairable = new UpdRepairable { Durability = Math.Round(random.GetDouble(max * 0.95, max)), MaxDurability = max };
            items[0].Upd.StackObjectsCount = 1;
            items[0].Upd.UnlimitedCount = false;
            assort.Items.AddRange(items);
            assort.BarterScheme[items[0].Id] = cloner.Clone(fence.Assort.BarterScheme[pick.Id]);
            assort.LoyalLevelItems[items[0].Id] = 1;
        }
        sora.Base.RefreshTraderRagfairOffers = true;
        return Task.FromResult(true);
    }
}
