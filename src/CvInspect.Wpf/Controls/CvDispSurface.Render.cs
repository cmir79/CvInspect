// CvDispSurface 의 그리기 — OnRender 파이프라인(이미지·결과 오버레이·편집 도형)과
// 도형 기하 계산, 텍스트·화살촉·핸들 등 렌더 헬퍼.

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CvInspect.Vision.Overlay;
using CvInspect.Vision.Edit;

namespace CvInspect.Controls;

internal sealed partial class CvDispSurface
{
    private const double HandleScreenPx = 7;   // 핸들 화면 크기(줌 무관)

    // === 렌더 ===

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _fitPending = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));   // 히트 영역 확보
        if (_bitmap is null || _imgW == 0)
        {
            RenderPlaceholder(dc);
            return;
        }

        if (_fitPending) ApplyFit();

        dc.PushTransform(new MatrixTransform(_view));
        dc.DrawImage(_bitmap, new Rect(0, 0, _imgW, _imgH));
        dc.Pop();

        if (_overlay is not null) RenderOverlay(dc, _overlay);
        if (_shapes is not null) RenderShapes(dc, _shapes);
    }

    /// <summary>무이미지 플레이스홀더 — 중앙 안내 문구만 그린다 (히트 없음 — 우클릭 메뉴 등 상위 인터랙션 유지).</summary>
    private void RenderPlaceholder(DrawingContext dc)
    {
        if (ActualWidth < 60 || ActualHeight < 40) return;
        var ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var gray = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
        // 안내 문구는 UI 폰트(Segoe UI) — 데이터 표기용 오버레이 라벨(Consolas Bold, Format 헬퍼)과 구분.
        var title = FormatUi(CvLoc.T("cv:NoImage"), 14, gray, ppd);
        var cx = ActualWidth / 2;
        var cy = ActualHeight / 2;

        if (!_showLoadHint)
        {
            // 힌트가 빠지면 제목 혼자 남으므로 두 줄 기준의 위쪽 자리가 아니라 정가운데로 온다.
            dc.DrawText(title, new System.Windows.Point(cx - title.Width / 2, cy - title.Height / 2));
            return;
        }

        var hint = FormatUi(CvLoc.T("cv:NoImageHint"), 11, gray, ppd);
        dc.DrawText(title, new System.Windows.Point(cx - title.Width / 2, cy - title.Height - 2));
        dc.DrawText(hint, new System.Windows.Point(cx - hint.Width / 2, cy + 2));
    }

    private static FormattedText FormatUi(string text, float sizePt, Brush brush, double ppd)
        => new(text, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            sizePt * 96.0 / 72.0, brush, ppd);

    private void ApplyFit()
    {
        _fitPending = false;
        if (ActualWidth < 8 || ActualHeight < 8 || _imgW == 0) return;
        var scale = Math.Min(ActualWidth / _imgW, ActualHeight / _imgH) * 0.97;
        scale = Math.Clamp(scale, MinScale, MaxScale);
        var m = Matrix.Identity;
        m.Scale(scale, scale);
        m.Translate((ActualWidth - _imgW * scale) / 2, (ActualHeight - _imgH * scale) / 2);
        _view = m;
        StatusChanged?.Invoke();   // 컨트롤이 디스패처 경유로 갱신 — OnRender 중 호출 안전
    }

    private void RenderOverlay(DrawingContext dc, ViOverlay overlay)
    {
        var ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var item in overlay.Items)
        {
            var brush = MapBrush(item.Color);
            var pen = new Pen(brush, 2);
            if (item.IsDashed) pen.DashStyle = DashStyles.Dash;
            pen.Freeze();   // 렌더 중 변경되지 않는 펜 — 얼려 두면 변경 추적 비용이 빠진다
            switch (item)
            {
                case ViOverlaySeg seg:
                {
                    var p1 = ToScreen(seg.X1, seg.Y1);
                    var p2 = ToScreen(seg.X2, seg.Y2);
                    dc.DrawLine(pen, p1, p2);
                    if (seg.HasEndArrow) DrawArrowHead(dc, brush, p1, p2);
                    break;
                }
                case ViOverlayLabel label:
                {
                    var ft = Format(label.Text, label.FontSize, brush, ppd);
                    var anchor = ToScreen(label.X, label.Y);
                    var x = label.Align switch
                    {
                        ViOverlayAlign.TopLeft or ViOverlayAlign.BottomLeft => anchor.X,
                        ViOverlayAlign.TopRight or ViOverlayAlign.BottomRight => anchor.X - ft.Width,
                        _ => anchor.X - ft.Width / 2,
                    };
                    var y = label.Align is ViOverlayAlign.BottomCenter or ViOverlayAlign.BottomLeft or ViOverlayAlign.BottomRight
                        ? anchor.Y - ft.Height
                        : anchor.Y;
                    if (label.HasBackground)
                    {
                        // 검은 배경 박스 — FormattedText.Height 는 줄높이(행간 포함)라 헐렁하므로
                        // 실제 잉크 경계(BuildGeometry.Bounds) 기준 + 소폭 패딩으로 타이트하게.
                        var ink = ft.BuildGeometry(new System.Windows.Point(x, y)).Bounds;
                        if (!ink.IsEmpty)
                        {
                            var back = new SolidColorBrush(System.Windows.Media.Color.FromArgb(200, 0, 0, 0));
                            back.Freeze();
                            ink.Inflate(2, 2);
                            dc.DrawRectangle(back, null, ink);
                        }
                    }
                    if (label.LineColors is null)
                    {
                        dc.DrawText(ft, new System.Windows.Point(x, y));
                    }
                    else
                    {
                        // 줄마다 색이 다르면 한 번에 못 그린다 — 줄 단위로 나눠 그린다.
                        // 줄 높이는 블록 높이를 줄 수로 나눠 쓴다(같은 폰트·같은 행간이라 균일).
                        var textLines = label.Text.Split('\n');
                        var lineH = ft.Height / Math.Max(1, textLines.Length);
                        for (var li = 0; li < textLines.Length; li++)
                        {
                            var lc = li < label.LineColors.Count ? label.LineColors[li] : null;
                            var lineFt = Format(textLines[li], label.FontSize, lc is null ? brush : MapBrush(lc.Value), ppd);
                            dc.DrawText(lineFt, new System.Windows.Point(x, y + li * lineH));
                        }
                    }
                    break;
                }
                case ViOverlayRect rect:
                {
                    var rad = rect.AngleDeg * Math.PI / 180.0;
                    var cos = Math.Cos(rad);
                    var sin = Math.Sin(rad);
                    var hw = rect.Width / 2;
                    var hh = rect.Height / 2;
                    var corners = new[] { (-hw, -hh), (hw, -hh), (hw, hh), (-hw, hh) };
                    var pts = new System.Windows.Point[4];
                    for (var i = 0; i < 4; i++)
                    {
                        var (ox, oy) = corners[i];
                        pts[i] = ToScreen(rect.CenterX + cos * ox - sin * oy, rect.CenterY + sin * ox + cos * oy);
                    }
                    for (var i = 0; i < 4; i++) dc.DrawLine(pen, pts[i], pts[(i + 1) % 4]);
                    break;
                }
                case ViOverlayPoly poly when poly.Points.Count >= 2:
                {
                    // 폴리라인은 <b>지오메트리 하나</b>로 낸다 — 점마다 DrawLine 을 부르면 드로잉 명령이
                    // 점 수만큼 쌓인다. 에지 블랍 윤곽은 한 장에 수천 점이 나오므로(실측 평균 3.2천·최대 5.6천),
                    // 줌·팬처럼 전체를 다시 그리는 조작마다 그 수만큼의 명령이 재생성돼 화면이 끊긴다.
                    // 점선은 선분마다 dash 를 계산하느라 그 비용이 더 커진다.
                    var thin = new Pen(brush, 1.2);
                    if (poly.IsDashed) thin.DashStyle = DashStyles.Dash;
                    thin.Freeze();

                    var geo = new StreamGeometry();
                    using (var ctx = geo.Open())
                    {
                        ctx.BeginFigure(ToScreen(poly.Points[0].X, poly.Points[0].Y),
                            isFilled: false, isClosed: poly.IsClosed && poly.Points.Count >= 3);
                        for (var i = 1; i < poly.Points.Count; i++)
                            ctx.LineTo(ToScreen(poly.Points[i].X, poly.Points[i].Y), isStroked: true, isSmoothJoin: false);
                    }
                    geo.Freeze();

                    dc.DrawGeometry(null, thin, geo);
                    break;
                }
            }
        }
    }

    private void RenderShapes(DrawingContext dc, IReadOnlyList<CvEditShape> shapes)
    {
        var ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var shape in shapes)
        {
            var brush = MapBrush(shape.Color);
            var pen = new Pen(brush, 2) { DashStyle = DashStyles.Dash };
            switch (shape)
            {
                case CvEditRect r:
                {
                    var corners = RectCorners(r);
                    var scr = new System.Windows.Point[4];
                    for (var i = 0; i < 4; i++) scr[i] = ToScreen(corners[i].X, corners[i].Y);
                    for (var i = 0; i < 4; i++) dc.DrawLine(pen, scr[i], scr[(i + 1) % 4]);
                    // 크기 고정 사각(IsResizable=false)은 그립 미표시 — 히트 판정과 짝 (이동만 가능함을 시각화).
                    if (r.IsResizable)
                        foreach (var (hx, hy) in corners)
                            DrawHandle(dc, brush, ToScreen(hx, hy));

                    if (r.IsResizable && r.IsRotatable)
                    {
                        // 회전 그립 — 상변 중앙에서 바깥으로 화면 고정 길이만큼 뻗은 원형 핸들
                        var (gx, gy) = RectGrip(r, GripScreenPx / Scale);
                        var topMid = ToScreen((corners[0].X + corners[1].X) / 2, (corners[0].Y + corners[1].Y) / 2);
                        var grip = ToScreen(gx, gy);
                        dc.DrawLine(new Pen(brush, 1.2), topMid, grip);
                        dc.DrawEllipse(brush, null, grip, HandleScreenPx / 2 + 1, HandleScreenPx / 2 + 1);
                    }

                    if (shape.Label.Length > 0)
                    {
                        var ft = Format(shape.Label, 12, brush, ppd);
                        dc.DrawText(ft, new System.Windows.Point(scr[0].X, scr[0].Y - ft.Height - 2));
                    }
                    break;
                }
                case CvEditSeg s:
                {
                    var p1 = ToScreen(s.X1, s.Y1);
                    var p2 = ToScreen(s.X2, s.Y2);
                    dc.DrawLine(pen, p1, p2);
                    // 방향 화살표(P1→P2) — 스캔 진행/에지 극성 해석이 세그먼트 방향에 걸려 있어 티칭 중 시인 필요
                    DrawArrowHead(dc, brush, p1, p2);
                    DrawHandle(dc, brush, p1);
                    DrawHandle(dc, brush, p2);
                    if (shape.Label.Length > 0)
                    {
                        var ft = Format(shape.Label, 12, brush, ppd);
                        dc.DrawText(ft, new System.Windows.Point((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2 - ft.Height - 4));
                    }
                    break;
                }
                case CvEditCircle circle:
                {
                    // 원주 + 중심 십자 + 반경 그립(우측 0°, 중심 연결선).
                    DrawCirclePolyline(dc, pen, circle.CenterX, circle.CenterY, circle.Radius);

                    var c = ToScreen(circle.CenterX, circle.CenterY);
                    dc.DrawLine(new Pen(brush, 1.2), new System.Windows.Point(c.X - 7, c.Y), new System.Windows.Point(c.X + 7, c.Y));
                    dc.DrawLine(new Pen(brush, 1.2), new System.Windows.Point(c.X, c.Y - 7), new System.Windows.Point(c.X, c.Y + 7));

                    var pRad = ToScreen(circle.CenterX + circle.Radius, circle.CenterY);
                    dc.DrawLine(new Pen(brush, 1.2), c, pRad);
                    DrawHandle(dc, brush, pRad);

                    if (shape.Label.Length > 0)
                    {
                        var ft = Format(shape.Label, 12, brush, ppd);
                        dc.DrawText(ft, new System.Windows.Point(pRad.X + 6, pRad.Y - ft.Height - 2));
                    }
                    break;
                }
                case CvEditRing ring:
                {
                    // min/max 동심원 쌍 — 안(밝은 대시)/바깥 원 + 중심 십자 + 반경 그립 (안=좌측 180°, 바깥=우측 0°).
                    var c = ToScreen(ring.CenterX, ring.CenterY);
                    DrawCirclePolyline(dc, pen, ring.CenterX, ring.CenterY, ring.RMin);
                    DrawCirclePolyline(dc, pen, ring.CenterX, ring.CenterY, ring.RMax);

                    dc.DrawLine(new Pen(brush, 1.2), new System.Windows.Point(c.X - 7, c.Y), new System.Windows.Point(c.X + 7, c.Y));
                    dc.DrawLine(new Pen(brush, 1.2), new System.Windows.Point(c.X, c.Y - 7), new System.Windows.Point(c.X, c.Y + 7));

                    var pIn = ToScreen(ring.CenterX - ring.RMin, ring.CenterY);
                    var pOut = ToScreen(ring.CenterX + ring.RMax, ring.CenterY);
                    DrawHandle(dc, brush, pIn);
                    DrawHandle(dc, brush, pOut);

                    if (shape.Label.Length > 0)
                    {
                        var ft = Format(shape.Label, 12, brush, ppd);
                        dc.DrawText(ft, new System.Windows.Point(pOut.X + 6, pOut.Y - ft.Height - 2));
                    }
                    break;
                }
                case CvEditArc arc:
                {
                    // 원호 폴리라인 — 스팬 비례 분할. 시작→끝(+방향) 순서로 그려 화살촉이 진행 방향을 가리킴.
                    var segs = Math.Max(12, (int)Math.Round(arc.SpanDeg / 5.0));
                    var prev = ToScreen(ArcPoint(arc, 0).X, ArcPoint(arc, 0).Y);
                    var beforeEnd = prev;
                    for (var i = 1; i <= segs; i++)
                    {
                        var t = arc.SpanDeg * i / segs;
                        var cur = ToScreen(ArcPoint(arc, t).X, ArcPoint(arc, t).Y);
                        dc.DrawLine(pen, prev, cur);
                        beforeEnd = prev;
                        prev = cur;
                    }
                    // 스팬 끝 화살촉 — 진행(+deg) 방향 시인 (라인 파인더 화살표와 동일 목적)
                    DrawArrowHead(dc, brush, beforeEnd, prev);

                    // 중심 십자 (이동 히트 지점 겸)
                    var c = ToScreen(arc.CenterX, arc.CenterY);
                    dc.DrawLine(new Pen(brush, 1.2), new System.Windows.Point(c.X - 7, c.Y), new System.Windows.Point(c.X + 7, c.Y));
                    dc.DrawLine(new Pen(brush, 1.2), new System.Windows.Point(c.X, c.Y - 7), new System.Windows.Point(c.X, c.Y + 7));

                    // 그립 — 0=시작각, 1=스팬 끝, 2=반경(중간각, 중심 연결선)
                    var pStart = ToScreen(ArcPoint(arc, 0).X, ArcPoint(arc, 0).Y);
                    var pEnd = ToScreen(ArcPoint(arc, arc.SpanDeg).X, ArcPoint(arc, arc.SpanDeg).Y);
                    var pRad = ToScreen(ArcPoint(arc, arc.SpanDeg / 2).X, ArcPoint(arc, arc.SpanDeg / 2).Y);
                    dc.DrawLine(new Pen(brush, 1.2), c, pRad);
                    DrawHandle(dc, brush, pStart);
                    DrawHandle(dc, brush, pEnd);
                    dc.DrawEllipse(brush, null, pRad, HandleScreenPx / 2 + 1, HandleScreenPx / 2 + 1);

                    if (shape.Label.Length > 0)
                    {
                        var ft = Format(shape.Label, 12, brush, ppd);
                        dc.DrawText(ft, new System.Windows.Point(pStart.X + 4, pStart.Y - ft.Height - 4));
                    }
                    break;
                }
            }
        }
    }

    /// <summary>원호 위 점(이미지 공간) — spanOffsetDeg 는 시작각으로부터의 + 방향 오프셋.</summary>
    private static (double X, double Y) ArcPoint(CvEditArc arc, double spanOffsetDeg)
    {
        var rad = (arc.StartDeg + spanOffsetDeg) * Math.PI / 180.0;
        return (arc.CenterX + arc.Radius * Math.Cos(rad), arc.CenterY + arc.Radius * Math.Sin(rad));
    }

    /// <summary>전체 원 폴리라인 (72 분할) — 링 도형 등 비편집 원주 표시용.</summary>
    private void DrawCirclePolyline(DrawingContext dc, Pen pen, double cx, double cy, double r)
    {
        const int segs = 72;
        var prev = ToScreen(cx + r, cy);
        for (var i = 1; i <= segs; i++)
        {
            var t = i * Math.PI * 2 / segs;
            var cur = ToScreen(cx + r * Math.Cos(t), cy + r * Math.Sin(t));
            dc.DrawLine(pen, prev, cur);
            prev = cur;
        }
    }

    private const double GripScreenPx = 26;   // 회전 그립의 상변 이탈 거리(화면 고정)

    /// <summary>사각 중심(이미지 공간).</summary>
    private static (double X, double Y) RectCenter(CvEditRect r) => (r.X + r.W / 2, r.Y + r.H / 2);

    /// <summary>회전 적용된 4 모서리(이미지 공간) — 좌상/우상/우하/좌하 순 (핸들 인덱스 0..3).</summary>
    private static (double X, double Y)[] RectCorners(CvEditRect r)
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
    private static (double X, double Y) RectGrip(CvEditRect r, double gripImgLen)
    {
        var (cx, cy) = RectCenter(r);
        var rad = r.AngleDeg * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var ly = -(r.H / 2 + gripImgLen);
        return (cx - sin * ly, cy + cos * ly);
    }

    /// <summary>이미지 좌표 → 사각 로컬 좌표 (중심 원점, 회전 해제).</summary>
    private static (double X, double Y) ToRectLocal(CvEditRect r, double imgX, double imgY)
    {
        var (cx, cy) = RectCenter(r);
        var rad = -r.AngleDeg * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var dx = imgX - cx;
        var dy = imgY - cy;
        return (cos * dx - sin * dy, sin * dx + cos * dy);
    }

    private static void DrawHandle(DrawingContext dc, Brush brush, System.Windows.Point p)
        => dc.DrawRectangle(brush, null,
            new Rect(p.X - HandleScreenPx / 2, p.Y - HandleScreenPx / 2, HandleScreenPx, HandleScreenPx));

    private static void DrawArrowHead(DrawingContext dc, Brush brush, System.Windows.Point from, System.Windows.Point to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 2) return;
        var ux = dx / len;
        var uy = dy / len;
        const double headLen = 12;
        const double headHalf = 4.5;
        var bx = to.X - ux * headLen;
        var by = to.Y - uy * headLen;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(to, true, true);
            ctx.LineTo(new System.Windows.Point(bx - uy * headHalf, by + ux * headHalf), false, false);
            ctx.LineTo(new System.Windows.Point(bx + uy * headHalf, by - ux * headHalf), false, false);
        }
        geo.Freeze();
        dc.DrawGeometry(brush, null, geo);
    }

    private static FormattedText Format(string text, float sizePt, Brush brush, double ppd)
        => new(text, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(new System.Windows.Media.FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            sizePt * 96.0 / 72.0, brush, ppd);

    private static Brush MapBrush(ViOverlayColor color) => color switch
    {
        // Green/Teal 은 어두운 톤 — 조명으로 하얗게 뜨는 제품 위에서 순색이 씻겨 안 보임.
        // Cyan(순색) 은 검사 결과 측정 기하용, Teal 은 티칭 편집 도형(Search/Band)용 — 용도별 분리.
        ViOverlayColor.Green => Brushes.Green,
        ViOverlayColor.Red => Brushes.Red,
        ViOverlayColor.Cyan => Brushes.Cyan,
        ViOverlayColor.Teal => Brushes.Teal,
        ViOverlayColor.Orange => Brushes.Orange,
        ViOverlayColor.Yellow => Brushes.Yellow,
        _ => Brushes.White,
    };
}
