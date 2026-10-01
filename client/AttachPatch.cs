using System;
using System.Collections.Generic;
using System.IO;
using Diz.Skinning;
using EFT;
using EFT.UI;
using EFT.Visual;
using HarmonyLib;
using PlayerIcons;
using UnityEngine;

namespace AstralDivide.Client;

internal static class InjectionRegistry
{
    internal class Record
    {
        public readonly List<string> InjectedNames = new List<string>();
        public readonly List<Transform> ChainRoots = new List<Transform>();
    }

    internal static readonly Dictionary<Skeleton, Dictionary<EBodyModelPart, Record>> Records =
        new Dictionary<Skeleton, Dictionary<EBodyModelPart, Record>>();

    internal static bool ContextActive;
    internal static Skeleton ContextSkeleton;
    internal static EBodyModelPart ContextPart;
    internal static PhysicsConfig ContextConfig;
    internal static readonly List<string> PendingNames = new List<string>();

    internal static void BeginContext(Skeleton skeleton, EBodyModelPart part, PhysicsConfig cfg)
    {
        ContextActive = true;
        ContextSkeleton = skeleton;
        ContextPart = part;
        ContextConfig = cfg;
        PendingNames.Clear();
    }

    internal static void Commit(Skeleton skeleton, EBodyModelPart part, List<Transform> roots)
    {
        if (ContextActive && ReferenceEquals(ContextSkeleton, skeleton) &&
            (PendingNames.Count > 0 || (roots != null && roots.Count > 0)))
        {
            if (!Records.TryGetValue(skeleton, out Dictionary<EBodyModelPart, Record> parts))
                Records[skeleton] = parts = new Dictionary<EBodyModelPart, Record>();
            var rec = new Record();
            rec.InjectedNames.AddRange(PendingNames);
            if (roots != null)
                rec.ChainRoots.AddRange(roots);
            parts[part] = rec;
        }
        EndContext();
    }

    internal static void EndContext()
    {
        ContextActive = false;
        ContextSkeleton = null;
        ContextConfig = null;
        PendingNames.Clear();
    }

    internal static void RemovePart(Skeleton skeleton, EBodyModelPart part)
    {
        RigRegistry.RemovePart(skeleton, (int)part);
        if (!Records.TryGetValue(skeleton, out Dictionary<EBodyModelPart, Record> parts) ||
            !parts.TryGetValue(part, out Record rec))
            return;
        Clear(skeleton, rec);
        parts.Remove(part);
        if (parts.Count == 0)
            Records.Remove(skeleton);
    }

    internal static void RemoveSkeleton(Skeleton skeleton)
    {
        RigRegistry.RemoveSkeleton(skeleton);
        if (ReferenceEquals(skeleton, null) || !Records.TryGetValue(skeleton, out Dictionary<EBodyModelPart, Record> parts))
            return;
        foreach (Record rec in parts.Values)
            Clear(skeleton, rec);
        Records.Remove(skeleton);
    }

    internal static HashSet<string> RegisteredNames(Skeleton skeleton)
    {
        if (ReferenceEquals(skeleton, null) || !Records.TryGetValue(skeleton, out Dictionary<EBodyModelPart, Record> parts))
            return null;
        var set = new HashSet<string>();
        foreach (Record rec in parts.Values)
            foreach (string name in rec.InjectedNames)
                set.Add(name);
        return set;
    }

    private static void Clear(Skeleton skeleton, Record rec)
    {
        foreach (string name in rec.InjectedNames)
            skeleton.Bones.Remove(name);
        foreach (Transform root in rec.ChainRoots)
            if (root != null)
                UnityEngine.Object.DestroyImmediate(root.gameObject);
    }
}

/// <summary>
/// 一个角色 **× 一个模型** 一套物理。同一模型的上衣和头发共用碰撞体；
/// **不同模型的部位各自一套，绝不能混。**
///
/// ⚠️ 首版按「角色」缓存、命中时把 `model` 参数丢掉了（2026-08-26 实机炸出来）：
/// 头戴阿斯缇亚、身穿贝丝蒂时，衣服那个部位拿到的是**阿斯缇亚的 rig**，
/// 于是拿 A 模型的刚体表去配 B 模型的骨名 —— `_segs` 又是拿 **PMX 刚体下标**当 key、
/// 而下标只在单个模型内有意义，两边直接撞在一起。日志实录：
/// `sop_upper: 建刚体 2（跳过 126）`、`关节零点偏差 173.62°`、
/// `骨 Jacket_6_5 离父骨 2.0 m，父骨 Base HumanLCalf`（外套挂到了小腿上）—— 画面上就是乱飞。
///
/// 一个角色两套 rig 不会互相干扰：每套 rig 在布料世界里各占一个**车位**（相距 200 单位），
/// 碰撞体够不着对方；而且身体碰撞体都是 kinematic，本来也不互相产生接触。
/// </summary>
internal static class RigRegistry
{
    private static readonly Dictionary<Skeleton, Dictionary<string, MmdRig>> _rigs =
        new Dictionary<Skeleton, Dictionary<string, MmdRig>>();

    internal static MmdRig Get(Skeleton skeleton, MmdModel model)
    {
        if (!_rigs.TryGetValue(skeleton, out Dictionary<string, MmdRig> byModel))
            _rigs[skeleton] = byModel = new Dictionary<string, MmdRig>();
        if (byModel.TryGetValue(model.Name, out MmdRig rig))
            return rig;
        byModel[model.Name] = rig = new MmdRig(model);
        PhysWorld.Register(rig);
        return rig;
    }

    internal static void RemovePart(Skeleton skeleton, int part)
    {
        if (ReferenceEquals(skeleton, null) || !_rigs.TryGetValue(skeleton, out Dictionary<string, MmdRig> byModel))
            return;
        var dead = new List<string>();
        foreach (KeyValuePair<string, MmdRig> kv in byModel)
        {
            kv.Value.RemovePart(part);
            if (!kv.Value.Alive)
                dead.Add(kv.Key);
        }
        foreach (string name in dead)
        {
            // ⚠️ 必须走 DestroyAll —— 车位号是在那儿归还的。
            // 首版这里只是 Unregister，换装走的就是这条路，于是车位号只涨不还，
            // 实机涨到 87 号（刚体被推到 17540 单位外，float 精度不够，布料错位）。
            byModel[name].DestroyAll();
            PhysWorld.Unregister(byModel[name]);
            byModel.Remove(name);
        }
        if (byModel.Count == 0)
            _rigs.Remove(skeleton);
    }

    internal static void RemoveSkeleton(Skeleton skeleton)
    {
        if (ReferenceEquals(skeleton, null) || !_rigs.TryGetValue(skeleton, out Dictionary<string, MmdRig> byModel))
            return;
        foreach (MmdRig rig in byModel.Values)
        {
            rig.DestroyAll();
            PhysWorld.Unregister(rig);
        }
        _rigs.Remove(skeleton);
    }
}

[HarmonyPatch(typeof(PlayerBody), nameof(PlayerBody.SetSkin))]
internal static class AttachPatch
{
    [HarmonyPrefix]
    private static void Prefix(KeyValuePair<EBodyModelPart, ResourceKey> part, Skeleton skeleton)
    {
        try
        {
            if (skeleton == null)
                return;
            InjectionRegistry.RemovePart(skeleton, part.Key);
            PhysicsConfig cfg = null;
            string path = part.Value?.path;
            if (!string.IsNullOrEmpty(path))
                Plugin.Configs.TryGetValue(Path.GetFileNameWithoutExtension(path), out cfg);
            InjectionRegistry.BeginContext(skeleton, part.Key, cfg);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"SetSkin prefix 异常: {e}");
        }
    }

    [HarmonyPostfix]
    private static void Postfix(PlayerBody __instance, KeyValuePair<EBodyModelPart, ResourceKey> part, Skeleton skeleton)
    {
        List<Transform> roots = null;
        try
        {
            string path = part.Value?.path;
            if (string.IsNullOrEmpty(path) || skeleton == null)
                return;
            string key = Path.GetFileNameWithoutExtension(path);
            if (!Plugin.Configs.TryGetValue(key, out PhysicsConfig cfg))
                return;
            if (!__instance.BodySkins.TryGetValue(part.Key, out LoddedSkin lodded) || lodded == null)
                return;
            Dictionary<string, Transform> leafIndex = BonePath.LeafIndex(skeleton);
            Dictionary<string, Transform> lookup = SkeletonPatch.BuildLookup(lodded.transform);
            MmdModel model = MmdRaw.For(key);
            bool physics = model != null && Plugin.Enabled.Value && PlayerGate.Allow(__instance, key);

            // 三步走：先在静止姿态下**量**，再**嫁接**，最后**建物理**。
            // 顺序不能换 —— 一嫁接骨就跟着玩家的动画姿势跑了，再量就把那一瞬间的姿势烘死了。
            var survey = physics ? new MmdSurvey(model) : null;
            var chains = new List<Grafted>();
            var used = new HashSet<string>();
            foreach (ChainConfig chain in cfg.chains)
            {
                Grafted g = Measure(chain, leafIndex, lookup, used);
                if (g == null)
                    continue;
                chains.Add(g);
                survey?.AddChain(chain.attachTo, chain.bones, g.Bones);
                foreach (string n in chain.bones)
                    used.Add(n);
            }

            if (physics)
                foreach (MmdBoneMap bm in model.boneMap)
                    if (leafIndex.TryGetValue(bm.eft, out Transform rt) && lookup.TryGetValue(bm.eft, out Transform rest))
                        survey.AddEft(bm.eft, rt, rest);

            roots = new List<Transform>();
            foreach (Grafted g in chains)
            {
                g.Apply();
                roots.Add(g.Root);
            }

            if (!physics || chains.Count == 0)
            {
                if (model == null && Plugin.Enabled.Value)
                    Plugin.Log.LogWarning($"[mmd] {key}: 找不到 {key.Split('_')[0]}_raw.json，本部位只嫁接骨、不建物理");
                return;
            }

            PhysWorld.Ensure();
            if (!PhysWorld.Ready || !survey.Solve(key))
                return;
            leafIndex.TryGetValue("Base HumanPelvis", out Transform reference);
            MmdRig rig = RigRegistry.Get(skeleton, model);
            rig.AddPart((int)part.Key, survey, used, reference ?? chains[0].Attach, key);
            if (PlayerGate.IsLocal(__instance))
                TuneUI.Follow(model.Name);          // 调参台跟着你身上穿的这套走
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"AttachPatch 异常: {e}");
        }
        finally
        {
            InjectionRegistry.Commit(skeleton, part.Key, roots);
        }
    }

    /// 一条链的校验 + 静止姿态测量。校验不过返回 null（整条链不挂，和以前一样）。
    private static Grafted Measure(ChainConfig chain, Dictionary<string, Transform> leafIndex,
                                   Dictionary<string, Transform> lookup, HashSet<string> used)
    {
        if (chain.bones == null || chain.bones.Count < 1)
        {
            Plugin.Log.LogWarning($"链没有骨, 跳过: attachTo={chain.attachTo}");
            return null;
        }
        foreach (string name in chain.bones)
            if (used.Contains(name))
            {
                Plugin.Log.LogError($"骨名 {name} 已被其他链占用, 整条链拒挂: attachTo={chain.attachTo}");
                return null;
            }
        if (!leafIndex.TryGetValue(chain.attachTo, out Transform attach) || attach == null)
        {
            Plugin.Log.LogWarning($"attachTo 骨不存在, 整条链跳过: {chain.attachTo}");
            return null;
        }
        var bones = new Transform[chain.bones.Count];
        for (int i = 0; i < bones.Length; i++)
        {
            if (!leafIndex.TryGetValue(chain.bones[i], out bones[i]) || bones[i] == null)
                lookup.TryGetValue(chain.bones[i], out bones[i]);
            if (bones[i] == null)
            {
                Plugin.Log.LogWarning($"链骨不存在, 整条链跳过: {chain.bones[i]}");
                return null;
            }
        }
        for (int i = 0; i < bones.Length - 1; i++)
            if (bones[i + 1].parent != bones[i])
            {
                Plugin.Log.LogError($"链骨 {chain.bones[i + 1]} 不是 {chain.bones[i]} 的直接子级, 整条链拒挂");
                return null;
            }
        if (!lookup.TryGetValue(chain.attachTo, out Transform attachNode) || attachNode == null)
        {
            Plugin.Log.LogWarning($"服装 prefab 里找不到 attachTo 同名节点, 整条链跳过: {chain.attachTo}");
            return null;
        }
        return new Grafted
        {
            Bones = bones,
            Root = bones[0],
            Attach = attach,
            // 用 prefab 自带的同名骨算绑定姿态的局部位姿；直接保世界变换会把动画姿势烘进去。
            LocalPos = attachNode.InverseTransformPoint(bones[0].position),
            LocalRot = Quaternion.Inverse(attachNode.rotation) * bones[0].rotation,
        };
    }

    private class Grafted
    {
        internal Transform[] Bones;
        internal Transform Root;
        internal Transform Attach;
        internal Vector3 LocalPos;
        internal Quaternion LocalRot;

        internal void Apply()
        {
            Root.SetParent(Attach, false);
            Root.localPosition = LocalPos;
            Root.localRotation = LocalRot;
        }
    }
}

/// <summary>
/// 谁能跑布料物理。口径：**只给真人，不给 bot**；FIKA 里的队友也是真人，一样给。
/// 无头/专用服务端整个关掉（那儿没人看，白烧 CPU）。
/// </summary>
internal static class PlayerGate
{
    /// <see cref="IconBodyMarkPatch"/> 在 PlayerIconCreatorModelView.CreateBody() 那一刻登记的身体 ——
    /// 头部选择卡片的离屏渲染专用，天生不该有物理。Dispose 时由 <see cref="DisposePatch"/> 摘除。
    internal static readonly HashSet<PlayerBody> IconBodies = new HashSet<PlayerBody>();

    internal static bool Allow(PlayerBody body, string key)
    {
        if (Plugin.HeadlessDetected)
            return false;
        if (IconBodies.Contains(body))
        {
            Plugin.Log.LogDebug($"[mmd] {key}: PlayerIconCreator 的图标身体，不建物理");
            return false;
        }
        Player player = body.GetComponentInParent<Player>();
        if (player != null)
        {
            if (player.IsAI)
            {
                Plugin.Log.LogDebug($"[mmd] {key}: 穿在 bot 身上，不建物理");
                return false;
            }
            return true;                    // 本地玩家 + FIKA 里的真人队友
        }
        // 没有 Player 的 PlayerBody = 主菜单 / 藏身处 / 服装页的预览人物，要物理，那是给你看的。
        // 唯一不该有物理的离屏身体（头部卡片）已在出生时被 IconBodies 拦下，这里不用再猜挂法
        //（按祖先 / 按相机猜的两版都栽过：DEV_NOTES 二十六、三十二·补 3~4）。
        return true;
    }

    /// 只有本地玩家和主菜单/藏身处的预览人物才算「你身上穿的」——
    /// FIKA 队友身上的衣服不许去抢调参台的下拉框。
    internal static bool IsLocal(PlayerBody body)
    {
        Player player = body.GetComponentInParent<Player>();
        return player == null || player.IsYourPlayer;
    }

}

/// <summary>
/// 头部选择卡片的身体在 PlayerIconCreatorModelView.CreateBody() 里诞生 —— 在**诞生那一刻**打标记，
/// 后面的 Init→SetSkin 就直接不建物理（2026-08-28）。
/// ⚠️ 为什么不能靠逐帧轮询「挂没挂到图标相机下」：Init 建完物理后还要 await bundle / mip 零级贴图，
/// 随便就超 30 帧观察期，解冻后头发被物理甩乱；而 spriteFactory 挂相机 + 渲染在同一帧内完成，
/// 轮询永远晚一步 —— 乱发已经烘进卡片缓存了。
/// </summary>
[HarmonyPatch(typeof(PlayerIconCreatorModelView), nameof(PlayerIconCreatorModelView.CreateBody))]
internal static class IconBodyMarkPatch
{
    [HarmonyPostfix]
    private static void Postfix(PlayerIconCreatorModelView __instance)
    {
        try
        {
            if (__instance._playerBody != null)
                PlayerGate.IconBodies.Add(__instance._playerBody);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"IconBodyMarkPatch 异常: {e}");
        }
    }
}

[HarmonyPatch(typeof(PlayerBody), nameof(PlayerBody.Dispose))]
internal static class DisposePatch
{
    [HarmonyPostfix]
    private static void Postfix(PlayerBody __instance)
    {
        try
        {
            PlayerGate.IconBodies.Remove(__instance);
            InjectionRegistry.RemoveSkeleton(__instance.SkeletonRootJoint);
            InjectionRegistry.RemoveSkeleton(__instance.SkeletonHands);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Dispose postfix 异常: {e}");
        }
    }
}
