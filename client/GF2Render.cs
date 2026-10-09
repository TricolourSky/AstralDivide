using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Visual;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace AstralDivide.Client;

/// <summary>
/// 少前 2 原版渲染（SDK 里的 GF2/Uber 等，DEV_NOTES 37.4 / 37.5）在塔科夫里要插件做的事：
/// ① 每帧把头骨（Base HumanHead）的「世界 → 本地」矩阵按渲染器传给 shader（_GF2HeadW2O，_GF2SpaceOn = 1）：
///    原版脸的 SDF 阴影、鼻梁高光、描边颜色都在头的本地坐标里算，我们网格的根骨是骨盆，所以要单独给；
///    「塔科夫头骨 → 原版头空间」的校正存在材质里（_GF2HeadFix0~2），这里不用管是哪套模型。
/// ② 按原版顺序画模板接力的三个特殊 Pass：所有头发的「头发影子」→ 所有脸的「脸重画」→ 所有头发的「透过刘海看眼睛」。
///    内置管线不会自动画自定义 LightMode 的 Pass（实测），所以给每个相机挂一个命令缓冲（不透明物体画完之后）。
///    那时 Unity 不准备灯光变量：主光 = RenderSettings.sun（没有就取场景里最亮的平行光），通过 _GF2SunDir / _GF2SunColor 给。
/// ③ 塔科夫是延迟渲染，屏幕空间反射、去抖动这些后期效果照着 G-buffer 算；角色走前向、不在 G-buffer 里，
///    那些效果就把她背后物体的数据算到她身上（看着透明）。所以延迟相机的那个命令缓冲在特殊 Pass 之后，
///    再把角色的 GBUFFER / GBUFFER_OUTLINE Pass 画进 G-buffer（改法线，高光 / 光滑度写 0，漫反射见 ⑪；深度、模板不写，她自己的画面不变）。
/// ④ 塔科夫的环境光：ToDController 等每帧调 AmbientLight.SetSH 设全局球谐 _SHAr …，天空反射是 _MyGlobalReflectionProbe。
///    本帧调过（且天空反射在）就设 _GF2EftAmbient = 1，shader 用塔科夫的；否则（主菜单这类没有的地方）退回 Unity 的。
/// ⑤ 设置（DEV_NOTES 37.6 / 37.7 / 37.29）：环境反射 / 环境光 / 主光 / 附加光源的强度倍数、环境光来源、调试开关，每帧给 shader；
///    开发版在 F12「4. 塔科夫光照换算」「5. 主菜单展示」「6. 调试」里拖，发布版没有这些菜单、用 Def* 固定值。
///    每套服装自己的微调（黑丝、胸前塑料）不在这里，见 GF2Tune。
///    进出战局后 5 秒、或者 F12 改了以后，往日志写一行当前的光照数值（对数、调默认值用）。
/// ⑥ 展示灯光（DEV_NOTES 37.7 / 37.9 / 37.11）：不是战局主视角的相机（主菜单、角色资料、选人、加载画面、背包预览 —— 全是塔科夫角色预览面板的相机，
///    HDR 关、画进 8 位贴图）画之前设 _GF2MenuOn = 1：主光 = 一盏中性白光（方向固定在相机左前上方，F12「主菜单白光强度」），
///    环境光 = 少前 2 指挥中心的弱环境光；塔科夫菜单里的灯乘 F12「主菜单附加光源强度」（默认 0 = 不照她）。
///    shader 里再按自制版的思路混：(1 − 跟随) × 材质本色 + 跟随 × 光照，跟随 = Plugin 的「主菜单跟随场景光」，亮部压回 1 以内。
/// ⑦ 半透明件的太阳阴影（DEV_NOTES 37.8 / 37.9）：内置管线不给半透明物体算平行光阴影，按塔科夫 ShadowMaskExtractor 的做法
///    把太阳的屏幕阴影当场拷出来给 shader（GF2Cameras）。
/// ⑧ 深度预写（DEV_NOTES 37.13）：塔科夫在光照前按场景深度算室内压暗图、太阳屏幕阴影，那时前向的她还不在深度里 →
///    这些图在她像素上是她身后的东西（暗处透明透视）。战局相机在 G-buffer 写完时先把她不透明件的深度写进去（RecordDepthPre）。
///    37.14 诊断：每台战局相机画了几个、每个渲染器画没画写日志；F12「调试：深度对照」看深度图里是不是她（37.15 实机全绿）。
///    37.16 / 37.18：同一时刻连 G-buffer 一起写（GBUFFERPRE），光照前读 G-buffer 法线的门窗漏光（室内压暗）按她自己的法线算。
/// ⑨ F12「室内阴影亮度」（37.18）：少前 2 的主光被挡住时还剩色阶暗部的颜色 × 太阳，暗屋子里会发亮；shader 按塔科夫的室内压暗把它压下去，这个值往回拉。
/// ⑩ 半透明件的队列按相机切（37.25）：包里是 2450~2452（37.24，队列 > 2500 不吃任何阴影 → 墙外的灯照穿过来）；
///    主菜单 / 预览面板的相机切回 3500（37.24 以前），不然头纱在不透明段画、混进那张带透明通道的贴图还没处理的清屏底色，空背景前发紫。
/// ⑪ 屋里的灯（37.28）：塔科夫的面光源 / 灯管（AreaLight / TubeLight）在不透明后期之后按 G-buffer 的漫反射色 × 法线给每个像素加光，
///    以前 G-buffer Pass 写的漫反射是 0 → 这些灯照不到她（主要靠它们照明的屋里全黑）。现在战局相机的 G-buffer Pass（③ 和 ⑧ 的）写她的漫反射色
///    × F12「屋内灯光强度」（_GF2AreaAlbedo，按相机在命令缓冲里设；别的相机 0 = 以前的样子）。F12「调试：主光」显示她拿到的主光（查屋里太阳为 0，GF2Diag 写日志）。
/// ⑫ 环境光球谐（37.28 / 37.28c）：GF2Probe 每帧按角色在头骨位置算「光照探针 + 附近『不重要』的灯（按 Unity 折球谐的算法）」写进属性块，
///    shader 在「塔科夫」环境光下 = 这份 + 天空球谐 × 室内压暗（塔科夫自己的东西走延迟渲染，被这些灯逐像素照亮；前向的她 Unity 只给逐顶点 / 球谐）。
/// ⑬ 主光兜底（37.28c）：街区上 Unity 从不把太阳当她的主光，战局相机画之前设好插件的太阳，shader 在 Unity 没给主光时用它（阴影用 ⑦ 拷的屏幕阴影）。
/// 材质的 _GF2Special：1 = 头发（头发影子 + 透过刘海看眼睛），2 = 脸（脸重画）。
/// </summary>
internal static class GF2Render
{
    private static readonly HashSet<string> Shaders = new HashSet<string> { "GF2/Uber", "GF2/UberTrans", "GF2/Eye", "GF2/EyeBlend" };
    private static readonly string[] GBufferPassNames = { "GBUFFER", "GBUFFER_OUTLINE" };
    private static readonly string[] DepthPrePassNames = { "GBUFFERPRE" };       // 光照前的深度 + G-buffer（37.18；不画描边壳）
    private static readonly int SpaceOnId = Shader.PropertyToID("_GF2SpaceOn");
    private static readonly int HeadW2OId = Shader.PropertyToID("_GF2HeadW2O");
    private static readonly int SunDirId = Shader.PropertyToID("_GF2SunDir");
    private static readonly int SunColorId = Shader.PropertyToID("_GF2SunColor");
    private static readonly int EftAmbientId = Shader.PropertyToID("_GF2EftAmbient");
    private static readonly int EftProbeId = Shader.PropertyToID("_MyGlobalReflectionProbe");
    private static readonly int TuneId = Shader.PropertyToID("_GF2Tune");
    private static readonly int TuneOnId = Shader.PropertyToID("_GF2TuneOn");
    private static readonly int DebugSolidId = Shader.PropertyToID("_GF2DebugSolid");
    private static readonly int MenuOnId = Shader.PropertyToID("_GF2MenuOn");
    private static readonly int MenuAmbientId = Shader.PropertyToID("_GF2MenuAmbient");
    private static readonly int MenuAddId = Shader.PropertyToID("_GF2MenuAdd");
    private static readonly int DebugShadowId = Shader.PropertyToID("_GF2DebugShadow");
    private static readonly int DebugDepthId = Shader.PropertyToID("_GF2DebugDepth");
    private static readonly int IndoorShadeId = Shader.PropertyToID("_GF2IndoorShade");
    private static readonly int DebugMainId = Shader.PropertyToID("_GF2DebugMain");
    private static readonly int SunFallbackId = Shader.PropertyToID("_GF2SunFallback");
    private static readonly int AreaAlbedoId = Shader.PropertyToID("_GF2AreaAlbedo");
    private const string SourceEft = "塔科夫", SourceUnity = "Unity";

    // 展示灯光：主光颜色（37.11 起中性白光；之前用过少前 2 指挥中心的线性 (0.72, 1.08, 1.80)，在 LDR 的预览相机里发蓝、截白），
    // 主光在相机坐标里的方向（右 / 上 / 前，指向光，少前 2 指挥中心 main_light 的方向）、环境光（指挥中心探针球谐的常数项）
    private static readonly Color MenuLight = Color.white;
    private static readonly Vector3 MenuLightCam = new Vector3(-0.173f, 0.457f, -0.873f);
    private static readonly Color MenuAmbient = new Color(0.030f, 0.045f, 0.056f);

    // G-buffer 的绑法照塔科夫自己往里画东西时的（延迟贴花、水面）：第四张 HDR 时是相机目标，深度用相机目标的
    private static readonly RenderTargetIdentifier[] GBufferHdr =
    {
        BuiltinRenderTextureType.GBuffer0, BuiltinRenderTextureType.GBuffer1, BuiltinRenderTextureType.GBuffer2, BuiltinRenderTextureType.CameraTarget,
    };
    private static readonly RenderTargetIdentifier[] GBufferLdr =
    {
        BuiltinRenderTextureType.GBuffer0, BuiltinRenderTextureType.GBuffer1, BuiltinRenderTextureType.GBuffer2, BuiltinRenderTextureType.GBuffer3,
    };

    private sealed class Special
    {
        internal Material Mat;
        internal int Sub, Kind, HairShadow, FaceShadow, HairTransE;
    }

    /// 要画进 G-buffer 的一个 Pass：材质、子网格、Pass 序号
    private sealed class GBufferPass
    {
        internal Material Mat;
        internal int Sub, Pass;
    }

    private sealed class Entry
    {
        internal SkinnedMeshRenderer Renderer;
        internal Transform Head;
        internal List<Special> Specials;
        internal List<GBufferPass> GBuffer, DepthPre;
        internal SphericalHarmonicsL2 Probe;        // 本帧头骨位置的环境光球谐（37.28 / 37.28c，GF2Probe）
        internal int Lamps = -1;                    // 折进去的「不重要」灯的盏数；-1 = 没算出来（属性块不写）
        internal float NextHeadSearch;              // 没找到头骨时下次再找的时刻（顺着骨架找比较费，1 秒一次）
    }

    private static readonly List<Entry> Entries = new List<Entry>();
    private static readonly Dictionary<Camera, CommandBuffer> Buffers = new Dictionary<Camera, CommandBuffer>();
    private static readonly Dictionary<Camera, CommandBuffer> PreBuffers = new Dictionary<Camera, CommandBuffer>();   // 深度预写（AfterGBuffer）
    private static readonly Dictionary<Camera, (int Key, float At)> PreLogged = new Dictionary<Camera, (int, float)>();   // 深度预写日志（37.14 诊断）
    private static readonly MaterialPropertyBlock Block = new MaterialPropertyBlock();
    private static readonly Dictionary<Transform, (SphericalHarmonicsL2, int)> ProbeCache = new Dictionary<Transform, (SphericalHarmonicsL2, int)>();   // 本帧每个头骨的环境光球谐
    private const int MenuTransQueue = 3500;                                                  // 非战局相机画半透明件用的队列（37.25）
    private static readonly Dictionary<Material, int> TransQueues = new Dictionary<Material, int>();   // 半透明材质 → 包里的队列（战局相机用）
    private static int _transState = -1;      // 材质上现在是哪一套：1 战局 / 0 其它 / -1 要重新设
    private static Vector4 _sunDir;
    private static Color _sunColor;
    private static Light _sunCache;
    private static float _nextSunSearch;
    private static int _frame = -1, _shFrame = -100;
    private static bool _hooked, _errorLogged;
    private static Material _snapMat;         // 深度对照拍快照用（37.14）
    private static int _snapPass = -1;
#pragma warning disable CS0649     // 开发版才绑；发布版一直是 null，用下面的 Def* 固定值
    private static ConfigEntry<float> _refl, _amb, _main, _add, _menuMain, _menuAmb, _menuAdd, _indoorShade, _areaLight;
    private static ConfigEntry<string> _source;
    private static ConfigEntry<bool> _debugSolid, _debugShadow, _debugDepth, _debugMain;
#pragma warning restore CS0649

    // 发布版没有 4 / 5 / 6 组菜单，用这些值；开发版是菜单的默认值（Tech Leader 2026-10-08 实机定的，DEV_NOTES 37.7；发版前按调好的值定，37.29）
    private const float DefRefl = 0f, DefAmb = 2f, DefMain = 1f, DefAdd = 1f, DefIndoorShade = 0f, DefArea = 1f;
    private const float DefMenuMain = 0.05f, DefMenuAmb = 1f, DefMenuAdd = 0.03f;   // 0.2.0 = Tech Leader 2026-10-10 F12 里调好的值

    private static float V(ConfigEntry<float> e, float def) => e != null ? e.Value : def;

    private static bool On(ConfigEntry<bool> e) => e != null && e.Value;
    private static Light _sunLight;
    private static bool _inWorld;             // 有 GameWorld（战局 / 加载地图 / 藏身处）；哪台相机用战局灯光另按相机分
    private static bool _eftAmbient;          // 本帧塔科夫的环境光在更新、来源选的是塔科夫；只给战局相机用（CameraLight，37.27）
    private static int _inRaid = -1;          // -1 = 还没判断过
    private static float _logAt = -1f;        // 到这个时刻写一行光照日志；-1 = 不写

    /// 开发版 F12「4. 塔科夫光照换算」「5. 主菜单展示」「6. 调试」（DEV_NOTES 37.29；以前都在「GF2 渲染」一组里，值从旧位置搬过来）。
    /// 拖动实时生效，改了以后 0.5 秒（拖完停下来）往日志写一行。发布版不绑，用 Def* 固定值
    internal static void BindConfig(ConfigFile cfg)
    {
#if SORA_DEV
        BindLight(cfg);
        BindMenu(cfg);
        BindDebug(cfg);
        foreach (ConfigEntry<float> e in new[] { _refl, _amb, _main, _add, _menuMain, _menuAmb, _menuAdd, _indoorShade, _areaLight })
            e.SettingChanged += OnTuneChanged;
        _source.SettingChanged += OnTuneChanged;
        foreach (ConfigEntry<bool> e in new[] { _debugSolid, _debugShadow, _debugDepth, _debugMain })
            e.SettingChanged += OnTuneChanged;
#endif
    }

#if SORA_DEV
    private static ConfigEntry<float> Num(ConfigFile cfg, string sec, string key, float def, float max, int order, string help, string name = null) =>
        cfg.Bind(sec, key, def, CfgUtil.Desc(help, order, new AcceptableValueRange<float>(0f, max), name));

    /// 「光照换算」：塔科夫的灯换算给这套 shader 的倍数，所有套装一样（默认值是 Tech Leader 2026-10-08 实机定的，DEV_NOTES 37.7）。
    /// 37.29 第一版在「4. 塔科夫光照换算」，值搬过来
    private static void BindLight(ConfigFile cfg)
    {
        const string sec = CfgUtil.SecLight;
        _amb = Num(cfg, sec, "环境光强度", DefAmb, 2f, 6, "塔科夫的环境光照在她身上的倍数。2（默认）= 和塔科夫自己的物体一样亮");
        _main = Num(cfg, sec, "主光强度", DefMain, 2f, 5, "太阳照在她身上的倍数");
        _add = Num(cfg, sec, "附加光源强度", DefAdd, 2f, 4, "战局里的灯（点光、聚光）照在她身上的倍数");
        _areaLight = Num(cfg, sec, "屋内灯光强度", DefArea, 2f, 3,
            "屋里的面光源 / 灯管（吊灯、灯带这类）照在她身上的倍数。1 = 和塔科夫自己的物体一样，0 = 不照；大于 1 时颜色亮的地方会封顶");
        _indoorShade = Num(cfg, sec, "室内阴影亮度", DefIndoorShade, 1f, 2,
            "屋里太阳照不到的地方：少前 2 的画风会留六七成太阳光。0 = 和塔科夫自己的物体一样压暗，1 = 不压；室外不受影响");
        _refl = Num(cfg, sec, "环境反射强度", DefRefl, 1f, 1, "环境反射（反射图）的强度。0 = 关（塔科夫的天空反射太亮，开着侧面一圈像玻璃）");
        foreach (ConfigEntry<float> e in new[] { _amb, _main, _add, _areaLight, _indoorShade, _refl })
            CfgUtil.Move(cfg, CfgUtil.Numbered[3], e.Definition.Key, e);
    }

    /// 「主菜单展示」的展示灯光三项（DEV_NOTES 37.7 / 37.9 / 37.11）；跟随场景光、挂机秒数在 Plugin.BindMenuExtras。
    /// 显示名去掉「主菜单」前缀（组名已经说了）；37.29 第一版在「5. 主菜单展示」，值搬过来
    private static void BindMenu(ConfigFile cfg)
    {
        const string sec = CfgUtil.SecMenu, where = "主菜单、角色资料、选人、加载画面、背包预览里";
        _menuMain = Num(cfg, sec, "主菜单白光强度", DefMenuMain, 2f, 5, where + "照她的白光（从相机左前上方来）的强度", "白光强度");
        _menuAmb = Num(cfg, sec, "主菜单环境光强度", DefMenuAmb, 4f, 4, where + "的环境光（少前 2 指挥中心的，很弱）的倍数", "环境光强度");
        _menuAdd = Num(cfg, sec, "主菜单附加光源强度", DefMenuAdd, 2f, 3, where + "塔科夫自带的展示灯照她的倍数。0 = 不照", "附加光源强度");
        foreach (ConfigEntry<float> e in new[] { _menuMain, _menuAmb, _menuAdd })
            CfgUtil.Move(cfg, CfgUtil.Numbered[4], e.Definition.Key, e);
    }

    /// 「调试」（37.6 / 37.9 / 37.14 / 37.28）：都是在战局里打开、截图给开发看的。高级项：勾 F12 窗口上方的「Advanced settings」才显示
    private static void BindDebug(ConfigFile cfg)
    {
        const string sec = CfgUtil.SecDebug;
        _source = cfg.Bind(sec, "环境光来源", SourceEft, CfgUtil.Desc(
            "塔科夫 = 和它自己的角色一样（光照探针 + 天空环境光，进屋按室内压暗）；Unity = 只用 Unity 的环境光和反射探针", 5,
            new AcceptableValueList<string>(SourceEft, SourceUnity), advanced: true));
        _debugSolid = cfg.Bind(sec, "画成纯色", false, CfgUtil.Desc(
            "她画成纯品红、不受灯光影响：纯色时还能看到她背后的东西 = 不是光照的问题", 4, advanced: true));
        _debugShadow = cfg.Bind(sec, "显示太阳阴影", false, CfgUtil.Desc(
            "她身上只显示太阳阴影：白 = 被太阳照到，黑 = 在阴影里，品红 = 半透明件没拿到太阳阴影", 3, advanced: true));
        _debugDepth = cfg.Bind(sec, "深度对照", false, CfgUtil.Desc(
            "深度图里存的是谁：绿 = 她自己（正常），红 = 她身后的东西，蓝 = 她前面的东西，灰 = 本来就不参与（半透明件、刘海）", 2, advanced: true));
        _debugMain = cfg.Bind(sec, "主光", false, CfgUtil.Desc(
            "她拿到的主光：暖白 = Unity 给的（颜色就是主光色），青 = 插件补的太阳，橙 = 插件补的但没有太阳阴影，蓝 = 没有主光，" +
            "黄 = 被当成了主菜单 / 预览相机。开着时每秒往日志写一行", 1, advanced: true));
        foreach (ConfigEntryBase e in new ConfigEntryBase[] { _source, _debugSolid, _debugShadow, _debugDepth, _debugMain })
            CfgUtil.Move(cfg, CfgUtil.Numbered[5], e.Definition.Key, e);
    }
#endif

    private static void OnTuneChanged(object sender, EventArgs e) => _logAt = Time.unscaledTime + 0.5f;

    /// 一个部位换上了：登记它里面用 GF2/* shader 的渲染器（同一个渲染器再登记就替换）
    internal static void Register(LoddedSkin lodded)
    {
        foreach (SkinnedMeshRenderer r in lodded.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (!r.sharedMaterials.Any(m => m != null && m.shader != null && Shaders.Contains(m.shader.name)))
                continue;
            Entries.RemoveAll(e => e.Renderer == null || e.Renderer == r);
            Entries.Add(new Entry
            {
                Renderer = r, Head = FindBone(r, "Base HumanHead"), Specials = CollectSpecials(r),
                GBuffer = CollectPasses(r, GBufferPassNames),
                DepthPre = CollectPasses(r, DepthPrePassNames).Where(g => !StencilMasked(g.Mat)).ToList(),
            });
            CollectTransQueues(r);
        }
        if (_hooked || Entries.Count == 0)
            return;
        Camera.onPreCull += OnPreCull;
        _hooked = true;
    }

    private static Transform FindBone(SkinnedMeshRenderer r, string name)
    {
        Transform[] bones = r.bones;
        return bones == null ? null : bones.FirstOrDefault(b => b != null && b.name == name);
    }

    private static List<Special> CollectSpecials(SkinnedMeshRenderer r)
    {
        var list = new List<Special>();
        Material[] mats = r.sharedMaterials;
        for (int i = 0; i < mats.Length; i++)
        {
            Material m = mats[i];
            int kind = m != null && m.HasProperty("_GF2Special") ? Mathf.RoundToInt(m.GetFloat("_GF2Special")) : 0;
            if (kind > 0)
                list.Add(new Special
                {
                    Mat = m, Sub = i, Kind = kind,
                    HairShadow = m.FindPass("HAIRSHADOW"), FaceShadow = m.FindPass("FACESHADOW"), HairTransE = m.FindPass("HAIRTRANSE"),
                });
        }
        return list;
    }

    /// 前向 Pass 带模板比较的材质（刘海：「≠ 眼睛的值」才画）在被挡的像素上实际不画，它的深度预写会把下面先画的眼睛、睫毛挡掉（离线实测），
    /// 所以不预写；刘海就在脸前面几厘米，那些像素的屏幕图按脸算，误差可以忽略（DEV_NOTES 37.13）
    private static bool StencilMasked(Material m) => m.HasProperty("_StencilComp") && m.GetFloat("_StencilComp") != 0f;

    /// 记下这个渲染器里半透明材质（GF2/UberTrans）在包里的队列（每个材质第一次见到时记，往日志写一行）。
    /// 如果它已经是 3500（可能是我们切过之后才复制出来的实例），按同名材质记下的算；都没有就是 3500（没改过队列的包，照旧）
    private static void CollectTransQueues(SkinnedMeshRenderer r)
    {
        foreach (Material m in r.sharedMaterials)
        {
            if (m == null || m.shader == null || m.shader.name != "GF2/UberTrans" || TransQueues.ContainsKey(m))
                continue;
            int q = m.renderQueue;
            if (q == MenuTransQueue)
                q = TransQueues.Where(kv => kv.Key != null && BaseName(kv.Key) == BaseName(m)).Select(kv => kv.Value).DefaultIfEmpty(q).First();
            TransQueues[m] = q;
            _transState = -1;
            Plugin.Log.LogInfo($"[GF2 渲染] 半透明件队列：{m.name} 战局相机 {q}，其它相机 {MenuTransQueue}");
        }
    }

    private static string BaseName(Material m) => m.name.Replace(" (Instance)", "");

    /// 这台相机画之前把半透明件切到它该用的队列：战局相机（主视角、瞄具）用包里的，其它相机用 3500（37.25）。
    /// Camera.onPreCull 里改 renderQueue 对这台相机当场生效（临时 gamma 工程实测，两台相机轮流切）
    private static void SwitchTransQueues(bool raid)
    {
        int state = raid ? 1 : 0;
        if (state == _transState)
            return;
        _transState = state;
        foreach (KeyValuePair<Material, int> kv in TransQueues)
            if (kv.Key != null)
                kv.Key.renderQueue = raid ? kv.Value : MenuTransQueue;
    }

    /// 每个子网格的材质里有这几个名字的 Pass 就记下。G-buffer：GBUFFER / GBUFFER_OUTLINE（GF2/Uber 两个都有，GF2/Eye、GF2/UberTrans 只有前一个，
    /// UberTrans 的是 37.28 加的）；光照前：GBUFFERPRE（三个都有，UberTrans 的是 37.27 加的）
    private static List<GBufferPass> CollectPasses(SkinnedMeshRenderer r, string[] names)
    {
        var list = new List<GBufferPass>();
        Material[] mats = r.sharedMaterials;
        for (int i = 0; i < mats.Length; i++)
            foreach (string name in names)
            {
                int pass = mats[i] != null ? mats[i].FindPass(name) : -1;
                if (pass >= 0)
                    list.Add(new GBufferPass { Mat = mats[i], Sub = i, Pass = pass });
            }
        return list;
    }

    /// 每个相机画之前：本帧第一次先更新头骨矩阵、主光、环境光开关；再按这台相机设灯（战局 / 展示）、太阳阴影，把它的命令缓冲重新录一遍
    private static void OnPreCull(Camera cam)
    {
        try
        {
            if (cam.cameraType != CameraType.Game)
                return;
            if (_frame != Time.frameCount)
            {
                _frame = Time.frameCount;
                NewFrame();
            }
            bool raid = GF2Cameras.RaidWorld(cam);
            SwitchTransQueues(raid);
            GF2Cameras.RecordSun(cam, raid, DepthSnapMat(), _snapPass);
            CameraLight(cam, raid, out Vector4 sunDir, out Color sunColor);
            if (!Buffers.TryGetValue(cam, out CommandBuffer cb))
            {
                if (Entries.Count == 0)
                    return;
                cb = new CommandBuffer { name = "AstralDivide GF2" };
                cam.AddCommandBuffer(CameraEvent.AfterForwardOpaque, cb);
                Buffers[cam] = cb;
                GF2Cameras.Log(cam, raid, !raid && GF2Cameras.Preview(cam));
            }
            cb.Clear();
            Record(cb, cam, sunDir, sunColor);
            if (cam.actualRenderingPath == RenderingPath.DeferredShading)
                RecordGBuffer(cb, cam, raid);
            RecordDepthPre(cam, raid);
        }
        catch (Exception e)
        {
            if (!_errorLogged)
                Plugin.Log.LogError($"GF2Render 异常（只报一次）: {e}");
            _errorLogged = true;
        }
    }

    private static void NewFrame()
    {
        Entries.RemoveAll(e => e.Renderer == null);
        GF2Probe.Rescan();
        ProbeCache.Clear();
        foreach (Entry e in Entries)
            UpdateEntry(e);
        UpdateSun();
#if SORA_DEV
        GF2Diag.Hook(On(_debugMain));
#endif
        _inWorld = Singleton<GameWorld>.Instantiated;
        GF2Cameras.HookSun(_inWorld);                         // 不在战局时摘掉，不往塔科夫的预览灯上挂东西
        ApplyTuning();
        MaybeLog();
        if (Time.frameCount % 300 != 0)
            return;
        foreach (Camera dead in Buffers.Keys.Where(c => c == null).ToList())
            Buffers.Remove(dead);
        foreach (Camera dead in PreBuffers.Keys.Where(c => c == null).ToList())
            PreBuffers.Remove(dead);
        foreach (Camera dead in PreLogged.Keys.Where(c => c == null).ToList())
            PreLogged.Remove(dead);
        foreach (Material dead in TransQueues.Keys.Where(m => m == null).ToList())
            TransQueues.Remove(dead);
        GF2Cameras.Cleanup();
    }

    /// 一个渲染器每帧的属性块：头骨的「世界 → 本地」矩阵（①）、环境光球谐（⑫ / 37.28c：同一个角色按头骨位置本帧只算一次）
    private static void UpdateEntry(Entry e)
    {
        if (e.Head == null && Time.unscaledTime >= e.NextHeadSearch)
        {
            e.Head = FindHead(e.Renderer);                  // SetSkin 那一刻骨可能还没接上，之后再找
            e.NextHeadSearch = Time.unscaledTime + 1f;
        }
        if (e.Head == null)
            return;
        e.Renderer.GetPropertyBlock(Block);
        Block.SetFloat(SpaceOnId, 1f);
        Block.SetMatrix(HeadW2OId, e.Head.worldToLocalMatrix);
        if (!ProbeCache.TryGetValue(e.Head, out (SphericalHarmonicsL2 Sh, int Lamps) p))
        {
            p.Lamps = GF2Probe.Sample(e.Head.position, e.Renderer, e.Renderer.gameObject.layer, out p.Sh);
            ProbeCache[e.Head] = p;
        }
        e.Probe = p.Sh;
        e.Lamps = p.Lamps;
        if (p.Lamps >= 0)
            GF2Probe.Write(Block, p.Sh);
        e.Renderer.SetPropertyBlock(Block);
    }

    /// 头骨：先在渲染器自己的骨里找；手部这类网格的骨里没有头（37.28c），就从根骨往上一层层找，找到含头骨的那层为止（同一个角色的骨架，最多 6 层）
    private static Transform FindHead(SkinnedMeshRenderer r)
    {
        Transform head = FindBone(r, "Base HumanHead");
        Transform t = r.rootBone;
        for (int depth = 0; head == null && t != null && depth < 6; depth++, t = t.parent)
            head = t.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == "Base HumanHead");
        return head;
    }

    /// 塔科夫的环境光在不在更新：本帧（或上一帧）调过 AmbientLight.SetSH，而且天空反射图在
    private static bool EftAmbientLive() => Time.frameCount - _shFrame <= 1 && Shader.GetGlobalTexture(EftProbeId) != null;

    /// AmbientLight.SetSH 被调了（GF2AmbientPatch）
    internal static void MarkEftAmbient() => _shFrame = Time.frameCount;

    /// 调节 → shader 全局（每帧一次；开发版取 F12，发布版取 Def* 固定值）；用塔科夫的环境光 = 它在更新、而且来源选的是塔科夫。
    /// 展示灯光那几样按相机设，见 CameraLight
    private static void ApplyTuning()
    {
        Shader.SetGlobalFloat(TuneOnId, 1f);
        Shader.SetGlobalVector(TuneId, new Vector4(V(_refl, DefRefl), V(_amb, DefAmb), V(_main, DefMain), V(_add, DefAdd)));
        Shader.SetGlobalFloat(DebugSolidId, On(_debugSolid) ? 1f : 0f);
        Shader.SetGlobalFloat(DebugShadowId, On(_debugShadow) ? 1f : 0f);
        Shader.SetGlobalFloat(DebugDepthId, On(_debugDepth) ? 1f : 0f);
        Shader.SetGlobalFloat(DebugMainId, On(_debugMain) ? 1f : 0f);
        Shader.SetGlobalFloat(IndoorShadeId, V(_indoorShade, DefIndoorShade));
        _eftAmbient = EftAmbientLive() && (_source == null || _source.Value == SourceEft);     // _GF2EftAmbient 按相机设（CameraLight）
        Shader.SetGlobalFloat(MenuAddId, V(_menuAdd, DefMenuAdd));
    }

    /// 这台相机画她时的灯，立刻设成全局（她的主体 Pass 在命令缓冲之前就画了，这台相机开画前就得是它的）。返回主光（指向光的方向、gamma 颜色）。
    /// 战局相机：_GF2MenuOn = 0，主光 = 太阳（UpdateSun 找的）。其它相机 = 展示灯光：方向跟着相机，
    /// 颜色 = 白光 × F12 主菜单白光强度、转 gamma（shader 再转回线性）；环境光 = 少前 2 的 × F12 主菜单环境光强度（线性，用向量传）
    /// _GF2EftAmbient 也按相机（37.27）：塔科夫的室内压暗图 _StencilShadow 是战局主视角相机的，战局里打开背包时预览面板的相机
    /// 拿自己的屏幕位置去读，读到的是主视角画面里的东西 → 刘海在脸上的影子（脸重画乘室内亮度）被压得发黑。展示相机一律不用。
    /// 主光兜底（37.28c）：战局相机也立刻设 _GF2SunDir / _GF2SunColor 和 _GF2SunFallback = 1（找到了太阳），Unity 没给主光时主体 Pass 用它们
    private static void CameraLight(Camera cam, bool raid, out Vector4 dir, out Color color)
    {
        Shader.SetGlobalFloat(MenuOnId, raid ? 0f : 1f);
        Shader.SetGlobalFloat(EftAmbientId, raid && _eftAmbient ? 1f : 0f);
        Shader.SetGlobalFloat(SunFallbackId, raid && _sunColor.maxColorComponent > 0.001f ? 1f : 0f);
        if (raid)
        {
            dir = _sunDir;
            color = _sunColor;
            Shader.SetGlobalVector(SunDirId, dir);      // 37.28c：主光兜底在主体 Pass 里就要用（以前只在命令缓冲里设，给特殊 Pass）
            Shader.SetGlobalColor(SunColorId, color);
            return;
        }
        Transform t = cam.transform;
        Vector3 d = (t.right * MenuLightCam.x + t.up * MenuLightCam.y + t.forward * MenuLightCam.z).normalized;
        dir = new Vector4(d.x, d.y, d.z, 0f);
        float m = V(_menuMain, DefMenuMain);
        color = new Color(ToGamma(MenuLight.r * m), ToGamma(MenuLight.g * m), ToGamma(MenuLight.b * m), 1f);
        Shader.SetGlobalVector(SunDirId, dir);
        Shader.SetGlobalColor(SunColorId, color);
        Shader.SetGlobalVector(MenuAmbientId, MenuAmbient * V(_menuAmb, DefMenuAmb));
    }

    /// shader 里 GF2_ToLinear 的反函数（大于 1 也走同一条曲线）
    private static float ToGamma(float v) => v <= 0.0031308f ? v * 12.92f : 1.055f * Mathf.Pow(v, 1f / 2.4f) - 0.055f;

    /// 进出战局后 5 秒（等光照稳定）、或者 F12 改完 0.5 秒后，写一行光照日志
    private static void MaybeLog()
    {
        int inRaid = _inWorld ? 1 : 0;
        if (inRaid != _inRaid)
        {
            _inRaid = inRaid;
            _logAt = Time.unscaledTime + 5f;
        }
        if (_logAt < 0f || Time.unscaledTime < _logAt)
            return;
        _logAt = -1f;
        LogLighting(inRaid == 1);
    }

    /// 一行：塔科夫的环境光（球谐在上 / 水平 / 下三个方向的值，线性）、Unity 的环境光、太阳、天空反射图、F12 当前的调节
    private static void LogLighting(bool inRaid)
    {
        string eft = EftAmbientLive() ? $"在更新 上 {Fmt(EftSH(Vector3.up))} 水平 {Fmt(EftSH(Vector3.forward))} 下 {Fmt(EftSH(Vector3.down))}" : "没有";
        string unity = $"上 {Fmt(UnitySH(Vector3.up))} 水平 {Fmt(UnitySH(Vector3.forward))} 下 {Fmt(UnitySH(Vector3.down))}（{RenderSettings.ambientMode}）";
        string sun = _sunLight != null ? $"{_sunLight.name} 颜色 {Fmt(_sunLight.color)} 强度 {_sunLight.intensity:0.00}" : "没有";
        Texture probe = Shader.GetGlobalTexture(EftProbeId);
        bool debug = On(_debugSolid) || On(_debugShadow) || On(_debugDepth) || On(_debugMain);
        string tune = $"反射 {V(_refl, DefRefl):0.00} 环境光 {V(_amb, DefAmb):0.00} 主光 {V(_main, DefMain):0.00} 附加光 {V(_add, DefAdd):0.00} " +
                      $"屋内灯光 {AreaScale():0.00} 室内阴影亮度 {V(_indoorShade, DefIndoorShade):0.00} 来源 {(_source != null ? _source.Value : SourceEft)} " +
                      $"主菜单白光 {V(_menuMain, DefMenuMain):0.00} 主菜单环境光 {V(_menuAmb, DefMenuAmb):0.00} 主菜单附加光 {V(_menuAdd, DefMenuAdd):0.00} " +
                      $"主菜单跟随场景光 {Plugin.MenuFollow:0.00}{(debug ? " 调试开着" : "")}{(_refl == null ? "（发布版固定值）" : "")}";
        Plugin.Log.LogInfo($"[GF2 渲染] {(inRaid ? "有 GameWorld（战局 / 加载地图）" : "主菜单")} | 塔科夫环境光 {eft} | Unity 环境光 {unity} | 太阳 {sun} | " +
                           $"天空反射图 {(probe != null ? $"{probe.width}×{probe.height}" : "没有")} | 调节 {tune}");
    }

    /// 主光调试日志（37.28，GF2Diag）：她每个渲染器（名字、层、本帧画没画、Unity 用不用光照探针）、头骨位置的光照探针、
    /// 塔科夫天空球谐（没乘室内压暗），都是上 / 水平 / 下三个方向的线性值；还有场景里开着的平行光
    internal static string DescribeForDiag()
    {
        string list = string.Join("、", Entries.Where(e => e.Renderer != null).Select(e =>
            $"{e.Renderer.name}（层 {e.Renderer.gameObject.layer}，{(e.Renderer.isVisible ? "画了" : "没画")}，头骨 {(e.Head != null ? "有" : "没有")}）"));
        Entry first = Entries.FirstOrDefault(e => e.Renderer != null && e.Lamps >= 0 && e.Renderer.isVisible);
        string probe = first == null ? "没有" : $"（折进 {first.Lamps} 盏不重要的灯）上 {Fmt(GF2Probe.Eval(first.Probe, Vector3.up))} " +
                                                $"水平 {Fmt(GF2Probe.Eval(first.Probe, Vector3.forward))} 下 {Fmt(GF2Probe.Eval(first.Probe, Vector3.down))}";
        string sky = $"上 {Fmt(EftSH(Vector3.up))} 水平 {Fmt(EftSH(Vector3.forward))} 下 {Fmt(EftSH(Vector3.down))}";
        string lights = first == null ? "" : GF2Probe.DescribeLights(first.Head.position);
        return $"她：{list} | 头骨处环境光 {probe} | 塔科夫天空球谐 {sky} | 主光兜底 {Shader.GetGlobalFloat(SunFallbackId):0} | " +
               $"附近的灯：{lights} | 平行光：{DirectionalLights()}";
    }

    /// 塔科夫的球谐在方向 n 上的值（和 shader 里同一个算法），线性
    private static Vector3 EftSH(Vector3 n)
    {
        var a = new Vector4(n.x, n.y, n.z, 1f);
        var b = new Vector4(n.x * n.y, n.y * n.z, n.z * n.z, n.z * n.x);
        float c = n.x * n.x - n.y * n.y;
        Vector4 shc = Shader.GetGlobalVector("_SHC");
        return new Vector3(
            Vector4.Dot(Shader.GetGlobalVector("_SHAr"), a) + Vector4.Dot(Shader.GetGlobalVector("_SHBr"), b) + shc.x * c,
            Vector4.Dot(Shader.GetGlobalVector("_SHAg"), a) + Vector4.Dot(Shader.GetGlobalVector("_SHBg"), b) + shc.y * c,
            Vector4.Dot(Shader.GetGlobalVector("_SHAb"), a) + Vector4.Dot(Shader.GetGlobalVector("_SHBb"), b) + shc.z * c);
    }

    private static Vector3 UnitySH(Vector3 n)
    {
        var c = new Color[1];
        RenderSettings.ambientProbe.Evaluate(new[] { n }, c);
        return new Vector3(c[0].r, c[0].g, c[0].b);
    }

    private static string Fmt(Vector3 v) => $"({v.x:0.00},{v.y:0.00},{v.z:0.00})";

    private static string Fmt(Color c) => $"({c.r:0.00},{c.g:0.00},{c.b:0.00})";

    /// 原版顺序：所有头发的头发影子 → 所有脸的脸重画 → 所有头发的透过刘海看眼睛（主光 = MainLight 给这个相机算的那盏）
    private static void Record(CommandBuffer cb, Camera cam, Vector4 sunDir, Color sunColor)
    {
        cb.SetGlobalVector(SunDirId, sunDir);
        cb.SetGlobalColor(SunColorId, sunColor);
        for (int step = 0; step < 3; step++)
            foreach (Entry e in Entries)
            {
                if (!Drawable(e.Renderer, cam))
                    continue;
                foreach (Special s in e.Specials)
                {
                    int pass = step == 0 && s.Kind == 1 ? s.HairShadow : step == 1 && s.Kind == 2 ? s.FaceShadow : step == 2 && s.Kind == 1 ? s.HairTransE : -1;
                    if (pass >= 0)
                        cb.DrawRenderer(e.Renderer, s.Mat, s.Sub, pass);
                }
            }
    }

    /// G-buffer（接在特殊 Pass 后面）：按塔科夫的绑法绑好 G-buffer，画每个渲染器的 G-buffer Pass，
    /// 画完换回相机目标（同一时刻后面别人的命令缓冲照常往相机目标上画）。
    /// 漫反射色的倍数（37.28）：战局相机 = F12「屋内灯光强度」（塔科夫的面光源 / 灯管照她），别的相机 0（和以前一样写 0）
    private static void RecordGBuffer(CommandBuffer cb, Camera cam, bool raid)
    {
        cb.SetGlobalFloat(AreaAlbedoId, raid ? AreaScale() : 0f);
        cb.SetRenderTarget(cam.allowHDR ? GBufferHdr : GBufferLdr, BuiltinRenderTextureType.CameraTarget);
        foreach (Entry e in Entries)
        {
            if (!Drawable(e.Renderer, cam))
                continue;
            foreach (GBufferPass g in e.GBuffer)
                cb.DrawRenderer(e.Renderer, g.Mat, g.Sub, g.Pass);
        }
        cb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
    }

    /// 光照前的深度 + G-buffer（DEV_NOTES 37.13 / 37.16 / 37.18）：战局相机、延迟渲染时，G-buffer 写完那一刻（AfterGBuffer）按塔科夫的 G-buffer 绑法
    /// 画她不透明件的 GBUFFERPRE：写深度（往相机外推 1 厘米，37.19）+ 法线、漫反射 / 高光 0。塔科夫之后按深度算的室内压暗图、太阳屏幕阴影、AO、雾、体积光，
    /// 和按 G-buffer 法线算的门窗漏光（AnalyticSource）就都按她自己算，不再是她身后的东西。写深度 → 每个像素只留最前面那层的法线
    /// （37.16 只比不写时嘴唇后面的牙齿盖掉了嘴唇的法线）。描边壳、刘海不画。别的相机（主菜单、预览面板）录成空的，画面不动
    private static void RecordDepthPre(Camera cam, bool raid)
    {
        if (!PreBuffers.TryGetValue(cam, out CommandBuffer pre))
        {
            pre = new CommandBuffer { name = "AstralDivide GF2 深度预写" };
            cam.AddCommandBuffer(CameraEvent.AfterGBuffer, pre);
            PreBuffers[cam] = pre;
        }
        pre.Clear();
        if (!raid || cam.actualRenderingPath != RenderingPath.DeferredShading)
            return;
        pre.SetGlobalFloat(AreaAlbedoId, AreaScale());      // 37.28：被半透明件挡住的地方，灯照的是这一遍写的漫反射色
        pre.SetRenderTarget(cam.allowHDR ? GBufferHdr : GBufferLdr, BuiltinRenderTextureType.CameraTarget);
        LogDepthPre(cam, DrawPasses(pre, cam, e => e.DepthPre));
    }

    /// 「屋内灯光强度」（37.28）；发布版用固定值
    private static float AreaScale() => V(_areaLight, DefArea);

    /// 把每个这台相机会画的渲染器的某组 Pass 画进命令缓冲，返回 Pass 数
    private static int DrawPasses(CommandBuffer cb, Camera cam, Func<Entry, List<GBufferPass>> pick)
    {
        int passes = 0;
        foreach (Entry e in Entries)
        {
            if (!Drawable(e.Renderer, cam))
                continue;
            foreach (GBufferPass g in pick(e))
                cb.DrawRenderer(e.Renderer, g.Mat, g.Sub, g.Pass);
            passes += pick(e).Count;
        }
        return passes;
    }

    /// 诊断（DEV_NOTES 37.14）：这台战局相机的深度预写画了几个 Pass、每个渲染器画没画（不画的原因）。
    /// 数目变了才写（换装、换 LOD 会变），同一台相机最多 2 秒一行
    private static void LogDepthPre(Camera cam, int passes)
    {
        int key = passes * 1000 + Entries.Count;
        if (PreLogged.TryGetValue(cam, out var last) && (last.Key == key || Time.unscaledTime < last.At + 2f))
            return;
        PreLogged[cam] = (key, Time.unscaledTime);
        string list = string.Join("、", Entries.Select(e => DescribePre(e, cam)));
        Plugin.Log.LogInfo($"[GF2 渲染] 深度预写：{cam.name} 画 {passes} 个 Pass | 登记的渲染器 {Entries.Count} 个：{list} | 平行光：{DirectionalLights()}");
    }

    /// 一个渲染器在这台相机的深度预写里：名字（画 / 不画的原因、本帧有没有被任何相机画、有几个 DEPTHPRE、接不接收阴影、在哪层）
    private static string DescribePre(Entry e, Camera cam)
    {
        SkinnedMeshRenderer r = e.Renderer;
        if (r == null)
            return "（已销毁）";
        string why = !r.enabled ? "没启用" : !r.gameObject.activeInHierarchy ? "没激活" :
            r.shadowCastingMode == ShadowCastingMode.ShadowsOnly ? "只投影" :
            (cam.cullingMask & (1 << r.gameObject.layer)) == 0 ? "不在相机的层里" : "画";
        return $"{r.name}（{why}{(r.isVisible ? "" : "，本帧没被画")}，GBUFFERPRE {e.DepthPre.Count}，" +
               $"接收阴影 {(r.receiveShadows ? "是" : "否")}，投影 {r.shadowCastingMode}，层 {r.gameObject.layer}）";
    }

    /// 场景里开着的平行光（Unity 挑最亮的那盏当她前向 Pass 的主光，只有它的阴影会给她；37.14 诊断）
    private static string DirectionalLights() => string.Join("、", UnityEngine.Object.FindObjectsOfType<Light>()
        .Where(l => l.type == LightType.Directional && l.isActiveAndEnabled)
        .Select(l => $"{l.name}（强度 {l.intensity:0.00} 颜色 {Fmt(l.color)} 阴影 {l.shadows} 层 0x{l.cullingMask:X8} {l.renderMode}）"));

    /// 深度对照（37.14）开着时拍快照用的材质：GF2/Uber 的一个新实例（不动她自己的材质），用它的 DEPTHSNAP Pass。关着、或者还没有 GF2/Uber 时是空
    private static Material DepthSnapMat()
    {
        if (!On(_debugDepth))
            return null;
        if (_snapMat != null)
            return _snapMat;
        Material src = Entries.Where(e => e.Renderer != null).SelectMany(e => e.Renderer.sharedMaterials)
            .FirstOrDefault(m => m != null && m.FindPass("DEPTHSNAP") >= 0);
        if (src == null)
            return null;
        _snapMat = new Material(src.shader) { name = "AstralDivide GF2 深度快照" };
        _snapPass = _snapMat.FindPass("DEPTHSNAP");
        return _snapMat;
    }

    /// 这个相机会不会画这个渲染器：启用、激活、不是只投影（第一人称时自己的头就是只投影）、在相机的层里
    private static bool Drawable(SkinnedMeshRenderer r, Camera cam) =>
        r != null && r.enabled && r.gameObject.activeInHierarchy && r.shadowCastingMode != ShadowCastingMode.ShadowsOnly &&
        (cam.cullingMask & (1 << r.gameObject.layer)) != 0;

    /// 主光：游戏设的 sun；没有（主菜单 / 藏身处可能没有）就每 2 秒找一次场景里最亮的平行光。颜色照内置管线 gamma 工程的 _LightColor0 = 颜色 × 强度
    private static void UpdateSun()
    {
        Light sun = RenderSettings.sun;
        if (sun == null || !sun.isActiveAndEnabled)
        {
            if (Time.unscaledTime >= _nextSunSearch)
            {
                _nextSunSearch = Time.unscaledTime + 2f;
                _sunCache = UnityEngine.Object.FindObjectsOfType<Light>()
                    .Where(l => l.type == LightType.Directional && l.isActiveAndEnabled)
                    .OrderByDescending(l => l.intensity).FirstOrDefault();
            }
            sun = _sunCache;
        }
        if (sun == null || !sun.isActiveAndEnabled)
        {
            _sunLight = null;
            _sunDir = new Vector4(0f, 1f, 0f, 0f);
            _sunColor = Color.black;
            return;
        }
        _sunLight = sun;
        Vector3 d = -sun.transform.forward;
        _sunDir = new Vector4(d.x, d.y, d.z, 0f);
        _sunColor = sun.color * sun.intensity;
    }
}

/// 塔科夫每帧给环境光设球谐（ToDController / TODSkySimple → AmbientLight.SetSH）：记下帧号，GF2Render 据此判断它在不在更新
[HarmonyPatch(typeof(AmbientLight), nameof(AmbientLight.SetSH))]
internal static class GF2AmbientPatch
{
    [HarmonyPostfix]
    private static void Postfix() => GF2Render.MarkEftAmbient();
}

/// 换上部位时登记 GF2 渲染器（和 BlinkPatch / AttachPatch 一样挂在 SetSkin 后面）
[HarmonyPatch(typeof(PlayerBody), nameof(PlayerBody.SetSkin))]
internal static class GF2SkinPatch
{
    [HarmonyPostfix]
    private static void Postfix(PlayerBody __instance, KeyValuePair<EBodyModelPart, ResourceKey> part)
    {
        try
        {
            if (Plugin.HeadlessDetected)
                return;
            if (__instance.BodySkins.TryGetValue(part.Key, out LoddedSkin lodded) && lodded != null)
            {
                GF2Render.Register(lodded);
                GF2Tune.OnSkin(__instance, part, lodded);           // 这套的微调表套到材质上（37.29）
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"GF2SkinPatch 异常: {e}");
        }
    }
}
