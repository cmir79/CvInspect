// UI 중립 그리기 목록(ViPrim)을 Avalonia DrawingContext 로 그리고, 글자 크기를 FormattedText 로 잰다.
// 무엇을 어디에 그릴지는 ViDrawBldr 가 정했다 — 여기는 프리미티브마다 Avalonia 호출 하나로 옮길 뿐이다.

using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CvInspect.Display;
using CvInspect.Vision.Overlay;

namespace CvInspect.Avalonia.Controls;

/// <summary>Avalonia 글자 측정 — 상태가 없어 하나를 나눠 쓴다. 크기는 DIP 그대로 넘긴다(Avalonia FormattedText 는 화소/DIP 배율 인자가 없고
/// 화면 배율은 렌더러가 적용한다).</summary>
internal sealed class CvDispTextPort : ITextMetricPort
{
    internal static readonly CvDispTextPort Instance = new();

    private CvDispTextPort()
    {
    }

    public ViTextMetric Measure(string text, double sizeDip, ViFontRole role, ViPaint paint)
    {
        var ft = CvDispDrawAdp.Format(text, sizeDip, role, CvDispDrawAdp.BrushOf(paint));
        return new ViTextMetric(ft.Width, ft.Height, ft);
    }

    public ViBox? MeasureInk(in ViTextMetric metric, string text, double sizeDip, ViFontRole role, double x, double y)
    {
        var ft = metric.Tag as FormattedText ?? CvDispDrawAdp.Format(text, sizeDip, role, Brushes.White);
        // 잉크가 없으면(공백뿐) 지오메트리가 null 이다. 빈 지오메트리의 경계는 0×0 사각이라 그것도 잉크 없음으로 본다.
        if (ft.BuildGeometry(new Point(x, y)) is not { } geo) return null;
        var ink = geo.Bounds;
        return ink.Width <= 0 && ink.Height <= 0 ? null : new ViBox(ink.X, ink.Y, ink.Width, ink.Height);
    }
}

/// <summary>그리기 목록 → DrawingContext.</summary>
internal static class CvDispDrawAdp
{
    // 점선 — Avalonia 펜은 캡이 하나(선 끝과 점선 조각 끝이 같은 캡)라 표시 기준 모양(선 끝 Flat, 점선 조각 끝 Square, 점선 [2,2] 위상 1)을
    // 같은 값으로 옮기면 선 끝이 늘어나거나 조각이 짧아진다. 굵기 1·2·3 실측으로 고른 대응:
    //  · 열린 선·경로 = Flat 캡 + [3,1] 위상 1.5 — Square 조각 끝(굵기의 반씩 양쪽)을 조각 길이에 미리 더한 꼴, 기준과 열 덮임 차 0~0.24px.
    //  · 닫힌 경로 = Square 캡 + [2,2] 위상 1 — 닫힌 경로는 선 끝이 없어 Square 가 조각 끝에만 붙는다, 굵기 2 에서 완전 일치.
    // 그래서 펜 캐시 키에 닫힘 여부가 들어간다(점선일 때만 갈린다).
    private static readonly ImmutableDashStyle OpenDash = new(new[] { 3.0, 1.0 }, 1.5);
    private static readonly ImmutableDashStyle ClosedDash = new(new[] { 2.0, 2.0 }, 1);

    // 글꼴 — 데이터 라벨은 고정폭 굵게, 안내 문구는 플랫폼 기본. 이름은 쉼표 대체 목록이다(앞에서부터 있는 것을 쓰고, 다 없으면 기본 글꼴).
    // Consolas 는 Windows 에만 있어 Linux(DejaVu Sans Mono)·macOS(Menlo) 를 뒤에 둔다.
    // 상태 줄(CvDispCtrl)도 같은 목록을 쓴다.
    internal const string DataFontNames = "Consolas, Cascadia Mono, DejaVu Sans Mono, Menlo";
    private static readonly Typeface DataFace = new(new FontFamily(DataFontNames), FontStyle.Normal, FontWeight.Bold);
    private static readonly Typeface UiFace = new(FontFamily.Default);

    /// <summary>목록 순서대로 그린다. 펜은 이 호출 안에서 같은 값(색·굵기·점선·닫힘)끼리 하나를 나눠 쓴다(불변 펜 — 변경 추적이 없다).
    /// 이름 색 브러시는 Avalonia 의 공용 불변 브러시, ARGB 브러시(라벨 배경·안내 회색)는 쓸 때마다 새로 만든다.
    /// 자르기는 밀어 넣은 상태를 스택으로 쥐고 푼다 — 목록이 짝을 못 맞춰 끝나도 남은 자르기를 여기서 풀어, 바깥 상태(변환 등)를 푸는
    /// 순서가 어긋나 던지는 일이 없게 한다.</summary>
    internal static void Draw(DrawingContext dc, IReadOnlyList<ViPrim> prims)
    {
        var pens = new Dictionary<(ViStroke Stroke, bool Closed), ImmutablePen>();
        var clips = new Stack<DrawingContext.PushedState>();
        try
        {
            foreach (var prim in prims)
            {
                switch (prim)
                {
                    case ViPrimLine line:
                        dc.DrawLine(PenOf(pens, line.Stroke, closed: false), Pt(line.P1), Pt(line.P2));
                        break;
                    case ViPrimPath path when path.Count >= 2:
                    {
                        var geo = new StreamGeometry();
                        using (var ctx = geo.Open())
                        {
                            ctx.BeginFigure(Pt(path.Points[0]), isFilled: false);
                            for (var i = 1; i < path.Count; i++) ctx.LineTo(Pt(path.Points[i]), isStroked: true);
                            ctx.EndFigure(path.IsClosed);
                        }
                        dc.DrawGeometry(null, PenOf(pens, path.Stroke, path.IsClosed), geo);
                        break;
                    }
                    case ViPrimFill fill when fill.Points.Length >= 3:
                    {
                        var geo = new StreamGeometry();
                        using (var ctx = geo.Open())
                        {
                            ctx.BeginFigure(Pt(fill.Points[0]), isFilled: true);
                            for (var i = 1; i < fill.Points.Length; i++) ctx.LineTo(Pt(fill.Points[i]), isStroked: false);
                            ctx.EndFigure(true);
                        }
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
                        else measured = Format(text.Text, text.SizeDip, text.Role, brush);
                        dc.DrawText(measured, Pt(text.Origin));
                        break;
                    }
                    case ViPrimClipPush clip:
                        clips.Push(dc.PushClip(new Rect(clip.Box.X, clip.Box.Y, clip.Box.W, clip.Box.H)));
                        break;
                    case ViPrimClipPop:
                        if (clips.Count > 0) clips.Pop().Dispose();
                        break;
                }
            }
        }
        finally
        {
            while (clips.Count > 0) clips.Pop().Dispose();
        }
    }

    private static Point Pt((double X, double Y) p) => new(p.X, p.Y);

    private static ImmutablePen PenOf(Dictionary<(ViStroke Stroke, bool Closed), ImmutablePen> pens, ViStroke stroke, bool closed)
    {
        var key = (stroke, stroke.IsDashed && closed);   // 실선은 닫힘과 무관하게 같은 펜
        if (pens.TryGetValue(key, out var pen)) return pen;
        pen = stroke.IsDashed
            ? new ImmutablePen(BrushOf(stroke.Paint), stroke.Thick, closed ? ClosedDash : OpenDash, closed ? PenLineCap.Square : PenLineCap.Flat)
            : new ImmutablePen(BrushOf(stroke.Paint), stroke.Thick);
        pens[key] = pen;
        return pen;
    }

    internal static IImmutableBrush BrushOf(ViPaint paint)
        => paint.Named is { } named ? MapBrush(named) : new ImmutableSolidColorBrush(Color.FromArgb(paint.A, paint.R, paint.G, paint.B));

    private static IImmutableBrush MapBrush(ViOverlayColor color) => color switch
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

    /// <summary>글자 — 크기는 코어가 이미 DIP 로 정해 그릴 수 있는 범위로 자른 값이다(글자 크기 상한이 표시 기준과 같다).</summary>
    internal static FormattedText Format(string text, double sizeDip, ViFontRole role, IBrush brush)
        => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, role == ViFontRole.Ui ? UiFace : DataFace, sizeDip, brush);
}
