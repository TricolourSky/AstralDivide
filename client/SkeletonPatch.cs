using System;
using System.Collections.Generic;
using Diz.Skinning;
using HarmonyLib;
using UnityEngine;

namespace AstralDivide.Client;

internal static class BonePath
{
    internal static string Leaf(string path)
    {
        int i = path.LastIndexOf('/');
        return i < 0 ? path : path.Substring(i + 1);
    }

    internal static Dictionary<string, Transform> LeafIndex(Skeleton skeleton)
    {
        var map = new Dictionary<string, Transform>();
        foreach (KeyValuePair<string, Transform> kv in skeleton.Bones)
        {
            string leaf = Leaf(kv.Key);
            if (!map.ContainsKey(leaf))
                map[leaf] = kv.Value;
        }
        return map;
    }
}

[HarmonyPatch(typeof(Skin), nameof(Skin.ApplySkin))]
internal static class SkeletonPatch
{
    private static readonly AccessTools.FieldRef<Skin, Skeleton> SkeletonRef =
        AccessTools.FieldRefAccess<Skin, Skeleton>("_skeleton");

    [HarmonyPrefix]
    private static void Prefix(Skin __instance)
    {
        try
        {
            Skeleton skeleton = SkeletonRef(__instance);
            if (skeleton == null || __instance._bonePaths == null)
                return;
            Dictionary<string, Transform> bones = skeleton.Bones;
            HashSet<string> registered = InjectionRegistry.RegisteredNames(skeleton);
            if (registered != null)
                foreach (string name in registered)
                    if (bones.TryGetValue(name, out Transform t) && t == null)
                        bones.Remove(name);
            bool record = InjectionRegistry.ContextActive && ReferenceEquals(InjectionRegistry.ContextSkeleton, skeleton);
            Dictionary<string, Transform> lookup = null;
            List<string> injected = null;
            foreach (string name in EnumeratePaths(__instance))
            {
                if (string.IsNullOrEmpty(name) || bones.ContainsKey(name))
                    continue;
                if (lookup == null)
                    lookup = BuildLookup(__instance.transform.root);
                if (lookup.TryGetValue(BonePath.Leaf(name), out Transform t))
                {
                    bones[name] = t;
                    (injected ?? (injected = new List<string>())).Add(name);
                    if (record && !InjectionRegistry.PendingNames.Contains(name))
                        InjectionRegistry.PendingNames.Add(name);
                }
                else
                    Plugin.Log.LogWarning($"骨 {name} 在骨架与服装 prefab 里都找不到, 跳过注入");
            }
            if (record)
                WarnUnmanaged(injected);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"SkeletonPatch 异常: {e}");
        }
    }

    private static void WarnUnmanaged(List<string> injected)
    {
        PhysicsConfig cfg = InjectionRegistry.ContextConfig;
        if (injected == null || cfg == null)
            return;
        List<string> orphans = null;
        foreach (string name in injected)
        {
            string leaf = BonePath.Leaf(name);
            bool managed = false;
            foreach (ChainConfig chain in cfg.chains)
                if (chain.bones.Contains(leaf))
                {
                    managed = true;
                    break;
                }
            if (!managed)
                (orphans ?? (orphans = new List<string>())).Add(name);
        }
        if (orphans != null)
            Plugin.Log.LogWarning($"注入了不属于任何已配置链的骨 (网格撕裂前兆): {string.Join(", ", orphans)}");
    }

    private static IEnumerable<string> EnumeratePaths(Skin skin)
    {
        foreach (string p in skin._bonePaths)
            yield return p;
        yield return skin._rootBonePath;
    }

    internal static Dictionary<string, Transform> BuildLookup(Transform root)
    {
        var map = new Dictionary<string, Transform>();
        Collect(root, map);
        return map;
    }

    private static void Collect(Transform node, Dictionary<string, Transform> map)
    {
        if (!map.ContainsKey(node.name))
            map[node.name] = node;
        for (int i = 0; i < node.childCount; i++)
            Collect(node.GetChild(i), map);
    }
}
