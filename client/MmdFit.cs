using System.Collections.Generic;
using UnityEngine;

namespace AstralDivide.Client;

/// <summary>
/// 「PMX 坐标 → 服装 prefab 里的骨架坐标」的刚体拟合。
///
/// **为什么需要它**：PMX 里的刚体坐标是模型的**原始站姿**，而我们 blend 里的骨是 Tech Leader
/// 摆过的**对齐姿势**（手臂和腿被转过）。两边对不上，刚体就会飘到几十厘米外。
///
/// **怎么解决**：对齐时是「整块整块地转」的 —— 头上所有头发骨跟着头一起转、袖布骨跟着小臂一起转。
/// 所以只要拿**同一块**里的骨（PMX 坐标 ↔ prefab 坐标）凑一堆点，求出「把这堆点转到那堆点」
/// 的那唯一一个旋转+平移+缩放，这块里的刚体就能精确落位。分块规则见 tools 里的 BoneMap.cs。
///
/// 算法是 Horn 1987 的四元数法（Kabsch 的等价形式）：它**天然只会给出真旋转**，
/// 不会像 SVD 那样偶尔给出一个镜像解 —— 镜像会把整个模型翻个面，那是灾难。
/// 缩放按 Umeyama 的公式一起求出来，顺带把「1 PMX 单位 = 多少米」也量出来了
/// （以前是硬编码 0.08，现在是量的）。
/// </summary>
internal class MmdFit
{
    internal Quaternion Rot = Quaternion.identity;
    internal Vector3 Move;
    internal float Scale = 1f;
    internal int Count;
    internal float Rms;             // 拟合残差（米），越小说明「整块刚性搬运」的假设越成立
    internal float Linearity;       // 点云离「最长那条轴」有多远 / 那条轴多长。接近 0 = 共线，转不出来

    internal Vector3 Apply(Vector3 pmx) => Rot * pmx * Scale + Move;

    internal Quaternion ApplyRot(Quaternion pmx) => Rot * pmx;

    internal bool Usable => Count >= 3 && Linearity > 0.02f;

    /// src = PMX 坐标，dst = prefab 里的骨世界坐标。两个表必须一一对应。
    internal static MmdFit Solve(List<Vector3> src, List<Vector3> dst)
    {
        var f = new MmdFit { Count = Mathf.Min(src.Count, dst.Count) };
        if (f.Count < 3)
            return f;

        Vector3 cs = Vector3.zero, cd = Vector3.zero;
        for (int i = 0; i < f.Count; i++) { cs += src[i]; cd += dst[i]; }
        cs /= f.Count;
        cd /= f.Count;

        // 3x3 相关矩阵 S[a][b] = Σ src_a * dst_b
        var S = new float[3, 3];
        float varSrc = 0f;
        for (int i = 0; i < f.Count; i++)
        {
            Vector3 a = src[i] - cs, b = dst[i] - cd;
            varSrc += a.sqrMagnitude;
            S[0, 0] += a.x * b.x; S[0, 1] += a.x * b.y; S[0, 2] += a.x * b.z;
            S[1, 0] += a.y * b.x; S[1, 1] += a.y * b.y; S[1, 2] += a.y * b.z;
            S[2, 0] += a.z * b.x; S[2, 1] += a.z * b.y; S[2, 2] += a.z * b.z;
        }
        f.Rot = LargestEigenQuat(S);

        // 缩放：Umeyama。分母是源点云的散度，为 0 说明所有点重合，直接放弃。
        if (varSrc < 1e-12f)
            return f;
        float num = 0f;
        for (int i = 0; i < f.Count; i++)
            num += Vector3.Dot(dst[i] - cd, f.Rot * (src[i] - cs));
        f.Scale = num / varSrc;
        f.Move = cd - f.Rot * cs * f.Scale;

        double sq = 0.0;
        for (int i = 0; i < f.Count; i++)
            sq += (f.Apply(src[i]) - dst[i]).sqrMagnitude;
        f.Rms = Mathf.Sqrt((float)(sq / f.Count));
        f.Linearity = Spread(src, cs);
        return f;
    }

    /// <summary>
    /// 点云「离共线有多远」：先找最长的那条轴，再看所有点离这条轴最远有多少，两者相除。
    ///
    /// ⚠️ 只有**共线**才会让刚体拟合退化（绕那条线转多少都一样好）。**共面是完全够用的** ——
    /// 三个不共线的点就唯一确定一个刚体变换。2026-08-23 首版用「AABB 最扁边 / 最大边」判，
    /// 结果把贝丝蒂尾巴那 9 根共面的骨误杀了；而且 AABB 不是旋转不变的 ——
    /// 一条沿 (1,1,1) 的直线会得到一个正方体包围盒，反而被判成「很立体」，两头都是错的。
    /// </summary>
    private static float Spread(List<Vector3> pts, Vector3 c)
    {
        Vector3 axis = Vector3.zero;
        float len = 0f;
        foreach (Vector3 p in pts)
        {
            float m = (p - c).sqrMagnitude;
            if (m > len) { len = m; axis = p - c; }
        }
        len = Mathf.Sqrt(len);
        if (len < 1e-6f)
            return 0f;
        axis /= len;
        float perp = 0f;
        foreach (Vector3 p in pts)
        {
            Vector3 d = p - c;
            perp = Mathf.Max(perp, (d - axis * Vector3.Dot(d, axis)).magnitude);
        }
        return perp / len;
    }

    /// Horn 的 4x4 对称矩阵，其最大特征向量就是所求旋转的四元数。用 Jacobi 求，确定性、不怕退化。
    private static Quaternion LargestEigenQuat(float[,] s)
    {
        var n = new double[4, 4];
        double tr = s[0, 0] + s[1, 1] + s[2, 2];
        n[0, 0] = tr;
        n[1, 1] = s[0, 0] - s[1, 1] - s[2, 2];
        n[2, 2] = -s[0, 0] + s[1, 1] - s[2, 2];
        n[3, 3] = -s[0, 0] - s[1, 1] + s[2, 2];
        n[0, 1] = n[1, 0] = s[1, 2] - s[2, 1];
        n[0, 2] = n[2, 0] = s[2, 0] - s[0, 2];
        n[0, 3] = n[3, 0] = s[0, 1] - s[1, 0];
        n[1, 2] = n[2, 1] = s[0, 1] + s[1, 0];
        n[1, 3] = n[3, 1] = s[2, 0] + s[0, 2];
        n[2, 3] = n[3, 2] = s[1, 2] + s[2, 1];

        var v = new double[4, 4];
        for (int i = 0; i < 4; i++)
            v[i, i] = 1.0;
        for (int sweep = 0; sweep < 64; sweep++)
        {
            double off = 0.0;
            for (int p = 0; p < 4; p++)
                for (int q = p + 1; q < 4; q++)
                    off += n[p, q] * n[p, q];
            if (off < 1e-24)
                break;
            for (int p = 0; p < 4; p++)
                for (int q = p + 1; q < 4; q++)
                {
                    if (System.Math.Abs(n[p, q]) < 1e-30)
                        continue;
                    double theta = (n[q, q] - n[p, p]) / (2.0 * n[p, q]);
                    double t = System.Math.Sign(theta) / (System.Math.Abs(theta) + System.Math.Sqrt(theta * theta + 1.0));
                    if (theta == 0.0)
                        t = 1.0;
                    double c = 1.0 / System.Math.Sqrt(t * t + 1.0), sn = t * c;
                    for (int k = 0; k < 4; k++)
                    {
                        double nkp = n[k, p], nkq = n[k, q];
                        n[k, p] = c * nkp - sn * nkq;
                        n[k, q] = sn * nkp + c * nkq;
                    }
                    for (int k = 0; k < 4; k++)
                    {
                        double npk = n[p, k], nqk = n[q, k];
                        n[p, k] = c * npk - sn * nqk;
                        n[q, k] = sn * npk + c * nqk;
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - sn * vkq;
                        v[k, q] = sn * vkp + c * vkq;
                    }
                }
        }

        int best = 0;
        for (int i = 1; i < 4; i++)
            if (n[i, i] > n[best, best])
                best = i;
        var quat = new Quaternion((float)v[1, best], (float)v[2, best], (float)v[3, best], (float)v[0, best]);
        float len = Mathf.Sqrt(quat.x * quat.x + quat.y * quat.y + quat.z * quat.z + quat.w * quat.w);
        return len < 1e-6f ? Quaternion.identity : new Quaternion(quat.x / len, quat.y / len, quat.z / len, quat.w / len);
    }
}
