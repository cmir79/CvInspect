// UI 중립 그리기 목록(ViPrim)을 WPF DrawingContext 로 그리고, 글자 크기를 FormattedText 로 잰다.
// 무엇을 어디에 그릴지는 ViDrawBldr 가 정했다 — 여기는 프리미티브마다 WPF 호출 하나로 옮길 뿐이다.

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CvInspect.Display;
using CvInspect.Vision.Overlay;

namespace CvInspect.Controls;

/// <summary>WPF 글자 측정 — 한 번 그리는 동안만 쓴다(DPI 가 바뀌면 새로 만든다).</summary>
internal sealed class CvDispTextPort(double pixelsPerDip) : ITextMetricPort
{
    public ViTextMetric Measure(string text, double sizeDip, ViFontRole role, ViPaint paint)
    {
        var ft = CvDispDrawAdp.Format(text, sizeDip, role, CvDispDrawAdp.BrushOf(paint), pixelsPerDip);
        return new ViTextMetric(ft.Width, ft.Height, ft);
    }

    public ViBox? MeasureInk(in ViTextMetric metric, string text, double sizeDip, ViFontRole role, double x, double y)
    {
        var ft = metric.Tag as FormattedText ?? CvDispDrawAdp.Format(text, sizeDip, role, Brushes.White, pixelsPerDip);
        var ink = ft.BuildGeometry(new System.Windows.Point(x, y)).Bounds;
        return ink.IsEmpty ? null : new ViBox(ink.X, ink.Y, ink.Width, ink.Height);
    }
}

/// <summary>그리기 목록 → DrawingContext.</summary>
internal static class CvDispDrawAdp
{
    /// <summary>목록 순서대로 그린다. 펜은 이 호출 안에서 같은 값(색·굵기·점선)끼리 하나를 나눠 쓴다(얼려서 변경 추적 비용을 뺀다).
    /// 이름 색 브러시는 WPF 의 공용 브러시, ARGB 브러시(라벨 배경·안내 회색)는 쓸 때마다 새로 얼린다.</summary>
    internal static void Draw(DrawingContext dc, IReadOnlyList<ViPrim> prims, double pixelsPerDip)
    {
        var pens = new Dictionary<ViStroke, Pen>();
        foreach (var prim in prims)
        {
            switch (prim)
            {
                case ViPrimLine line:
                    dc.DrawLine(PenOf(pens, line.Stroke), Pt(line.P1), Pt(line.P2));
                    break;
                case ViPrimPath path when path.Count >= 2:
                {
                    var geo = new StreamGeometry();
                    using (var ctx = geo.Open())
                    {
                        ctx.BeginFigure(Pt(path.Points[0]), isFilled: false, isClosed: path.IsClosed);
                        for (var i = 1; i < path.Count; i++) ctx.LineTo(Pt(path.Points[i]), isStroked: true, isSmoothJoin: false);
                    }
                    geo.Freeze();
                    dc.DrawGeometry(null, PenOf(pens, path.Stroke), geo);
                    break;
                }
                case ViPrimFill fill when fill.Points.Length >= 3:
                {
                    var geo = new StreamGeometry();
                    using (var ctx = geo.Open())
                    {
                        ctx.BeginFigure(Pt(fill.Points[0]), isFilled: true, isClosed: true);
                        for (var i = 1; i < fill.Points.Length; i++) ctx.LineTo(Pt(fill.Points[i]), isStroked: false, isSmoothJoin: false);
                    }
                    geo.Freeze();
                    dc.DrawGeometry(BrushOf(fill.Paint), null, geo);
                    break;
                }
                case ViPrimBox box:
                    dc.DrawRectangle(BrushOf(box.Paint), null, new Rect(box.Box.X, box.Box.Y, box.Box.W, box.Box.H));
                    break;
                case ViPrimDot dot:
                    dc.DrawEllipse(BrushOf(dot.Paint), null, Pt(dot.Center), dot.Radius, dot.Radius);
                    break;
                case ViPrimText text:
                {
                    // 잰 객체를 다시 쓸 때도 색은 프리미티브가 정한다 — 잴 때 넘긴 색과 다르게 칠하는 목록이 생겨도 옛 색으로 그리지 않게.
                    var brush = BrushOf(text.Paint);
                    if (text.Metric?.Tag is FormattedText measured) measured.SetForegroundBrush(brush);
                    else measured = Format(text.Text, text.SizeDip, text.Role, brush, pixelsPerDip);
                    dc.DrawText(measured, Pt(text.Origin));
                    break;
                }
                case ViPrimClipPush clip:
                    dc.PushClip(new RectangleGeometry(new Rect(clip.Box.X, clip.Box.Y, clip.Box.W, clip.Box.H)));
                    break;
                case ViPrimClipPop:
                    dc.Pop();
                    break;
            }
        }
    }

    private static System.Windows.Point Pt((double X, double Y) p) => new(p.X, p.Y);

    private static Pen PenOf(Dictionary<ViStroke, Pen> pens, ViStroke stroke)
    {
        if (pens.TryGetValue(stroke, out var pen)) return pen;
        pen = new Pen(BrushOf(stroke.Paint), stroke.Thick);
        if (stroke.IsDashed) pen.DashStyle = DashStyles.Dash;
        pen.Freeze();
        pens[stroke] = pen;
        return pen;
    }

    internal static Brush BrushOf(ViPaint paint)
    {
        if (paint.Named is { } named) return MapBrush(named);
        var b = new SolidColorBrush(Color.FromArgb(paint.A, paint.R, paint.G, paint.B));
        b.Freeze();
        return b;
    }

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

    /// <summary>글자 — 데이터 라벨은 Consolas Bold, 안내 문구는 Segoe UI. 크기는 코어가 이미 DIP 로 정해 자른 값이다.</summary>
    internal static FormattedText Format(string text, double sizeDip, ViFontRole role, Brush brush, double ppd)
        => new(text, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
            role == ViFontRole.Ui
                ? new Typeface(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal)
                : new Typeface(new System.Windows.Media.FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            sizeDip, brush, ppd);
}
