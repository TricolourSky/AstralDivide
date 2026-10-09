using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AstralDivide.Client;

/// <summary>
/// 布料专用的**独立物理场景**，外加 MMD 的定步驱动。
///
/// 为什么不直接用游戏的物理世界：布料刚体会撞墙、会被子弹打、会把玩家顶飞，
/// 还得去改游戏全局的碰撞矩阵。Unity 支持建一个只属于自己的 PhysicsScene
/// （`LocalPhysicsMode.Physics3D`），里面的东西和 EFT 的世界**互相看不见**，
/// 而且时间步由我们自己推 —— MMD 是 60Hz 定步，这里照抄，手感才对得上。
///
/// **重力是我们自己加的**，不用 Unity 的：`Physics.gravity` 是全局的，改了会影响整个游戏；
/// 而且我们跑在 PMX 单位里，重力按每套 tuning.json 的值（PMX 单位/s²；MMD 原生 ≈ 98 = 地球重力，界面上的 9.8 内部 ×10，DEV_NOTES 37.31 更正）。
///
/// **浮动原点**：布料世界比真实世界放大 12.5 倍，角色跑到地图边上时坐标会大到掉精度，
/// 所以离原点太远就整体平移一次（整体平移不改变任何物理量）。
/// </summary>
internal static class PhysWorld
{
    /// 定步长。MMD 原生 60Hz；裙板很薄，120 明显减少穿透。
    internal static float Step => 1f / Mathf.Max(30, Plugin.StepHz.Value);

    /// 掉帧时最多补多少步，防止雪崩。按步频等比放大，保证「最多补 1/15 秒」。
    private static int MaxStepsPerFrame => Mathf.Max(4, Plugin.StepHz.Value / 15);

    private static Scene _scene;
    private static PhysicsScene _phys;
    private static bool _ready;
    private static float _acc;
    private static readonly List<MmdRig> _rigs = new List<MmdRig>();
    private static readonly HashSet<int> _slots = new HashSet<int>();

    internal static bool Ready => _ready;

    internal static void Ensure()
    {
        if (_ready && _scene.IsValid())
            return;
        _scene = SceneManager.CreateScene("BDS_Cloth", new CreateSceneParameters(LocalPhysicsMode.Physics3D));
        _phys = _scene.GetPhysicsScene();
        _ready = _scene.IsValid() && _phys.IsValid();
        Plugin.Log.LogInfo(_ready
            ? $"[mmd] 独立物理场景已建立（{Plugin.StepHz.Value}Hz 定步、PMX 单位、和游戏世界互不可见）"
            : "[mmd] 独立物理场景建立失败，本次不建布料物理");
    }

    /// 把一个 GameObject 挪进布料场景。不挪的话它待在游戏世界里，会撞墙。
    internal static void Adopt(GameObject go)
    {
        if (_ready)
            SceneManager.MoveGameObjectToScene(go, _scene);
    }

    internal static void Register(MmdRig r)
    {
        if (!_rigs.Contains(r))
            _rigs.Add(r);
    }

    internal static void Unregister(MmdRig r) => _rigs.Remove(r);

    /// <summary>
    /// 角色在布料世界里的「车位号」。**必须回收** —— 首版是个只增不减的计数器，
    /// 主菜单→仓库→换装→战局每建一次 rig 就 +1，进战局时刚体已经被推到几千单位外。
    /// 布料世界比真实世界大 9.4 倍，坐标一大 float 精度就不够用了（裙板才 0.08 单位厚）。
    /// </summary>
    internal static int TakeSlot()
    {
        for (int i = 0; ; i++)
            if (_slots.Add(i))
                return i;
    }

    internal static void FreeSlot(int slot) => _slots.Remove(slot);



    /// 由 Plugin 在 **LateUpdate** 里调（必须在动画摆完姿势之后，否则读到的是上一帧的骨）。

    /// 调参台改了某套模型的参数 —— 把它推给场上**所有穿着这套衣服**的角色，别人不受影响。
    internal static void ApplyTuning(string model)
    {
        for (int i = 0; i < _rigs.Count; i++)
            if (_rigs[i] != null && _rigs[i].Alive && _rigs[i].Model == model)
                _rigs[i].ApplyTuning();
    }

    /// 调参台那行「这套身上有几条」：场上穿着这套的角色里，这个部位交给新做法的链数（取最多的那个）
    internal static int SwayCount(string model, SwayKind kind)
    {
        int n = 0;
        for (int i = 0; i < _rigs.Count; i++)
            if (_rigs[i] != null && _rigs[i].Alive && _rigs[i].Model == model)
                n = Mathf.Max(n, _rigs[i].SwayCount(kind));
        return n;
    }

    internal static void Tick(float dt)
    {
        if (!_ready || _rigs.Count == 0)
            return;
        // ⚠️ 游戏加载地图会把所有场景（包括我们这个）一起销毁，句柄随之失效。
        // 不每帧查一次就会拿着死句柄 Simulate，每帧抛一次异常刷爆日志。
        if (!_scene.IsValid() || !_phys.IsValid())
        {
            _ready = false;
            _rigs.Clear();
            _acc = 0f;
            Plugin.Log.LogInfo("[mmd] 物理场景随地图卸载了，已复位；换装/重进战局会自动重建");
            return;
        }

        _rigs.RemoveAll(r => r == null || !r.Alive);
        Cull();
        bool anyAwake = false;
        for (int i = 0; i < _rigs.Count; i++)
        {
            _rigs[i].KeepNear();                             // 浮动原点：每个角色各管各的
            _rigs[i].CheckTeleport();
            anyAwake |= _rigs[i].IsAwake;
        }
        // 全场都在计算距离之外（刚体已被 MmdRig.SetAwake 冻住）= 这一帧没有东西可推，整步跳过。
        if (!anyAwake)
        {
            _acc = 0f;
            return;
        }

        float step = Step;
        int cap = MaxStepsPerFrame;
        _acc += Mathf.Min(dt, step * cap);
        int steps = 0;
        while (_acc >= step && steps < cap)
        {
            for (int i = 0; i < _rigs.Count; i++)
                _rigs[i].PreStep();
            _phys.Simulate(step);
            _acc -= step;
            steps++;
        }
        if (steps > 0)
            for (int i = 0; i < _rigs.Count; i++)
                _rigs[i].PostStep();
        // 插值（37.31）：每帧都按「下一步攒了多少」把头发 / 胸摆在最近两次物理结果之间，60Hz 物理在高帧率下也顺
        float alpha = Mathf.Clamp01(_acc / step);
        for (int i = 0; i < _rigs.Count; i++)
            _rigs[i].Interp(alpha);
        // 衣服新做法（37.31）每帧算一次（内部按每步 ≤ 1/60 秒拆，最多 4 步），不跟 PhysX 的 120Hz 步数走
        for (int i = 0; i < _rigs.Count; i++)
            _rigs[i].SwayTick(dt);
    }

    /// <summary>
    /// 距离剔除。
    /// ⚠️ **找不到相机时一律不剔除**：EFT 主菜单没有 tag 为 `MainCamera` 的相机，
    /// 首版拿 `Camera.main == null` 当「没人在看」的兜底，结果**主菜单的裙子头发全不动**
    /// （2026-08-23 实机）。无头客户端已经由 FIKA 反射探测挡掉了，这里不用再兜一层。
    /// </summary>
    private static void Cull()
    {
        Camera cam = Camera.main;
        float far = Plugin.CullRange.Value;
        bool measurable = cam != null && far > 0f;
        for (int i = 0; i < _rigs.Count; i++)
        {
            Transform r = _rigs[i].Reference;
            bool on = !measurable || r == null ||
                      (r.position - cam.transform.position).sqrMagnitude < far * far;
            _rigs[i].SetAwake(on);
        }
    }

    internal static void Shutdown()
    {
        _rigs.Clear();
        if (_ready && _scene.IsValid())
            SceneManager.UnloadSceneAsync(_scene);
        _ready = false;
        _acc = 0f;
    }
}
