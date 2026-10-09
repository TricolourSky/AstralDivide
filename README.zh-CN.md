# AstralDivide

[English](README.md) | **简体中文**

AstralDivide 的源代码。这是一个 [SPT](https://sp-tarkov.com/) 4.1.x 的模组。

SORA，一位来自 Astral Divide 的狐耳商人，带着七套「像在 MMD 里那样会动」的二次元服装、一间自己的商店，还有一段讲她怎么来到塔科夫的剧情章节。

> **0.2.0 是测试版。** 数值和细节还可能调整。

## 模组做了什么

- **新商人 SORA。** 卖服装，有一个每次补货都会重抽的整枪货架，提供保险和修理，共四档忠诚等级。
- **7 套服装和 7 个头部**，少女前线 2：追放的画风，每套都有上衣、下装和配套的第一人称手臂。
- **布料物理。** 头发、裙子、外套、飘带、尾巴，都按 MMD 原模型的物理数据实时计算：模型里有一个刚体就建一个刚体、有一条关节就建一条关节，跑在一个独立的物理场景里。
- **剧情章节「初次接触」。** 16 个任务，靠战局里的碰面、藏身处里的无线电通话和带插画的对话来推进。需要 [VisitAPI](https://github.com/TricolourSky/VisitAPI)。
- **中英双语。**

## 仓库里有什么

| 路径 | 是什么 | 编出来的文件 |
|---|---|---|
| `src/` | SPT 服务端模组（.NET 10）：注册商人、货架、服装和头部 | `AstralDivide.dll` |
| `client/` | BepInEx 客户端插件（.NET Framework 4.7.2）：布料物理、服装缩略图、F12 调参台 | `AstralDivide.Client.dll` |
| `LICENSE`、`NOTICE.txt` | 许可证文本 | |

发布包里的两个 DLL 就是用这份代码编译的。

**仓库里没有的：** 模型资源包（bundle）、商人数据、每套模型的物理数据、服装缩略图、剧情章节。这些不是代码，随发布包一起发。用这个仓库编译，只会得到两个 DLL。

## 安装

玩家不需要自己编译。下载发布包，解压到 SPT 游戏目录，也就是有 `EscapeFromTarkov.exe` 的那个文件夹。

依赖：

| 模组 | 版本 | 用途 |
|---|---|---|
| SPT | 4.1.x | |
| WTT-CommonLib | 3.0.x | 注册服装、头部和资源包。没有它，服务端不会加载 AstralDivide。 |
| [VisitAPI](https://github.com/TricolourSky/VisitAPI) | [1.3.5](https://github.com/TricolourSky/VisitAPI/releases/download/V1.3.5/VisitAPI-1.3.5.zip) 或更新 | 运行剧情章节。没有它，SORA 一直是锁着的。 |
| Black Division | 当前版本 | 可选。提供一场夜间遭遇战的敌人。 |

## 从源码编译

需要：

- .NET 10 SDK；
- .NET Framework 4.7.2 目标包（Developer Pack），客户端插件要用；
- 一份 SPT 4.1.x。客户端工程引用的是它里面游戏和 BepInEx 的程序集，仓库里不含任何游戏文件。

服务端工程编译时不用这份安装：它按官方 NuGet 包 `SPTarkov.*` 4.1.0 和 `WTT-ServerCommonLib` 3.0.0 编译，也就是本模组支持的最低版本，这样编出来的 DLL 在所有 4.1.x 服务端上都能加载。`SptDir` 只决定编完装到哪里。

下面的 `<SPT>` 指你的 SPT 游戏目录，也就是有 `EscapeFromTarkov.exe` 的那个文件夹。

```
dotnet build client\BinaryDimensionStore.Client.csproj -c Release -p:EftDir=<SPT>
dotnet build src\BinaryDimensionStore.csproj           -c Release -p:SptDir=<SPT>\SPT_Runtime
```

编出来的 DLL 在 `client\bin\Release\net472\` 和 `src\bin\Release\net10.0\`。

**`dotnet build` 编完会顺手安装。** 它会把 DLL、`LICENSE` 和 `NOTICE.txt` 拷进 `<SPT>\BepInEx\plugins\AstralDivide\`（客户端）和 `<SPT>\SPT_Runtime\user\mods\AstralDivide\`（服务端）。先关掉游戏和服务端，否则 DLL 被占用，拷贝会失败。

只编译、不动 SPT 目录：

```
dotnet restore client\BinaryDimensionStore.Client.csproj -p:EftDir=<SPT>
dotnet msbuild client\BinaryDimensionStore.Client.csproj -t:CoreBuild -p:Configuration=Release -p:EftDir=<SPT>

dotnet restore src\BinaryDimensionStore.csproj -p:SptDir=<SPT>\SPT_Runtime
dotnet msbuild src\BinaryDimensionStore.csproj -t:CoreBuild -p:Configuration=Release -p:SptDir=<SPT>\SPT_Runtime
```

工程文件的名字还是模组早期的工作名 BinaryDimensionStore，编出来的程序集叫 AstralDivide。

## 许可

- **代码：** GNU 通用公共许可证第 3 版（GPL-3.0），见 [LICENSE](LICENSE)。
- **作者自己制作的图片和剧情**（商人头像、剧情场景背景和视频、章节横幅和图标；对话剧本、任务文件及其文本）：© TricolourSky，CC BY-NC-ND 4.0。
- **角色模型和服装缩略图：** 出自少女前线 2：追放，权利归其权利人（散爆网络）所有，不适用以上两种许可。

细节见 [NOTICE.txt](NOTICE.txt)。

AstralDivide 是非商业的同人作品，与散爆网络、Battlestate Games、SPT 团队均无关联。
