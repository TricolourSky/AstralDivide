using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;

namespace AstralDivide;

/// <summary>
/// SORA 商店的总入口。
///
/// <c>OnLoadOrder.TraderRegistration</c> 是 SPT 4.1 专门给商人注册留的档位：
/// 排在数据库导入之后、路由和跳蚤生成之前，正好是往商人表里塞人的时机。
///
/// 顺序不能改：WTT 的服装服务是往**已存在**的商人身上挂衣服的，商人必须先进表。
/// </summary>
[Injectable(typePriority: OnLoadOrder.TraderRegistration)]
public class SoraTrader(
    ISptLogger<SoraTrader> log,
    TraderRegistrar registrar,
    TraderDialogue dialogue,
    TraderLocales localeText,
    TraderRules rules,
    ContentLoader content) : IOnLoad
{
    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var trader = registrar.Register(assembly, dialogue.Load(assembly));
        localeText.Apply(trader);
        rules.Apply(trader);
        await content.LoadAsync(assembly, trader);
        log.Debug("[AstralDivide] SORA 已上线");
    }
}
