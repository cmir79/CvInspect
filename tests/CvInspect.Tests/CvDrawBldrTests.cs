// 그리기 목록 빌더(CvInspect.Display.ViDrawBldr)의 회귀 — 프리미티브의 종류·순서·값이 곧 화면 모양이다.
// 같은 프리미티브라도 순서가 바뀌면 겹친 자리의 색이, 경로를 합치면 점선 위상과 모서리가 바뀐다.
// 글자는 고정폭 가짜 측정 포트로 잰다(폭 = 가장 긴 줄의 글자 수 × 0.6 × pt, 높이 = 줄 수 × 1.2 × pt,
// 잉크 = 원점에서 폭×높이의 안쪽 10% 를 줄인 상자, 빈 문자열이면 잉크 없음) — 글꼴 없이 Linux CI 에서도 같은 값이 나온다.
// 판정 경계(면 핸들 변 길이 21 DIP, 화살촉 2 DIP, 표면 60×40) 바로 위에는 시험점을 두지 않는다.
// WPF 와의 비트 단위 대조(Rect.Inflate·클립 상자·FormattedText 측정)는 Windows 스위트(CvInspect.Wpf.Tests/CvDrawWpfParityTests)가 맡는다.
using CvInspect.Display;
using CvInspect.Vision.Edit;
using CvInspect.Vision.Overlay;
using Xunit;

namespace CvInspect.Tests;

public class CvDrawBldrTests
{
    private const double Tol = 1e-9;

    /// <summary>고정폭 가짜 측정 포트. 크기는 DIP 로 받아 pt 로 되돌려 쓴다(아래 수치는 pt 기준). 잰 인자를 글자별로 남긴다 —
    /// 그리는 쪽은 잰 객체를 다시 쓰므로, 빌더가 포트에 넘긴 역할·색·크기가 틀리면 화면이 틀린다.</summary>
    private sealed class MonoPort : ITextMetricPort
    {
        internal readonly Dictionary<string, (double SizeDip, ViFontRole Role, ViPaint Paint)> Last = new();

        public ViTextMetric Measure(string text, double sizeDip, ViFontRole role, ViPaint paint)
        {
            Last[text] = (sizeDip, role, paint);
            var sizePt = sizeDip * 72.0 / 96.0;
            var lines = text.Split('\n');
            var chars = lines.Max(l => l.Length);
            return new ViTextMetric(chars * 0.6 * sizePt, lines.Length * 1.2 * sizePt, null);
        }

        public ViBox? MeasureInk(in ViTextMetric metric, string text, double sizeDip, ViFontRole role, double x, double y)
            => text.Length == 0
                ? null
                : new ViBox(x + metric.Width * 0.1, y + metric.Height * 0.1, metric.Width * 0.8, metric.Height * 0.8);
    }

    private static readonly MonoPort Tm = new();

    private static void Near(double expected, double actual, string why, double tol = Tol)
        => Assert.True(Math.Abs(expected - actual) <= tol, $"{why} (expected {expected:R}, got {actual:R})");

    private static void NearPt((double X, double Y) expected, (double X, double Y) actual, string why, double tol = Tol)
        => Assert.True(Math.Abs(expected.X - actual.X) <= tol && Math.Abs(expected.Y - actual.Y) <= tol,
            $"{why} (expected {expected}, got {actual})");

    private static void NearBox(ViBox expected, ViBox actual, string why)
        => Assert.True(Math.Abs(expected.X - actual.X) <= Tol && Math.Abs(expected.Y - actual.Y) <= Tol
                       && Math.Abs(expected.W - actual.W) <= Tol && Math.Abs(expected.H - actual.H) <= Tol,
            $"{why} (expected {expected}, got {actual})");

    private static string Kind(ViPrim p) => p switch
    {
        ViPrimLine => "Line",
        ViPrimPath => "Path",
        ViPrimFill => "Fill",
        ViPrimBox => "Box",
        ViPrimDot => "Dot",
        ViPrimText => "Text",
        ViPrimClipPush => "Push",
        ViPrimClipPop => "Pop",
        _ => "?",
    };

    /// <summary>종류 순서 단언 — 반복은 "Line*4" 처럼 적는다.</summary>
    private static void Seq(List<ViPrim> prims, string expected, string why)
    {
        var want = new List<string>();
        foreach (var tok in expected.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var star = tok.IndexOf('*');
            if (star < 0) want.Add(tok);
            else want.AddRange(Enumerable.Repeat(tok[..star], int.Parse(tok[(star + 1)..])));
        }
        var got = prims.Select(Kind).ToList();
        Assert.True(want.SequenceEqual(got), $"{why} (expected [{string.Join(' ', want)}], got [{string.Join(' ', got)}])");
    }

    private static bool IsNamed(ViPaint p, ViOverlayColor c) => p.Named == c;

    private static bool IsArgb(ViPaint p, ViPaint want)
        => p.Named is null && want.Named is null && p.A == want.A && p.R == want.R && p.G == want.G && p.B == want.B;

    private static List<ViPrim> Overlay(CvViewXform view, double labelScale, bool clip, int imgW, int imgH, params ViOverlayItem[] items)
    {
        var ov = new ViOverlay();
        foreach (var it in items) ov.Add(it);
        var dst = new List<ViPrim>();
        ViDrawBldr.Overlay(dst, ov, view, Tm, labelScale, clip, imgW, imgH);
        return dst;
    }

    private static List<ViPrim> Shapes(CvViewXform view, params CvEditShape[] shapes)
    {
        var dst = new List<ViPrim>();
        ViDrawBldr.Shapes(dst, shapes, view, Tm);
        return dst;
    }

    private static CvEditRect Rect(double x, double y, double w, double h, bool rotatable = false, bool resizable = true, string label = "")
    {
        var r = new CvEditRect { IsRotatable = rotatable, IsResizable = resizable, Label = label };
        r.Set(x, y, w, h);
        return r;
    }

    // ───────────── 결과 오버레이 ─────────────

    [Fact]
    public void OverlaySegIsOneLineFollowedByItsArrowHead()
    {
        var view = new CvViewXform(2, 10, 20);
        var dst = Overlay(view, 1, false, 100, 100,
            new ViOverlaySeg { X1 = 0, Y1 = 0, X2 = 50, Y2 = 0, HasEndArrow = true, Color = ViOverlayColor.Red, StrokeScale = 2 });
        Seq(dst, "Line Fill", "the arrow head is filled after the line so it sits on top of the line end");
        var line = (ViPrimLine)dst[0];
        NearPt((10, 20), line.P1, "the segment start goes through the view transform");
        NearPt((110, 20), line.P2, "the segment end goes through the view transform");
        Near(4, line.Stroke.Thick, "the overlay line thickness is the base 2 DIP times the stroke scale");
        Assert.False(line.Stroke.IsDashed, "an undashed item draws a solid line");
        Assert.True(IsNamed(line.Stroke.Paint, ViOverlayColor.Red), "the line takes the item color");
        var fill = (ViPrimFill)dst[1];
        Assert.Equal(3, fill.Points.Length);
        NearPt((110, 20), fill.Points[0], "the arrow tip sits on the segment end");
        NearPt((86, 29), fill.Points[1], "the head scales with the stroke so a thick line does not bury it (12×2 long, 4.5×2 half width)");
        NearPt((86, 11), fill.Points[2], "the right wing mirrors the left wing across the line");

        var plain = Overlay(view, 1, false, 100, 100, new ViOverlaySeg { X1 = 0, Y1 = 0, X2 = 50, Y2 = 0 });
        Seq(plain, "Line", "without an end arrow the segment is only its line");
        var tiny = Overlay(view, 1, false, 100, 100, new ViOverlaySeg { X1 = 0, Y1 = 0, X2 = 0.5, Y2 = 0, HasEndArrow = true });
        Seq(tiny, "Line", "a segment shorter than the arrow minimum on screen gets no head (1 DIP, well under 2)");
    }

    [Theory]
    [InlineData(ViOverlayAlign.TopCenter, 92, 120)]
    [InlineData(ViOverlayAlign.BottomCenter, 92, 102)]
    [InlineData(ViOverlayAlign.TopLeft, 110, 120)]
    [InlineData(ViOverlayAlign.BottomLeft, 110, 102)]
    [InlineData(ViOverlayAlign.TopRight, 74, 120)]
    [InlineData(ViOverlayAlign.BottomRight, 74, 102)]
    public void LabelOriginFollowsAlignAndBackgroundPrecedesText(ViOverlayAlign align, double ox, double oy)
    {
        // 앵커 (50,50) → 화면 (110,120). 10 pt × 배율 1.5 = 15 pt → 블록 4 글자 = 36 × 18.
        var view = new CvViewXform(2, 10, 20);
        var dst = Overlay(view, 1.5, false, 100, 100,
            new ViOverlayLabel { Text = "abcd", X = 50, Y = 50, FontSize = 10, Align = align, Color = ViOverlayColor.Cyan });
        Seq(dst, "Box Text", "the background box is filled before the text so the text stays readable on top of it");
        var text = (ViPrimText)dst[1];
        NearPt((ox, oy), text.Origin, "the block's top-left is the anchor shifted by the measured size per the alignment");
        Assert.True(text.SizePt == 15f, "a non-HUD label's font size is multiplied by the surface label scale");
        Assert.True(text.Metric is { Width: 36 }, "a single-color label is drawn with the metric it was measured with");
        Assert.True(text.Role == ViFontRole.Data, "overlay labels use the data font role");
        Assert.True(IsNamed(text.Paint, ViOverlayColor.Cyan), "the label text takes the item color");
        var measured = Tm.Last["abcd"];
        Assert.True(measured.Role == ViFontRole.Data && IsNamed(measured.Paint, ViOverlayColor.Cyan),
            "the label is measured with the role and color it is drawn with — the adapter reuses the measured object");
        Assert.True(measured.SizeDip == ViDrawGeom.LabelDip(15f) && text.SizeDip == measured.SizeDip,
            "the core resolves the clamped DIP size once and both the measure and the draw use it");
        var box = (ViPrimBox)dst[0];
        // 잉크 = (x+3.6, y+1.8, 28.8, 14.4) → 사방 2 부풀림
        NearBox(new ViBox(ox + 1.6, oy - 0.2, 32.8, 18.4), box.Box, "the background hugs the ink bounds plus 2 DIP on every side, not the loose line box");
        Assert.True(IsArgb(box.Paint, ViDrawBldr.LabelBack), "the background is the translucent black label back");
    }

    [Fact]
    public void OutOfRangeLabelSizesAreClampedOnceInTheCore()
    {
        // 측정 포트는 이미 자른 DIP 를 받는다 — 어댑터마다 자르기를 되풀이하면 하나가 빠뜨린 순간 NaN·초대형 크기로 그리기가 던진다.
        var view = new CvViewXform(1, 0, 0);
        var cases = new (string Text, float Pt, double WantDip)[]
        {
            ("nan", float.NaN, ViDrawGeom.MinLabelPt * 96.0 / 72.0),
            ("zero", 0f, ViDrawGeom.MinLabelPt * 96.0 / 72.0),
            ("huge", 1e6f, ViDrawGeom.MaxLabelPt * 96.0 / 72.0),
        };
        foreach (var (text, pt, want) in cases)
        {
            var dst = Overlay(view, 1, false, 100, 100, new ViOverlayLabel { Text = text, FontSize = pt, HasBackground = false });
            var t = (ViPrimText)dst.Single();
            Assert.True(Tm.Last[text].SizeDip == want && t.SizeDip == want,
                $"a {pt} pt label is measured and drawn at the clamped {want:R} DIP (got measure {Tm.Last[text].SizeDip:R}, draw {t.SizeDip:R})");
        }
    }

    [Fact]
    public void LabelWithoutBackgroundOrInkHasNoBox()
    {
        var view = new CvViewXform(1, 0, 0);
        var noBack = Overlay(view, 1, false, 100, 100, new ViOverlayLabel { Text = "ab", HasBackground = false });
        Seq(noBack, "Text", "a label that opts out of the background is only its text");
        var empty = Overlay(view, 1, false, 100, 100, new ViOverlayLabel { Text = "" });
        Seq(empty, "Text", "an empty label has no ink, so there is no background box to draw");
    }

    [Fact]
    public void PerLineColorsSplitTheTextAndFallBackToTheItemColor()
    {
        // 10 pt × 1 → 블록 3 줄 = 높이 36, 줄 높이 12.
        var view = new CvViewXform(1, 0, 0);
        var dst = Overlay(view, 1, false, 100, 100, new ViOverlayLabel
        {
            Text = "ab\ncd\nef", X = 30, Y = 40, FontSize = 10, Align = ViOverlayAlign.TopLeft, Color = ViOverlayColor.Green,
            LineColors = [ViOverlayColor.Red, null],
        });
        Seq(dst, "Box Text*3", "a per-line-colored label is one background then one text per line");
        var lines = dst.Skip(1).Cast<ViPrimText>().ToArray();
        Assert.Equal(new[] { "ab", "cd", "ef" }, lines.Select(l => l.Text));
        for (var i = 0; i < 3; i++)
        {
            NearPt((30, 40 + i * 12), lines[i].Origin, "each line steps down by the block height divided by the line count");
            Assert.True(lines[i].Metric is null, "a split line is re-measured by the adapter, not drawn with the whole-block metric");
        }
        Assert.True(IsNamed(lines[0].Paint, ViOverlayColor.Red), "a line with its own color uses it");
        Assert.True(IsNamed(lines[1].Paint, ViOverlayColor.Green), "a null entry falls back to the item color");
        Assert.True(IsNamed(lines[2].Paint, ViOverlayColor.Green), "a line beyond the color list falls back to the item color");
        // 배경은 블록 전체의 잉크 — 폭 2 글자 × 0.6 × 10 = 12, 높이 36.
        NearBox(new ViBox(30 + 1.2 - 2, 40 + 3.6 - 2, 9.6 + 4, 28.8 + 4), ((ViPrimBox)dst[0]).Box,
            "the background covers the ink of the whole block, not of one line");
    }

    [Fact]
    public void CornerHudIgnoresTheLabelScale()
    {
        var view = new CvViewXform(1, 0, 0);
        var dst = Overlay(view, 3, false, 100, 100,
            new ViOverlayLabel { Text = "h", FontSize = 10, Align = ViOverlayAlign.TopLeft, IsHud = true },
            new ViOverlayLabel { Text = "c", FontSize = 10, Align = ViOverlayAlign.TopCenter, IsHud = true });
        Seq(dst, "Box Text Box Text", "both labels draw background then text");
        Assert.True(((ViPrimText)dst[1]).SizePt == 10f, "a corner HUD keeps its own size so the summary stays a fixed screen size");
        Assert.True(((ViPrimText)dst[3]).SizePt == 30f, "a HUD flag without a corner alignment is an ordinary label and scales");
    }

    [Fact]
    public void ClipWrapsNonHudItemsAndDrawsTheHudAfterward()
    {
        var view = new CvViewXform(0.5, 7, 9);
        ViOverlayItem a = new ViOverlaySeg { X1 = 0, Y1 = 0, X2 = 100, Y2 = 0 };
        ViOverlayItem hud = new ViOverlayLabel { Text = "OK", FontSize = 10, Align = ViOverlayAlign.TopLeft, IsHud = true };
        ViOverlayItem b = new ViOverlaySeg { X1 = 0, Y1 = 10, X2 = 100, Y2 = 10 };

        var clipped = Overlay(view, 1, true, 640, 480, a, hud, b);
        Seq(clipped, "Push Line Line Pop Box Text", "with the clip on, results are cut to the image and the HUD comes after the clip so it is never cut");
        NearBox(new ViBox(7, 9, 320, 240), ((ViPrimClipPush)clipped[0]).Box, "the clip is the image's screen rect: offset at top-left, image size times scale");
        NearPt((7, 9), ((ViPrimLine)clipped[1]).P1, "non-HUD items keep their list order inside the clip");
        NearPt((7, 14), ((ViPrimLine)clipped[2]).P1, "the second non-HUD item follows the first inside the clip");

        var open = Overlay(view, 1, false, 640, 480, a, hud, b);
        Seq(open, "Line Box Text Line", "with the clip off, items draw in list order and the surface edge is the only clip");
    }

    [Fact]
    public void OverlayRectIsOneClosedFourPointPath()
    {
        var view = new CvViewXform(2, 10, 20);
        var dst = Overlay(view, 1, false, 100, 100,
            new ViOverlayRect { CenterX = 50, CenterY = 40, Width = 20, Height = 10, Color = ViOverlayColor.Orange, IsDashed = true });
        Seq(dst, "Path", "a rect is one closed figure so the corners join instead of leaving notches");
        var path = (ViPrimPath)dst[0];
        Assert.True(path.IsClosed, "the rect path closes back to its first corner");
        Assert.Equal(4, path.Count);
        NearPt((90, 90), path.Points[0], "corners run top-left first");
        NearPt((130, 90), path.Points[1], "then top-right");
        NearPt((130, 110), path.Points[2], "then bottom-right");
        NearPt((90, 110), path.Points[3], "then bottom-left");
        Near(2, path.Stroke.Thick, "a rect uses the 2 DIP overlay line base");
        Assert.True(path.Stroke.IsDashed, "a dashed item keeps its dash on the path");
    }

    [Fact]
    public void PolylineNeedsTwoPointsAndThreeToClose()
    {
        var view = new CvViewXform(1, 0, 0);
        Seq(Overlay(view, 1, false, 100, 100, new ViOverlayPoly { Points = [(1, 1)] }), "",
            "a single point is no line, so nothing is drawn");

        var two = Overlay(view, 1, false, 100, 100, new ViOverlayPoly { Points = [(1, 1), (20, 5)], IsClosed = true, StrokeScale = 2 });
        Seq(two, "Path", "two points are one open path");
        Assert.False(((ViPrimPath)two[0]).IsClosed, "closing two points would draw the same edge twice, so two points stay open");
        Near(2.4, ((ViPrimPath)two[0]).Stroke.Thick, "a polyline uses the thinner 1.2 DIP base times the stroke scale");

        var three = Overlay(view, 1, false, 100, 100, new ViOverlayPoly { Points = [(1, 1), (20, 5), (10, 30)], IsClosed = true });
        Assert.True(((ViPrimPath)three[0]).IsClosed, "three or more points close when the item asks");
        Assert.Equal(3, ((ViPrimPath)three[0]).Count);
        Assert.True(((ViPrimPath)three[0]).IsPooled, "polyline screen points come from the shared pool — thousands of points per outline per frame");

        // 풀에서 빌린 배열은 목록을 비울 때 돌려준다 — 빌린 크기가 점 수보다 클 수 있어 경로는 앞 Count 개만 쓴다.
        var held = three.ToList();
        ViDrawBldr.Clear(three);
        Assert.Empty(three);
        Assert.True(held.OfType<ViPrimPath>().All(p => p.Points.Length >= p.Count), "a pooled array holds at least the points it carries");

        var openThree = Overlay(view, 1, false, 100, 100, new ViOverlayPoly { Points = [(1, 1), (20, 5), (10, 30)], IsClosed = false });
        Assert.False(((ViPrimPath)openThree[0]).IsClosed, "an open polyline stays open whatever its point count");
    }

    // ───────────── 편집 도형 ─────────────

    [Fact]
    public void EditRectDrawsDashedEdgesThenHandlesThenEdgeHandlesThenLabel()
    {
        // (10,10,40,30) × 2 → 화면 80 × 60 — 두 변 모두 면 핸들 하한 21 DIP 보다 한참 길다.
        var view = new CvViewXform(2, 0, 0);
        var r = Rect(10, 10, 40, 30, label: "Train");
        var dst = Shapes(view, r);
        Seq(dst, "Line*4 Box*4 Line*4 Text", "outline per edge, then corner handles, then edge handles, then the name tag");
        var scr = new (double X, double Y)[] { (20, 20), (100, 20), (100, 80), (20, 80) };
        for (var i = 0; i < 4; i++)
        {
            var edge = (ViPrimLine)dst[i];
            NearPt(scr[i], edge.P1, "each outline edge starts at its corner so the dash restarts per edge");
            NearPt(scr[(i + 1) % 4], edge.P2, "each outline edge ends at the next corner");
            Assert.True(edge.Stroke.IsDashed && edge.Stroke.Thick == ViDrawBldr.ShapeLineThick, "the outline is the dashed 2 DIP shape line");
            var h = ((ViPrimBox)dst[4 + i]).Box;
            NearBox(new ViBox(scr[i].X - 3.5, scr[i].Y - 3.5, 7, 7), h, "a corner handle is a 7 DIP square centered on the corner");
        }
        var want = CvEditGeom.RectEdgeHandles(r, view);
        Assert.Equal(4, want.Count);
        for (var i = 0; i < 4; i++)
        {
            var eh = (ViPrimLine)dst[8 + i];
            NearPt(want[i].P1, eh.P1, "the drawn edge handle is the same segment the hit test grabs");
            Assert.True(!eh.Stroke.IsDashed && eh.Stroke.Thick == CvEditGeom.EdgeHandleThickPx, "an edge handle is a solid thicker line over the dash");
        }
        var tag = (ViPrimText)dst[12];
        // 이름표 5 글자 × 0.6 × 12 pt → 높이 14.4 — 첫 모서리 위로 높이 + 2.
        NearPt((20, 20 - 14.4 - 2), tag.Origin, "the name tag sits above the first corner by its block height plus 2 DIP");
        Assert.True(tag.Text == "Train" && tag.SizePt == ViDrawBldr.ShapeLabelPt, "the tag shows the shape label at the fixed shape label size");
        var measured = Tm.Last["Train"];
        Assert.True(measured.Role == ViFontRole.Data && measured.Paint == tag.Paint && measured.SizeDip == tag.SizeDip,
            "the shape tag is measured with the data font role, its own color and the size it is drawn at");
    }

    [Fact]
    public void RotatableRectAddsGripBarAndKnobBeforeTheLabel()
    {
        var view = new CvViewXform(2, 0, 0);
        var dst = Shapes(view, Rect(10, 10, 40, 30, rotatable: true, label: "R"));
        Seq(dst, "Line*4 Box*4 Line*4 Line Dot Text", "a rotatable rect adds the grip bar and knob after the handles");
        var bar = (ViPrimLine)dst[12];
        NearPt((60, 20), bar.P1, "the grip bar starts at the top edge middle");
        NearPt((60, -6), bar.P2, "the grip sits a fixed 26 DIP outward from the top edge");
        Assert.True(bar.Stroke.Thick == ViDrawBldr.GuideThick && !bar.Stroke.IsDashed, "the grip bar is a solid guide line");
        var knob = (ViPrimDot)dst[13];
        NearPt((60, -6), knob.Center, "the knob is centered on the grip");
        Near(4.5, knob.Radius, "the knob radius is half a handle plus 1 DIP");
    }

    [Fact]
    public void FixedSizeRectHasNoHandlesAndUnlabeledShapesHaveNoTag()
    {
        var view = new CvViewXform(2, 0, 0);
        Seq(Shapes(view, Rect(10, 10, 40, 30, rotatable: true, resizable: false, label: "Crop")), "Line*4 Text",
            "a fixed-size rect shows no grips so it reads as move-only, matching the hit test");
        Seq(Shapes(view, Rect(10, 10, 40, 30)), "Line*4 Box*4 Line*4", "a shape without a label draws no name tag");
    }

    [Fact]
    public void EditSegDrawsLineArrowTwoHandlesAndMidpointTag()
    {
        var view = new CvViewXform(2, 0, 0);
        var s = new CvEditSeg { Label = "S" };
        s.Set(10, 10, 40, 10);
        var dst = Shapes(view, s);
        Seq(dst, "Line Fill Box Box Text", "a segment is its dashed line, direction head, two end handles, then the tag");
        Assert.True(((ViPrimLine)dst[0]).Stroke.IsDashed, "the segment body is the dashed shape line");
        NearPt((80, 20), ((ViPrimFill)dst[1]).Points[0], "the direction head points at P2");
        NearBox(new ViBox(16.5, 16.5, 7, 7), ((ViPrimBox)dst[2]).Box, "the first handle is on P1");
        NearBox(new ViBox(76.5, 16.5, 7, 7), ((ViPrimBox)dst[3]).Box, "the second handle is on P2");
        // 1 글자 → 높이 14.4 — 중점 (50,20) 위로 높이 + 4.
        NearPt((50, 20 - 14.4 - 4), ((ViPrimText)dst[4]).Origin, "the tag sits above the segment midpoint by its height plus 4 DIP");
    }

    [Fact]
    public void EditCircleDraws72ChordsCrossRadiusLineAndHandle()
    {
        var view = new CvViewXform(1, 0, 0);
        var c = new CvEditCircle { Label = "C" };
        c.Set(50, 50, 20);
        var dst = Shapes(view, c);
        Seq(dst, "Line*72 Line*2 Line Box Text", "a circle is 72 dashed chords, the center cross, the radius line, its handle, then the tag");
        NearPt((70, 50), ((ViPrimLine)dst[0]).P1, "the rim starts at 0 degrees (right)");
        NearPt((70, 50), ((ViPrimLine)dst[71]).P2, "the rim closes back on its start", 1e-9);
        NearPt((43, 50), ((ViPrimLine)dst[72]).P1, "the horizontal cross arm spans 7 DIP either side of the center");
        NearPt((50, 57), ((ViPrimLine)dst[73]).P2, "the vertical cross arm spans 7 DIP either side of the center");
        var rad = (ViPrimLine)dst[74];
        NearPt((50, 50), rad.P1, "the radius line starts at the center");
        NearPt((70, 50), rad.P2, "the radius line ends at the radius grip");
        NearBox(new ViBox(66.5, 46.5, 7, 7), ((ViPrimBox)dst[75]).Box, "the radius handle sits on the right of the rim");
        NearPt((76, 50 - 14.4 - 2), ((ViPrimText)dst[76]).Origin, "the circle tag sits right 6 and above by height plus 2 from the grip");
    }

    [Fact]
    public void EditRingDrawsTwoRimsCrossAndTwoHandles()
    {
        var view = new CvViewXform(1, 0, 0);
        var g = new CvEditRing { Label = "G" };
        g.Set(50, 50, 10, 30);
        var dst = Shapes(view, g);
        Seq(dst, "Line*144 Line*2 Box Box Text", "a ring is inner then outer rim, the cross, the inner and outer handles, then the tag");
        NearPt((60, 50), ((ViPrimLine)dst[0]).P1, "the inner rim comes first");
        NearPt((80, 50), ((ViPrimLine)dst[72]).P1, "the outer rim follows the inner");
        NearBox(new ViBox(36.5, 46.5, 7, 7), ((ViPrimBox)dst[146]).Box, "the inner handle is on the left at 180 degrees");
        NearBox(new ViBox(76.5, 46.5, 7, 7), ((ViPrimBox)dst[147]).Box, "the outer handle is on the right at 0 degrees");
        NearPt((86, 50 - 14.4 - 2), ((ViPrimText)dst[148]).Origin, "the ring tag hangs off the outer handle");
    }

    [Fact]
    public void EditArcDrawsChordsEndHeadCrossRadiusHandlesKnobAndTag()
    {
        // 스팬 90° → 분할 18(90/5, 반올림 경계에서 멀다).
        var view = new CvViewXform(1, 0, 0);
        var a = new CvEditArc { Label = "A" };
        a.Set(50, 50, 30, 0, 90);
        var dst = Shapes(view, a);
        Seq(dst, "Line*18 Fill Line*2 Line Box Box Dot Text",
            "an arc is its chords, the end head, the cross, the radius line, start and end handles, the radius knob, then the tag");
        var p0 = CvEditGeom.ArcPoint(a, 0);
        var p1 = CvEditGeom.ArcPoint(a, 90);
        var pm = CvEditGeom.ArcPoint(a, 45);
        NearPt(p1, ((ViPrimFill)dst[18]).Points[0], "the head points along the sweep at the arc end");
        NearPt(pm, ((ViPrimLine)dst[21]).P2, "the radius line runs to the mid-span grip");
        NearBox(new ViBox(p0.X - 3.5, p0.Y - 3.5, 7, 7), ((ViPrimBox)dst[22]).Box, "the first handle is the start angle");
        NearBox(new ViBox(p1.X - 3.5, p1.Y - 3.5, 7, 7), ((ViPrimBox)dst[23]).Box, "the second handle is the span end");
        NearPt(pm, ((ViPrimDot)dst[24]).Center, "the radius grip is a round knob at mid-span");
        NearPt((p0.X + 4, p0.Y - 14.4 - 4), ((ViPrimText)dst[25]).Origin, "the arc tag sits right 4 and above by height plus 4 from the start");
    }

    // ───────────── 무이미지 안내 ─────────────

    [Fact]
    public void PlaceholderCentersTitleAloneOrStacksTitleOverHint()
    {
        var tiny = new List<ViPrim>();
        ViDrawBldr.Placeholder(tiny, 50, 300, "No image", "Open", Tm);
        ViDrawBldr.Placeholder(tiny, 300, 30, "No image", "Open", Tm);
        Seq(tiny, "", "a surface too small for the text gets nothing rather than clipped words");

        // 제목 8 글자 × 0.6 × 14 = 67.2 × 16.8, 힌트 4 글자 × 0.6 × 11 = 26.4 × 13.2.
        var one = new List<ViPrim>();
        ViDrawBldr.Placeholder(one, 300, 200, "No image", null, Tm);
        Seq(one, "Text", "without a hint the title is the only line");
        var t = (ViPrimText)one[0];
        NearPt((150 - 33.6, 100 - 8.4), t.Origin, "a lone title sits dead center instead of in the upper slot of a two-line layout");
        Assert.True(t.SizePt == 14f && t.Role == ViFontRole.Ui && IsArgb(t.Paint, ViDrawBldr.HintGray), "the title is 14 pt UI-font gray");

        var two = new List<ViPrim>();
        ViDrawBldr.Placeholder(two, 300, 200, "No image", "Open", Tm);
        Seq(two, "Text Text", "with a hint the title and hint are two lines");
        NearPt((150 - 33.6, 100 - 16.8 - 2), ((ViPrimText)two[0]).Origin, "the title sits just above the center line");
        var h = (ViPrimText)two[1];
        NearPt((150 - 13.2, 102), h.Origin, "the hint sits just below the center line");
        Assert.True(h.SizePt == 11f && h.Text == "Open", "the hint is the smaller 11 pt line");

        // 호스트가 준 색(UI 패키지의 안내 글자 색 키)은 두 줄 모두에 간다 — 한 줄만 바뀌면 밝은 바탕에서 힌트만 옅게 남는다.
        var host = ViPaint.Argb(255, 0x5F, 0x5F, 0x5F);
        var colored = new List<ViPrim>();
        ViDrawBldr.Placeholder(colored, 300, 200, "No image", "Open", Tm, host);
        Assert.True(colored.OfType<ViPrimText>().Count() == 2 && colored.OfType<ViPrimText>().All(x => IsArgb(x.Paint, host)),
            "a host placeholder color paints both the title and the hint");
        Assert.True(!IsArgb(host, ViDrawBldr.HintGray), "control: the host color differs from the default gray, so the check above can fail");
    }

    // ───────────── Inflate ─────────────

    [Fact]
    public void InflateAddsEachSideSeparately()
    {
        NearBox(new ViBox(-1, 0, 7, 8), ViDrawBldr.Inflate(new ViBox(1, 2, 3, 4), 2, 2), "inflating moves the corner out and grows each size by both sides");

        // 0.001 + 2 + 2 와 0.001 + 4 는 끝자리가 다르다(IEEE 덧셈 — 플랫폼 무관). 앞의 것이 WPF Rect.Inflate 의 순서다.
        const double w = 0.001;
        var twoStep = w + 2 + 2;
        var oneStep = w + 4;
        Assert.True(BitConverter.DoubleToInt64Bits(twoStep) != BitConverter.DoubleToInt64Bits(oneStep),
            "this input must tell the two orders apart, or the next assertion proves nothing");
        var b = ViDrawBldr.Inflate(new ViBox(0, 0, w, w), 2, 2);
        Assert.True(BitConverter.DoubleToInt64Bits(b.W) == BitConverter.DoubleToInt64Bits(twoStep),
            $"the width adds one side at a time like WPF so the background edge lands on the same last bit (got {b.W:R}, want {twoStep:R})");
        Assert.True(BitConverter.DoubleToInt64Bits(b.H) == BitConverter.DoubleToInt64Bits(twoStep),
            $"the height adds one side at a time like WPF (got {b.H:R}, want {twoStep:R})");
    }
}
