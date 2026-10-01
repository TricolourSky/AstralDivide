using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace AstralDivide;

/// <summary>
/// 商人的邮件文案。**开了保险就必须有这个**：
/// <c>InsuranceService</c> 对 <c>dialogue["insuranceStart"]</c> 和 <c>dialogue["insuranceFound"]</c>
/// 是直接索引（没有 TryGetValue），缺 key 会让 <c>/client/match/local/end</c> 整个崩掉，玩家结算不了战局。
/// 原生 12 个商人里只有开保险的 Prapor 和 Therapist 带 dialogue.json，就是这个原因。
/// </summary>
[Injectable]
public class TraderDialogue(LocaleTable locales, ModHelper mod)
{
    record Line(string Id, Dictionary<string, string> Text);

    /// 返回给 Trader.Dialogue 用的 "分类 -> 文案id 列表"，同时把文案本身挂进各语言 locale。
    public Dictionary<string, List<string>?> Load(Assembly assembly)
    {
        var root = mod.GetAbsolutePathToModFolder(assembly);
        var raw = mod.GetJsonDataFromFile<Dictionary<string, JsonElement>>(root, "db/dialogue.json");
        var lines = raw.Where(kv => kv.Value.ValueKind == JsonValueKind.Array)
            .ToDictionary(kv => kv.Key, kv => kv.Value.EnumerateArray().Select(Parse).ToList());

        ApplyLocales(lines.Values.SelectMany(l => l).ToList());
        return lines.ToDictionary(kv => kv.Key, kv => (List<string>?)kv.Value.Select(l => l.Id).ToList());
    }

    static Line Parse(JsonElement e) => new(
        e.GetProperty("id").GetString()!,
        e.EnumerateObject().Where(p => p.Name != "id").ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty));

    void ApplyLocales(List<Line> lines)
    {
        foreach (var (code, lazyLocale) in locales.Global)
            lazyLocale.AddTransformer(data =>
            {
                if (data == null) return data;
                foreach (var line in lines)
                    data[line.Id] = line.Text.GetValueOrDefault(code) ?? line.Text.GetValueOrDefault("en") ?? line.Id;
                return data;
            });
    }
}
