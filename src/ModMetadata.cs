using System.Collections.Generic;
using SemanticVersioning;
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace AstralDivide;

public record ModMetadata : IModMetadata
{
    /// ⚠️ GUID 是模组的身份证（别的模组声明依赖时写的就是它）。
    /// Forge 规定它必须和客户端 BepInPlugin 的 GUID、上传时填的 GUID 三者相同 —— 改这里就要一起改 client\Plugin.cs。
    /// 2026-10-01 改名（BinaryDimensionStore → AstralDivide）时一起换的，旧值 com.tricoloursky.binarydimension。
    public string ModGuid { get; init; } = "com.tricoloursky.astraldivide";
    public string Name { get; init; } = "AstralDivide";
    public string Author { get; init; } = "TricolourSky";
    public List<string>? Contributors { get; init; }
    public Version Version { get; init; } = new("0.2.0");
    public Range SptVersion { get; init; } = new("~4.1.0");
    public bool HasPrepatcher { get; init; }
    public List<string>? Incompatibilities { get; init; }

    /// WTT 没装的话服务端会直接报"缺依赖"并拒绝加载，而不是跑到一半崩。
    public Dictionary<string, Range>? ModDependencies { get; init; } =
        new() { { "com.wtt.commonlib", new Range("~3.0.0") } };

    public string? Url { get; init; } = "https://github.com/TricolourSky/AstralDivide";

    /// 代码 GPL-3.0（Tech Leader 2026-10-02 定：要上 Forge 就得公开源码）。图片和模型另有归属，见根目录 NOTICE.txt。
    public string License { get; init; } = "GPL-3.0";
}
