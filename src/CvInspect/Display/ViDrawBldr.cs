// 그리기 목록을 만든다 — 결과 오버레이, 편집 도형, 무이미지 안내. 화면 좌표(DIP)의 프리미티브를 그리는 순서대로 낸다.
// 순서와 값은 화면 모양 그 자체다: 같은 프리미티브라도 순서가 바뀌면 겹친 자리의 색이, 경로를 합치면 점선 위상과 모서리가 바뀐다.

using System.Buffers;
using CvInspect.Vision.Edit;
using CvInspect.Vision.Overlay;

namespace CvInspect.Display;

/// <summary>그리기 목록 빌더. UI 프레임워크 무관 — 글자 크기만 <see cref="ITextMetricPort"/> 로 잰다.</summary>
internal static class ViDrawBldr
{
    /// <summary>라벨 배경 — 반투명 검정.</summary>
    internal static readonly ViPaint LabelBack = ViPaint.Argb(200, 0, 0, 0);

    /// <summary>무이미지 안내 문구 기본 색 — 호스트가 색을 주지 않을 때(UI 패키지의 안내 글자 색 키·속성).</summary>
    internal static readonly ViPaint HintGray = ViPaint.Argb(255, 0x9E, 0x9E, 0x9E);

    /// <summary>편집 도형 라벨 글자 크기(pt).</summary>
    internal const float ShapeLabelPt = 12;

    /// <summary>편집 도형의 보조 선(중심 십자·반경 연결선·회전 그립 막대) 굵기(DIP).</summary>
    internal const double GuideThick = 1.2;

    /// <summary>편집 도형 외곽선 굵기(DIP, 점선).</summary>
    internal const double ShapeLineThick = 2;

    /// <summary>중심 십자 반 길이(DIP).</summary>
    private const double CrossHalfPx = 7;

    /// <summary>목록을 비운다 — 풀에서 빌린 폴리라인 점 배열을 돌려준다. 다음 그리기 전에 부른다(그리는 동안 배열이 쓰이므로 그 뒤에).</summary>
    internal static void Clear(List<ViPrim> prims)
    {
        foreach (var p in prims)
            if (p is ViPrimPath { IsPooled: true } path) ArrayPool<(double X, double Y)>.Shared.Return(path.Points);
        prims.Clear();
    }

    /// <summary>결과 오버레이 전부 — 클립을 켜면 이미지 사각 안으로 자르고, 모서리 요약 HUD 는 자르지 않고 그 뒤에 낸다.</summary>
    /// <remarks>HUD 를 자르면 글자가 화면 고정 크기라 작게 그려진 이미지에서 판정 사유 줄이 사라지고 남은 헤더 한 줄이 온전한 HUD 처럼
    /// 보인다(게시 전 검토 실측: 1000×60 띠를 400×400 칸에 → HUD 픽셀 83% 소실). 끄면(기본) 표면 경계가 유일한 클립이다.</remarks>
    internal static void Overlay(List<ViPrim> dst, ViOverlay overlay, in CvViewXform view, ITextMetricPort tm,
        double labelScale, bool clipToImage, int imgW, int imgH)
    {
        if (clipToImage)
        {
            // 이미지가 차지한 화면 사각 — 배율+이동이라 왼쪽 위는 이동량, 폭·높이는 이미지 크기 × 배율.
            dst.Add(new ViPrimClipPush(new ViBox(view.OffsetX, view.OffsetY, imgW * view.Scale, imgH * view.Scale)));
            OverlayItems(dst, overlay, view, tm, labelScale, item => !ViDrawGeom.IsCornerHud(item));
            dst.Add(ViPrimClipPop.Instance);
            OverlayItems(dst, overlay, view, tm, labelScale, ViDrawGeom.IsCornerHud);
        }
        else
        {
            OverlayItems(dst, overlay, view, tm, labelScale, null);
        }
    }

    /// <summary>오버레이 항목을 목록 순서대로. <paramref name="include"/> 가 있으면 그것이 참인 항목만.</summary>
    private static void OverlayItems(List<ViPrim> dst, ViOverlay overlay, in CvViewXform view, ITextMetricPort tm,
        double labelScale, Func<ViOverlayItem, bool>? include)
    {
        foreach (var item in overlay.Items)
        {
            if (include is not null && !include(item)) continue;
            var paint = ViPaint.Of(item.Color);
            // 굵기 배수 — 화면 고정 기본 굵기에 곱한다(줌과 무관). 이상한 값은 기본 굵기로, 범위 밖은 끝값으로(ViOverlayItem.StrokeScale 설명).
            var stroke = ViDrawGeom.ResolveStroke(item.StrokeScale);
            var line = new ViStroke(paint, ViDrawGeom.OverlayLineThick * stroke, item.IsDashed);
            switch (item)
            {
                case ViOverlaySeg seg:
                {
                    var p1 = view.ToScreen(seg.X1, seg.Y1);
                    var p2 = view.ToScreen(seg.X2, seg.Y2);
                    dst.Add(new ViPrimLine(line, p1, p2));
                    if (seg.HasEndArrow) Arrow(dst, paint, p1, p2, stroke);
                    break;
                }
                case ViOverlayLabel label:
                    Label(dst, label, view, tm, labelScale, paint);
                    break;
                case ViOverlayRect rect:
                    // 닫힌 도형 하나로 — 변마다 선분이면 끝이 평평해 모서리 바깥 (굵기/2)² 칸이 비어, 굵게 한 NG 박스의 네 모서리가
                    // 이 빠진 것처럼 보였다(배수 3 이면 3×3). 닫힌 도형은 모서리를 이어 그린다(마이터).
                    dst.Add(new ViPrimPath(line, ViDrawGeom.OverlayRectCorners(rect, view), 4, isClosed: true));
                    break;
                case ViOverlayPoly poly when poly.Points.Count >= 2:
                {
                    // 폴리라인은 <b>경로 하나</b>로 — 점마다 선분이면 드로잉 명령이 점 수만큼 쌓인다. 에지 블랍 윤곽은 한 장에 수천 점이
                    // 나오므로(실측 평균 3.2천·최대 5.6천), 줌·팬처럼 전체를 다시 그리는 조작마다 그 수만큼의 명령이 재생성돼 화면이 끊긴다.
                    // 화면 점 배열은 풀에서 빌린다 — 5.3천 점부터 대형 객체 힙으로 가서, 매 줌·팬마다 새로 만들면 그 쓰레기가 쌓인다.
                    var n = poly.Points.Count;
                    var pts = ArrayPool<(double X, double Y)>.Shared.Rent(n);
                    for (var i = 0; i < n; i++) pts[i] = view.ToScreen(poly.Points[i].X, poly.Points[i].Y);
                    dst.Add(new ViPrimPath(new ViStroke(paint, ViDrawGeom.OverlayPolyThick * stroke, poly.IsDashed), pts, n,
                        isClosed: poly.IsClosed && n >= 3, isPooled: true));
                    break;
                }
            }
        }
    }

    private static void Label(List<ViPrim> dst, ViOverlayLabel label, in CvViewXform view, ITextMetricPort tm, double labelScale, ViPaint paint)
    {
        // 글자 크기만 키운다 — 기준점·정렬은 그대로, 배경 상자는 글자의 잉크 경계를 따라간다. 모서리 요약 HUD 는 그대로다
        // (자르기와 같은 판정 — HUD 표식이 있어도 모서리 정렬이 아니면 붙은 모서리가 없으므로 다른 라벨처럼 다룬다).
        var sizePt = ViDrawGeom.LabelSizePt(label, labelScale);
        var dip = ViDrawGeom.LabelDip(sizePt);
        var m = tm.Measure(label.Text, dip, ViFontRole.Data, paint);
        var (x, y) = ViDrawGeom.LabelOrigin(label.Align, view.ToScreen(label.X, label.Y), m.Width, m.Height);
        if (label.HasBackground && tm.MeasureInk(m, label.Text, dip, ViFontRole.Data, x, y) is { } ink)
        {
            // 줄 높이(행간 포함)는 헐렁하므로 실제 잉크 경계 + 소폭 패딩으로 타이트하게.
            dst.Add(new ViPrimBox(LabelBack, Inflate(ink, 2, 2)));
        }
        if (label.LineColors is null)
        {
            dst.Add(new ViPrimText(label.Text, sizePt, dip, ViFontRole.Data, paint, (x, y), m));
            return;
        }
        // 줄마다 색이 다르면 한 번에 못 그린다 — 줄 단위로 나눈다. 줄 높이는 블록 높이를 줄 수로 나눠 쓴다(같은 폰트·같은 행간이라 균일).
        var textLines = label.Text.Split('\n');
        var lineH = m.Height / Math.Max(1, textLines.Length);
        for (var li = 0; li < textLines.Length; li++)
        {
            var lc = li < label.LineColors.Count ? label.LineColors[li] : null;
            dst.Add(new ViPrimText(textLines[li], sizePt, dip, ViFontRole.Data, lc is null ? paint : ViPaint.Of(lc.Value), (x, y + li * lineH), null));
        }
    }

    /// <summary>상자를 사방으로 부풀린다 — 폭·높이에 한쪽씩 두 번 더한다(WPF Rect.Inflate 와 같은 순서·같은 끝자리. Windows 회귀가 대조).</summary>
    internal static ViBox Inflate(ViBox b, double dx, double dy) => new(b.X - dx, b.Y - dy, b.W + dx + dx, b.H + dy + dy);

    /// <summary>편집 도형 전부 — 목록 순서대로.</summary>
    internal static void Shapes(List<ViPrim> dst, IReadOnlyList<CvEditShape> shapes, in CvViewXform view, ITextMetricPort tm)
    {
        foreach (var shape in shapes)
        {
            var paint = ViPaint.Of(shape.Color);
            var dash = new ViStroke(paint, ShapeLineThick, IsDashed: true);
            var guide = new ViStroke(paint, GuideThick, IsDashed: false);
            switch (shape)
            {
                case CvEditRect r:
                {
                    var corners = CvEditGeom.RectCorners(r);
                    var scr = new (double X, double Y)[4];
                    for (var i = 0; i < 4; i++) scr[i] = view.ToScreen(corners[i].X, corners[i].Y);
                    // 외곽선은 변마다 따로 — 점선 위상이 변마다 처음부터 시작하는 지금 모양. 경로 하나로 합치면 점선과 모서리가 바뀐다.
                    for (var i = 0; i < 4; i++) dst.Add(new ViPrimLine(dash, scr[i], scr[(i + 1) % 4]));
                    // 크기 고정 사각(IsResizable=false)은 그립 미표시 — 히트 판정과 짝 (이동만 가능함을 시각화).
                    if (r.IsResizable)
                    {
                        foreach (var (hx, hy) in corners) Handle(dst, paint, view.ToScreen(hx, hy));
                        // 면 핸들 — 외곽선(점선) 위 변 가운데만 실선으로 조금 굵게. 잡으면 그 변만 움직인다(반대 변 고정).
                        var edge = new ViStroke(paint, CvEditGeom.EdgeHandleThickPx, IsDashed: false);
                        foreach (var (_, e1, e2) in CvEditGeom.RectEdgeHandles(r, view)) dst.Add(new ViPrimLine(edge, e1, e2));
                    }
                    if (r.IsResizable && r.IsRotatable)
                    {
                        // 회전 그립 — 상변 중앙에서 바깥으로 화면 고정 길이만큼 뻗은 원형 핸들
                        var (gx, gy) = CvEditGeom.RectGrip(r, CvEditGeom.GripScreenPx / view.Scale);
                        var topMid = view.ToScreen((corners[0].X + corners[1].X) / 2, (corners[0].Y + corners[1].Y) / 2);
                        var grip = view.ToScreen(gx, gy);
                        dst.Add(new ViPrimLine(guide, topMid, grip));
                        dst.Add(new ViPrimDot(paint, grip, CvEditGeom.HandleScreenPx / 2 + 1));
                    }
                    ShapeLabel(dst, shape, tm, paint, scr[0], 0, 2);
                    break;
                }
                case CvEditSeg s:
                {
                    var p1 = view.ToScreen(s.X1, s.Y1);
                    var p2 = view.ToScreen(s.X2, s.Y2);
                    dst.Add(new ViPrimLine(dash, p1, p2));
                    // 방향 화살표(P1→P2) — 스캔 진행/에지 극성 해석이 세그먼트 방향에 걸려 있어 티칭 중 시인 필요
                    Arrow(dst, paint, p1, p2, 1.0);
                    Handle(dst, paint, p1);
                    Handle(dst, paint, p2);
                    ShapeLabel(dst, shape, tm, paint, ((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2), 0, 4);
                    break;
                }
                case CvEditCircle circle:
                {
                    // 원주 + 중심 십자 + 반경 그립(우측 0°, 중심 연결선).
                    Polyline(dst, dash, ViDrawGeom.CirclePoints(circle.CenterX, circle.CenterY, circle.Radius, view));
                    var c = view.ToScreen(circle.CenterX, circle.CenterY);
                    Cross(dst, guide, c);
                    var pRad = view.ToScreen(circle.CenterX + circle.Radius, circle.CenterY);
                    dst.Add(new ViPrimLine(guide, c, pRad));
                    Handle(dst, paint, pRad);
                    ShapeLabel(dst, shape, tm, paint, pRad, 6, 2);
                    break;
                }
                case CvEditRing ring:
                {
                    // min/max 동심원 쌍 + 중심 십자 + 반경 그립 (안=좌측 180°, 바깥=우측 0°).
                    var c = view.ToScreen(ring.CenterX, ring.CenterY);
                    Polyline(dst, dash, ViDrawGeom.CirclePoints(ring.CenterX, ring.CenterY, ring.RMin, view));
                    Polyline(dst, dash, ViDrawGeom.CirclePoints(ring.CenterX, ring.CenterY, ring.RMax, view));
                    Cross(dst, guide, c);
                    var pIn = view.ToScreen(ring.CenterX - ring.RMin, ring.CenterY);
                    var pOut = view.ToScreen(ring.CenterX + ring.RMax, ring.CenterY);
                    Handle(dst, paint, pIn);
                    Handle(dst, paint, pOut);
                    ShapeLabel(dst, shape, tm, paint, pOut, 6, 2);
                    break;
                }
                case CvEditArc arc:
                {
                    // 원호 폴리라인 — 스팬 비례 분할, 시작→끝(+방향). 끝 화살촉이 진행 방향을 가리킨다(라인 파인더 화살표와 같은 목적).
                    var arcPts = ViDrawGeom.ArcPoints(arc, view);
                    Polyline(dst, dash, arcPts);
                    Arrow(dst, paint, arcPts[arcPts.Length - 2], arcPts[arcPts.Length - 1], 1.0);
                    var c = view.ToScreen(arc.CenterX, arc.CenterY);
                    Cross(dst, guide, c);   // 중심 십자 (이동 히트 지점 겸)
                    // 그립 — 0=시작각, 1=스팬 끝, 2=반경(중간각, 중심 연결선)
                    var a0 = CvEditGeom.ArcPoint(arc, 0);
                    var a1 = CvEditGeom.ArcPoint(arc, arc.SpanDeg);
                    var a2 = CvEditGeom.ArcPoint(arc, arc.SpanDeg / 2);
                    var pStart = view.ToScreen(a0.X, a0.Y);
                    var pEnd = view.ToScreen(a1.X, a1.Y);
                    var pRad = view.ToScreen(a2.X, a2.Y);
                    dst.Add(new ViPrimLine(guide, c, pRad));
                    Handle(dst, paint, pStart);
                    Handle(dst, paint, pEnd);
                    dst.Add(new ViPrimDot(paint, pRad, CvEditGeom.HandleScreenPx / 2 + 1));
                    ShapeLabel(dst, shape, tm, paint, pStart, 4, 4);
                    break;
                }
            }
        }
    }

    /// <summary>무이미지 안내 — 표면 가운데에 제목(과 불러오기 힌트). 표면이 너무 작으면 아무것도 안 낸다. <paramref name="paint"/> 가 없으면 <see cref="HintGray"/>.</summary>
    internal static void Placeholder(List<ViPrim> dst, double viewW, double viewH, string title, string? hint, ITextMetricPort tm, ViPaint? paint = null)
    {
        if (viewW < 60 || viewH < 40) return;
        var p = paint ?? HintGray;
        var titleDip = ViDrawGeom.UiDip(14);
        var tm1 = tm.Measure(title, titleDip, ViFontRole.Ui, p);
        var cx = viewW / 2;
        var cy = viewH / 2;
        if (hint is null)
        {
            // 힌트가 빠지면 제목 혼자 남으므로 두 줄 기준의 위쪽 자리가 아니라 정가운데로 온다.
            dst.Add(new ViPrimText(title, 14, titleDip, ViFontRole.Ui, p, (cx - tm1.Width / 2, cy - tm1.Height / 2), tm1));
            return;
        }
        var hintDip = ViDrawGeom.UiDip(11);
        var tm2 = tm.Measure(hint, hintDip, ViFontRole.Ui, p);
        dst.Add(new ViPrimText(title, 14, titleDip, ViFontRole.Ui, p, (cx - tm1.Width / 2, cy - tm1.Height - 2), tm1));
        dst.Add(new ViPrimText(hint, 11, hintDip, ViFontRole.Ui, p, (cx - tm2.Width / 2, cy + 2), tm2));
    }

    /// <summary>편집 도형 이름표 — 기준점에서 오른쪽 dx, 글자 블록 높이 + dy 만큼 위.</summary>
    private static void ShapeLabel(List<ViPrim> dst, CvEditShape shape, ITextMetricPort tm, ViPaint paint, (double X, double Y) at, double dx, double dy)
    {
        if (shape.Label.Length == 0) return;
        var dip = ViDrawGeom.LabelDip(ShapeLabelPt);
        var m = tm.Measure(shape.Label, dip, ViFontRole.Data, paint);
        // 오른쪽으로 밀지 않는 이름표는 기준점 X 를 그대로 쓴다 — 0 을 더하면 -0.0 이 +0.0 으로 바뀐다(화면은 같지만 값이 달라진다).
        dst.Add(new ViPrimText(shape.Label, ShapeLabelPt, dip, ViFontRole.Data, paint, (dx == 0 ? at.X : at.X + dx, at.Y - m.Height - dy), m));
    }

    private static void Handle(List<ViPrim> dst, ViPaint paint, (double X, double Y) p)
    {
        const double s = CvEditGeom.HandleScreenPx;
        dst.Add(new ViPrimBox(paint, new ViBox(p.X - s / 2, p.Y - s / 2, s, s)));
    }

    private static void Cross(List<ViPrim> dst, ViStroke stroke, (double X, double Y) c)
    {
        dst.Add(new ViPrimLine(stroke, (c.X - CrossHalfPx, c.Y), (c.X + CrossHalfPx, c.Y)));
        dst.Add(new ViPrimLine(stroke, (c.X, c.Y - CrossHalfPx), (c.X, c.Y + CrossHalfPx)));
    }

    /// <summary>점 목록을 이웃끼리 선분으로 — 편집 원주·원호용. 선분마다 따로 낸다(지금 점선 모양 — 경로 하나로 합치지 않는다).</summary>
    private static void Polyline(List<ViPrim> dst, ViStroke stroke, (double X, double Y)[] pts)
    {
        for (var i = 1; i < pts.Length; i++) dst.Add(new ViPrimLine(stroke, pts[i - 1], pts[i]));
    }

    private static void Arrow(List<ViPrim> dst, ViPaint paint, (double X, double Y) from, (double X, double Y) to, double scale)
    {
        if (ViDrawGeom.ArrowHead(from, to, scale) is not { } head) return;
        dst.Add(new ViPrimFill(paint, [head.Tip, head.Left, head.Right]));
    }
}
