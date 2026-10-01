using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using WTTServerCommonLib.Models;
using Path = System.IO.Path;   // SPT 自己也有个 Tables.Path，不消歧义会撞名

namespace AstralDivide;

/// <summary>
/// 通过 WTT-CommonLib 把自定义内容灌进数据库。
///
/// **必须在商人注册之后调用**：WTT 的服装服务是往<b>已存在</b>的商人身上挂衣服的
/// （内部 <c>tradersTable[traderId].Suits.Add(...)</c> 是直接索引），商人不在表里当场就抛异常。
/// </summary>
[Injectable]
public class ContentLoader(
    ISptLogger<ContentLoader> log,
    TradersTable traders,
    TemplateTable templates,
    BotTable bots,
    ModHelper mod,
    WTTServerCommonLib.WTTServerCommonLib wtt)
{
    public async Task LoadAsync(Assembly assembly, TraderBase trader)
    {
        // 注册别名后，各种配置文件里可以写 "traderId": "sora" 而不是那串 24 位 id
        TraderIds.Add("sora", trader.Id);

        await wtt.CustomClothingService.CreateCustomClothing(assembly);
        var heads = await CountNewCharacterOptions(() => wtt.CustomHeadService.CreateCustomHeads(assembly));
        var voices = await CountNewCharacterOptions(() => wtt.CustomVoiceService.CreateCustomVoices(assembly));
        await wtt.CustomBotLoadoutService.CreateCustomBotLoadouts(assembly);

        // WTT 内部把异常都吞掉只打日志，所以这些数字全部回读真实结果，不能靠"没报错"当成功
        log.Debug($"[AstralDivide] 内容已挂载：服装 {traders[trader.Id].Suits?.Count ?? 0} 套 / "
            + $"头部 {heads} 个 / 语音 {voices} 个 / bot 外观底稿 {BotStubCount(assembly)} 份");
    }

    /// 头和语音都是往 templateTable.Character 里塞可选项，跑之前后各数一次就知道真加了几个。
    async Task<int> CountNewCharacterOptions(Func<Task> load)
    {
        var before = templates.Character.Count;
        await load();
        return templates.Character.Count - before;
    }

    /// <summary>
    /// 返回 "命中/总数"。WTT 拿**文件名**当 bot 类型去查表，而表的 key 是全小写；
    /// 文件名写错或大小写不对，它只 logger.Error 一句就跳过，功能静悄悄地没生效。
    /// </summary>
    string BotStubCount(Assembly assembly)
    {
        var dir = Path.Combine(mod.GetAbsolutePathToModFolder(assembly), "db", "CustomBotLoadouts");
        if (!Directory.Exists(dir)) return "0/0";
        var files = Directory.GetFiles(dir, "*.json");
        var hit = files.Count(f => bots.Types.ContainsKey(Path.GetFileNameWithoutExtension(f)));
        return $"{hit}/{files.Length}";
    }
}
