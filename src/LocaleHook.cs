using System;
using System.Collections.Generic;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace AstralDivide;

/// <summary>
/// 往每种语言的文案表里写东西。SPT 的语言表是懒加载的：这里给每种语言挂一个转换，第一次用到那种语言时才执行。
/// write 拿到的是语言代码（"ch" = 中文）和那种语言的文案表。
/// </summary>
static class LocaleHook
{
    internal static void Each(LocaleTable locales, Action<string, Dictionary<string, string>> write)
    {
        foreach (var (code, lazyLocale) in locales.Global)
            lazyLocale.AddTransformer(data =>
            {
                if (data != null) write(code, data);
                return data;
            });
    }
}
