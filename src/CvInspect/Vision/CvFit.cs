namespace CvInspect.Vision;

/// <summary>기하 피팅 — 캘리퍼 검출점들에 라인/원을 맞춘다. 순수 계산(결정적).</summary>
public static class CvFit
{
    /// <summary>
    /// PCA(총최소제곱) 라인 피팅 — 수직 잔차 최소화라 수직선도 정상.
    /// 반환: 평균점(Mx,My) + 단위 주방향(Ux,Uy).
    /// </summary>
    public static (double Mx, double My, double Ux, double Uy) Line(IReadOnlyList<(double X, double Y)> pts)
    {
        double mx = 0, my = 0;
        foreach (var (x, y) in pts) { mx += x; my += y; }
        mx /= pts.Count;
        my /= pts.Count;

        double sxx = 0, sxy = 0, syy = 0;
        foreach (var (x, y) in pts)
        {
            var dx = x - mx;
            var dy = y - my;
            sxx += dx * dx;
            sxy += dx * dy;
            syy += dy * dy;
        }
        var theta = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        return (mx, my, Math.Cos(theta), Math.Sin(theta));
    }

    /// <summary>점을 라인(평균점+방향)에 투영.</summary>
    public static (double X, double Y) ProjectOnLine(double px, double py, double mx, double my, double ux, double uy)
    {
        var t = (px - mx) * ux + (py - my) * uy;
        return (mx + t * ux, my + t * uy);
    }

    /// <summary>
    /// 대수적(Kåsa) 원 피팅 — x²+y²+Dx+Ey+F=0 을 선형 최소제곱으로.
    /// 점 3개 미만/공선(퇴화)이면 null. 부분 원호에서 약간의 반경 편향이 있으나 캘리퍼 검출점(저노이즈)에는 충분.
    /// </summary>
    public static (double Cx, double Cy, double R)? Circle(IReadOnlyList<(double X, double Y)> pts)
    {
        if (pts.Count < 3) return null;

        double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0;
        double sxz = 0, syz = 0, sz = 0;
        var n = pts.Count;
        foreach (var (x, y) in pts)
        {
            var z = x * x + y * y;
            sxx += x * x;
            sxy += x * y;
            syy += y * y;
            sx += x;
            sy += y;
            sxz += x * z;
            syz += y * z;
            sz += z;
        }

        // [sxx sxy sx][D]   [−sxz]
        // [sxy syy sy][E] = [−syz]
        // [sx  sy  n ][F]   [−sz ]
        var det = Det3(sxx, sxy, sx, sxy, syy, sy, sx, sy, n);
        if (Math.Abs(det) < 1e-9) return null;   // 공선/퇴화

        var d = Det3(-sxz, sxy, sx, -syz, syy, sy, -sz, sy, n) / det;
        var e = Det3(sxx, -sxz, sx, sxy, -syz, sy, sx, -sz, n) / det;
        var f = Det3(sxx, sxy, -sxz, sxy, syy, -syz, sx, sy, -sz) / det;

        var cx = -d / 2.0;
        var cy = -e / 2.0;
        var r2 = cx * cx + cy * cy - f;
        if (r2 <= 0) return null;
        return (cx, cy, Math.Sqrt(r2));
    }

    private static double Det3(
        double a, double b, double c,
        double d, double e, double f,
        double g, double h, double i)
        => a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
}
