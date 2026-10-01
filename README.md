# AstralDivide

**English** | [简体中文](README.zh-CN.md)

Source code of AstralDivide, a mod for [SPT](https://sp-tarkov.com/) 4.1.x.

SORA, a fox-eared merchant from the Astral Divide, arrives in Tarkov with seven anime outfits that move the way they do in MMD, a shop of her own, and a story chapter about how she got here.

> **0.1.0 is a test release.** Numbers and details may still change.

## What the mod does

- **A new trader, SORA.** She sells the outfits, keeps a gun shelf that is re-rolled on every restock, insures and repairs your gear, and has four loyalty levels.
- **7 outfit sets and 7 heads** in the style of Girls' Frontline 2: Exilium, each with a top, a bottom and matching first-person arms.
- **Cloth physics.** Hair, skirts, coats, ribbons and tails are simulated from the physics data of the original MMD models: one rigid body per rigid body in the model, one joint per joint, in a physics scene of its own.
- **A story chapter, "First Contact".** 16 quests told through encounters in raid, radio calls from the hideout and illustrated dialogue scenes. It needs [VisitAPI](https://github.com/TricolourSky/VisitAPI).
- **English and Simplified Chinese.**

## What is in this repository

| Path | What it is | Builds |
|---|---|---|
| `src/` | The SPT server mod (.NET 10): registers the trader, her stock, the clothing and the heads | `AstralDivide.dll` |
| `client/` | The BepInEx client plugin (.NET Framework 4.7.2): cloth physics, clothing thumbnails, the F12 tuning console | `AstralDivide.Client.dll` |
| `LICENSE`, `NOTICE.txt` | License texts | |

This is the exact code the released DLLs are built from.

**Not in this repository:** the model bundles, the trader data, the physics data of each model, the clothing thumbnails and the story chapter. They are not code; they ship in the release archive. A build from this repository gives you the two DLLs and nothing else.

## Installing the mod

Players do not need to build anything. Take the release archive and extract it into your SPT folder, the one that contains `EscapeFromTarkov.exe`.

Requirements:

| Mod | Version | Why |
|---|---|---|
| SPT | 4.1.x | |
| WTT-CommonLib | 3.0.x | Registers the clothing, heads and bundles. The server will not load AstralDivide without it. |
| [VisitAPI](https://github.com/TricolourSky/VisitAPI) | 1.3.5 or newer | Runs the story chapter. Without it SORA stays locked. |
| Black Division | current version | Optional. Provides the enemies of one night encounter. |

## Building from source

You need:

- the .NET 10 SDK;
- the .NET Framework 4.7.2 targeting pack (Developer Pack), for the client plugin;
- an SPT 4.1.x installation with WTT-CommonLib installed. The projects reference its assemblies; nothing from the game is in this repository.

Below, `<SPT>` is your SPT folder, the one that contains `EscapeFromTarkov.exe`.

```
dotnet build client\BinaryDimensionStore.Client.csproj -c Release -p:EftDir=<SPT>
dotnet build src\BinaryDimensionStore.csproj           -c Release -p:SptDir=<SPT>\SPT_Runtime
```

The DLLs end up in `client\bin\Release\net472\` and `src\bin\Release\net10.0\`.

**`dotnet build` also installs what it built.** After compiling, it copies the DLL, `LICENSE` and `NOTICE.txt` into `<SPT>\BepInEx\plugins\AstralDivide\` (client) and `<SPT>\SPT_Runtime\user\mods\AstralDivide\` (server). Close the game and the server first, or the copy fails because the DLLs are in use.

To compile without touching the SPT folder:

```
dotnet restore client\BinaryDimensionStore.Client.csproj -p:EftDir=<SPT>
dotnet msbuild client\BinaryDimensionStore.Client.csproj -t:CoreBuild -p:Configuration=Release -p:EftDir=<SPT>

dotnet restore src\BinaryDimensionStore.csproj -p:SptDir=<SPT>\SPT_Runtime
dotnet msbuild src\BinaryDimensionStore.csproj -t:CoreBuild -p:Configuration=Release -p:SptDir=<SPT>\SPT_Runtime
```

The project files are still named after the mod's working title, BinaryDimensionStore. The assemblies they produce are named AstralDivide.

## License

- **Code:** GNU General Public License v3.0, see [LICENSE](LICENSE).
- **Images and story made by the author** (the trader portrait, the story scene backgrounds and video, the chapter banner and icon; the dialogue scripts and the quest files with their texts): © TricolourSky, CC BY-NC-ND 4.0.
- **Character models and clothing thumbnails:** from Girls' Frontline 2: Exilium. They belong to their copyright holder (Sunborn) and are covered by neither license.

The details are in [NOTICE.txt](NOTICE.txt).

AstralDivide is a non-commercial fan work. It is not affiliated with or endorsed by Sunborn, Battlestate Games or the SPT team.
