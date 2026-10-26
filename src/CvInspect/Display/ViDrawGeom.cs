// 오버레이·편집 도형을 그릴 때의 수치 계산 — 선 굵기 배수, 라벨 글자 크기, 화살촉·꼭짓점·원주 분할점.
// 그리기 호출(펜·브러시·지오메트리)은 UI 어댑터에 남고, 무엇을 어디에 그릴지만 여기서 정한다.
// 결과는 화면 좌표(DIP)다. 계산 순서는 지금 화면과 같은 값이 나오도록 그대로 지킨다.

using CvInspect.Vision.Edit;
using CvInspect.Vision.Overlay;

namespace CvInspect.Display;

/// <summary>렌더 기하 계산. UI 프레임워크 무관.</summary>
internal static class ViDrawGeom
{
    // 선 굵기 배수(ViOverlayItem.StrokeScale)의 그릴 수 있는 범위. 아래 끝: 0 에 가까운 배수는 선을 사실상 지운다 — 0 은 1.0 으로
    // 바꾸면서 1e-6 은 안 보이게 두면 같은 실수가 값에 따라 갈린다. 위 끝: 아주 큰 유한값은 기본 굵기와 곱해 무한대가 되고, 화살촉
    // 좌표는 0×∞ 로 NaN 이 된다(독립 검토 지적). 50 배면 선분 굵기 100 DIP — "눈에 띄게" 의 쓰임은 그 한참 안쪽이다.
    internal const double MinStrokeScale = 0.25;
    internal const double MaxStrokeScale = 50;

    // 글자 크기(포인트)의 그릴 수 있는 범위 — WPF FormattedText 는 0 이하나 35791 DIP(≈ 26843 pt) 넘는 크기를 던지는데, 그 예외는 OnRender
    // 안이라 레이아웃 패스를 타고 앱의 처리되지 않은 예외로 올라간다. 라벨 글자 크기는 검사 코드가 정하고(검사하지 않는다) 화면 배율이
    // 곱해지므로(검토 실측: 20 pt 라벨이 배율 1500 에서, 아주 작은 배율은 float 로 0 이 되어 던졌다) 그리는 자리에서 잡는다.
    internal const float MinLabelPt = 0.5f;
    internal const float MaxLabelPt = 26000f;

    /// <summary>오버레이 선분·사각의 기본 굵기(DIP, 화면 고정).</summary>
    internal const double OverlayLineThick = 2;

    /// <summary>오버레이 폴리라인의 기본 굵기(DIP) — 윤곽은 점이 많아 얇게.</summary>
    internal const double OverlayPolyThick = 1.2;

    /// <summary>편집 원주의 분할 수.</summary>
    internal const int CircleSegs = 72;

    /// <summary>이미지 모서리에 붙은 요약 HUD — <c>ViOverlay.CropTo</c> 가 같은 모서리로 다시 붙이는 것과 같은 판정.</summary>
    internal static bool IsCornerHud(ViOverlayItem item)
        => item is ViOverlayLabel
        {
            IsHud: true,
            Align: ViOverlayAlign.TopLeft or ViOverlayAlign.TopRight or ViOverlayAlign.BottomLeft or ViOverlayAlign.BottomRight,
        };

    /// <summary>굵기 배수 — 화면 고정 기본 굵기에 곱한다(줌과 무관). 이상한 값(0 이하·NaN·∞)은 1.0, 범위 밖은 끝값.</summary>
    internal static double ResolveStroke(double strokeScale)
        => strokeScale is > 0 and < double.PositiveInfinity
            ? Math.Clamp(strokeScale, MinStrokeScale, MaxStrokeScale)
            : 1.0;

    /// <summary>라벨의 글자 크기(pt) — 모서리 요약 HUD 는 그대로, 그 밖은 표면의 라벨 배율을 곱한다(float 로 — 지금 화면과 같은 값).</summary>
    internal static float LabelSizePt(ViOverlayLabel label, double labelScale)
        => IsCornerHud(label) ? label.FontSize : (float)(label.FontSize * labelScale);

    /// <summary>글자 크기 pt → DIP. NaN 은 하한, 범위 밖은 끝값으로 자른 뒤 환산한다.</summary>
    internal static double LabelDip(float sizePt)
        => (float.IsNaN(sizePt) ? MinLabelPt : Math.Clamp(sizePt, MinLabelPt, MaxLabelPt)) * 96.0 / 72.0;

    /// <summary>라벨 글자 블록의 왼쪽 위 — 기준점(화면)과 측정한 블록 폭·높이로 정렬을 푼다.</summary>
    internal static (double X, double Y) LabelOrigin(ViOverlayAlign align, (double X, double Y) anchor, double width, double height)
    {
        var x = align switch
        {
            ViOverlayAlign.TopLeft or ViOverlayAlign.BottomLeft => anchor.X,
            ViOverlayAlign.TopRight or ViOverlayAlign.BottomRight => anchor.X - width,
            _ => anchor.X - width / 2,
        };
        var y = align is ViOverlayAlign.BottomCenter or ViOverlayAlign.BottomLeft or ViOverlayAlign.BottomRight
            ? anchor.Y - height
            : anchor.Y;
        return (x, y);
    }

    /// <summary>회전 적용된 오버레이 사각의 4 꼭짓점(화면) — 좌상/우상/우하/좌하.</summary>
    internal static (double X, double Y)[] OverlayRectCorners(ViOverlayRect rect, in CvViewXform view)
    {
        var rad = rect.AngleDeg * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var hw = rect.Width / 2;
        var hh = rect.Height / 2;
        var corners = new[] { (-hw, -hh), (hw, -hh), (hw, hh), (-hw, hh) };
        var pts = new (double X, double Y)[4];
        for (var i = 0; i < 4; i++)
        {
            var (ox, oy) = corners[i];
            pts[i] = view.ToScreen(rect.CenterX + cos * ox - sin * oy, rect.CenterY + sin * ox + cos * oy);
        }
        return pts;
    }

    /// <summary>끝점의 채운 화살촉 세 점(화면) — 끝점, 왼쪽 날개, 오른쪽 날개. 선분이 2 DIP 보다 짧으면 null.
    /// <paramref name="scale"/> 은 선 굵기 배수 — 굵은 선에 기본 크기 촉을 달면 촉이 선에 묻힌다.</summary>
    internal static ((double X, double Y) Tip, (double X, double Y) Left, (double X, double Y) Right)? ArrowHead(
        (double X, double Y) from, (double X, double Y) to, double scale = 1.0)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 2) return null;
        var ux = dx / len;
        var uy = dy / len;
        // 촉이 선분보다 길면 선분 길이로 줄인다(비율 유지) — 짧은 선분에 키운 촉이 시작점 뒤 빈 곳에서 튀어나오지 않게.
        var shrink = Math.Min(1.0, len / (12 * scale));
        var headLen = 12 * scale * shrink;
        var headHalf = 4.5 * scale * shrink;
        var bx = to.X - ux * headLen;
        var by = to.Y - uy * headLen;
        return (to, (bx - uy * headHalf, by + ux * headHalf), (bx + uy * headHalf, by - ux * headHalf));
    }

    /// <summary>전체 원 폴리라인 점(화면) — 0° 에서 시작해 <see cref="CircleSegs"/> 분할, 끝점이 첫 점으로 돌아온다(점 수 = 분할 + 1).</summary>
    internal static (double X, double Y)[] CirclePoints(double cx, double cy, double r, in CvViewXform view)
    {
        var pts = new (double X, double Y)[CircleSegs + 1];
        pts[0] = view.ToScreen(cx + r, cy);
        for (var i = 1; i <= CircleSegs; i++)
        {
            var t = i * Math.PI * 2 / CircleSegs;
            pts[i] = view.ToScreen(cx + r * Math.Cos(t), cy + r * Math.Sin(t));
        }
        return pts;
    }

    /// <summary>원호 폴리라인 점(화면) — 시작→끝(+방향) 순서, 스팬 비례 분할(최소 12). 끝 화살촉은 마지막 두 점으로 낸다.</summary>
    internal static (double X, double Y)[] ArcPoints(CvEditArc arc, in CvViewXform view)
    {
        var segs = Math.Max(12, (int)Math.Round(arc.SpanDeg / 5.0));
        var pts = new (double X, double Y)[segs + 1];
        var p0 = CvEditGeom.ArcPoint(arc, 0);
        pts[0] = view.ToScreen(p0.X, p0.Y);
        for (var i = 1; i <= segs; i++)
        {
            var t = arc.SpanDeg * i / segs;
            var p = CvEditGeom.ArcPoint(arc, t);
            pts[i] = view.ToScreen(p.X, p.Y);
        }
        return pts;
    }
}
