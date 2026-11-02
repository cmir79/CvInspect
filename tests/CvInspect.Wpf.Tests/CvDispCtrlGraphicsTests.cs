// 그래픽 숨기기 토글(👁)과 그래픽 포함 내보내기(RenderWithOverlay) — 창 없이 오프스크린으로 그려 픽셀을 센다(창을 띄우지 않는다).
// 계약은 CvInspect.Avalonia 판과 같다(사용자 결정 0.35):
//  - 숨김 = 결과 오버레이 전부(모서리 HUD 포함)를 그리지 않는다. 티칭 도형(Shapes)과 이미지는 그대로. 숨긴 동안에도 Overlay 값은 받아 둔다.
//  - 일시 상태: 새 Frame·새 Overlay 값이 들어오면 저절로 풀린다. Shapes 변경과 같은 참조 재대입은 풀지 않는다.
//  - 메뉴의 숨기기 항목은 체크 항목이고 열 때마다 툴바 토글을 따른다. 그래픽 포함 저장 항목은 저장과 같은 활성 규칙.
//  - RenderWithOverlay = 원본 해상도 W×H BGR 3채널, 이미지 픽셀 1:1, 글자·선은 k 배, 편집 도형 없음, 숨김과 무관, 프레임이 없으면 null.
// WPF 요소는 STA 스레드에서만 만들 수 있어 본문을 STA 로 감싼다.

using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CvInspect.Controls;
using CvInspect.Imaging;
using CvInspect.Vision.Edit;
using CvInspect.Vision.Overlay;
using OpenCvSharp;
using Xunit;

namespace CvInspect.Wpf.Tests;

public class CvDispCtrlGraphicsTests
{
    private const int Box = 400;

    private static void Check(bool cond, string name) => Assert.True(cond, name);

    private static void RunSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    /// <summary>레이아웃 → 큐 비우기 → 오프스크린 렌더(Pbgra32).</summary>
    private static byte[] Render(FrameworkElement ctrl, int w = Box, int h = Box)
    {
        ctrl.Measure(new System.Windows.Size(w, h));
        ctrl.Arrange(new System.Windows.Rect(0, 0, w, h));
        ctrl.UpdateLayout();
        Pump();
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(ctrl);
        var buf = new byte[w * h * 4];
        rtb.CopyPixels(buf, w * 4, 0);
        return buf;
    }

    private static int Diff(byte[] a, byte[] b)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) n++;
        return n;
    }

    private static CamFrame GrayFrame(int w, int h)
    {
        var px = new byte[w * h];
        Array.Fill(px, (byte)128);
        return new CamFrame(px, w, h, w, CamPixelFormat.Mono8);
    }

    private static CamFrame Gradient(int w, int h)
    {
        var px = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 3;
                px[i] = (byte)(x * 7 + y);
                px[i + 1] = (byte)(y * 5 + x * 3);
                px[i + 2] = (byte)((x ^ y) * 11);
            }
        return new CamFrame(px, w, h, w * 3, CamPixelFormat.Bgr24);
    }

    private static ViOverlay RectAndHud(double cx = 200, double cy = 200)
    {
        var o = new ViOverlay();
        o.Add(new ViOverlayRect { CenterX = cx, CenterY = cy, Width = 160, Height = 100, Color = ViOverlayColor.Red });
        o.AddSummary(false, "PatternFind", "score 0.41");
        return o;
    }

    private static ViOverlay HudOnly()
    {
        var o = new ViOverlay();
        o.AddSummary(true, "PatternFind", "score 0.97");
        return o;
    }

    private static IReadOnlyList<CvEditShape> OneShape()
    {
        var r = new CvEditRect { Label = "Search" };
        r.Set(60, 260, 120, 80);
        return [r];
    }

    private static ToggleButton HideOf(DependencyObject root) => ToggleOf(root, "Hide Graphics");

    /// <summary>툴바 토글을 툴팁 문구로 찾는다(툴팁은 번역하지 않는 고정 영어다).</summary>
    private static ToggleButton ToggleOf(DependencyObject root, string tooltip)
        => FindToggle(root, tooltip) ?? throw new InvalidOperationException($"the '{tooltip}' toggle was not found");

    private static ToggleButton? FindToggle(DependencyObject root, string tooltip)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is ToggleButton t && Equals(t.ToolTip, tooltip)) return t;
            if (FindToggle(c, tooltip) is { } found) return found;
        }
        return null;
    }

    /// <summary>화면에 올린 컨트롤 — 도형 구독은 올라와 있어야 걸리지만 여기서 보는 것은 그리기라 레이아웃만 돌린다.</summary>
    private static CvDispCtrl Ctrl(object? frame, object? overlay, IReadOnlyList<CvEditShape>? shapes)
        => new() { IsToolbarVisible = false, Frame = frame, Overlay = overlay, Shapes = shapes };

    [Fact]
    public void HidingDropsTheWholeOverlayKeepsShapesAndShowsAgainOnNewValues() => RunSta(() =>
    {
        var shapes = OneShape();
        var ctrl = Ctrl(GrayFrame(Box, Box), RectAndHud(), shapes);
        var bare = Render(Ctrl(GrayFrame(Box, Box), null, shapes));
        var noShapes = Render(Ctrl(GrayFrame(Box, Box), null, null));
        Check(Diff(bare, noShapes) > 0, "premise: the teaching shape draws on the baseline");

        var shown = Render(ctrl);
        var hide = HideOf(ctrl);
        Check(Diff(shown, bare) > 0, "premise: the overlay draws while not hidden");

        hide.IsChecked = true;
        Check(Diff(Render(ctrl), bare) == 0,
            "hidden: the whole result overlay (rect and corner HUD) is gone while the teaching shape and the image stay — the picture equals the no-overlay baseline");

        ctrl.Overlay = HudOnly();
        Check(hide.IsChecked == false, "a new Overlay value un-hides the graphics — hiding is temporary");
        Check(Diff(Render(ctrl), bare) > 0, "premise: the HUD-only overlay draws");
        hide.IsChecked = true;
        Check(Diff(Render(ctrl), bare) == 0, "hidden: a HUD-only overlay leaves nothing on screen — the HUD is part of what is hidden");

        var moved = RectAndHud(260, 120);
        ctrl.Overlay = moved;
        var reference = Render(Ctrl(GrayFrame(Box, Box), moved, shapes));
        Check(hide.IsChecked == false && Diff(Render(ctrl), reference) == 0,
            "the overlay assigned while hidden un-hides and is the one drawn (equals a fresh control with that overlay)");

        hide.IsChecked = true;
        ctrl.Overlay = moved;
        Check(hide.IsChecked == true && Diff(Render(ctrl), bare) == 0,
            "assigning the same overlay reference again is not a property change, so it does not un-hide (documented)");

        ctrl.Shapes = OneShape();
        Check(hide.IsChecked == true && Diff(Render(ctrl), bare) == 0, "a Shapes change does not un-hide");

        ctrl.Frame = GrayFrame(Box, Box);
        Check(hide.IsChecked == false, "a new Frame value un-hides the graphics");
        Check(Diff(Render(ctrl), bare) > 0, "...and the overlay is drawn again");
    });

    /// <summary>메뉴를 사람이 여는 길로 연다 — 컨트롤은 ContextMenuOpening 에서 라벨·체크·활성을 맞춘다. 그 인자의 생성자는 공개돼 있지 않아 리플렉션으로 만든다.</summary>
    private static void RaiseMenuOpening(FrameworkElement el)
    {
        var ctor = typeof(ContextMenuEventArgs).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, [typeof(object), typeof(bool)], null)
                   ?? throw new InvalidOperationException("ContextMenuEventArgs(object, bool) not found");
        var args = (ContextMenuEventArgs)ctor.Invoke([el, true]);
        args.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
        el.RaiseEvent(args);
    }

    [Fact]
    public void MenuHideItemFollowsTheToggleBothWays() => RunSta(() =>
    {
        var ctrl = new CvDispCtrl { Overlay = RectAndHud() };
        Render(ctrl);
        var hide = HideOf(ctrl);
        var items = ctrl.ContextMenu.Items.OfType<MenuItem>().ToList();
        var hideItem = items[^1];
        var save = items[1];
        var saveGfx = items[2];
        Check(hideItem.IsCheckable, "the hide entry is a checkable item, the last one in the menu");

        bool Opened()
        {
            RaiseMenuOpening(ctrl);
            return hideItem.IsChecked;
        }

        Check(!Opened(), "the hide entry opens unchecked while graphics show");
        Check(!saveGfx.IsEnabled && !save.IsEnabled, $"without a frame save-with-graphics is grey like save (save {save.IsEnabled}, with graphics {saveGfx.IsEnabled})");
        hide.IsChecked = true;
        Check(Opened(), "after the toolbar toggle hides, the entry opens checked");

        ctrl.Frame = GrayFrame(Box, Box);
        Check(hide.IsChecked == false && !Opened(), "after the auto re-show the entry opens unchecked again — it is re-read on every open");
        Check(saveGfx.IsEnabled && save.IsEnabled, "with a frame save-with-graphics is enabled like save");

        // 체크 항목 클릭 — WPF 는 자기 체크를 먼저 뒤집고 Click 을 낸다(사람의 클릭 길 = 자동화 피어의 Invoke).
        ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.MenuItemAutomationPeer(hideItem)).Invoke();
        Pump();
        Check(hide.IsChecked == true, "clicking the hide entry hides through the toolbar toggle");
        Check(Opened(), "...and the entry then opens checked");

        ctrl.IsToolbarVisible = false;
        Check(hideItem.Visibility == Visibility.Collapsed && ctrl.ContextMenu.Items[^2] is Separator { Visibility: Visibility.Collapsed },
            "hiding the toolbar hides the hide entry together with its separator");
    });

    private static byte[] Bytes(Mat m)
    {
        Check(m.IsContinuous(), "the exported Mat is continuous");
        var b = new byte[m.Rows * m.Cols * m.Channels()];
        Marshal.Copy(m.Data, b, 0, b.Length);
        return b;
    }

    private static bool IsRed(byte[] bgr, int w, int x, int y)
    {
        int i = (y * w + x) * 3;
        return bgr[i + 2] > 180 && bgr[i + 1] < 90 && bgr[i] < 90;
    }

    [Fact]
    public void RenderWithOverlayIsTheFrameAtNativeSizeWithTheOverlay() => RunSta(() =>
    {
        Check(new CvDispCtrl().RenderWithOverlay() is null, "no frame -> null, not an empty Mat");

        const int w = 640, h = 480;
        var frame = Gradient(w, h);
        var overlay = new ViOverlay();
        overlay.Add(new ViOverlayRect { CenterX = 320, CenterY = 240, Width = 200, Height = 100, Color = ViOverlayColor.Red });
        var ctrl = Ctrl(frame, overlay, OneShape());
        Render(ctrl);   // 화면 배율(400 칸 맞춤)과 무관해야 한다

        using var mat = ctrl.RenderWithOverlay()!;
        Check(mat is not null && mat.Rows == h && mat.Cols == w && mat.Type() == MatType.CV_8UC3,
            $"the export is the frame's own size as an 8-bit BGR 3-channel Mat ({mat?.Cols}x{mat?.Rows} {mat?.Type()})");
        var px = Bytes(mat!);

        bool RedNear(int x, int y)
        {
            for (int d = -2; d <= 2; d++)
                if (IsRed(px, w, x, y + d) || IsRed(px, w, x + d, y)) return true;
            return false;
        }
        Check(RedNear(320, 190) && RedNear(320, 290) && RedNear(220, 240) && RedNear(420, 240),
            "the overlay rect is drawn at its image coordinates in the export (all four edges red)");

        var (changed, inBand) = CompareOutsideBand(px, frame, 220, 190, 420, 290, 6);
        Check(changed == 0, $"outside the rect's stroke band every pixel equals the source frame byte for byte — the image is drawn 1:1 ({changed} differ)");
        Check(inBand > 0, $"control: the stroke band does differ ({inBand} pixels) — the comparison can see a change");

        var noShapes = Ctrl(frame, overlay, null);
        using var plain = noShapes.RenderWithOverlay()!;
        Check(Diff(Bytes(plain), px) == 0, "teaching shapes are not part of the export");

        HideOf(ctrl).IsChecked = true;
        using var whileHidden = ctrl.RenderWithOverlay()!;
        Check(Diff(Bytes(whileHidden), px) == 0, "the export includes the graphics even while they are hidden on screen");

        ctrl.Overlay = null;
        using var imageOnly = ctrl.RenderWithOverlay()!;
        Check(Diff(Bytes(imageOnly), frame.Pixels) == 0, "without an overlay the export is the source image unchanged");

        var small = new ViOverlay();
        small.Add(new ViOverlayRect { CenterX = 150, CenterY = 100, Width = 100, Height = 60, Color = ViOverlayColor.Red });
        using var gm = Ctrl(GrayFrame(300, 200), small, null).RenderWithOverlay()!;
        var gp = Bytes(gm);
        Check(gm.Channels() == 3 && gm.Cols == 300 && gm.Rows == 200 && gp[0] == 128 && gp[1] == 128 && gp[2] == 128,
            $"a mono frame exports as BGR with its gray value in all three channels ({gm.Channels()}ch {gm.Cols}x{gm.Rows}, {gp[0]},{gp[1]},{gp[2]})");
        Check(IsRed(gp, 300, 150, 69) || IsRed(gp, 300, 150, 70) || IsRed(gp, 300, 150, 71), "...and the overlay keeps its colour on it (top edge at y=70 is red)");

        // 다른 스레드에서 부르면 던진다 — 렌더러를 쓰는 UI 스레드 전용 계약.
        Exception? off = null;
        var t = new Thread(() => off = Record.Exception(() => ctrl.RenderWithOverlay()));
        t.Start();
        t.Join();
        Check(off is InvalidOperationException, $"calling from another thread throws InvalidOperationException ({off?.GetType().Name ?? "nothing"})");
    });

    /// <summary>글자·선은 k 배. 선은 굵게(StrokeScale 4 → k=1 에서 8 px) 그리고 <b>완전히 덮인 화소</b>(선 중심 색과 같은 화소)만 센다 —
    /// 가장자리 반투명 화소까지 세면 얇은 선에서 ±1 px 이 비를 흔들어, k 대신 1.5 배로 굵어진 선도 통과했다(허용 오차 0.6 시절).</summary>
    [Fact]
    public void RenderWithOverlayScalesStrokesAndTextByK() => RunSta(() =>
    {
        (int Line, int TextH) Measure(int w, int h)
        {
            double f = w / 640.0;
            var o = new ViOverlay();
            o.Add(new ViOverlaySeg { X1 = 40 * f, Y1 = 400 * f, X2 = 600 * f, Y2 = 400 * f, Color = ViOverlayColor.Red, StrokeScale = 4 });
            o.Add(new ViOverlayLabel { Text = "OK 12.3", X = 40 * f, Y = 60 * f, FontSize = 14, Align = ViOverlayAlign.TopLeft, Color = ViOverlayColor.Red, HasBackground = false });
            using var m = Ctrl(GrayFrame(w, h), o, null).RenderWithOverlay()!;
            var p = Bytes(m);
            Check(m.Cols == w && m.Rows == h, $"the export is {w}x{h} ({m.Cols}x{m.Rows})");
            int col = (int)(320 * f), cy = (int)(400 * f);
            int ci = (cy * w + col) * 3;
            var (cb, cg, cr) = (p[ci], p[ci + 1], p[ci + 2]);
            Check(cr > 180 && cg < 90 && cb < 90, $"premise: the stroke centre at {col},{cy} is red ({cr},{cg},{cb})");
            int line = 0;
            for (int y = 0; y < h; y++)
            {
                int i = (y * w + col) * 3;
                if (Math.Abs(p[i] - cb) <= 1 && Math.Abs(p[i + 1] - cg) <= 1 && Math.Abs(p[i + 2] - cr) <= 1) line++;
            }
            int top = -1, bottom = -1;
            for (int y = 0; y < (int)(300 * f); y++)
                for (int x = 0; x < w; x++)
                    if (p[(y * w + x) * 3 + 2] != 128)
                    {
                        if (top < 0) top = y;
                        bottom = y;
                        break;
                    }
            return (line, bottom - top + 1);
        }

        var small = Measure(640, 480);
        var big = Measure(2560, 1920);
        double kBig = 2560 / (1280 * 0.97);
        Check(small.Line >= 6 && small.TextH > 5, $"premise: the small frame draws a thick line ({small.Line} fully covered px, want >= 6) and the text ({small.TextH}px)");
        var lineRatio = big.Line / (double)small.Line;
        var textRatio = big.TextH / (double)small.TextH;
        Check(Math.Abs(lineRatio - kBig) < 0.25,
            $"the stroke is k times thicker on the large frame: {small.Line}px at k=1 vs {big.Line}px at k={kBig:0.###} (ratio {lineRatio:0.##}; a 1.5x stroke would be ~1.5)");
        Check(Math.Abs(textRatio - kBig) < 0.35, $"the label text is k times taller on the large frame: {small.TextH}px at k=1 vs {big.TextH}px at k={kBig:0.###} (ratio {textRatio:0.##})");
    });

    /// <summary>사각(l,t)-(r,b) 둘레 ±margin 띠 밖에서 내보낸 BGR 이 원본 프레임(Bgr24·Bgra32)의 B·G·R 과 다른 화소 수, 띠 안에서 다른 화소 수.</summary>
    private static (int Changed, int InBand) CompareOutsideBand(byte[] bgr, CamFrame src, int l, int t, int r, int b, int margin)
    {
        int w = src.Width, h = src.Height, ch = src.Format == CamPixelFormat.Bgra32 ? 4 : 3;
        int changed = 0, inBand = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool band = x >= l - margin && x <= r + margin && y >= t - margin && y <= b + margin
                            && !(x >= l + margin && x <= r - margin && y >= t + margin && y <= b - margin);
                int i = (y * w + x) * 3, s = y * src.Stride + x * ch;
                bool same = bgr[i] == src.Pixels[s] && bgr[i + 1] == src.Pixels[s + 1] && bgr[i + 2] == src.Pixels[s + 2];
                if (band) { if (!same) inBand++; }
                else if (!same) changed++;
            }
        return (changed, inBand);
    }

    /// <summary>k &gt; 1 에서도 이미지는 1:1 — 5MP 그라디언트를 DPI 96·k(≈2.2) 비트맵에 뷰 1/k 로 그려도 띠 밖 화소가 원본 바이트 그대로다.
    /// k = 1 절(640×480)만 보면 배율이 정확히 1 이라 표본 어긋남이 드러나지 않는다.</summary>
    [Fact]
    public void RenderWithOverlayCopiesTheImage1To1WhenKIsAboveOne() => RunSta(() =>
    {
        const int w = 2448, h = 2048;
        var frame = Gradient(w, h);
        var overlay = new ViOverlay();
        overlay.Add(new ViOverlayRect { CenterX = 1224, CenterY = 1024, Width = 800, Height = 400, Color = ViOverlayColor.Red });
        var ctrl = Ctrl(frame, overlay, null);
        Render(ctrl);
        using var mat = ctrl.RenderWithOverlay()!;
        Check(mat.Cols == w && mat.Rows == h, $"the export is native size ({mat.Cols}x{mat.Rows})");
        var (changed, inBand) = CompareOutsideBand(Bytes(mat), frame, 824, 824, 1624, 1224, 10);
        Check(changed == 0, $"at k≈2.2 every pixel outside the rect's stroke band still equals the source byte for byte — the image is drawn 1:1 ({changed} differ)");
        Check(inBand > 0, $"control: the stroke band does differ ({inBand} pixels)");
    });

    /// <summary>4채널 프레임은 불투명으로 내보낸다 — 알파 0·128 화소도 원본 B·G·R 그대로(알파를 그대로 그리면 미리 곱한 알파 대상에서 검정·반 밝기가 된다).</summary>
    [Fact]
    public void RenderWithOverlayExportsFourChannelFramesAsOpaqueSourceColours() => RunSta(() =>
    {
        const int w = 200, h = 150;
        var px = new byte[w * h * 4];
        int translucent = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                px[i] = (byte)(x + 40);
                px[i + 1] = (byte)(y + 60);
                px[i + 2] = (byte)(200 - x / 2);
                px[i + 3] = y < 10 ? (byte)255 : x < 100 ? (byte)0 : (byte)128;
                if (px[i + 3] < 255) translucent++;
            }
        var frame = new CamFrame(px, w, h, w * 4, CamPixelFormat.Bgra32);
        var overlay = new ViOverlay();
        overlay.Add(new ViOverlayRect { CenterX = 100, CenterY = 75, Width = 80, Height = 60, Color = ViOverlayColor.Red });
        var ctrl = Ctrl(frame, overlay, null);
        Render(ctrl);
        Check(translucent > w * h / 2, $"premise: most pixels have alpha 0 or 128 ({translucent})");

        using var mat = ctrl.RenderWithOverlay()!;
        Check(mat.Channels() == 3 && mat.Cols == w && mat.Rows == h, $"the export is BGR at native size ({mat.Channels()}ch {mat.Cols}x{mat.Rows})");
        var (changed, inBand) = CompareOutsideBand(Bytes(mat), frame, 60, 45, 140, 105, 6);
        Check(changed == 0,
            $"outside the stroke band every pixel equals the source B,G,R whatever its alpha (0, 128, 255) — the frame is drawn as opaque, not premultiplied ({changed} differ)");
        Check(inBand > 0, $"control: the stroke band does differ ({inBand} pixels)");
    });

    /// <summary>툴바를 감추면 숨긴 그래픽이 다시 보인다 — 👁 토글과 메뉴 항목이 둘 다 툴바 묶음이라 감춘 채로는 되돌릴 수단이 없다.</summary>
    [Fact]
    public void HidingTheToolbarShowsHiddenGraphicsAgain() => RunSta(() =>
    {
        var overlay = RectAndHud();
        var reference = Render(Ctrl(GrayFrame(Box, Box), overlay, null));   // 툴바 없음, 그래픽 보임

        var stuck = Ctrl(GrayFrame(Box, Box), overlay, null);
        HideOf(stuck).IsChecked = true;
        Check(Diff(Render(stuck), reference) > 0, "control: a toolbar-less control whose graphics are hidden does differ from the shown reference");

        var ctrl = new CvDispCtrl { Frame = GrayFrame(Box, Box), Overlay = overlay };   // 툴바 보임
        var hide = HideOf(ctrl);
        Render(ctrl);
        hide.IsChecked = true;
        ctrl.IsToolbarVisible = false;
        Check(hide.IsChecked == false, "hiding the toolbar releases the hide toggle — its toggle and menu entry go away with the toolbar");
        Check(Diff(Render(ctrl), reference) == 0, "...and the graphics are drawn again (equals the shown reference)");

        ctrl.IsToolbarVisible = true;
        Check(hide.IsChecked == false, "showing the toolbar again does not re-hide");
    });

    /// <summary>메뉴가 열린 사이 숨김이 저절로 풀려도, 숨기기 항목 클릭은 "열 때 본 체크의 반대" 다(두 UI 패키지 같은 규칙).
    /// 체크된 항목을 누른 사람은 그래픽을 보이게 하려던 것 — 지금 토글을 뒤집으면 다시 숨겨 정반대가 된다.</summary>
    [Fact]
    public void MenuHideClickSetsTheOppositeOfWhatTheEntryShowed() => RunSta(() =>
    {
        var ctrl = new CvDispCtrl { Frame = GrayFrame(Box, Box), Overlay = RectAndHud() };
        Render(ctrl);
        var hide = HideOf(ctrl);
        var hideItem = ctrl.ContextMenu.Items.OfType<MenuItem>().Last();

        void Click()
        {
            ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.MenuItemAutomationPeer(hideItem)).Invoke();
            Pump();
        }

        hide.IsChecked = true;
        RaiseMenuOpening(ctrl);
        Check(hideItem.IsChecked, "premise: the entry opens checked while graphics are hidden");
        ctrl.Frame = GrayFrame(Box, Box);   // 메뉴가 열린 사이 새 프레임 — 저절로 풀린다
        Check(hide.IsChecked == false && hideItem.IsChecked, "premise: the toggle re-showed while the open entry still shows the old check");
        Click();
        Check(hide.IsChecked == false,
            "clicking the entry that showed checked leaves the graphics shown — the click means 'uncheck what I saw', not 'flip the current toggle'");

        RaiseMenuOpening(ctrl);
        Check(!hideItem.IsChecked, "premise: the entry now opens unchecked");
        Click();
        Check(hide.IsChecked == true, "clicking the entry that showed unchecked hides the graphics");
    });

    /// <summary>팬 항목도 같은 규칙 — 클릭은 "열 때 본 체크의 반대" 다(두 UI 패키지 같은 규칙). 메뉴가 열린 사이 호스트 코드가 ✋ 를 켠 뒤,
    /// 꺼짐으로 보였던 항목을 누르면 팬은 켜져 있어야 한다. Click 을 직접 올리는 길(항목 체크를 뒤집지 않는다)로도 본다.</summary>
    [Fact]
    public void MenuPanClickSetsTheOppositeOfWhatTheEntryShowed() => RunSta(() =>
    {
        var ctrl = new CvDispCtrl { Frame = GrayFrame(Box, Box) };
        Render(ctrl);
        var pan = ToggleOf(ctrl, "Pan");
        var panItem = ctrl.ContextMenu.Items.OfType<MenuItem>().First(i => i.IsCheckable);

        RaiseMenuOpening(ctrl);
        Check(!panItem.IsChecked, "premise: the pan entry opens unchecked");
        pan.IsChecked = true;   // 메뉴가 열린 사이 호스트 코드가 ✋ 를 켠다
        panItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Pump();
        Check(pan.IsChecked == true,
            "a raw Click on the pan entry that showed unchecked turns pan on — the opposite of what it showed, as in the Avalonia control (the item's own check was not flipped on this path)");
    });
}
