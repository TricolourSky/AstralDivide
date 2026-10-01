using System;
using System.Collections.Generic;
using System.IO;
using EFT.Customization;
using HarmonyLib;
using UnityEngine;

namespace AstralDivide.Client;

[HarmonyPatch(typeof(ClothingIconCreator), nameof(ClothingIconCreator.GetIcon))]
internal static class ClothingIconPatch
{
    private static readonly Dictionary<long, ClothingIcon> Cache = new Dictionary<long, ClothingIcon>();
    private static string[] _pngs;

    private static bool Prefix(CustomizationClothing clothing, IntVec2 textureSize, ref ClothingIcon __result)
    {
        try
        {
            string path = clothing.Prefab.path;
            if (string.IsNullOrEmpty(path))
                return true;
            if (_pngs == null)
            {
                string dir = Path.Combine(Plugin.PluginDir, "icons");
                _pngs = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.png") : new string[0];
            }
            string best = null;
            int bestLen = 0;
            string lower = path.ToLowerInvariant();
            foreach (string png in _pngs)
            {
                string key = Path.GetFileNameWithoutExtension(png).ToLowerInvariant();
                if (key.Length > bestLen && lower.Contains(key))
                {
                    best = png;
                    bestLen = key.Length;
                }
            }
            if (best == null)
                return true;
            int hash = IconsHash.GetClothingHash(clothing);
            // 关键: 精灵必须缩放到游戏请求的尺寸(列表小格/解锁弹窗尺寸不同,弹窗按精灵
            // 原始尺寸排版,直接塞 512 原图会把弹窗撑爆),按 哈希+尺寸 分开缓存。
            long cacheKey = ((long)hash << 16) ^ (textureSize.X * 397) ^ textureSize.Y;
            if (!Cache.TryGetValue(cacheKey, out ClothingIcon icon))
            {
                icon = new ClothingIcon(hash);
                icon.Sprite = LoadScaled(best, textureSize.X, textureSize.Y);
                icon.Changed.Invoke();
                Cache[cacheKey] = icon;
            }
            __result = icon;
            return false;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"服装缩略图补丁异常, 回退原生成: {e}");
            return true;
        }
    }

    /// <summary>
    /// 游戏要的 w×h 是 UI 格子的**逻辑尺寸**（~110px），屏幕缩放后实际画出来更大，
    /// 按 1:1 出图再被放大就糊（2026-08-26 Tech Leader：「图片不够清晰」）。
    /// 所以按 k 倍超采样出图（k 不超过源图分辨率），再把精灵的 pixelsPerUnit 也乘 k ——
    /// 布局量到的尺寸和以前一模一样（解锁弹窗不会再被撑爆），只是像素密度翻了 k 倍。
    /// </summary>
    private static Sprite LoadScaled(string file, int w, int h)
    {
        // mipChain=true + Trilinear: 大图降采样走 mip 链，单次 bilinear 缩 4 倍会出马赛克。
        var src = new Texture2D(2, 2, TextureFormat.RGBA32, true);
        src.LoadImage(File.ReadAllBytes(file));
        src.filterMode = FilterMode.Trilinear;
        int k = Mathf.Clamp(src.width / Mathf.Max(1, Mathf.Max(w, h)), 1, 4);
        int rw = w * k, rh = h * k;
        RenderTexture rt = RenderTexture.GetTemporary(rw, rh, 0, RenderTextureFormat.ARGB32);
        RenderTexture prev = RenderTexture.active;
        Graphics.Blit(src, rt);
        RenderTexture.active = rt;
        var dst = new Texture2D(rw, rh, TextureFormat.RGBA32, false);
        dst.ReadPixels(new Rect(0, 0, rw, rh), 0, 0);
        dst.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        UnityEngine.Object.Destroy(src);
        dst.filterMode = FilterMode.Trilinear;
        return Sprite.Create(dst, new Rect(0, 0, rw, rh), new Vector2(0.5f, 0.5f), 100f * k);
    }
}
