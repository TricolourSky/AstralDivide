using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
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
        await wtt.CustomClothingService.CreateCustomClothing(assembly);
        var heads = await CountNewCharacterOptions(() => wtt.CustomHeadService.CreateCustomHeads(assembly));
        var voices = await CountNewCharacterOptions(() => wtt.CustomVoiceService.CreateCustomVoices(assembly));
        await wtt.CustomBotLoadoutService.CreateCustomBotLoadouts(assembly);
        Check(mod.GetAbsolutePathToModFolder(assembly), traders[trader.Id].Suits?.Count ?? 0, heads, voices);
    }

    /// 头和语音都是往 templateTable.Character 里塞可选项，跑之前后各数一次就知道真加了几个。
    async Task<int> CountNewCharacterOptions(Func<Task> load)
    {
        var before = templates.Character.Count;
        await load();
        return templates.Character.Count - before;
    }

    /// <summary>
    /// WTT 内部把异常都吞掉、只打一行它自己的日志，所以不能靠「没报错」当成功：回读真实结果，和 db 里写了几条比，少了才报。
    /// 头：WTT 只有 addHeadToPlayer = true 的才进 Character（heads.json 现在每条都是 true），加了 false 的头这里要跟着改。
    /// bot 外观底稿另有一个坑：WTT 拿**文件名**当 bot 类型去查表，表的 key 是全小写，名字写错它也只报一句就跳过。
    /// </summary>
    void Check(string root, int suits, int heads, int voices)
    {
        int wantSuits = Entries(root, "CustomClothing"), wantHeads = Entries(root, "CustomHeads"), wantVoices = Entries(root, "CustomVoices");
        var (hit, stubs) = BotStubs(root);
        if (suits < wantSuits || heads < wantHeads || voices < wantVoices || hit < stubs)
            log.Warning($"[AstralDivide] WTT did not load everything: outfits {suits}/{wantSuits}, heads {heads}/{wantHeads}, "
                + $"voices {voices}/{wantVoices}, bot loadouts {hit}/{stubs} (see the WTT errors above)");
    }

    /// db\&lt;目录&gt;\ 下所有 json 一共写了几条（数组 = 元素数，对象 = 键数）；读不了就当 0（不报）
    static int Entries(string root, string folder)
    {
        var dir = Path.Combine(root, "db", folder);
        if (!Directory.Exists(dir)) return 0;
        var n = 0;
        try
        {
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var e = doc.RootElement;
                n += e.ValueKind == JsonValueKind.Array ? e.GetArrayLength() : e.ValueKind == JsonValueKind.Object ? e.EnumerateObject().Count() : 0;
            }
        }
        catch (Exception) { return 0; }
        return n;
    }

    /// bot 外观底稿：（文件名查得到 bot 类型的，总数）
    (int hit, int total) BotStubs(string root)
    {
        var dir = Path.Combine(root, "db", "CustomBotLoadouts");
        if (!Directory.Exists(dir)) return (0, 0);
        var files = Directory.GetFiles(dir, "*.json");
        return (files.Count(f => bots.Types.ContainsKey(Path.GetFileNameWithoutExtension(f))), files.Length);
    }
}
