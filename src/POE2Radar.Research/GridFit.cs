using POE2Radar.Core;
using POE2Radar.Core.Game;
using Vector2 = System.Numerics.Vector2;

namespace POE2Radar.Research;

/// <summary>Read-only: are atlas node relPos values stable (static view) and is relPos an affine function of grid?
/// Samples every node's relPos N times; classifies stable vs noisy, fits pos = A·grid + b on stable nodes.</summary>
internal static class GridFit
{
    public static int Run(MemoryReader reader, nint igs)
    {
        var atlas = new Poe2Atlas(reader);
        var nb = atlas.ReadNodes(igs);
        for (var w = 0; (nb.Count == 0 || !atlas.AllTagsResolved) && w < 100; w++) { Thread.Sleep(50); nb = atlas.ReadNodes(igs); }
        int N = nb.Count; var mn = new Vector2[N]; var mx = new Vector2[N]; var last = new Vector2[N];
        for (var i = 0; i < N; i++) { mn[i] = new Vector2(float.MaxValue); mx[i] = new Vector2(float.MinValue); }
        for (var s = 0; s < 20; s++)
        {
            for (var i = 0; i < N; i++)
                if (reader.TryReadStruct<Vector2>(nb[i].Element + Poe2.UiElement.RelativePos, out var v)) { mn[i] = Vector2.Min(mn[i], v); mx[i] = Vector2.Max(mx[i], v); last[i] = v; }
            Thread.Sleep(50);
        }
        var noisy0 = Enumerable.Range(0, N).Where(i => (mx[i] - mn[i]).Length() >= 0.5f).Take(4).ToList();
        foreach (var i in noisy0)
        {
            var seq = new List<string>();
            for (var s = 0; s < 40; s++) { if (reader.TryReadStruct<Vector2>(nb[i].Element + Poe2.UiElement.RelativePos, out var v)) seq.Add($"{v.X:0},{v.Y:0}"); Thread.Sleep(5); }
            Console.WriteLine($"SEQ ({nb[i].GridX},{nb[i].GridY}) fog {(nb[i].Visible ? 0 : 1)}: {string.Join(" ", seq)}");
        }
        var stable = Enumerable.Range(0, N).Where(i => (mx[i] - mn[i]).Length() < 0.5f).ToList();
        Console.WriteLine($"nodes {N}, stable over 1s {stable.Count}, noisy {N - stable.Count}");
        // least squares x = a*gx + b*gy + c ; y = d*gx + e*gy + f
        double[] Fit(Func<int, double> f)
        {
            double s00 = 0, s01 = 0, s02 = 0, s11 = 0, s12 = 0, s22 = 0, t0 = 0, t1 = 0, t2 = 0;
            foreach (var i in stable) { double gx = nb[i].GridX, gy = nb[i].GridY, z = f(i); s00 += gx * gx; s01 += gx * gy; s02 += gx; s11 += gy * gy; s12 += gy; s22 += 1; t0 += gx * z; t1 += gy * z; t2 += z; }
            var M = new double[,] { { s00, s01, s02, t0 }, { s01, s11, s12, t1 }, { s02, s12, s22, t2 } };
            for (int c = 0; c < 3; c++) { int p = c; for (int r = c + 1; r < 3; r++) if (Math.Abs(M[r, c]) > Math.Abs(M[p, c])) p = r; for (int k = 0; k < 4; k++) (M[c, k], M[p, k]) = (M[p, k], M[c, k]); for (int r = 0; r < 3; r++) if (r != c) { var q = M[r, c] / M[c, c]; for (int k = 0; k < 4; k++) M[r, k] -= q * M[c, k]; } }
            return new[] { M[0, 3] / M[0, 0], M[1, 3] / M[1, 1], M[2, 3] / M[2, 2] };
        }
        if (stable.Count < 10) return 0;
        var fx = Fit(i => last[i].X); var fy = Fit(i => last[i].Y);
        for (var round = 0; round < 6; round++)
        {
            var fxx = fx; var fyy = fy;
            float Err(int i) => (new Vector2((float)(fxx[0] * nb[i].GridX + fxx[1] * nb[i].GridY + fxx[2]), (float)(fyy[0] * nb[i].GridX + fyy[1] * nb[i].GridY + fyy[2])) - last[i]).Length();
            stable = stable.OrderBy(Err).Take(Math.Max(10, (int)(stable.Count * 0.8))).ToList();
            fx = Fit(i => last[i].X); fy = Fit(i => last[i].Y);
        }
        Console.WriteLine($"trimmed fit set {stable.Count}");
        Console.WriteLine($"x = {fx[0]:0.000}*gx + {fx[1]:0.000}*gy + {fx[2]:0.0};  y = {fy[0]:0.000}*gx + {fy[1]:0.000}*gy + {fy[2]:0.0}");
        var res = stable.Select(i => (new Vector2((float)(fx[0] * nb[i].GridX + fx[1] * nb[i].GridY + fx[2]), (float)(fy[0] * nb[i].GridX + fy[1] * nb[i].GridY + fy[2])) - last[i]).Length()).OrderBy(r => r).ToList();
        Console.WriteLine($"stable residual px(canvas): median {res[res.Count / 2]:0.0}  p90 {res[(int)(res.Count * 0.9)]:0.0}  max {res[^1]:0.0}");
        var noisy = Enumerable.Range(0, N).Except(stable).Take(8);
        foreach (var i in noisy) Console.WriteLine($"  noisy ({nb[i].GridX},{nb[i].GridY}) range {(mx[i] - mn[i]).Length():0.0} fitErr {(new Vector2((float)(fx[0] * nb[i].GridX + fx[1] * nb[i].GridY + fx[2]), (float)(fy[0] * nb[i].GridX + fy[1] * nb[i].GridY + fy[2])) - last[i]).Length():0.0}");
        return 0;
    }
}
