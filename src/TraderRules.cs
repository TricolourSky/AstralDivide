using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Utils;

namespace AstralDivide;

/// <summary>
/// SPT 侧对自定义商人的两项**必办登记**。base.json 里写了没用，这两处不登记功能就是坏的。
/// </summary>
[Injectable]
public class TraderRules(
    ISptLogger<TraderRules> log,
    TraderConfig traderConfig,
    InsuranceConfig insuranceConfig,
    TimeUtil time)
{
    const double ReturnChancePercent = 95;

    public void Apply(TraderBase trader)
    {
        SetRefreshInterval(trader.Id);
        SetInsuranceReturnChance(trader.Id);
        log.Debug($"[AstralDivide] 商人规则已登记（货架 1~2 小时刷新，保险归还 {ReturnChancePercent}%）");
    }

    /// <summary>
    /// 不登记不会崩，但 SPT 每次取刷新时间都会刷一行 warning 并退回默认值。
    /// </summary>
    void SetRefreshInterval(MongoId traderId) =>
        traderConfig.UpdateTime.Add(new UpdateTime
        {
            Name = "SORA",
            TraderId = traderId,
            Seconds = new MinMax<int>(time.GetHoursAsSeconds(1), time.GetHoursAsSeconds(2))
        });

    /// <summary>
    /// **不登记必崩**：<c>InsuranceController.RollForDelete</c> 里是
    /// <c>insuranceConfig.ReturnChancePercent[traderId]</c> —— 直接索引，没有 TryGetValue。
    /// 原生 insurance.json 只有 Prapor 和 Therapist 两条，玩家一旦拿 SORA 投保，
    /// 返程结算时就会抛 KeyNotFoundException。
    /// </summary>
    void SetInsuranceReturnChance(MongoId traderId) =>
        insuranceConfig.ReturnChancePercent[traderId] = ReturnChancePercent;
}
