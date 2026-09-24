// 자르기 분리(0.27) 회귀 — 자르기 → 전처리 → 환산이 분리 전 한 단계와 같은 픽셀·같은 자리를 내는지, 옛 저장본의 크롭이
// 조용히 사라지지 않는지(이관 전엔 저장에 남고 실행을 거부, 이관 뒤엔 파일에서 빠짐), 잘라 보이는 도우미(오버레이·프레임)가
// HUD 를 같은 모서리에 다시 붙이고 원본을 건드리지 않는지. 옛 필드가 없어지면 역직렬화가 모르는 키로 건너뛰어
// 잘라서 티칭한 레시피가 예외도 경고도 없이 전체 이미지에서 돈다 — 그 길을 여기서 막는다.
using System.Text.Json;
using CvInspect.Imaging;
using CvInspect.Vision;
using CvInspect.Vision.Opts;
using CvInspect.Vision.Overlay;
using OpenCvSharp;
using Xunit;

namespace CvInspect.Tests;

public class CvCropTests
{
    private static void Check(bool cond, string name) => Assert.True(cond, name);

    // 0.26 이 저장하던 모양 그대로 — 선언 순서대로 전 속성이 실렸다.
    private const string OldCroppedJson =
        """{"UseCrop":true,"CropX":200,"CropY":80,"CropW":120,"CropH":100,"SampleX":2,"SampleY":2,"MedianKernel":3}""";
    private const string OldUncroppedJson =
        """{"UseCrop":false,"CropX":0,"CropY":0,"CropW":640,"CropH":480,"SampleX":2,"SampleY":2,"MedianKernel":3}""";

    /// <summary>400×300 장면 — 축소·미디언이 실제로 일하도록 무늬를 깔고, 원본 (246..253, 116..123) 에 255 점 하나.
    /// 점의 두 좌표가 짝수라 (200,80) 에서 잘라 2:1 로 줄이면 정확히 4×4 칸이 된다.</summary>
    private static Mat Scene()
    {
        var img = new Mat(300, 400, MatType.CV_8UC1, Scalar.All(30));
        Cv2.Rectangle(img, new Rect(20, 20, 150, 90), Scalar.All(90), -1);
        Cv2.Circle(img, new Point(300, 200), 60, Scalar.All(150), -1);
        Cv2.Line(img, new Point(0, 299), new Point(399, 0), Scalar.All(200), 3);
        Cv2.Rectangle(img, new Rect(246, 116, 8, 8), Scalar.All(255), -1);
        return img;
    }

    [Fact]
    public void CropThenPreprocessReproducesTheOldSingleStage()
    {
        using var img = Scene();
        var crop = new CvCropOpt { UseCrop = true, CropX = 200, CropY = 80, CropW = 120, CropH = 100 };
        var ip = new CvImageProcessOpt();   // 2:1 + 미디언 3

        using var cut = CvImageOps.Crop(img, crop, out var used);
        using var pre = CvImageOps.Preprocess(cut, ip);
        Check(used == new Rect(200, 80, 120, 100), $"the crop reports the rect it used: {used}");

        // 분리 전 Preprocess 가 한 단계로 하던 것 — 뷰로 잘라 면적 축소 후 미디언.
        using var roi = new Mat(img, used);
        using var old = new Mat();
        Cv2.Resize(roi, old, new Size(roi.Cols / 2, roi.Rows / 2), 0, 0, InterpolationFlags.Area);
        Cv2.MedianBlur(old, old, 3);
        Check(pre.Size() == old.Size() && Cv2.Norm(pre, old, NormTypes.L1) == 0,
            "Crop then Preprocess gives the same pixels the single pre-0.27 stage gave — tools taught before the split see the same image");

        using var bright = new Mat();
        Cv2.Compare(pre, 255, bright, CmpTypes.EQ);
        var m = Cv2.Moments(bright, binaryImage: true);
        var (ox, oy) = CvImageOps.MapOf(used, pre).Apply(m.M10 / m.M00, m.M01 / m.M00);
        Check(Math.Abs(ox - 249.5) <= 1.0 && Math.Abs(oy - 119.5) <= 1.0,
            $"MapOf(used, pre) carries the crop origin and the 2:1 scale back to the original: ({ox:F2}, {oy:F2}) vs (249.5, 119.5)");
    }

    [Fact]
    public void CropReturnsACopyThatOwnsItsPixels()
    {
        // 카메라 프레임을 무복사로 감싼 Mat — 자른 것을 제자리로 고쳐도 발행된 배열은 그대로여야 한다.
        var pixels = new byte[60 * 40 * 3];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i % 251);
        var frame = new CamFrame((byte[])pixels.Clone(), 60, 40, 60 * 3, CamPixelFormat.Bgr24);
        using var wrapped = frame.AsMat();

        var crop = new CvCropOpt { UseCrop = true, CropX = 10, CropY = 5, CropW = 30, CropH = 20 };
        using var cut = CvImageOps.Crop(wrapped, crop, out var used);
        cut.SetTo(Scalar.All(0));
        Check(frame.Pixels.AsSpan().SequenceEqual(pixels),
            "writing into the cut leaves the camera frame's published pixels untouched — a view would have rewritten what history and display hold");
        Check(cut.Type() == MatType.CV_8UC3 && cut.Cols == 30 && cut.Rows == 20 && cut.IsContinuous(),
            $"colour stays colour and the cut is its own continuous image: {cut.Type()} {cut.Cols}x{cut.Rows}");

        using var whole = CvImageOps.Crop(wrapped, new CvCropOpt(), out var full);
        Check(full == new Rect(0, 0, 60, 40) && whole.Data != wrapped.Data,
            $"with the crop off it is the whole frame, still a copy: {full}");
    }

    [Fact]
    public void AnOldCropIsKeptRefusedAndMovedNeverDropped()
    {
        var prevSink = CvLog.Sink;
        var lines = new List<string>();
        CvLog.Sink = (lv, src, msg, _) => { if (src == nameof(CvCropOpt)) lines.Add($"{lv}|{msg}"); };
        try
        {
            var ip = JsonSerializer.Deserialize<CvImageProcessOpt>(OldCroppedJson)!;
            Check(ip.HasLegacyCrop, "a pre-0.27 file with the crop on is recognised, not skipped as an unknown key");

            var saved = JsonSerializer.Serialize(ip);
            Check(saved.Contains("\"UseCrop\":true") && saved.Contains("\"CropX\":200") && saved.Contains("\"CropH\":100"),
                $"saving before the move keeps the crop in the file — a host that saves without migrating must not lose it: {saved}");

            using var img = Scene();
            var ex = Assert.Throws<InvalidOperationException>(() => CvImageOps.Preprocess(img, ip));
            Check(ex.Message.Contains("CvCropOpt.FromLegacy") && ex.Message.Contains("200,80 120x100"),
                $"Preprocess refuses an unmoved crop and says where it went — running uncropped would put every taught tool off by the crop origin: {ex.Message}");

            var direct = JsonSerializer.Deserialize<CvCropOpt>(OldCroppedJson)!;
            Check(direct is { UseCrop: true, CropX: 200, CropY: 80, CropW: 120, CropH: 100 },
                "the old file read as a CvCropOpt gives the crop back — the property names carried over unchanged");

            var moved = CvCropOpt.FromLegacy(ip);
            Check(moved is { UseCrop: true, CropX: 200, CropY: 80, CropW: 120, CropH: 100 }, "FromLegacy moves the crop with its values");
            Check(!ip.HasLegacyCrop, "and turns it off on the image-process side, so Preprocess runs again");
            using (var pre = CvImageOps.Preprocess(img, ip)) Check(pre.Cols == 200, "Preprocess runs after the move");
            var resaved = JsonSerializer.Serialize(ip);
            Check(!resaved.Contains("Crop"), $"saving after the move drops the old keys: {resaved}");
            Check(CvCropOpt.FromLegacy(ip) is null, "nothing left to move the second time");
            Check(lines.Count == 1 && lines[0].StartsWith("Info|Moved the crop"),
                $"the move leaves one Info line, so a site log can tell a migrated recipe apart: {string.Join(" / ", lines)}");
        }
        finally
        {
            CvLog.Sink = prevSink;
        }
    }

    [Fact]
    public void AnOldCropWithoutItsSizeMovesAtTheOldDefaultSize()
    {
        // 0.26 은 폭·높이 키가 없는 파일을 기본값 640×480 으로 잘랐다. 0 으로 옮기면 퇴화 사각이 되어 자르기가 전체로
        // 폴백하고, 원점만큼 어긋난 판정이 정상처럼 나간다 — Preprocess 가 막으려던 바로 그 결과가 한 단계 뒤에서 난다.
        var ip = JsonSerializer.Deserialize<CvImageProcessOpt>("""{"UseCrop":true,"CropX":100,"CropY":50,"SampleX":2}""")!;
        var moved = CvCropOpt.FromLegacy(ip);
        Check(moved is { CropX: 100, CropY: 50, CropW: 640, CropH: 480 },
            $"missing CropW/CropH move as 640x480, as 0.26 cropped that file: {moved?.CropW}x{moved?.CropH}");
        using var big = new Mat(800, 1000, MatType.CV_8UC1, Scalar.All(0));
        using var cut = CvImageOps.Crop(big, moved!, out var used);
        Check(used == new Rect(100, 50, 640, 480), $"and the crop actually cuts there instead of falling back to the whole image: {used}");
        var direct = JsonSerializer.Deserialize<CvCropOpt>("""{"UseCrop":true,"CropX":100,"CropY":50,"SampleX":2}""")!;
        Check((direct.CropW, direct.CropH) == (moved!.CropW, moved.CropH), "both documented migration paths agree on the missing size");
    }

    [Fact]
    public void AnOldFileWithTheCropOffLoadsQuietly()
    {
        var ip = JsonSerializer.Deserialize<CvImageProcessOpt>(OldUncroppedJson)!;
        Check(!ip.HasLegacyCrop, "an old file whose crop was off has nothing to move");
        using var img = Scene();
        using (var pre = CvImageOps.Preprocess(img, ip)) Check(pre.Cols == 200 && pre.Rows == 150, "and runs as before");
        var saved = JsonSerializer.Serialize(ip);
        Check(!saved.Contains("Crop"), $"its unused old rect is not carried into new saves: {saved}");
        Check(CvCropOpt.FromLegacy(ip) is null, "FromLegacy gives null for it");
    }

    [Fact]
    public void CroppedOverlayMovesItemsAndPutsTheHudBackInItsCorner()
    {
        const double imgW = 1000, imgH = 800, x = 300, y = 200, w = 400, h = 300;
        var src = new ViOverlay();
        var seg = new ViOverlaySeg { X1 = 310, Y1 = 220, X2 = 350, Y2 = 260, HasEndArrow = true, IsDashed = true, Color = ViOverlayColor.Cyan };
        var rect = new ViOverlayRect { CenterX = 400, CenterY = 300, Width = 50, Height = 30, AngleDeg = 10, Color = ViOverlayColor.Orange, IsDashed = true };
        var poly = new ViOverlayPoly { Points = [(300, 200), (320, 210)], IsClosed = false, Color = ViOverlayColor.Teal, IsDashed = true };
        var boxLabel = new ViOverlayLabel { Text = "[OK] A 12", X = 330, Y = 240, Align = ViOverlayAlign.BottomLeft };
        var centredHud = new ViOverlayLabel { Text = "[NG] centred", X = 500, Y = 250, Align = ViOverlayAlign.TopCenter, IsHud = true };
        src.Add(seg);
        src.Add(rect);
        src.Add(poly);
        src.Add(boxLabel);
        src.Add(centredHud);
        src.AddSummary(true, "tl", "a");
        src.AddSummary(30, false, "tl-y");
        src.AddSummary(ViHudPos.TopRight, imgW, imgH, true, "tr");
        src.AddSummary(ViHudPos.BottomLeft, imgW, imgH, false, "bl");
        src.AddSummary(ViHudPos.BottomRight, imgW, imgH, true, "br", [("x", ViOverlayColor.Yellow)]);
        var huds = src.Items.OfType<ViOverlayLabel>().Where(l => l.IsHud && !ReferenceEquals(l, centredHud)).ToList();
        Check(huds.Count == 5 && !boxLabel.IsHud,
            "every AddSummary branch marks its label as HUD; a corner-aligned box label starting with [OK] does not get the mark — Align and text cannot tell them apart");
        var before = src.Items.ToList();

        var cut = src.CropTo(x, y, w, h);

        Check(src.Items.SequenceEqual(before) && seg.X1 == 310 && huds[4].X == imgW - 12,
            "the source overlay is untouched — history and other screens hold the same reference");
        Check(cut.Items.Count == src.Items.Count, "nothing is dropped — items outside the region are the renderer's to clip");

        var s = (ViOverlaySeg)cut.Items[0];
        Check((s.X1, s.Y1, s.X2, s.Y2, s.HasEndArrow, s.IsDashed, s.Color) == (10, 20, 50, 60, true, true, ViOverlayColor.Cyan), $"segment moved by (-x,-y) with its style: {s.X1},{s.Y1}");
        var r = (ViOverlayRect)cut.Items[1];
        Check((r.CenterX, r.CenterY, r.Width, r.Height, r.AngleDeg, r.Color, r.IsDashed) == (100, 100, 50, 30, 10, ViOverlayColor.Orange, true),
            "rect moves its centre only and keeps its style");
        var p = (ViOverlayPoly)cut.Items[2];
        Check(p.Points.SequenceEqual(new (double X, double Y)[] { (0, 0), (20, 10) }) && !p.IsClosed && p.Color == ViOverlayColor.Teal && p.IsDashed,
            "poly points move and it keeps its style");
        var bl = (ViOverlayLabel)cut.Items[3];
        Check((bl.X, bl.Y, bl.Align, bl.IsHud) == (30, 40, ViOverlayAlign.BottomLeft, false), "a non-HUD label moves with its geometry");
        var ch = (ViOverlayLabel)cut.Items[4];
        Check((ch.X, ch.Y, ch.IsHud) == (200, 50, true),
            "a HUD-marked label with no corner alignment has no corner to go back to — it moves like any label");

        // HUD — 잘린 이미지에 처음부터 붙인 것과 같은 자리여야 한다.
        var fresh = new ViOverlay();
        fresh.AddSummary(true, "tl", "a");
        fresh.AddSummary(30, false, "tl-y");
        fresh.AddSummary(ViHudPos.TopRight, w, h, true, "tr");
        fresh.AddSummary(ViHudPos.BottomLeft, w, h, false, "bl");
        fresh.AddSummary(ViHudPos.BottomRight, w, h, true, "br", [("x", ViOverlayColor.Yellow)]);
        var got = cut.Items.Skip(5).OfType<ViOverlayLabel>().ToList();
        for (var i = 0; i < 5; i++)
        {
            var a = got[i];
            var e = (ViOverlayLabel)fresh.Items[i];
            Check(a.X == e.X && a.Y == e.Y && a.Align == e.Align,
                $"HUD '{a.Text.Split('\n')[0]}' sits where AddSummary would put it on the cut image: ({a.X},{a.Y}) vs ({e.X},{e.Y})");
            Check(a.X is >= 0 and <= w && a.Y is >= 0 and <= h, $"HUD stays inside the cut image: ({a.X},{a.Y})");
            Check(a.Text == huds[i].Text && a.Color == huds[i].Color && a.FontSize == huds[i].FontSize && a.HasBackground
                  && ReferenceEquals(a.LineColors, huds[i].LineColors) && a.IsHud,
                "the HUD keeps its text, verdict colour, line colours and mark");
        }
    }

    [Fact]
    public void CamFrameCropKeepsFormatAndBothClocks()
    {
        // 폭 10, 행 끝 패딩 6바이트(0xEE) — 자르기는 stride 로 행을 걸어야 한다.
        var buf = new byte[16 * 6];
        for (var yy = 0; yy < 6; yy++)
        for (var xx = 0; xx < 16; xx++)
            buf[yy * 16 + xx] = xx < 10 ? (byte)(yy * 10 + xx) : (byte)0xEE;
        var dev = TimeSpan.FromMilliseconds(123);
        var mono = new CamFrame(buf, 10, 6, 16, CamPixelFormat.Mono8, dev);
        Thread.Sleep(20);   // 지금을 새로 찍으면 원본 도착 시각과 갈리게

        var c = mono.Crop(2, 3, 5, 2);
        Check((c.Width, c.Height, c.Stride, c.Format) == (5, 2, 5, CamPixelFormat.Mono8), $"tight stride, same format: {c.Width}x{c.Height} stride {c.Stride}");
        Check(c.Pixels.SequenceEqual(new byte[] { 32, 33, 34, 35, 36, 42, 43, 44, 45, 46 }), "rows walked by stride, padding left behind");
        Check(c.DeviceTimestamp == dev && c.TimestampUtc == mono.TimestampUtc,
            "the cut is the same shot — device time and host arrival time carry over (a fresh 'now' would fake the queueing delay)");
        c.Pixels[0] = 0;
        Check(mono.Pixels[3 * 16 + 2] == 32, "the cut owns its pixels");

        var bgr = new byte[4 * 3 * 3];
        for (var i = 0; i < bgr.Length; i++) bgr[i] = (byte)i;
        var color = new CamFrame(bgr, 4, 3, 12, CamPixelFormat.Bgr24).Crop(1, 1, 2, 2);
        Check(color is { Format: CamPixelFormat.Bgr24, Stride: 6 } && color.Pixels.SequenceEqual(new byte[] { 15, 16, 17, 18, 19, 20, 27, 28, 29, 30, 31, 32 }),
            "colour frames crop whole pixels");

        Assert.Throws<ArgumentOutOfRangeException>(() => mono.Crop(6, 0, 5, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => mono.Crop(-1, 0, 2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => mono.Crop(0, 0, 0, 1));
    }
}
