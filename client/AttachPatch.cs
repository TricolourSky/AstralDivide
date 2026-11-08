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
    internal static PhysicsConfig ContextConfig;
    internal static readonly List<string> PendingNames = new List<string>();

    internal static void BeginContext(Skeleton skeleton, PhysicsConfig cfg)
    {
        ContextActive = true;
        ContextSkeleton = skeleton;
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

    /// 布料场景随地图卸载了（PhysWorld.Lost）：所有 rig 的刚体、关节都没了，整张表作废（车位号一起还），下次换装重建
    internal static void ForgetAll()
    {
        foreach (Dictionary<string, MmdRig> byModel in _rigs.Values)
            foreach (MmdRig rig in byModel.Values)
                rig.DestroyAll();
        _rigs.Clear();
    }
}

/// <summary>
/// 三个 SetSkin 补丁（布料 <see cref="AttachPatch"/>、眨眼 <see cref="BlinkPatch"/>、少前 2 渲染 GF2SkinPatch）共用：
/// 这次换上的部位的包名（`basti_upper`，没有路径时是 null）和它的 LoddedSkin。三个补丁本身保持分开（10-10 定的）
/// </summary>
internal static class SkinPart
{
    internal static bool TryGet(PlayerBody body, KeyValuePair<EBodyModelPart, ResourceKey> part, out string key, out LoddedSkin lodded)
    {
        string path = part.Value?.path;
        key = string.IsNullOrEmpty(path) ? null : Path.GetFileNameWithoutExtension(path);
        return body.BodySkins.TryGetValue(part.Key, out lodded) && lodded != null;
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
            InjectionRegistry.BeginContext(skeleton, cfg);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"SetSkin prefix error: {e}");
        }
    }

    /// 三步走：先在静止姿态下**量**，再**嫁接**，最后**建物理**。
    /// 顺序不能换 —— 一嫁接骨就跟着玩家的动画姿势跑了，再量就把那一瞬间的姿势烘死了。
    [HarmonyPostfix]
    private static void Postfix(PlayerBody __instance, KeyValuePair<EBodyModelPart, ResourceKey> part, Skeleton skeleton)
    {
        List<Transform> roots = null;
        try
        {
            if (skeleton == null || !SkinPart.TryGet(__instance, part, out string key, out LoddedSkin lodded)
                || key == null || !Plugin.Configs.TryGetValue(key, out PhysicsConfig cfg))
                return;
            MmdModel model = MmdRaw.For(key);
            if (model == null && Plugin.Enabled.Value)
                LogOnce.Warn($"[mmd] {key}: {MmdRaw.ModelOf(key)}_raw.json not found, bones are grafted without physics");
            bool physics = model != null && Plugin.Enabled.Value && PlayerGate.Allow(__instance);
            var bones = new BoneMaps(skeleton, lodded);
            var survey = physics ? new MmdSurvey(model) : null;
            var used = new HashSet<string>();
            List<Grafted> chains = MeasureAll(cfg, bones, survey, used);
            if (physics)
                foreach (MmdBoneMap bm in model.boneMap)
                    if (bones.Leaf.TryGetValue(bm.eft, out Transform rt) && bones.Rest.TryGetValue(bm.eft, out Transform rest))
                        survey.AddEft(bm.eft, rt, rest);
            roots = new List<Transform>();
            foreach (Grafted g in chains)
            {
                g.Apply();
                roots.Add(g.Root);
            }
            if (physics && chains.Count > 0)
                BuildPhysics(__instance, skeleton, (int)part.Key, key, model, survey, chains, used, bones);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"AttachPatch error: {e}");
        }
        finally
        {
            InjectionRegistry.Commit(skeleton, part.Key, roots);
        }
    }

    /// 第三步：布料场景、拟合、这个角色这套模型的 rig，交给新做法的链和腿带动一起交过去
    private static void BuildPhysics(PlayerBody body, Skeleton skeleton, int owner, string key, MmdModel model, MmdSurvey survey,
                                     List<Grafted> chains, HashSet<string> used, BoneMaps bones)
    {
        PhysWorld.Ensure();
        if (!PhysWorld.Ready || !survey.Solve(key))
            return;
        bones.Leaf.TryGetValue("Base HumanPelvis", out Transform pelvis);
        MmdRig rig = RigRegistry.Get(skeleton, model);
        List<ClothSway.Input> sway = SwayInputs(model, chains, owner);
        ClothSway.Body legs = sway.Count > 0 ? SwayBody(pelvis, bones) : null;
        rig.AddPart(owner, survey, used, pelvis ?? chains[0].Attach, key, sway, legs);
#if SORA_DEV
        if (PlayerGate.IsLocal(body))
            TuneUI.Follow(model.Name);          // 调参台跟着你身上穿的这套走（开发版才有调参台，37.29）
#endif
    }

    /// 玩家骨架（按骨名末段索引）和服装 prefab 自带的静止骨架（按名字索引）
    private sealed class BoneMaps
    {
        internal readonly Dictionary<string, Transform> Leaf, Rest;

        internal BoneMaps(Skeleton skeleton, LoddedSkin lodded)
        {
            Leaf = BonePath.LeafIndex(skeleton);
            Rest = SkeletonPatch.BuildLookup(lodded.transform);
        }
    }

    /// 第一步：每条链校验 + 量静止姿态（校验不过的整条不挂）；量好的交给拟合，骨名记进 used
    private static List<Grafted> MeasureAll(PhysicsConfig cfg, BoneMaps bones, MmdSurvey survey, HashSet<string> used)
    {
        var chains = new List<Grafted>();
        foreach (ChainConfig chain in cfg.chains)
        {
            Grafted g = Measure(chain, bones, used);
            if (g == null)
                continue;
            chains.Add(g);
            survey?.AddChain(chain.attachTo, chain.bones, g.Bones);
            foreach (string n in chain.bones)
                used.Add(n);
        }
        return chains;
    }

    /// 一条链的校验 + 静止姿态测量。校验不过返回 null（整条链不挂，和以前一样）。
    private static Grafted Measure(ChainConfig chain, BoneMaps bones, HashSet<string> used)
    {
        Transform[] chainBones = ChainBones(chain, bones, used, out Transform attach);
        if (chainBones == null)
            return null;
        if (!bones.Rest.TryGetValue(chain.attachTo, out Transform attachNode) || attachNode == null)
        {
            LogOnce.Warn($"[mmd] No node named {chain.attachTo} in the outfit prefab, chain skipped");
            return null;
        }
        return new Grafted
        {
            Bones = chainBones,
            Names = chain.bones.ToArray(),
            Root = chainBones[0],
            Attach = attach,
            RestRoot = chainBones[0].position,               // 还没嫁接：骨在服装 prefab 的静止骨架里（新做法定方位用，37.31）
            // 用 prefab 自带的同名骨算绑定姿态的局部位姿；直接保世界变换会把动画姿势烘进去。
            LocalPos = attachNode.InverseTransformPoint(chainBones[0].position),
            LocalRot = Quaternion.Inverse(attachNode.rotation) * chainBones[0].rotation,
        };
    }

    /// 链上的骨（从根到梢）和挂点。没骨 / 骨名被别的链占了 / 挂点不存在 / 骨找不到 / 不是一条直系链 → null（报一次原因）
    private static Transform[] ChainBones(ChainConfig chain, BoneMaps bones, HashSet<string> used, out Transform attach)
    {
        attach = null;
        if (chain.bones == null || chain.bones.Count < 1)
            return Skip($"[mmd] Chain without bones skipped: attachTo={chain.attachTo}", false);
        foreach (string name in chain.bones)
            if (used.Contains(name))
                return Skip($"[mmd] Bone {name} already belongs to another chain, chain on {chain.attachTo} rejected", true);
        if (!bones.Leaf.TryGetValue(chain.attachTo, out attach) || attach == null)
            return Skip($"[mmd] attachTo bone {chain.attachTo} not found, chain skipped", false);
        var result = new Transform[chain.bones.Count];
        for (int i = 0; i < result.Length; i++)
        {
            if (!bones.Leaf.TryGetValue(chain.bones[i], out result[i]) || result[i] == null)
                bones.Rest.TryGetValue(chain.bones[i], out result[i]);
            if (result[i] == null)
                return Skip($"[mmd] Chain bone {chain.bones[i]} not found, chain skipped", false);
        }
        for (int i = 0; i < result.Length - 1; i++)
            if (result[i + 1].parent != result[i])
                return Skip($"[mmd] Chain bone {chain.bones[i + 1]} is not a direct child of {chain.bones[i]}, chain rejected", true);
        return result;
    }

    private static Transform[] Skip(string message, bool error)
    {
        if (error)
            LogOnce.Error(message);
        else
            LogOnce.Warn(message);
        return null;
    }

    /// <summary>
    /// 这个部位里哪些链交给衣服新做法（DEV_NOTES 37.31）：按链的第一根骨名和挂点分部位（头发、兽耳不算），
    /// 这套 tuning.json 里该部位 on 了才交；只有一根骨的链没有「下一节」可追，除了胸部（补虚拟尖端）都照旧走 PMX 物理。
    /// </summary>
    private static List<ClothSway.Input> SwayInputs(MmdModel model, List<Grafted> chains, int owner)
    {
        var list = new List<ClothSway.Input>();
        foreach (Grafted g in chains)
        {
            string at = g.Attach.name;
            SwayKind kind = ClothSway.KindOf(g.Names[0], at);
            // 只有一节骨的链：胸部会在 MmdRig 里拿 PMX 胸部刚体的位置补一个虚拟尖端；别的照旧 PMX
            if (kind == SwayKind.None || !model.Tuning.Part(kind).on || (g.Bones.Length < 2 && kind != SwayKind.Chest))
                continue;
            list.Add(new ClothSway.Input
            {
                Owner = owner, Kind = kind, Bones = g.Bones, Names = g.Names, Attach = g.Attach, RestRoot = g.RestRoot,
                Torso = at.Contains("Pelvis") || at.Contains("Spine"),
            });
        }
        return list;
    }

    /// <summary>
    /// 腿带裙要用的身体：运行时的胯 / 两条大腿，和服装 prefab 静止骨架上量的框架（大腿相对胯的静止朝向、身体的右 / 前 / 上）。
    /// 上 = 脚 → 胯；右 = 左大腿 → 右大腿；前 = 右 × 上，再用脚尖校正正负。缺骨就只给能给的（大腿缺了 = 不跟腿，只跟挂点）。
    /// </summary>
    private static ClothSway.Body SwayBody(Transform pelvis, BoneMaps bones)
    {
        var b = new ClothSway.Body { Pelvis = pelvis };
        bones.Leaf.TryGetValue("Base HumanLThigh1", out b.ThighL);
        bones.Leaf.TryGetValue("Base HumanRThigh1", out b.ThighR);
        b.Root = CharacterRoot(pelvis);
        Dictionary<string, Transform> rest = bones.Rest;
        if (!rest.TryGetValue("Base HumanPelvis", out Transform p) || !rest.TryGetValue("Base HumanLThigh1", out Transform l)
            || !rest.TryGetValue("Base HumanRThigh1", out Transform r) || p == null || l == null || r == null)
        {
            b.ThighL = b.ThighR = null;
            return b;
        }
        b.RestL = Quaternion.Inverse(p.rotation) * l.rotation;
        b.RestR = Quaternion.Inverse(p.rotation) * r.rotation;
        b.Center = (l.position + r.position) * 0.5f;
        if (rest.TryGetValue("Base HumanLFoot", out Transform fl) && rest.TryGetValue("Base HumanRFoot", out Transform fr) && fl != null && fr != null)
            b.Up = (p.position - (fl.position + fr.position) * 0.5f).normalized;
        b.Right = Vector3.ProjectOnPlane(r.position - l.position, b.Up).normalized;
        b.HalfWidth = Mathf.Abs(Vector3.Dot(r.position - l.position, b.Right)) * 0.5f;
        b.Forward = Vector3.Cross(b.Right, b.Up);
        if (rest.TryGetValue("Base HumanLFoot", out Transform foot) && rest.TryGetValue("Base HumanLToe", out Transform toe)
            && foot != null && toe != null && Vector3.Dot(toe.position - foot.position, b.Forward) < 0f)
            b.Forward = -b.Forward;
        return b;
    }

    /// 转身用的「角色根」：从胯往上第一个不叫 Base Human… 的节点 = 整副骨架的容器，跟着角色转身、不带走路时胯的扭动
    private static Transform CharacterRoot(Transform pelvis)
    {
        Transform t = pelvis;
        while (t != null && t.parent != null && t.parent.name.StartsWith("Base Human", StringComparison.Ordinal))
            t = t.parent;
        return t != null ? t.parent ?? t : null;
    }

    private class Grafted
    {
        internal Transform[] Bones;
        internal string[] Names;
        internal Transform Root;
        internal Transform Attach;
        internal Vector3 RestRoot;
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
/// 谁能跑布料物理。口径：**只给真人，不给 bot**；FIKA 里的队友也是真人，一样给
/// （F12「联机时只算自己」开着时不给队友，37.29）。
/// 无头/专用服务端整个关掉（那儿没人看，白烧 CPU）。
/// </summary>
internal static partial class PlayerGate
{
    /// <see cref="IconBodyMarkPatch"/> 在 PlayerIconCreatorModelView.CreateBody() 那一刻登记的身体 ——
    /// 头部选择卡片的离屏渲染专用，天生不该有物理。Dispose 时由 <see cref="DisposePatch"/> 摘除。
    internal static readonly HashSet<PlayerBody> IconBodies = new HashSet<PlayerBody>();

    internal static bool Allow(PlayerBody body)
    {
        if (Plugin.HeadlessDetected || IconBodies.Contains(body))
            return false;
        Player player = body.GetComponentInParent<Player>();
        if (player != null)
        {
            if (IsBot(player))
                return false;
            return !Plugin.OnlySelf.Value || player.IsYourPlayer;     // 本地玩家 + FIKA 里的真人队友（「联机时只算自己」开着时只有自己）
        }
        // 没有 Player 的 PlayerBody = 主菜单 / 藏身处 / 服装页的预览人物，要物理，那是给你看的。
        // 唯一不该有物理的离屏身体（头部卡片）已在出生时被 IconBodies 拦下，这里不用再猜挂法
        //（按祖先 / 按相机猜的两版都栽过：DEV_NOTES 二十六、三十二·补 3~4）。
        return true;
    }

    /// bot 不建物理（开发版里可以有例外，见 OverrideBot）
    private static bool IsBot(Player player)
    {
        bool bot = player.IsAI;
        OverrideBot(player, ref bot);
        return bot;
    }

    /// 开发版的扩展点：实现在 client\Unreleased\（只进开发版）。没有实现时（发布版、公开仓库）编译器连这次调用一起拿掉
    static partial void OverrideBot(Player player, ref bool bot);

#if SORA_DEV
    /// 只有本地玩家和主菜单/藏身处的预览人物才算「你身上穿的」——
    /// FIKA 队友身上的衣服不许去抢调参台的下拉框。
    internal static bool IsLocal(PlayerBody body)
    {
        Player player = body.GetComponentInParent<Player>();
        return player == null || player.IsYourPlayer;
    }
#endif
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
            Plugin.Log.LogError($"IconBodyMarkPatch error: {e}");
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
            Plugin.Log.LogError($"Dispose postfix error: {e}");
        }
    }
}
