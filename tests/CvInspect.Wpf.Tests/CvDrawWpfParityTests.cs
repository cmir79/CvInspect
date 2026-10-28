// 그리기 목록(ViDrawBldr)이 옛 WPF 그리기와 같은 끝자리를 내는지 — 표시 표면은 0.33 까지 라벨 배경을 Rect.Inflate 로,
// 이미지 클립을 Rect.Transform 으로, 글자 크기를 FormattedText 로 구했다. UI 중립 계층은 같은 식을 같은 순서로 계산해야
// 배경 상자·클립 가장자리가 한 픽셀도 움직이지 않는다. 근사 비교로는 순서 차이가 안 보여 비트 단위로 대조한다.
// 살아 있음 대조: 같은 루프에서 일부러 다른 식(폭 + 4, 모서리 차이로 구한 폭)으로 구한 값이 WPF 와 한 번 이상 달라야 한다 —
// 안 다르면 그 비교는 식 순서를 가려내지 못하는 무력한 시험이다.
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using CvInspect.Controls;
using CvInspect.Display;
using CvInspect.Vision.Overlay;
using Xunit;
using Xunit.Abstractions;

namespace CvInspect.Wpf.Tests;

public class CvDrawWpfParityTests(ITestOutputHelper output)
{
    private static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    private static void SameBits(double wpf, double neutral, string what)
    {
        if (!Same(wpf, neutral)) Assert.Fail($"{what}: WPF {wpf:R} vs neutral {neutral:R} — the draw list must match the WPF path bit for bit");
    }

    /// <summary>STA 스레드에서 실행 — 본문의 예외는 원래 스택 그대로 밖으로 던진다.</summary>
    private static void RunSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public void InflateMatchesRectInflateBitForBit()
    {
        var rng = new Random(20261026);
        var altDiffers = 0;
        const int n = 5000;
        for (var i = 0; i < n; i++)
        {
            // 라벨 잉크 상자 범위 — 화면 어디든, 크기는 글자 한 줄부터 큰 블록까지. 일부는 아주 작은 폭(끝자리 차이가 잘 나는 쪽).
            var x = (rng.NextDouble() - 0.5) * 4000;
            var y = (rng.NextDouble() - 0.5) * 4000;
            var w = i % 4 == 0 ? rng.NextDouble() * 0.1 : rng.NextDouble() * 800;
            var h = i % 4 == 1 ? rng.NextDouble() * 0.1 : rng.NextDouble() * 300;
            var r = new Rect(x, y, w, h);
            r.Inflate(2, 2);
            var b = ViDrawBldr.Inflate(new ViBox(x, y, w, h), 2, 2);
            SameBits(r.X, b.X, $"Inflate X #{i}");
            SameBits(r.Y, b.Y, $"Inflate Y #{i}");
            SameBits(r.Width, b.W, $"Inflate W #{i} (w={w:R})");
            SameBits(r.Height, b.H, $"Inflate H #{i} (h={h:R})");
            if (!Same(r.Width, w + 4) || !Same(r.Height, h + 4)) altDiffers++;
        }
        output.WriteLine($"Inflate: {n} boxes bit-equal; width/height + 4 differed from Rect.Inflate in {altDiffers}");
        Assert.True(altDiffers > 0, "the + 4 control never differed, so this comparison cannot tell the two-step order from a single add");
    }

    [Fact]
    public void ClipBoxMatchesRectTransformBitForBit()
    {
        var rng = new Random(20261027);
        var altDiffers = 0;
        const int n = 3000;
        var tm = new NullPort();
        for (var i = 0; i < n; i++)
        {
            var imgW = rng.Next(1, 9000);
            var imgH = rng.Next(1, 9000);
            var s = Math.Exp(rng.NextDouble() * (Math.Log(CvViewXform.MaxScale) - Math.Log(CvViewXform.MinScale)) + Math.Log(CvViewXform.MinScale));
            var ox = (rng.NextDouble() - 0.5) * 3000;
            var oy = (rng.NextDouble() - 0.5) * 3000;
            var dst = new List<ViPrim>();
            ViDrawBldr.Overlay(dst, new ViOverlay(), new CvViewXform(s, ox, oy), tm, 1, true, imgW, imgH);
            var clip = Assert.IsType<ViPrimClipPush>(dst[0]).Box;
            var r = Rect.Transform(new Rect(0, 0, imgW, imgH), new Matrix(s, 0, 0, s, ox, oy));
            SameBits(r.X, clip.X, $"clip X #{i}");
            SameBits(r.Y, clip.Y, $"clip Y #{i}");
            SameBits(r.Width, clip.W, $"clip W #{i}");
            SameBits(r.Height, clip.H, $"clip H #{i}");
            // 대조: 오른쪽 아래 모서리를 변환해 빼서 구한 폭 — 수학적으로 같지만 이동량이 끝자리를 흔든다.
            if (!Same(r.Width, (imgW * s + ox) - ox) || !Same(r.Height, (imgH * s + oy) - oy)) altDiffers++;
        }
        output.WriteLine($"Clip: {n} rects bit-equal; corner-difference width/height differed from Rect.Transform in {altDiffers}");
        Assert.True(altDiffers > 0, "the corner-difference control never differed, so this comparison cannot tell formulas apart");
    }

    /// <summary>클립 상자만 볼 때 — 글자를 재지 않는다(빈 오버레이).</summary>
    private sealed class NullPort : ITextMetricPort
    {
        public ViTextMetric Measure(string text, double sizeDip, ViFontRole role, ViPaint paint) => new(0, 0, null);

        public ViBox? MeasureInk(in ViTextMetric metric, string text, double sizeDip, ViFontRole role, double x, double y) => null;
    }

    private static FormattedText Ft(string text, string family, FontWeight weight, double dip, double ppd)
        => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(family), FontStyles.Normal, weight, FontStretches.Normal), dip, Brushes.White, ppd);

    [Fact]
    public void TextPortMeasuresLikeFormattedText() => RunSta(() =>
    {
        var cases = new (string Text, float Pt, ViFontRole Role, double Ppd)[]
        {
            ("OK 12.5", 12, ViFontRole.Data, 1.0),
            ("NG\nscore 0.41", 20, ViFontRole.Data, 1.25),
            ("W", 7.5f, ViFontRole.Data, 1.5),
            ("No image", 14, ViFontRole.Ui, 1.0),
            ("Open an image file", 11, ViFontRole.Ui, 1.75),
        };
        foreach (var (text, pt, role, ppd) in cases)
        {
            var port = new CvDispTextPort(ppd);
            // 크기는 코어가 DIP 로 정해 넘긴다 — 옛 표면의 식(데이터 라벨 = 자르고 환산, 안내 = 그대로 환산)과 같은 값인지도 함께 본다.
            var dip = role == ViFontRole.Ui ? ViDrawGeom.UiDip(pt) : ViDrawGeom.LabelDip(pt);
            SameBits(pt * 96.0 / 72.0, dip, $"DIP size of {pt}pt {role} (inside the clamp range it is the plain conversion)");
            var m = port.Measure(text, dip, role, ViPaint.Of(ViOverlayColor.Green));
            var ft = role == ViFontRole.Ui
                ? Ft(text, "Segoe UI", FontWeights.Normal, pt * 96.0 / 72.0, ppd)
                : Ft(text, "Consolas", FontWeights.Bold, pt * 96.0 / 72.0, ppd);
            SameBits(ft.Width, m.Width, $"width of \"{text}\" {pt}pt {role} ppd {ppd}");
            SameBits(ft.Height, m.Height, $"height of \"{text}\" {pt}pt {role} ppd {ppd}");
            Assert.True(m.Tag is FormattedText, "the port hands back its FormattedText so the adapter draws the measured object");

            var ink = port.MeasureInk(m, text, dip, role, 31.7, 12.3);
            var want = ft.BuildGeometry(new Point(31.7, 12.3)).Bounds;
            Assert.True(ink is { } box && Same(want.X, box.X) && Same(want.Y, box.Y) && Same(want.Width, box.W) && Same(want.Height, box.H),
                $"the ink box of \"{text}\" is the glyph geometry bounds built at the drawn origin (want {want}, got {ink})");
            output.WriteLine($"\"{text.Replace('\n', '|')}\" {pt}pt {role} ppd {ppd}: {m.Width:R} x {m.Height:R}");
        }

        // 대조: 글꼴이나 글자가 다르면 폭이 달라야 한다 — 같으면 위 비교가 글꼴 해석을 가려내지 못한다.
        var p = new CvDispTextPort(1.0);
        var d12 = ViDrawGeom.LabelDip(12);
        var data = p.Measure("Wil 1", d12, ViFontRole.Data, ViPaint.Of(ViOverlayColor.White)).Width;
        var ui = p.Measure("Wil 1", d12, ViFontRole.Ui, ViPaint.Of(ViOverlayColor.White)).Width;
        var data2 = p.Measure("Wil 11", d12, ViFontRole.Data, ViPaint.Of(ViOverlayColor.White)).Width;
        output.WriteLine($"control: data {data:R}, ui {ui:R}, data+1 char {data2:R}");
        Assert.True(!Same(data, ui), "the data and UI roles must resolve to different fonts, or the role mapping is untested");
        Assert.True(!Same(data, data2), "one more character must widen the block, or the measurement is not live");
        Assert.Null(p.MeasureInk(p.Measure("", d12, ViFontRole.Data, ViPaint.Of(ViOverlayColor.White)), "", d12, ViFontRole.Data, 0, 0));

        // 범위 밖 라벨 크기 — 코어가 잘라 넘긴 값이면 FormattedText 가 던지지 않는다(그리는 자리의 예외는 레이아웃 패스를 타고 앱까지 올라간다).
        foreach (var odd in new[] { float.NaN, 0f, -5f, 0.1f, 1e6f, float.PositiveInfinity })
        {
            var oddDip = ViDrawGeom.LabelDip(odd);
            var mm = p.Measure("x", oddDip, ViFontRole.Data, ViPaint.Of(ViOverlayColor.White));
            Assert.True(mm.Width > 0, $"a label of {odd} pt is measured at the clamped size {oddDip:R} DIP instead of throwing");
        }
    });

    /// <summary>그리기 트리를 펼친다 — DrawingGroup 안쪽까지.</summary>
    private static IEnumerable<Drawing> Flatten(Drawing d)
    {
        yield return d;
        if (d is DrawingGroup g)
            foreach (var c in g.Children)
                foreach (var x in Flatten(c)) yield return x;
    }

    [Fact]
    public void AdapterDrawsEachPrimitiveWithTheFlagsTheOldSurfaceUsed() => RunSta(() =>
    {
        // 어댑터가 옮겨야 하는 세부 — 점선, 경로 닫힘, 채움 다각형의 테두리 없음, 이음 부드럽게 안 함, 상자·원은 펜 없음, 클립 짝.
        // 화면 바이트 대조(저장소 밖 골든)에만 기대지 않고 저장소 안에서 못 박는다.
        var red = ViPaint.Of(ViOverlayColor.Red);
        var prims = new List<ViPrim>
        {
            new ViPrimLine(new ViStroke(red, 2, IsDashed: true), (0, 0), (10, 0)),
            new ViPrimPath(new ViStroke(red, 3, IsDashed: false), [(0, 0), (10, 0), (10, 10), (0, 10)], 4, isClosed: true),
            new ViPrimPath(new ViStroke(red, 1.2, IsDashed: false), [(0, 0), (5, 5), (9, 1), (99, 99)], 3, isClosed: false),
            new ViPrimFill(red, [(10, 0), (0, 4), (0, -4)]),
            new ViPrimClipPush(new ViBox(1, 2, 30, 40)),
            new ViPrimBox(ViDrawBldr.LabelBack, new ViBox(3, 4, 7, 7)),
            ViPrimClipPop.Instance,
            new ViPrimDot(red, (5, 6), 4.5),
        };
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) CvDispDrawAdp.Draw(dc, prims, 1.0);
        var all = Flatten(dv.Drawing).ToList();
        var geos = all.OfType<GeometryDrawing>().ToList();

        var line = geos.Single(g => g.Geometry is LineGeometry);
        Assert.True(line.Pen is { Thickness: 2 } && line.Pen.DashStyle.Dashes.SequenceEqual(DashStyles.Dash.Dashes),
            "a dashed stroke draws with the WPF Dash pattern at its thickness");

        var paths = geos.Where(g => g.Geometry is StreamGeometry && g.Pen is not null).ToList();
        Assert.Equal(2, paths.Count);
        var closed = PathGeometry.CreateFromGeometry(paths[0].Geometry).Figures.Single();
        Assert.True(closed.IsClosed && !closed.IsFilled, "a closed path is one closed, unfilled figure (corners join instead of leaving notches)");
        Assert.True(closed.Segments.All(s => s.IsStroked && !s.IsSmoothJoin), "path segments are stroked with sharp joins");
        Assert.True(paths[0].Pen!.DashStyle.Dashes.Count == 0, "a solid stroke has no dash");
        var open = PathGeometry.CreateFromGeometry(paths[1].Geometry).Figures.Single();
        Assert.True(!open.IsClosed, "an open path stays open");
        var openPts = open.Segments.OfType<PolyLineSegment>().SelectMany(s => s.Points)
            .Concat(open.Segments.OfType<LineSegment>().Select(s => s.Point)).ToList();
        Assert.True(openPts.Count == 2 && !openPts.Contains(new Point(99, 99)), "only the first Count points of a pooled array are drawn");

        var fill = geos.Single(g => g.Geometry is StreamGeometry && g.Pen is null);
        var head = PathGeometry.CreateFromGeometry(fill.Geometry).Figures.Single();
        Assert.True(fill.Brush is not null && head.IsFilled && head.IsClosed && head.Segments.All(s => !s.IsStroked),
            "an arrow head is a filled closed figure with no outline");

        var box = geos.Single(g => g.Geometry is RectangleGeometry);
        Assert.True(box.Pen is null && ((RectangleGeometry)box.Geometry).Rect == new Rect(3, 4, 7, 7)
                    && box.Brush is SolidColorBrush { Color.A: 200 }, "a box is a pen-less fill at its rect in its own color");
        var clipGroup = all.OfType<DrawingGroup>().Single(g => g.ClipGeometry is RectangleGeometry);
        Assert.True(((RectangleGeometry)clipGroup.ClipGeometry).Rect == new Rect(1, 2, 30, 40)
                    && Flatten(clipGroup).OfType<GeometryDrawing>().Any(g => g.Geometry is RectangleGeometry),
            "a clip push wraps exactly the primitives up to its pop");
        Assert.False(Flatten(clipGroup).OfType<GeometryDrawing>().Any(g => g.Geometry is EllipseGeometry), "the dot after the pop is outside the clip");

        var dot = geos.Single(g => g.Geometry is EllipseGeometry);
        var e = (EllipseGeometry)dot.Geometry;
        Assert.True(dot.Pen is null && e.Center == new Point(5, 6) && e.RadiusX == 4.5 && e.RadiusY == 4.5, "a dot is a pen-less filled circle");
    });
}
