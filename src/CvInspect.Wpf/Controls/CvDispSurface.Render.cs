// CvDispSurface 의 그리기 — OnRender 파이프라인(이미지·결과 오버레이·편집 도형)과 WPF 그리기 헬퍼.
// 무엇을 어디에 그릴지(꼭짓점·화살촉·원주 분할·굵기·글자 크기)는 UI 중립 계층(CvInspect.Display)이 정하고,
// 여기는 그것을 펜·브러시·지오메트리로 그린다.

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CvInspect.Display;
using CvInspect.Vision.Overlay;
using CvInspect.Vision.Edit;

namespace CvInspect.Controls;

internal sealed partial class CvDispSurface
{
    // === 렌더 ===

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        // 맞춤을 예약만 하지 않고 다시 그리기를 함께 청한다. 크기가 바뀐 배치에서 WPF 는 Arrange 안에서 OnRender 를
        // 먼저 부르고(그때는 예약이 없어 옛 배율로 그린다) 이 통지는 배치가 끝난 뒤에 보낸다(PresentationCore 8.0.31
        // UIElement.Arrange → ContextLayoutManager.UpdateLayout 의 fireSizeChangedEvents, 원문 확인). 예약만 세우고
        // 나가면 두 가지가 남는다 — 실측(1000×500 영상, 표면 800→1400 폭, 새 프레임 없음):
        //  ① 다음 프레임이 올 때까지 옛 배율: 776px 그대로(맞춤은 1126px). 프레임이 드문 화면(관제·라인 정지 중)에서
        //     창이 커지면 여백, 작아지면 잘림이 그대로 남았다.
        //  ② 다음 그리기가 무엇이든 예약을 소화한다: 휠 한 칸 축소가 621px 이 아니라 1126px 로 커졌다(줌이 덮인다).
        // 부모 쪽 SizeChanged 에서 무효화해서는 안 된다 — 이 통지는 바깥 요소부터 나가고, 핸들러가 다시 그리기를
        // 청하면 WPF 가 통지를 멈추고 배치부터 다시 해서 이 표면은 예약이 서기 전에 다시 그려진다(실측 776px 그대로).
        // 여기서는 예약이 먼저 서 있으니 이 요청이 부르는 그리기가 맞춘다. 대가는 크기가 바뀔 때 OnRender 한 번 더.
        _fitPending = true;
        InvalidateVisual();
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

        var viewMatrix = ViewMatrix();
        dc.PushTransform(new MatrixTransform(viewMatrix));
        dc.DrawImage(_bitmap, new Rect(0, 0, _imgW, _imgH));
        dc.Pop();

        if (_overlay is not null)
        {
            // 켜면 이미지가 차지한 화면 사각으로 자른다(근거·실측은 CvDispCtrl.ClipOverlayToImage 주석). 끄면(기본) 이 표면의
            // 경계가 유일한 클립이라 맞춤 여백에도 그려진다.
            // 모서리 HUD 는 켜도 자르지 않고 그 뒤에 그린다 — 글자가 화면 고정 크기라 이미지가 작게 그려지면 블록이 이미지보다
            // 커지는데, 자르면 판정 사유 줄이 사라지고 남은 헤더 한 줄이 온전한 HUD 처럼 보인다(게시 전 검토 실측:
            // 1000×60 띠를 400×400 칸에 → HUD 픽셀 83% 소실). 편집 도형도 자르지 않는다 — 가장자리 핸들을 잡을 수 있어야 한다.
            if (_clipOverlayToImage)
            {
                dc.PushClip(new RectangleGeometry(Rect.Transform(new Rect(0, 0, _imgW, _imgH), viewMatrix)));
                RenderOverlay(dc, _overlay, item => !ViDrawGeom.IsCornerHud(item));
                dc.Pop();
                RenderOverlay(dc, _overlay, ViDrawGeom.IsCornerHud);
            }
            else
            {
                RenderOverlay(dc, _overlay, null);
            }
        }
        if (_shapes is not null) RenderShapes(dc, _shapes);
    }

    /// <summary>뷰 변환을 WPF 행렬로 — 이미지 그리기와 클립 사각에 쓴다(등방 배율 + 이동).</summary>
    private Matrix ViewMatrix() => new(_view.Scale, 0, 0, _view.Scale, _view.OffsetX, _view.OffsetY);

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
        _fitPending = false;   // 맞추지 못해도(표면이 너무 작음) 예약은 소화한다 — 다음 크기 변경이 다시 세운다
        if (!CvViewXform.TryFit(ActualWidth, ActualHeight, _imgW, _imgH, out var fit)) return;
        _view = fit;
        StatusChanged?.Invoke();   // 컨트롤이 디스패처 경유로 갱신 — OnRender 중 호출 안전
    }

    /// <summary>오버레이 항목을 목록 순서대로 그린다. <paramref name="include"/> 가 있으면 그것이 참인 항목만.</summary>
    private void RenderOverlay(DrawingContext dc, ViOverlay overlay, Func<ViOverlayItem, bool>? include)
    {
        var ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var item in overlay.Items)
        {
            if (include is not null && !include(item)) continue;
            var brush = MapBrush(item.Color);
            // 굵기 배수 — 화면 고정 기본 굵기에 곱한다(줌과 무관). 이상한 값은 기본 굵기로, 범위 밖은 끝값으로(ViOverlayItem.StrokeScale 설명).
            var stroke = ViDrawGeom.ResolveStroke(item.StrokeScale);
            var pen = new Pen(brush, ViDrawGeom.OverlayLineThick * stroke);
            if (item.IsDashed) pen.DashStyle = DashStyles.Dash;
            pen.Freeze();   // 렌더 중 변경되지 않는 펜 — 얼려 두면 변경 추적 비용이 빠진다
            switch (item)
            {
                case ViOverlaySeg seg:
                {
                    var p1 = ToScreen(seg.X1, seg.Y1);
                    var p2 = ToScreen(seg.X2, seg.Y2);
                    dc.DrawLine(pen, p1, p2);
                    if (seg.HasEndArrow) DrawArrowHead(dc, brush, p1, p2, stroke);
                    break;
                }
                case ViOverlayLabel label:
                {
                    // 글자 크기만 키운다 — 기준점·정렬은 그대로, 배경 상자는 아래에서 글자의 잉크 경계를 따라간다. 모서리 요약 HUD 는 그대로다
                    // (자르기와 같은 판정 — HUD 표식이 있어도 모서리 정렬이 아니면 붙은 모서리가 없으므로 다른 라벨처럼 다룬다).
                    var sizePt = ViDrawGeom.LabelSizePt(label, _overlayLabelScale);
                    var ft = Format(label.Text, sizePt, brush, ppd);
                    var anchor = ToScreen(label.X, label.Y);
                    var (x, y) = ViDrawGeom.LabelOrigin(label.Align, (anchor.X, anchor.Y), ft.Width, ft.Height);
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
                            var lineFt = Format(textLines[li], sizePt, lc is null ? brush : MapBrush(lc.Value), ppd);
                            dc.DrawText(lineFt, new System.Windows.Point(x, y + li * lineH));
                        }
                    }
                    break;
                }
                case ViOverlayRect rect:
                {
                    var pts = ViDrawGeom.OverlayRectCorners(rect, _view);
                    // 닫힌 도형 하나로 그린다 — 변마다 DrawLine 이면 끝이 평평해 모서리 바깥 (굵기/2)² 칸이 비어, 굵게 한 NG 박스의
                    // 네 모서리가 이 빠진 것처럼 보였다(배수 3 이면 3×3). 닫힌 도형은 모서리를 이어 그린다(마이터).
                    var box = new StreamGeometry();
                    using (var ctx = box.Open())
                    {
                        ctx.BeginFigure(Pt(pts[0]), isFilled: false, isClosed: true);
                        for (var i = 1; i < 4; i++) ctx.LineTo(Pt(pts[i]), isStroked: true, isSmoothJoin: false);
                    }
                    box.Freeze();
                    dc.DrawGeometry(null, pen, box);
                    break;
                }
                case ViOverlayPoly poly when poly.Points.Count >= 2:
                {
                    // 폴리라인은 <b>지오메트리 하나</b>로 낸다 — 점마다 DrawLine 을 부르면 드로잉 명령이
                    // 점 수만큼 쌓인다. 에지 블랍 윤곽은 한 장에 수천 점이 나오므로(실측 평균 3.2천·최대 5.6천),
                    // 줌·팬처럼 전체를 다시 그리는 조작마다 그 수만큼의 명령이 재생성돼 화면이 끊긴다.
                    // 점선은 선분마다 dash 를 계산하느라 그 비용이 더 커진다.
                    var thin = new Pen(brush, ViDrawGeom.OverlayPolyThick * stroke);
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
                    var corners = CvEditGeom.RectCorners(r);
                    var scr = new System.Windows.Point[4];
                    for (var i = 0; i < 4; i++) scr[i] = ToScreen(corners[i].X, corners[i].Y);
                    // 편집 외곽선은 변마다 따로 그린다 — 점선 위상이 변마다 처음부터 시작하는 지금 모양. 닫힌 경로 하나로 합치면 점선과 모서리가 바뀐다.
                    for (var i = 0; i < 4; i++) dc.DrawLine(pen, scr[i], scr[(i + 1) % 4]);
                    // 크기 고정 사각(IsResizable=false)은 그립 미표시 — 히트 판정과 짝 (이동만 가능함을 시각화).
                    if (r.IsResizable)
                    {
                        foreach (var (hx, hy) in corners)
                            DrawHandle(dc, brush, ToScreen(hx, hy));
                        // 면 핸들 — 외곽선(점선) 위 변 가운데만 실선으로 조금 굵게. 잡으면 그 변만 움직인다(반대 변 고정).
                        var edgePen = new Pen(brush, CvEditGeom.EdgeHandleThickPx);
                        edgePen.Freeze();
                        foreach (var (_, e1, e2) in CvEditGeom.RectEdgeHandles(r, _view)) dc.DrawLine(edgePen, Pt(e1), Pt(e2));
                    }

                    if (r.IsResizable && r.IsRotatable)
                    {
                        // 회전 그립 — 상변 중앙에서 바깥으로 화면 고정 길이만큼 뻗은 원형 핸들
                        var (gx, gy) = CvEditGeom.RectGrip(r, CvEditGeom.GripScreenPx / Scale);
                        var topMid = ToScreen((corners[0].X + corners[1].X) / 2, (corners[0].Y + corners[1].Y) / 2);
                        var grip = ToScreen(gx, gy);
                        dc.DrawLine(new Pen(brush, 1.2), topMid, grip);
                        dc.DrawEllipse(brush, null, grip, CvEditGeom.HandleScreenPx / 2 + 1, CvEditGeom.HandleScreenPx / 2 + 1);
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
                    DrawPolyline(dc, pen, ViDrawGeom.CirclePoints(circle.CenterX, circle.CenterY, circle.Radius, _view));

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
                    DrawPolyline(dc, pen, ViDrawGeom.CirclePoints(ring.CenterX, ring.CenterY, ring.RMin, _view));
                    DrawPolyline(dc, pen, ViDrawGeom.CirclePoints(ring.CenterX, ring.CenterY, ring.RMax, _view));

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
                    var arcPts = ViDrawGeom.ArcPoints(arc, _view);
                    DrawPolyline(dc, pen, arcPts);
                    // 스팬 끝 화살촉 — 진행(+deg) 방향 시인 (라인 파인더 화살표와 동일 목적)
                    DrawArrowHead(dc, brush, Pt(arcPts[arcPts.Length - 2]), Pt(arcPts[arcPts.Length - 1]));

                    // 중심 십자 (이동 히트 지점 겸)
                    var c = ToScreen(arc.CenterX, arc.CenterY);
                    dc.DrawLine(new Pen(brush, 1.2), new System.Windows.Point(c.X - 7, c.Y), new System.Windows.Point(c.X + 7, c.Y));
                    dc.DrawLine(new Pen(brush, 1.2), new System.Windows.Point(c.X, c.Y - 7), new System.Windows.Point(c.X, c.Y + 7));

                    // 그립 — 0=시작각, 1=스팬 끝, 2=반경(중간각, 중심 연결선)
                    var pStart = ToScreen(CvEditGeom.ArcPoint(arc, 0).X, CvEditGeom.ArcPoint(arc, 0).Y);
                    var pEnd = ToScreen(CvEditGeom.ArcPoint(arc, arc.SpanDeg).X, CvEditGeom.ArcPoint(arc, arc.SpanDeg).Y);
                    var pRad = ToScreen(CvEditGeom.ArcPoint(arc, arc.SpanDeg / 2).X, CvEditGeom.ArcPoint(arc, arc.SpanDeg / 2).Y);
                    dc.DrawLine(new Pen(brush, 1.2), c, pRad);
                    DrawHandle(dc, brush, pStart);
                    DrawHandle(dc, brush, pEnd);
                    dc.DrawEllipse(brush, null, pRad, CvEditGeom.HandleScreenPx / 2 + 1, CvEditGeom.HandleScreenPx / 2 + 1);

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

    private static System.Windows.Point Pt((double X, double Y) p) => new(p.X, p.Y);

    /// <summary>점 목록을 이웃끼리 선분으로 — 편집 원주·원호용. 선분마다 따로 그린다(지금 점선 모양 — 경로 하나로 합치지 않는다).</summary>
    private static void DrawPolyline(DrawingContext dc, Pen pen, (double X, double Y)[] pts)
    {
        for (var i = 1; i < pts.Length; i++) dc.DrawLine(pen, Pt(pts[i - 1]), Pt(pts[i]));
    }

    private static void DrawHandle(DrawingContext dc, Brush brush, System.Windows.Point p)
        => dc.DrawRectangle(brush, null,
            new Rect(p.X - CvEditGeom.HandleScreenPx / 2, p.Y - CvEditGeom.HandleScreenPx / 2, CvEditGeom.HandleScreenPx, CvEditGeom.HandleScreenPx));

    /// <summary>끝점의 채운 화살촉. <paramref name="scale"/> 은 선 굵기 배수 — 자리 계산은 <see cref="ViDrawGeom.ArrowHead"/>.</summary>
    private static void DrawArrowHead(DrawingContext dc, Brush brush, System.Windows.Point from, System.Windows.Point to, double scale = 1.0)
    {
        if (ViDrawGeom.ArrowHead((from.X, from.Y), (to.X, to.Y), scale) is not { } head) return;
        var (tip, left, right) = head;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(Pt(tip), true, true);
            ctx.LineTo(Pt(left), false, false);
            ctx.LineTo(Pt(right), false, false);
        }
        geo.Freeze();
        dc.DrawGeometry(brush, null, geo);
    }

    private static FormattedText Format(string text, float sizePt, Brush brush, double ppd)
        => new(text, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(new System.Windows.Media.FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            ViDrawGeom.LabelDip(sizePt), brush, ppd);

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
