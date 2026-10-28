// 편집 도형의 기하 — 모서리·그립·면 핸들 자리와 히트 판정에 쓰는 거리·면적. 그리기와 잡기가 같은 함수를 쓴다:
// 따로 계산하면 보이는 자리와 잡히는 자리가 어긋난다. 화면 단위 상수는 전부 DIP(장치 독립 픽셀)다 — 물리 화소가 아니다.

using CvInspect.Vision.Edit;

namespace CvInspect.Display;

/// <summary>편집 도형 기하 계산(이미지 공간·화면 공간). UI 프레임워크 무관.</summary>
internal static class CvEditGeom
{
    /// <summary>선·핸들 히트 판정의 화면 여유(DIP).</summary>
    internal const double HitScreenPx = 8;

    /// <summary>핸들 사각의 화면 크기(DIP, 줌 무관).</summary>
    internal const double HandleScreenPx = 7;

    /// <summary>회전 그립이 상변에서 바깥으로 떨어진 화면 거리(DIP).</summary>
    internal const double GripScreenPx = 26;

    // 면 핸들 — 각 변 가운데를 조금 굵게 그린 짧은 선. 길이는 변의 1/3 이되 화면 고정 상한이 있어, 큰 사각이면 변 가운데만
    // 살짝 굵어 보인다. 변이 화면에서 짧으면 아예 내지 않는다 — 모서리 핸들과 겹쳐 어느 것을 잡았는지 알 수 없게 된다.
    internal const double EdgeHandleMaxScreenPx = 24;
    internal const double EdgeHandleThickPx = 5;
    internal const double EdgeHandleMinEdgeScreenPx = 3 * HandleScreenPx;

    /// <summary>사각 중심(이미지 공간).</summary>
    internal static (double X, double Y) RectCenter(CvEditRect r) => (r.X + r.W / 2, r.Y + r.H / 2);

    /// <summary>회전 적용된 4 모서리(이미지 공간) — 좌상/우상/우하/좌하 순 (핸들 인덱스 0..3).</summary>
    internal static (double X, double Y)[] RectCorners(CvEditRect r)
    {
        var (cx, cy) = RectCenter(r);
        var rad = r.AngleDeg * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var hw = r.W / 2;
        var hh = r.H / 2;
        var local = new[] { (-hw, -hh), (hw, -hh), (hw, hh), (-hw, hh) };
        var pts = new (double X, double Y)[4];
        for (var i = 0; i < 4; i++)
        {
            var (lx, ly) = local[i];
            pts[i] = (cx + cos * lx - sin * ly, cy + sin * lx + cos * ly);
        }
        return pts;
    }

    /// <summary>회전 그립 위치(이미지 공간) — 상변 중앙에서 로컬 −y 방향으로 gripImgLen 만큼.</summary>
    internal static (double X, double Y) RectGrip(CvEditRect r, double gripImgLen)
    {
        var (cx, cy) = RectCenter(r);
        var rad = r.AngleDeg * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var ly = -(r.H / 2 + gripImgLen);
        return (cx - sin * ly, cy + cos * ly);
    }

    /// <summary>이미지 좌표 → 사각 로컬 좌표 (중심 원점, 회전 해제).</summary>
    internal static (double X, double Y) ToRectLocal(CvEditRect r, double imgX, double imgY)
    {
        var (cx, cy) = RectCenter(r);
        var rad = -r.AngleDeg * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var dx = imgX - cx;
        var dy = imgY - cy;
        return (cos * dx - sin * dy, sin * dx + cos * dy);
    }

    /// <summary>원호 위 점(이미지 공간) — spanOffsetDeg 는 시작각으로부터의 + 방향 오프셋.</summary>
    internal static (double X, double Y) ArcPoint(CvEditArc arc, double spanOffsetDeg)
    {
        var rad = (arc.StartDeg + spanOffsetDeg) * Math.PI / 180.0;
        return (arc.CenterX + arc.Radius * Math.Cos(rad), arc.CenterY + arc.Radius * Math.Sin(rad));
    }

    /// <summary>도형 몸체 면적 — 중첩 히트 시 작은 도형 우선 선택 기준. 선분은 몸체가 얇아 항상 최우선.</summary>
    internal static double BodyArea(CvEditShape shape) => shape switch
    {
        CvEditRect r => r.W * r.H,
        CvEditCircle c => Math.PI * c.Radius * c.Radius,
        CvEditArc a => Math.PI * a.Radius * a.Radius,
        CvEditRing g => Math.PI * (g.RMax * g.RMax - g.RMin * g.RMin),
        CvEditSeg => 0,
        _ => double.MaxValue,
    };

    /// <summary>점 p 에서 선분 a–b 까지의 거리(같은 공간). 길이가 0 에 가까우면 a 까지의 거리.</summary>
    internal static double DistToSegment((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        if (len2 < 1e-9) return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        var qx = a.X + t * dx;
        var qy = a.Y + t * dy;
        return Math.Sqrt((p.X - qx) * (p.X - qx) + (p.Y - qy) * (p.Y - qy));
    }

    /// <summary>모서리·끝점·반경 그립 자리(이미지 공간) — 인덱스가 곧 핸들 번호다. 면 핸들(5..8)은 여기 없다(<see cref="RectEdgeHandles"/>).
    /// 사각 0..3 = 모서리, 4 = 회전 그립(IsRotatable 일 때만). 크기 고정 사각은 그립 없음(이동만).</summary>
    internal static (double X, double Y)[] GripPoints(CvEditShape shape, in CvViewXform view) => shape switch
    {
        CvEditRect { IsResizable: false } => Array.Empty<(double, double)>(),
        CvEditRect r => r.IsRotatable
            ? [.. RectCorners(r), RectGrip(r, GripScreenPx / view.Scale)]
            : RectCorners(r),
        CvEditSeg s => [(s.X1, s.Y1), (s.X2, s.Y2)],
        // 원 핸들 0=반경(우측 0°)
        CvEditCircle cc => [(cc.CenterX + cc.Radius, cc.CenterY)],
        // 원호 핸들 0=시작각, 1=스팬 끝, 2=반경(중간각)
        CvEditArc a => [ArcPoint(a, 0), ArcPoint(a, a.SpanDeg), ArcPoint(a, a.SpanDeg / 2)],
        // 링 핸들 0=안 반경(좌측 180°), 1=바깥 반경(우측 0°)
        CvEditRing g => [(g.CenterX - g.RMin, g.CenterY), (g.CenterX + g.RMax, g.CenterY)],
        _ => Array.Empty<(double, double)>(),
    };

    /// <summary>사각의 면 핸들(화면 좌표 선분) — 핸들 인덱스 5..8 = 위/오른쪽/아래/왼쪽 변(회전 전 로컬 기준). 화면에서 짧은
    /// 변은 빠진다. 그리기와 잡기 판정이 같은 목록을 쓴다 — 따로 계산하면 보이는 자리와 잡히는 자리가 어긋난다.</summary>
    internal static List<(int Handle, (double X, double Y) P1, (double X, double Y) P2)> RectEdgeHandles(CvEditRect r, in CvViewXform view)
    {
        var handles = new List<(int, (double, double), (double, double))>(4);
        var corners = RectCorners(r);
        for (var e = 0; e < 4; e++)
        {
            var a = view.ToScreen(corners[e].X, corners[e].Y);
            var b = view.ToScreen(corners[(e + 1) % 4].X, corners[(e + 1) % 4].Y);
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < EdgeHandleMinEdgeScreenPx) continue;
            var half = Math.Min(EdgeHandleMaxScreenPx, len / 3) / 2 / len;
            var mx = (a.X + b.X) / 2;
            var my = (a.Y + b.Y) / 2;
            // 변 가운데가 모서리 판정 칸(화면 축 정렬 ±HitScreenPx) 안이면 낸다 해도 잡히지 않는다 — 모서리가 먼저 잡힌다.
            // 길이 하한만으로는 45° 사각에서 빠진다(칸의 대각 거리 √2·8 ≈ 11.3 이 21~22.6 px 변 가운데까지 닿는다, 독립 검토 지적).
            if ((Math.Abs(mx - a.X) <= HitScreenPx && Math.Abs(my - a.Y) <= HitScreenPx)
                || (Math.Abs(mx - b.X) <= HitScreenPx && Math.Abs(my - b.Y) <= HitScreenPx)) continue;
            handles.Add((5 + e, (mx - dx * half, my - dy * half), (mx + dx * half, my + dy * half)));
        }
        return handles;
    }
}
