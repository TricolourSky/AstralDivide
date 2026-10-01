using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace AstralDivide.Client;

/// <summary>
/// `&lt;模型&gt;_&lt;部位&gt;.json` —— 现在**只管「挂哪儿」**，不再带任何物理参数。
///
/// 物理参数（刚度/阻尼/重力/半径）和碰撞球整套作废了：那是给旧的自研弹簧骨用的四参数近似，
/// 换算系数是我们编的。现在一律从 `&lt;模型&gt;_raw.json` 读 PMX 原值，见 <see cref="MmdRaw"/>。
/// JSON 里那些旧字段留着不管，反序列化会忽略。
///
/// ⚠️ `attachTo` 一个字都不要动：DEV_NOTES 十三写明 `RopeDown_M01~04` 挂 `Base HumanSpine2`
/// 与脊柱终版不一致，但**那是实机验收过的那一版**，运行时读的就是 JSON。
/// </summary>
public class PhysicsConfig
{
    public List<ChainConfig> chains = new List<ChainConfig>();

    public static PhysicsConfig Load(string file)
    {
        try
        {
            PhysicsConfig cfg = JsonConvert.DeserializeObject<PhysicsConfig>(File.ReadAllText(file));
            if (cfg != null)
                Normalize(cfg, file);
            return cfg;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"物理配置解析失败 {file}: {e.Message}");
            return null;
        }
    }

    private static void Normalize(PhysicsConfig cfg, string file)
    {
        if (cfg.chains == null)
            cfg.chains = new List<ChainConfig>();
        int dropped = cfg.chains.RemoveAll(c => c == null || string.IsNullOrEmpty(c.attachTo));
        if (dropped > 0)
            Plugin.Log.LogWarning($"剔除 {dropped} 条 attachTo 为空的链: {file}");
        foreach (ChainConfig chain in cfg.chains)
        {
            if (chain.bones == null)
                chain.bones = new List<string>();
            int bad = chain.bones.RemoveAll(string.IsNullOrEmpty);
            if (bad > 0)
                Plugin.Log.LogWarning($"剔除 {bad} 个空骨名 (链 {chain.attachTo}): {file}");
        }
    }
}

public class ChainConfig
{
    public string attachTo;
    public List<string> bones = new List<string>();
}
