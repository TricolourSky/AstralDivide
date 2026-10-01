using System.Collections.Generic;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace AstralDivide;

/// <summary>
/// 写 SPT 认的 5 个商人文案 key，中英双语跟随游戏语言（"ch" = 中文）。
/// </summary>
[Injectable]
public class TraderLocales(LocaleTable locales)
{
    /// 大小写不敏感：SPT 的 locales.Global 本身就是 OrdinalIgnoreCase 建的，这里跟上免得对不上号。
    static readonly Dictionary<string, string[]> Text = new(System.StringComparer.OrdinalIgnoreCase)
    {
        //                     FullName  FirstName  Nickname  Location          Description
        // 2026-10-01 跟着模组改名：Binary Dimension / 二进制维度 → Astral Divide。中文名还没定，先沿用英文原名。
        ["en"] = ["SORA", "SORA", "SORA", "Astral Divide", "A mysterious merchant from the Astral Divide."],
        ["ch"] = ["SORA", "SORA", "SORA", "Astral Divide", "来自 Astral Divide 的神秘商人。"]
    };

    static readonly string[] Suffixes = ["FullName", "FirstName", "Nickname", "Location", "Description"];

    public void Apply(TraderBase trader)
    {
        foreach (var (code, lazyLocale) in locales.Global)
        {
            var values = Text.GetValueOrDefault(code, Text["en"]);
            lazyLocale.AddTransformer(data =>
            {
                if (data == null) return data;
                for (var i = 0; i < Suffixes.Length; i++) data[$"{trader.Id} {Suffixes[i]}"] = values[i];
                return data;
            });
        }
    }
}
