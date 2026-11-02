// 그래픽 숨기기 토글(👁)과 그래픽 포함 내보내기(RenderWithOverlay·메뉴 "이미지 저장(그래픽 포함)") — 헤드리스 창(Skia)과 내보낸 Mat 의 픽셀로 잰다.
// 계약(사용자 결정 0.35):
//  - 숨김 = 결과 오버레이 전부(모서리 HUD 포함)를 그리지 않는다. 티칭 도형(Shapes)과 이미지는 그대로. 숨긴 동안에도 Overlay 값은 받아 둔다.
//  - 일시 상태: 새 Frame·새 Overlay 값이 들어오면 저절로 풀린다. Shapes 변경과 같은 참조 재대입은 풀지 않는다.
//  - 메뉴의 숨기기 항목은 체크 항목이고 열 때마다 툴바 토글을 따른다(양방향). 그래픽 포함 저장 항목은 저장과 같은 활성 규칙(프레임이 있어야).
//  - RenderWithOverlay = 원본 해상도 W×H BGR 3채널, 이미지 픽셀 1:1, 글자·선은 k 배(k = max(1, 1/맞춤 배율 @1280×960)), 편집 도형 없음,
//    숨김과 무관하게 그래픽 포함, 프레임이 없으면 null.
// 각 절의 대조(살아 있음)는 같은 런 안에 둔다 — "같다" 만 있으면 아무것도 안 그린 것과 구분되지 않는다.

using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using CvInspect.Avalonia.Controls;
using CvInspect.Imaging;
using CvInspect.Vision.Edit;
using CvInspect.Vision.Overlay;
using OpenCvSharp;
using Xunit;
using static CvInspect.Avalonia.Tests.AvaTest;

namespace CvInspect.Avalonia.Tests;

public class CvDispCtrlGraphicsTests
{
    private const int Box = 400;   // 화면 절의 창 크기(정사각)

    private static CamFrame GrayFrame(int w, int h)
    {
        var px = new byte[w * h];
        Array.Fill(px, (byte)128);
        return new CamFrame(px, w, h, w, CamPixelFormat.Mono8);
    }

    /// <summary>Bgr24 그라디언트 — 화소마다 값이 달라 한 화소만 밀려도 바이트 대조에서 드러난다.</summary>
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

    /// <summary>빨강 사각 + 모서리 HUD — 숨김이 HUD 까지 거두는지 함께 본다.</summary>
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

    private static int Diff(byte[] a, byte[] b)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) n++;
        return n;
    }

    private static ToggleButton HideOf(CvDispCtrl ctrl)
        => FindToggle(ctrl, "Hide Graphics") ?? throw new InvalidOperationException("the 'Hide Graphics' toggle was not found");

    // === 숨김 토글 — 화면 ===

    [AvaloniaFact]
    public void HidingDropsTheWholeOverlayKeepsShapesAndShowsAgainOnNewValues()
    {
        var shapes = OneShape();
        var overlay = RectAndHud();
        var ctrl = new CvDispCtrl { IsToolbarVisible = false, Frame = GrayFrame(Box, Box), Shapes = shapes, Overlay = overlay };
        var hide = HideOf(ctrl);

        // 기준 — 같은 프레임·도형에 오버레이만 없는 화면. 도형이 실제로 그려졌는지도 대조한다(안 그려졌으면 "도형은 남는다" 가 공짜다).
        var bareCtrl = new CvDispCtrl { IsToolbarVisible = false, Frame = GrayFrame(Box, Box), Shapes = shapes };
        var bare = Render(bareCtrl, Box, Box);
        var noShapes = Render(new CvDispCtrl { IsToolbarVisible = false, Frame = GrayFrame(Box, Box) }, Box, Box);
        Check(Diff(bare, noShapes) > 0, "premise: the teaching shape draws on the baseline");

        var shown = Render(ctrl, Box, Box);
        Check(Diff(shown, bare) > 0, $"premise: the overlay draws while not hidden ({Diff(shown, bare)} bytes differ from the baseline)");

        hide.IsChecked = true;
        var hidden = Render(ctrl, Box, Box);
        Check(Diff(hidden, bare) == 0,
            $"hidden: the whole result overlay (rect and corner HUD) is gone while the teaching shape and the image stay — the picture equals the no-overlay baseline ({Diff(hidden, bare)} bytes differ)");

        // HUD 만 있는 오버레이 — 새 값이라 풀린다. 다시 숨기면 HUD 도 사라져야 한다(HUD 를 따로 그리는 갈래가 숨김을 건너뛰면 남는다).
        ctrl.Overlay = HudOnly();
        Check(hide.IsChecked == false, "a new Overlay value un-hides the graphics — hiding is temporary");
        var hudShown = Render(ctrl, Box, Box);
        Check(Diff(hudShown, bare) > 0, "premise: the HUD-only overlay draws");
        hide.IsChecked = true;
        Check(Diff(Render(ctrl, Box, Box), bare) == 0, "hidden: a HUD-only overlay leaves nothing on screen — the HUD is part of what is hidden");

        // 숨긴 동안 들어온 값은 받아 둔다 — 풀린 뒤 보이는 것은 옛 오버레이가 아니라 새 값이다.
        var moved = RectAndHud(260, 120);
        ctrl.Overlay = moved;
        var afterNew = Render(ctrl, Box, Box);
        var reference = Render(new CvDispCtrl { IsToolbarVisible = false, Frame = GrayFrame(Box, Box), Shapes = shapes, Overlay = moved }, Box, Box);
        Check(hide.IsChecked == false && Diff(afterNew, reference) == 0,
            $"the overlay assigned while hidden un-hides and is the one drawn (checked={hide.IsChecked}, {Diff(afterNew, reference)} bytes differ from a fresh control with that overlay)");

        // 같은 참조 재대입은 속성 변경이 아니다 — 풀리지 않는다(문서화된 동작).
        hide.IsChecked = true;
        ctrl.Overlay = moved;
        Check(hide.IsChecked == true && Diff(Render(ctrl, Box, Box), bare) == 0,
            "assigning the same overlay reference again is not a property change, so it does not un-hide (documented)");

        // Shapes 변경은 풀지 않는다 — 티칭 도형은 숨김 대상이 아니다.
        ctrl.Shapes = OneShape();
        Check(hide.IsChecked == true && Diff(Render(ctrl, Box, Box), bare) == 0,
            $"a Shapes change does not un-hide (checked={hide.IsChecked})");

        // 새 프레임 — 풀린다(라이브 중이면 다음 프레임에 풀린다).
        ctrl.Frame = GrayFrame(Box, Box);
        Check(hide.IsChecked == false, "a new Frame value un-hides the graphics");
        Check(Diff(Render(ctrl, Box, Box), bare) > 0, "...and the overlay is drawn again");
    }

    /// <summary>메뉴의 숨기기 항목 — 열 때마다 툴바 토글을 따르고(저절로 풀린 뒤에도), 누르면 토글을 뒤집는다. 저장(그래픽 포함)은 저장과 같은 활성 규칙.</summary>
    [AvaloniaFact]
    public void MenuHideItemFollowsTheToggleBothWays()
    {
        var ctrl = new CvDispCtrl { Overlay = RectAndHud() };
        Shoot(ctrl, Box, Box);
        var hide = HideOf(ctrl);
        var menu = ctrl.ContextMenu!;
        var items = menu.Items.OfType<MenuItem>().ToList();
        var hideItem = items[^1];
        var save = items[1];
        var saveGfx = items[2];
        Check(hideItem.ToggleType == MenuItemToggleType.CheckBox, "the hide entry is a checkable item, the last one in the menu");

        // 사람이 여는 길(문맥 요청)로 연다 — Open() 직접 호출은 Opening 을 내지 않아(실측: 0회) 열 때 맞추는 처리기를 건너뛴다.
        bool? Opened(MenuItem item)
        {
            ctrl.Surface.RaiseEvent(new global::Avalonia.Input.ContextRequestedEventArgs());
            Pump();
            var c = item.IsChecked;
            menu.Close();
            Pump();
            return c;
        }

        int openings = 0;
        menu.Opening += (_, _) => openings++;
        Check(Opened(hideItem) == false, "the hide entry opens unchecked while graphics show");
        Check(openings == 1, $"premise: the context request opened the menu through Opening, where the entry is synced ({openings} openings)");
        Check(!saveGfx.IsEnabled && !save.IsEnabled, $"without a frame save-with-graphics is grey like save (save {save.IsEnabled}, with graphics {saveGfx.IsEnabled})");

        hide.IsChecked = true;
        Check(Opened(hideItem) == true, "after the toolbar toggle hides, the entry opens checked");

        ctrl.Frame = GrayFrame(Box, Box);   // 새 프레임 — 저절로 풀린다
        Check(hide.IsChecked == false && Opened(hideItem) == false, "after the auto re-show the entry opens unchecked again — it is re-read on every open, not left stale");
        Check(saveGfx.IsEnabled && save.IsEnabled, "with a frame save-with-graphics is enabled like save");
        Check(saveGfx.IsVisible == save.IsVisible, $"save-with-graphics follows save's file-picker visibility (save {save.IsVisible}, with graphics {saveGfx.IsVisible})");

        hideItem.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Pump();
        Check(hide.IsChecked == true && ctrl.Surface.IsOverlayHidden, "clicking the hide entry hides through the toolbar toggle");
        Check(Opened(hideItem) == true, "...and the entry then opens checked");
        hideItem.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Pump();
        Check(hide.IsChecked == false && !ctrl.Surface.IsOverlayHidden, "clicking it again shows the graphics");

        // 툴바를 감추면 숨기기 항목도 그 구분선과 함께 감춘다(툴바 짝 묶음).
        ctrl.IsToolbarVisible = false;
        Check(!hideItem.IsVisible && menu.Items[^2] is Separator { IsVisible: false }, "hiding the toolbar hides the hide entry together with its separator");
    }

    // === 그래픽 포함 내보내기 ===

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

    [AvaloniaFact]
    public void RenderWithOverlayIsTheFrameAtNativeSizeWithTheOverlay()
    {
        Check(new CvDispCtrl().RenderWithOverlay() is null, "no frame -> null, not an empty Mat");

        const int w = 640, h = 480;   // k = 1 — 기준 화면보다 작다
        var frame = Gradient(w, h);
        var overlay = new ViOverlay();
        overlay.Add(new ViOverlayRect { CenterX = 320, CenterY = 240, Width = 200, Height = 100, Color = ViOverlayColor.Red });
        var ctrl = new CvDispCtrl { IsToolbarVisible = false, Frame = frame, Overlay = overlay, Shapes = OneShape() };
        Shoot(ctrl, Box, Box);   // 화면 배율(맞춤)과 무관해야 한다 — 400 칸에 맞춘 화면이지만 내보내기는 원본 크기다

        using var mat = ctrl.RenderWithOverlay()!;
        Check(mat is not null && mat.Rows == h && mat.Cols == w && mat.Type() == MatType.CV_8UC3,
            $"the export is the frame's own size as an 8-bit BGR 3-channel Mat ({mat?.Cols}x{mat?.Rows} {mat?.Type()})");
        var px = Bytes(mat!);

        // 사각 윗변은 y=190, 아랫변 y=290, 왼변 x=220, 오른변 x=420 — 굵기 2 픽셀(k=1)이 그 선을 가운데로 덮는다.
        bool RedNear(int x, int y)
        {
            for (int d = -2; d <= 2; d++)
                if (IsRed(px, w, x, y + d) || IsRed(px, w, x + d, y)) return true;
            return false;
        }
        Check(RedNear(320, 190) && RedNear(320, 290) && RedNear(220, 240) && RedNear(420, 240),
            "the overlay rect is drawn at its image coordinates in the export (all four edges red)");

        // 이미지는 원본 1:1 — 사각 둘레 띠 밖의 화소는 원본 바이트 그대로다. 한 화소라도 밀리거나 보간되면 그라디언트가 어긋난다.
        var (changed, inBand) = CompareOutsideBand(px, frame, 220, 190, 420, 290, 6);
        Check(changed == 0, $"outside the rect's stroke band every pixel equals the source frame byte for byte — the image is drawn 1:1 ({changed} differ)");
        Check(inBand > 0, $"control: the stroke band does differ ({inBand} pixels) — the comparison can see a change");

        // 편집 도형은 넣지 않는다 — 도형 있는 컨트롤과 없는 컨트롤의 내보내기가 같다.
        var noShapes = new CvDispCtrl { IsToolbarVisible = false, Frame = frame, Overlay = overlay };
        Shoot(noShapes, Box, Box);
        using var plain = noShapes.RenderWithOverlay()!;
        Check(Diff(Bytes(plain), px) == 0, "teaching shapes are not part of the export");

        // 숨김과 무관 — 숨긴 중에도 그래픽을 넣는다(이름 그대로 "그래픽 포함").
        HideOf(ctrl).IsChecked = true;
        using var whileHidden = ctrl.RenderWithOverlay()!;
        Check(Diff(Bytes(whileHidden), px) == 0, "the export includes the graphics even while they are hidden on screen");

        // 오버레이 없음 — 이미지만(원본 그대로).
        ctrl.Overlay = null;
        using var imageOnly = ctrl.RenderWithOverlay()!;
        Check(Diff(Bytes(imageOnly), frame.Pixels) == 0, "without an overlay the export is the source image unchanged");

        // 1채널 프레임도 3채널로 — 그래픽 색을 담아야 한다. 회색은 세 채널 모두 원본 값.
        var gray = GrayFrame(300, 200);
        var small = new ViOverlay();
        small.Add(new ViOverlayRect { CenterX = 150, CenterY = 100, Width = 100, Height = 60, Color = ViOverlayColor.Red });
        var g = new CvDispCtrl { IsToolbarVisible = false, Frame = gray, Overlay = small };
        using var gm = g.RenderWithOverlay()!;
        var gp = Bytes(gm);
        Check(gm.Channels() == 3 && gm.Cols == 300 && gm.Rows == 200 && gp[0] == 128 && gp[1] == 128 && gp[2] == 128,
            $"a mono frame exports as BGR with its gray value in all three channels ({gm.Channels()}ch {gm.Cols}x{gm.Rows}, {gp[0]},{gp[1]},{gp[2]})");
        Check(IsRed(gp, 300, 150, 69) || IsRed(gp, 300, 150, 70) || IsRed(gp, 300, 150, 71), "...and the overlay keeps its colour on it (top edge at y=70 is red)");

        // 다른 스레드에서 부르면 던진다 — 렌더러를 쓰는 UI 스레드 전용 계약(WPF 판과 같다).
        Exception? off = null;
        var t = new Thread(() => off = Record.Exception(() => ctrl.RenderWithOverlay()));
        t.Start();
        t.Join();
        Check(off is InvalidOperationException, $"calling from another thread throws InvalidOperationException ({off?.GetType().Name ?? "nothing"})");
    }

    /// <summary>사각(l,t)-(r,b) 둘레 ±margin 띠 밖에서 내보낸 BGR 이 원본 프레임(Bgr24·Bgra32)의 B·G·R 과 다른 화소 수, 띠 안에서 다른 화소 수.</summary>
    private static (int Changed, int InBand) CompareOutsideBand(byte[] bgr, CamFrame src, int l, int t, int r, int b, int margin)
    {
        int w = src.Width, h = src.Height, ch = src.Channels;
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

    /// <summary>k &gt; 1 에서도 이미지는 1:1 — 5MP 그라디언트를 DPI 96·k(≈2.2) 렌더 대상에 뷰 1/k 로 그려도 띠 밖 화소가 원본 바이트 그대로다.
    /// k = 1 절(640×480)만 보면 배율이 정확히 1 이라 표본 어긋남이 드러나지 않는다.</summary>
    [AvaloniaFact]
    public void RenderWithOverlayCopiesTheImage1To1WhenKIsAboveOne()
    {
        const int w = 2448, h = 2048;
        var frame = Gradient(w, h);
        var overlay = new ViOverlay();
        overlay.Add(new ViOverlayRect { CenterX = 1224, CenterY = 1024, Width = 800, Height = 400, Color = ViOverlayColor.Red });
        var ctrl = new CvDispCtrl { IsToolbarVisible = false, Frame = frame, Overlay = overlay };
        using var mat = ctrl.RenderWithOverlay()!;
        Check(mat.Cols == w && mat.Rows == h, $"the export is native size ({mat.Cols}x{mat.Rows})");
        var (changed, inBand) = CompareOutsideBand(Bytes(mat), frame, 824, 824, 1624, 1224, 10);
        Check(changed == 0, $"at k≈2.2 every pixel outside the rect's stroke band still equals the source byte for byte — the image is drawn 1:1 ({changed} differ)");
        Check(inBand > 0, $"control: the stroke band does differ ({inBand} pixels)");
    }

    /// <summary>4채널 프레임은 불투명으로 내보낸다 — 알파 0·128 화소도 원본 B·G·R 그대로(알파를 그대로 그리면 미리 곱한 알파 대상에서 검정·반 밝기가 된다).</summary>
    [AvaloniaFact]
    public void RenderWithOverlayExportsFourChannelFramesAsOpaqueSourceColours()
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
        var ctrl = new CvDispCtrl { IsToolbarVisible = false, Frame = frame, Overlay = overlay };
        Check(translucent > w * h / 2, $"premise: most pixels have alpha 0 or 128 ({translucent})");

        using var mat = ctrl.RenderWithOverlay()!;
        Check(mat.Channels() == 3 && mat.Cols == w && mat.Rows == h, $"the export is BGR at native size ({mat.Channels()}ch {mat.Cols}x{mat.Rows})");
        var (changed, inBand) = CompareOutsideBand(Bytes(mat), frame, 60, 45, 140, 105, 6);
        Check(changed == 0,
            $"outside the stroke band every pixel equals the source B,G,R whatever its alpha (0, 128, 255) — the frame is drawn as opaque, not premultiplied ({changed} differ)");
        Check(inBand > 0, $"control: the stroke band does differ ({inBand} pixels)");
    }

    /// <summary>툴바를 감추면 숨긴 그래픽이 다시 보인다 — 👁 토글과 메뉴 항목이 둘 다 툴바 묶음이라 감춘 채로는 되돌릴 수단이 없다.</summary>
    [AvaloniaFact]
    public void HidingTheToolbarShowsHiddenGraphicsAgain()
    {
        var ctrl = new CvDispCtrl { Frame = GrayFrame(Box, Box), Overlay = RectAndHud() };   // 툴바 보임
        Shoot(ctrl, Box, Box);
        var hide = HideOf(ctrl);
        hide.IsChecked = true;
        Check(ctrl.Surface.IsOverlayHidden, "premise: the toggle hides the overlay on the surface");
        ctrl.IsToolbarVisible = false;
        Check(hide.IsChecked == false && !ctrl.Surface.IsOverlayHidden,
            $"hiding the toolbar releases the hide toggle and shows the graphics — its toggle and menu entry go away with the toolbar (checked={hide.IsChecked}, hidden={ctrl.Surface.IsOverlayHidden})");
        ctrl.IsToolbarVisible = true;
        Check(hide.IsChecked == false, "showing the toolbar again does not re-hide");
    }

    /// <summary>메뉴가 열린 사이 숨김이 저절로 풀려도, 숨기기 항목 클릭은 "열 때 본 체크의 반대" 다(두 UI 패키지 같은 규칙).
    /// 클릭은 사람의 길 그대로 메뉴 상호작용 처리기(DefaultMenuInteractionHandler.Click)로 낸다 — 그 길은 CheckBox 항목의 체크를 먼저 뒤집고
    /// Click 을 낸다(Avalonia 12.1.4 IL 확인). Click 이벤트만 직접 내는 길(뒤집지 않음)도 같은 결과여야 한다.</summary>
    [AvaloniaFact]
    public void MenuHideClickSetsTheOppositeOfWhatTheEntryShowed()
    {
        var ctrl = new CvDispCtrl { Frame = GrayFrame(Box, Box), Overlay = RectAndHud() };
        Shoot(ctrl, Box, Box);
        var hide = HideOf(ctrl);
        var menu = ctrl.ContextMenu!;
        var hideItem = menu.Items.OfType<MenuItem>().Last();

        void Open()
        {
            ctrl.Surface.RaiseEvent(new global::Avalonia.Input.ContextRequestedEventArgs());
            Pump();
        }

        // 사람의 클릭 길 — 메뉴의 상호작용 처리기. 공개 표면이 아니라 리플렉션으로 부른다(없으면 여기서 실패해 알린다).
        void HumanClick()
        {
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var handler = typeof(MenuBase).GetProperty("InteractionHandler", all)?.GetValue(menu)
                          ?? throw new InvalidOperationException("the context menu's interaction handler was not found");
            var click = handler.GetType().GetMethods(all).FirstOrDefault(m => m.Name == "Click" && m.GetParameters().Length == 1)
                        ?? throw new InvalidOperationException($"{handler.GetType().Name}.Click(item) was not found");
            click.Invoke(handler, [hideItem]);
            Pump();
        }

        void RawClick()
        {
            hideItem.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Pump();
        }

        foreach (var (how, click) in new (string, Action)[] { ("the menu interaction handler", HumanClick), ("a raw Click event", RawClick) })
        {
            hide.IsChecked = true;
            Open();
            Check(menu.IsOpen && hideItem.IsChecked == true, $"premise ({how}): the menu is open and the entry shows checked (open={menu.IsOpen})");
            ctrl.Frame = GrayFrame(Box, Box);   // 메뉴가 열린 사이 새 프레임 — 저절로 풀린다
            Check(hide.IsChecked == false && hideItem.IsChecked == true, $"premise ({how}): the toggle re-showed while the open entry still shows the old check");
            click();
            Check(hide.IsChecked == false && !ctrl.Surface.IsOverlayHidden,
                $"clicking the entry that showed checked through {how} leaves the graphics shown — the click means 'uncheck what I saw', not 'flip the current toggle' (checked={hide.IsChecked})");
            menu.Close();
            Pump();

            Open();
            Check(hideItem.IsChecked == false, $"premise ({how}): the entry now opens unchecked");
            click();
            Check(hide.IsChecked == true, $"clicking the entry that showed unchecked through {how} hides the graphics");
            menu.Close();
            Pump();
            hide.IsChecked = false;
        }
    }

    /// <summary>팬 항목도 같은 규칙 — 클릭은 "열 때 본 체크의 반대" 다. 팬은 저절로 바뀌지 않지만 호스트 코드가 ✋ 를 바꿀 수 있고, 그때 두 UI 패키지가
    /// 정반대로 움직였다(WPF 는 항목 체크를 옮기고, 이 판은 지금 ✋ 를 뒤집었다). 메뉴가 열린 사이 ✋ 를 코드로 켠 뒤, 꺼짐으로 보였던 항목을 누른다.</summary>
    [AvaloniaFact]
    public void MenuPanClickSetsTheOppositeOfWhatTheEntryShowed()
    {
        var ctrl = new CvDispCtrl { Frame = GrayFrame(Box, Box) };
        Shoot(ctrl, Box, Box);
        var pan = FindToggle(ctrl, "Pan")!;
        var menu = ctrl.ContextMenu!;
        var panItem = menu.Items.OfType<MenuItem>().First(i => i.ToggleType == MenuItemToggleType.CheckBox);

        ctrl.Surface.RaiseEvent(new global::Avalonia.Input.ContextRequestedEventArgs());
        Pump();
        Check(menu.IsOpen && panItem.IsChecked == false, $"premise: the menu is open and the pan entry shows unchecked (open={menu.IsOpen})");
        pan.IsChecked = true;   // 메뉴가 열린 사이 호스트 코드가 ✋ 를 켠다
        Check(panItem.IsChecked == false, "premise: the open entry still shows the old (unchecked) state");
        panItem.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Pump();
        Check(pan.IsChecked == true,
            $"clicking the pan entry that showed unchecked turns pan on — the opposite of what it showed, as in the WPF control, not a flip of the current ✋ (checked={pan.IsChecked})");
        menu.Close();
        Pump();
    }

    /// <summary>글자·선은 k 배 — 큰 프레임은 "기준 화면에 맞춰 봤을 때 보이는 비율" 을 원본 해상도로 옮긴다. 작은 프레임(k=1)과 같은 오버레이를
    /// 좌표만 비례해 그리고, 선 굵기·글자 높이의 비가 두 프레임 k 의 비와 맞는지 잰다.
    /// 선은 굵게(StrokeScale 4 → k=1 에서 8 px) 그리고 <b>완전히 덮인 화소</b>(선 중심 색과 같은 화소)만 센다 — 가장자리 반투명 화소까지 세면
    /// 얇은 선에서 ±1 px 이 비를 흔들어, k 대신 1.5 배로 굵어진 선도 통과했다(허용 오차 0.6 시절).</summary>
    [AvaloniaFact]
    public void RenderWithOverlayScalesStrokesAndTextByK()
    {
        (int Line, int TextH, Mat Mat) Measure(int w, int h)
        {
            double f = w / 640.0;
            var o = new ViOverlay();
            o.Add(new ViOverlaySeg { X1 = 40 * f, Y1 = 400 * f, X2 = 600 * f, Y2 = 400 * f, Color = ViOverlayColor.Red, StrokeScale = 4 });
            o.Add(new ViOverlayLabel { Text = "OK 12.3", X = 40 * f, Y = 60 * f, FontSize = 14, Align = ViOverlayAlign.TopLeft, Color = ViOverlayColor.Red, HasBackground = false });
            var ctrl = new CvDispCtrl { IsToolbarVisible = false, Frame = GrayFrame(w, h), Overlay = o };
            var m = ctrl.RenderWithOverlay()!;
            var p = Bytes(m);
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
            return (line, bottom - top + 1, m);
        }

        var small = Measure(640, 480);
        var big = Measure(2560, 1920);
        using var sm = small.Mat;
        using var bm = big.Mat;
        double kSmall = 1.0, kBig = 2560 / (1280 * 0.97);   // 640×480 은 하한 1, 2560×1920 은 1/맞춤 배율
        Check(small.Line >= 6 && small.TextH > 5, $"premise: the small frame draws a thick line ({small.Line} fully covered px, want >= 6) and the text ({small.TextH}px)");
        var lineRatio = big.Line / (double)small.Line;
        var textRatio = big.TextH / (double)small.TextH;
        Check(Math.Abs(lineRatio - kBig / kSmall) < 0.25,
            $"the stroke is k times thicker on the large frame: {small.Line}px at k=1 vs {big.Line}px at k={kBig:0.###} (ratio {lineRatio:0.##}; a 1.5x stroke would be ~1.5)");
        Check(Math.Abs(textRatio - kBig / kSmall) < 0.35,
            $"the label text is k times taller on the large frame: {small.TextH}px at k=1 vs {big.TextH}px at k={kBig:0.###} (ratio {textRatio:0.##})");
        Check(bm.Rows == 1920 && bm.Cols == 2560, $"the large export is its native size ({bm.Cols}x{bm.Rows}), not the reference view");
    }
}
