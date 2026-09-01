
namespace CvInspect.Vision;

/// <summary>사잇각을 어느 구간으로 접을지. 검사마다 다르므로 호출측(레시피)이 지정한다.</summary>
public enum CvAngleWrap
{
    /// <summary>(-180, 180] — 방향까지 구분하는 각도(부품 위상각 등).</summary>
    Pm180,

    /// <summary>(-90, 90] — 직선의 기울기만 보는 각도. 선의 앞뒤 방향이 무의미할 때.</summary>
    Pm90,
}

/// <summary>기준점과 방향으로 정의한 무한 직선. 각도는 이미지 좌표(y 아래) atan2 규약, 도 단위.</summary>
public readonly record struct CvLine(double X, double Y, double AngleDeg)
{
    /// <summary>직선 위에서 기준점으로부터 distance 만큼 떨어진 점.</summary>
    public (double X, double Y) PointAt(double distance)
    {
        var rad = AngleDeg * Math.PI / 180.0;
        return (X + distance * Math.Cos(rad), Y + distance * Math.Sin(rad));
    }
}

/// <summary>보조선 생성과 두 직선 사잇각 — 순수 수학.</summary>
public static class CvLineGeom
{
    /// <summary>기준점 + 방향각으로 직선 생성.</summary>
    public static CvLine FromPointAngle(double x, double y, double angleDeg) => new(x, y, angleDeg);

    /// <summary>기준점 + 방향(라디안)으로 직선 생성 — 각도 입력이 라디안인 경로용.</summary>
    public static CvLine FromPointRadian(double x, double y, double angleRad)
        => new(x, y, angleRad * 180.0 / Math.PI);

    /// <summary>두 점을 잇는 직선 — 시작점이 기준점, 방향은 시작→끝.</summary>
    public static CvLine FromPoints(double sx, double sy, double ex, double ey)
        => new(sx, sy, Math.Atan2(ey - sy, ex - sx) * 180.0 / Math.PI);

    /// <summary>피팅 결과를 직선으로 — 기준점은 피팅 세그먼트의 중점.</summary>
    public static CvLine FromFit(CvLineFit f)
        => new((f.StartX + f.EndX) / 2.0, (f.StartY + f.EndY) / 2.0, f.AngleDeg);

    /// <summary>
    /// 기준선(from)에서 대상선(to)까지의 사잇각. 부호는 이미지 좌표 회전 방향(y 아래 기준 시계 방향이 양).
    /// </summary>
    public static double AngleBetween(CvLine from, CvLine to, CvAngleWrap wrap = CvAngleWrap.Pm180)
        => Wrap(to.AngleDeg - from.AngleDeg, wrap);

    public static double AngleBetween(CvLineFit from, CvLineFit to, CvAngleWrap wrap = CvAngleWrap.Pm180)
        => Wrap(to.AngleDeg - from.AngleDeg, wrap);

    /// <summary>지정 구간으로 각도 접기.</summary>
    public static double Wrap(double deg, CvAngleWrap wrap)
    {
        var d = CvInspGeom.Wrap180(deg);
        if (wrap != CvAngleWrap.Pm90) return d;

        // 앞뒤 방향이 무의미한 직선 — 180° 돌린 것은 같은 직선이므로 (-90, 90] 로 접는다.
        if (d > 90) d -= 180;
        else if (d <= -90) d += 180;
        return d;
    }

    /// <summary>두 직선의 교점. 평행(각도 차가 tolDeg 미만)이면 null.</summary>
    public static (double X, double Y)? Intersect(CvLine a, CvLine b, double tolDeg = 1e-6)
    {
        var da = a.AngleDeg * Math.PI / 180.0;
        var db = b.AngleDeg * Math.PI / 180.0;
        var (ax, ay) = (Math.Cos(da), Math.Sin(da));
        var (bx, by) = (Math.Cos(db), Math.Sin(db));

        var denom = ax * by - ay * bx;
        if (Math.Abs(denom) < Math.Abs(Math.Sin(tolDeg * Math.PI / 180.0)) + double.Epsilon) return null;

        var t = ((b.X - a.X) * by - (b.Y - a.Y) * bx) / denom;
        return (a.X + t * ax, a.Y + t * ay);
    }
}
