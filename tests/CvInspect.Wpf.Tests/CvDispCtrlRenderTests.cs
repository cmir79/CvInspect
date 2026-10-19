// CvDispCtrl 오프스크린 렌더 실증 — 창 없이 RenderTargetBitmap 으로 그려 픽셀을 센다.
// 각 절은 계약 하나를 못 박는다: Frame 에 ICvPixelSource 를 직접 넣은 렌더가 Mat 경로와 같은지, 대입 뒤 원본
// 배열을 고쳐도 화면이 그대로인지(픽셀은 대입 시점에 백버퍼로 옮겨진다), 행 끝 패딩 버퍼가 제대로 걸리는지,
// 계약을 어긴 값이 예외 대신 빈 화면 + 경고로 끝나는지, 크기가 바뀌면 새 프레임 없이도 그 크기에 맞추는지,
// 오버레이를 이미지 사각으로 자르는 옵션이 여백만 비우고 모서리 HUD·편집 도형은 남기는지.
// WPF 요소는 STA 스레드에서만 만들 수 있어 본문을 STA 로 감싼다.
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CvInspect.Controls;
using CvInspect.Imaging;
using CvInspect.Vision.Edit;
using CvInspect.Vision.Overlay;
using Xunit;

namespace CvInspect.Wpf.Tests;

public class CvDispCtrlRenderTests
{
    private const int W = 1280, H = 960;      // 소스 프레임
    private const int VW = 800, VH = 600;     // 뷰포트 — 소스보다 작아 Fit 축소까지 지난 결과를 본다

    /// <summary>단언 하나 — 실패 메시지가 곧 그 회귀가 존재하는 이유다.</summary>
    private static void Check(bool cond, string name) => Assert.True(cond, name);

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

    /// <summary>레이아웃 → 디스패처 큐 비우기 → 오프스크린 렌더. 반환은 Pbgra32 픽셀.</summary>
    private static byte[] Render(FrameworkElement ctrl, int w = VW, int h = VH)
    {
        ctrl.Measure(new Size(w, h));
        ctrl.Arrange(new Rect(0, 0, w, h));
        ctrl.UpdateLayout();
        Pump();
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(ctrl);
        var buf = new byte[w * h * 4];
        rtb.CopyPixels(buf, w * 4, 0);
        return buf;
    }

    /// <summary>바인딩·InvalidateVisual 이 큐에 남긴 일을 렌더 전에 소화한다.</summary>
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static int DistinctColors(byte[] px)
    {
        var set = new HashSet<int>();
        for (int i = 0; i < px.Length; i += 4) set.Add(px[i] | px[i + 1] << 8 | px[i + 2] << 16);
        return set.Count;
    }

    private static int Diff(byte[] a, byte[] b)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) n++;
        return n;
    }

    /// <summary>Bgr24 그라디언트 — 픽셀마다 값이 달라 "그려졌다" 와 "같다" 를 색 수와 바이트 차이로 판정할 수 있다.
    /// stride 를 폭×3 보다 크게 주면 행 끝 패딩이 생긴다. 패딩 바이트는 0xEE 로 채운다 — 행을 stride 가 아니라
    /// 폭×채널로 걸면 그 값이 화면에 섞여 들어와 티가 난다.</summary>
    private static CamFrame Gradient(int stride) => Gradient(CamPixelFormat.Bgr24, stride - W * 3);

    /// <summary>포맷별 그라디언트. pad 는 행 끝 패딩 바이트 수. Bgra32 는 알파를 255 로 고정한다 — 알파가 섞이면
    /// 오프스크린 합성 결과가 소스 픽셀과 달라져 "같다" 판정이 흔들린다.</summary>
    private static CamFrame Gradient(CamPixelFormat fmt, int pad)
    {
        int ch = fmt switch { CamPixelFormat.Mono8 => 1, CamPixelFormat.Bgr24 => 3, _ => 4 };
        int stride = W * ch + pad;
        var px = new byte[stride * H];
        Array.Fill(px, (byte)0xEE);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * stride + x * ch;
                px[i] = (byte)(x * 255 / W);
                if (ch >= 3)
                {
                    px[i + 1] = (byte)(y * 255 / H);
                    px[i + 2] = (byte)((x + y) & 0xFF);
                }
                if (ch == 4) px[i + 3] = 255;
            }
        return new CamFrame(px, W, H, stride, fmt);
    }

    private static CvDispCtrl Ctrl(object? frame = null) => new() { IsToolbarVisible = false, Frame = frame };

    /// <summary>가운데 행에서 이미지(회색 128) 화소가 차지하는 가로 폭 — 그려진 배율을 잰다. 컨트롤 배경(0x2A)과
    /// 상태 줄은 이 값에서 멀어 섞이지 않는다.</summary>
    private static int GrayWidth(byte[] px, int w, int h)
    {
        int row = h / 2, first = -1, last = -1;
        for (int x = 0; x < w; x++)
        {
            int i = (row * w + x) * 4;
            if (px[i + 3] > 200 && Math.Abs(px[i] - 128) < 20 && Math.Abs(px[i + 1] - 128) < 20 && Math.Abs(px[i + 2] - 128) < 20)
            {
                if (first < 0) first = x;
                last = x;
            }
        }
        return first < 0 ? 0 : last - first + 1;
    }

    [Fact]
    public void PixelSourcePathMatchesMatPath() => RunSta(() =>
    {
        var cam = Gradient(W * 3);
        var viaSource = Render(Ctrl(cam));
        Check(DistinctColors(viaSource) > 1000, $"ICvPixelSource path actually draws: distinct colors={DistinctColors(viaSource)}");

        var blank = Render(Ctrl());
        Check(Diff(viaSource, blank) > 0, "ICvPixelSource path differs from the empty control");

        using var mat = cam.AsMat();
        var viaMat = Render(Ctrl(mat));
        Check(Diff(viaSource, viaMat) == 0, $"ICvPixelSource render == Mat render (differing bytes={Diff(viaSource, viaMat)})");
    });

    [Fact]
    public void PixelsAreMovedToTheBackBufferAtAssignment() => RunSta(() =>
    {
        // 계약("발행 뒤 불변")을 어겼을 때 깨지는 것은 화면이 아니다 — 픽셀은 대입 시점에 WritePixels 로 옮겨지고
        // 렌더는 백버퍼만 그린다. 여기서 화면이 바뀌면 그 문서가 틀린 것이다.
        var cam = Gradient(W * 3);
        var ctrl = Ctrl(cam);
        var first = Render(ctrl);
        Array.Fill(cam.Pixels, (byte)0);
        var again = Render(ctrl);
        Check(Diff(first, again) == 0, $"mutating the source array after assignment leaves the picture unchanged (diff={Diff(first, again)})");

        // 참조를 새 프레임으로 바꾸면 새 내용이 보인다 — 위 결과가 "아무것도 다시 안 그린다" 때문이 아님을 가른다.
        var solid = new byte[W * 3 * H];
        Array.Fill(solid, (byte)200);
        ctrl.Frame = new CamFrame(solid, W, H, W * 3, CamPixelFormat.Bgr24);
        var replaced = Render(ctrl);
        Check(Diff(first, replaced) > 0, "replacing the frame shows the new content");
    });

    [Theory]
    [InlineData(CamPixelFormat.Mono8)]
    [InlineData(CamPixelFormat.Bgr24)]
    [InlineData(CamPixelFormat.Bgra32)]
    public void EveryFormatMatchesMatPath(CamPixelFormat fmt) => RunSta(() =>
    {
        // 세 포맷 모두 ICvPixelSource 경로(채널 수 → WPF 포맷)와 Mat 경로(MatType → WPF 포맷)가 같은 그림이어야 한다.
        var cam = Gradient(fmt, 0);
        var viaSource = Render(Ctrl(cam));
        using var mat = cam.AsMat();
        var viaMat = Render(Ctrl(mat));
        Check(DistinctColors(viaSource) > 100, $"{fmt}: ICvPixelSource path draws (distinct colors={DistinctColors(viaSource)})");
        Check(Diff(viaSource, viaMat) == 0, $"{fmt}: ICvPixelSource render == Mat render (diff={Diff(viaSource, viaMat)})");
    });

    [Fact]
    public void RowPaddingIsHonoured() => RunSta(() =>
    {
        // 공급자 버퍼의 stride 가 폭×채널보다 클 때(행 끝 패딩) 타이트한 버퍼와 같은 그림이 나와야 한다.
        var tight = Render(Ctrl(Gradient(W * 3)));
        var padded = Render(Ctrl(Gradient(W * 3 + 64)));
        Check(Diff(tight, padded) == 0, $"padded-stride source renders identically to tight source (diff={Diff(tight, padded)})");
    });

    [Fact]
    public void ContractViolationsFallBackToBlankWithWarning() => RunSta(() =>
    {
        var warnings = new List<string>();
        var prev = CvLog.Sink;
        CvLog.Sink = (level, src, msg, ex) => { if (level == CvLogLevel.Warning) warnings.Add($"{src}: {msg}"); };
        warnings.Clear();   // 붙는 순간 그동안 붙잡혀 있던 줄이 흘러든다 — 이 절이 세는 것은 그 뒤의 것이다
        try
        {
            var blank = Render(Ctrl());

            // 길이가 stride×height 에 못 미치는 버퍼 — WritePixels 가 던지기 전에 걸러야 한다.
            var shortBuf = new CamFrame(new byte[W * 3 * H / 2], W, H, W * 3, CamPixelFormat.Bgr24);
            var r1 = Render(Ctrl(shortBuf));
            Check(Diff(r1, blank) == 0, "short buffer shows the placeholder, no exception");
            Check(warnings.Count == 1 && warnings[0].Contains("violates its contract"),
                $"short buffer logs exactly one warning: [{string.Join(" | ", warnings)}]");

            // Mat 도 ICvPixelSource 도 아닌 값 — 빈 화면 + 경고. Overlay 처럼 조용히 넘기면 화면이 왜 비었는지 알 길이 없다.
            warnings.Clear();
            var r2 = Render(Ctrl("not a frame"));
            Check(Diff(r2, blank) == 0, "unsupported value shows the placeholder");
            Check(warnings.Count == 1 && warnings[0].Contains("expected Mat or ICvPixelSource"),
                $"unsupported value logs exactly one warning: [{string.Join(" | ", warnings)}]");
        }
        finally
        {
            CvLog.Sink = prev;
        }
    });

    [Theory]
    [InlineData(800, 1400)]   // 커지면 여백이 남았다
    [InlineData(1400, 800)]   // 작아지면 잘린 채 남았다
    public void ResizeRefitsWithoutANewFrame(int fromW, int toW) => RunSta(() =>
    {
        // 프레임이 드문 화면(관제·라인 정지 중)에서 창 크기가 바뀌면 다음 프레임이 올 때까지 옛 배율로 남아
        // 여백이 생기거나 잘렸다(0.26.2 현장 보고 — 모니터가 빠졌다 붙어 풀스크린 창이 커진 경우).
        // 기준은 처음부터 그 크기로 뜬 컨트롤이다 — 맞춤 식을 여기 다시 적으면 식이 바뀔 때 같이 틀린다.
        var gray = new byte[1000 * 500];
        Array.Fill(gray, (byte)128);
        CamFrame Frame() => new(gray, 1000, 500, 1000, CamPixelFormat.Mono8);   // 대입마다 새 값이어야 교체로 친다

        var ctrl = Ctrl(Frame());
        Render(ctrl, fromW, 600);
        // 한 장 더 받고 선 화면 — 첫 배치의 크기 변경이 남긴 맞춤 예약을 이 프레임이 소화한다. 이 단계를 빼면 남은
        // 예약이 리사이즈 뒤 그리기에서 쓰여 결함이 가려진다(실측: 빼면 고치기 전 코드에서도 통과했다).
        ctrl.Frame = Frame();
        var before = GrayWidth(Render(ctrl, fromW, 600), fromW, 600);
        var resized = GrayWidth(Render(ctrl, toW, 600), toW, 600);   // 새 프레임 없이 크기만 바꾼다
        var born = GrayWidth(Render(Ctrl(Frame()), toW, 600), toW, 600);

        Check(before > 0 && born > 0, $"the probe sees the picture at all ({fromW} wide={before}px, {toW} wide={born}px)");
        Check(Math.Abs(born - before) > 50, $"a control of the other width fits the picture differently — else the next check proves nothing ({fromW}={before}px, {toW}={born}px)");
        Check(resized == born,
            $"resizing {fromW}->{toW} refits at once without a new frame: drew {resized}px, a control born at that size draws {born}px (the old fit was {before}px)");
    });

    [Fact]
    public void HidingAndShowingKeepsTheZoom() => RunSta(() =>
    {
        // README 가 두 쪽을 다 약속한다 — 크기가 바뀌면 수동 줌 대신 맞추고, 숨겼다 다시 보이는 것(탭 전환)은
        // 크기 변경이 아니라 줌이 남는다. WPF 는 접힌 요소를 배치하지 않고 돌아가 크기를 건드리지 않는다
        // (UIElement.Arrange). 소비자 실측: 페이지 Visibility 를 접었다 펴는 탭 전환에서 표면 크기 통지 0건.
        // 뒤쪽만 틀리면 탭을 오갈 때마다 줌이 풀리는데, 그렇게 문서를 읽은 호스트는 탭 구조를 피하게 된다.
        var gray = new byte[1000 * 500];
        Array.Fill(gray, (byte)128);
        CamFrame Frame() => new(gray, 1000, 500, 1000, CamPixelFormat.Mono8);

        var ctrl = new CvDispCtrl { Frame = Frame() };   // 툴바를 켠다 — 사람이 하는 줌 그대로 버튼으로 준다
        var page = new System.Windows.Controls.Grid();   // 탭 페이지 자리 — 접었다 펴는 것은 이쪽이다
        page.Children.Add(ctrl);
        var fit = GrayWidth(Render(page, 800, 600), 800, 600);

        var zoomOut = FindButton(ctrl, "Zoom Out");
        Check(zoomOut is not null, "the toolbar's Zoom Out button is found");
        zoomOut!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        var zoomed = GrayWidth(Render(page, 800, 600), 800, 600);
        Check(fit > 0 && Math.Abs(zoomed * 1.25 - fit) <= 3,
            $"the zoom took effect — else the next check proves nothing (fit {fit}px, one step out {zoomed}px)");

        page.Visibility = Visibility.Collapsed;
        Render(page, 800, 600);
        page.Visibility = Visibility.Visible;
        var shown = GrayWidth(Render(page, 800, 600), 800, 600);
        Check(shown == zoomed, $"hiding and showing the page keeps the zoom: drew {shown}px, zoomed was {zoomed}px (fit is {fit}px)");

        // 대조군 — 같은 컨트롤에서 크기가 바뀌면 줌이 맞춤으로 바뀐다(앞 절과 같은 계약, 이 절의 판정이 살아 있음을 보인다).
        var resized = GrayWidth(Render(page, 1400, 600), 1400, 600);
        var bornPage = new System.Windows.Controls.Grid();
        bornPage.Children.Add(new CvDispCtrl { Frame = Frame() });
        var born = GrayWidth(Render(bornPage, 1400, 600), 1400, 600);
        Check(resized == born, $"a resize still replaces the zoom with a fit: drew {resized}px, a control born at that size draws {born}px");

        // 숨긴 사이에 크기가 바뀌면(탭이 가려진 동안 창 크기 변경) 다시 보일 때 맞춘다 — README 의 단서.
        FindButton(ctrl, "Zoom Out")!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        var zoomedWide = GrayWidth(Render(page, 1400, 600), 1400, 600);
        Check(zoomedWide != born, $"zoomed again before hiding (drew {zoomedWide}px, fit is {born}px)");
        page.Visibility = Visibility.Collapsed;
        Render(page, 1400, 600);
        Render(page, 1000, 600);   // 가려진 채로 크기가 바뀐다
        page.Visibility = Visibility.Visible;
        var reshown = GrayWidth(Render(page, 1000, 600), 1000, 600);
        var bornMid = new System.Windows.Controls.Grid();
        bornMid.Children.Add(new CvDispCtrl { Frame = Frame() });
        var fitMid = GrayWidth(Render(bornMid, 1000, 600), 1000, 600);
        Check(reshown == fitMid, $"a size change while hidden refits on show: drew {reshown}px, a control born at that size draws {fitMid}px");
    });

    [Fact]
    public void FitToViewRestoresTheFitEvenWhileHidden() => RunSta(() =>
    {
        // 숨겼다 보이는 것은 줌을 남긴다(앞 절). 페이지를 오갈 때 첫 화면으로 돌아오고 싶은 호스트는 그 계약을 깰 수 없어
        // 프레임을 비웠다 다시 넣는 우회를 썼다(소비자 보고 — 맞춤이 내부에만 있었다). FitToView 가 그 자리다.
        var gray = new byte[1000 * 500];
        Array.Fill(gray, (byte)128);
        CamFrame Frame() => new(gray, 1000, 500, 1000, CamPixelFormat.Mono8);
        void ZoomOut(CvDispCtrl c) => FindButton(c, "Zoom Out")!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

        var ctrl = new CvDispCtrl { Frame = Frame() };
        var page = new System.Windows.Controls.Grid();
        page.Children.Add(ctrl);
        var fit = GrayWidth(Render(page, 800, 600), 800, 600);

        // 보이는 채로 — 줌을 준 뒤 부르면 맞춤으로 돌아온다. 줌이 실제로 먹었는지부터 본다(아니면 아래 판정이 빈말이다).
        ZoomOut(ctrl);
        var zoomed = GrayWidth(Render(page, 800, 600), 800, 600);
        Check(fit > 0 && zoomed < fit - 50, $"the zoom took effect (fit {fit}px, zoomed {zoomed}px)");
        ctrl.FitToView();
        var back = GrayWidth(Render(page, 800, 600), 800, 600);
        Check(back == fit, $"FitToView on a visible control returns to the fit: drew {back}px, fit is {fit}px");

        // 숨긴 채로 — 줌 → 숨김 → FitToView → 보임. 같은 순서에서 FitToView 만 빼면 줌이 남는다(대조군, 앞 절의 계약).
        // 숨김은 두 가지 다 본다: Collapsed(배치에서 빠진다)와 Hidden(자리는 남고 그리지만 않는다).
        foreach (var hidden in new[] { Visibility.Collapsed, Visibility.Hidden })
        {
            ZoomOut(ctrl);
            Render(page, 800, 600);
            page.Visibility = hidden;
            Render(page, 800, 600);
            ctrl.FitToView();
            Render(page, 800, 600);
            page.Visibility = Visibility.Visible;
            var shown = GrayWidth(Render(page, 800, 600), 800, 600);
            Check(shown == fit, $"FitToView called while {hidden} shows the fit on return: drew {shown}px, fit is {fit}px");

            ZoomOut(ctrl);
            var zoomedAgain = GrayWidth(Render(page, 800, 600), 800, 600);
            page.Visibility = hidden;
            Render(page, 800, 600);
            page.Visibility = Visibility.Visible;
            var kept = GrayWidth(Render(page, 800, 600), 800, 600);
            Check(kept == zoomedAgain && kept != fit, $"control: without FitToView the same {hidden}/show keeps the zoom ({kept}px, zoomed {zoomedAgain}px, fit {fit}px)");
            ctrl.FitToView();
            Render(page, 800, 600);
        }

        // 프레임이 오기 전에 불러도 던지지 않는다. 첫 프레임이 맞춰 나오는 것은 FitToView 덕이 아니다(첫 프레임은 늘 맞춘다) —
        // 이 절은 "부르면 안 되는 때" 가 없음만 못 박는다.
        var empty = new CvDispCtrl();
        var emptyPage = new System.Windows.Controls.Grid();
        emptyPage.Children.Add(empty);
        Render(emptyPage, 800, 600);
        empty.FitToView();
        empty.Frame = Frame();
        var first = GrayWidth(Render(emptyPage, 800, 600), 800, 600);
        Check(first == fit, $"FitToView before any frame is harmless and the first frame is fitted: drew {first}px, fit is {fit}px");
    });

    private const int ClipBox = 400;   // 맞춤 여백 절의 칸 크기(정사각)

    private static CamFrame GrayFrame(int w, int h)
    {
        var px = new byte[w * h];
        Array.Fill(px, (byte)128);
        return new CamFrame(px, w, h, w, CamPixelFormat.Mono8);
    }

    private static CvDispCtrl ClipCtrl(CamFrame frame, bool clip, ViOverlay? overlay = null, IReadOnlyList<CvEditShape>? shapes = null)
        => new() { IsToolbarVisible = false, Frame = frame, Overlay = overlay, Shapes = shapes, ClipOverlayToImage = clip };

    /// <summary>이미지가 그려진 사각 — 오버레이 없이 그린 화면에서 회색(128) 화소의 외접 사각. 맨 아래 상태 줄은 글자
    /// 가장자리가 회색 계열이라 훑지 않는다(표면 밖이라 오버레이도 거기엔 못 그린다). 상태 줄은 밝은 바탕이고 표면의
    /// 맞춤 여백은 어두운 컨트롤 바탕이라, 맨 왼쪽 열이 처음 밝아지는 행을 표면의 끝으로 본다(이미지는 맞춤 둘레 때문에
    /// 그 열에 닿지 않는다). 고정 행수로 자르면 세로로 긴 이미지의 아래쪽을 여백으로 잘못 센다. 못 찾으면 X1 &lt; X0 이다.</summary>
    private static (int X0, int Y0, int X1, int Y1) GrayBox(byte[] bare)
    {
        var surfaceRows = ClipBox;
        for (int y = 0; y < ClipBox; y++)
            if (bare[y * ClipBox * 4] > 0x80) { surfaceRows = y; break; }
        int x0 = ClipBox, y0 = ClipBox, x1 = -1, y1 = -1;
        for (int y = 0; y < surfaceRows; y++)
            for (int x = 0; x < ClipBox; x++)
            {
                int i = (y * ClipBox + x) * 4;
                if (Math.Abs(bare[i] - 128) < 4 && Math.Abs(bare[i + 1] - 128) < 4 && Math.Abs(bare[i + 2] - 128) < 4)
                {
                    x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                }
            }
        return (x0, y0, x1, y1);
    }

    /// <summary>오버레이 없는 화면과 달라진 화소를 이미지 안 / 여백으로 나눠 센다. 이미지 경계 한 칸(섞인 화소)은 어느 쪽에도
    /// 세지 않는다.</summary>
    private static (int Margin, int Image) CountChanged(byte[] px, byte[] bare, (int X0, int Y0, int X1, int Y1) b)
    {
        int margin = 0, image = 0;
        for (int y = 0; y < ClipBox; y++)
            for (int x = 0; x < ClipBox; x++)
            {
                int i = (y * ClipBox + x) * 4;
                if (px[i] == bare[i] && px[i + 1] == bare[i + 1] && px[i + 2] == bare[i + 2] && px[i + 3] == bare[i + 3]) continue;
                if (x >= b.X0 && x <= b.X1 && y >= b.Y0 && y <= b.Y1) image++;
                else if (x < b.X0 - 1 || x > b.X1 + 1 || y < b.Y0 - 1 || y > b.Y1 + 1) margin++;
            }
        return (margin, image);
    }

    [Theory]
    [InlineData(400, 150)]   // 위아래 여백
    [InlineData(150, 400)]   // 좌우 여백
    public void ClipOverlayToImageKeepsTheFitMarginsClean(int fw, int fh) => RunSta(() =>
    {
        // 잘린 프레임은 칸과 비율이 달라 맞춤 여백이 크다. 오버레이를 표면 경계로만 자르면 영역에 걸친 도형과 영역 밖
        // 라벨이 그 여백에 그려져 이미지가 이어지는 것처럼 보인다(소비자 실측 0.26.3 — 조건은 속성 주석).
        // 켜면 여백은 비고 이미지 안은 그대로 그려져야 한다. 끄면(기본) 종전대로 여백에도 그린다.
        var frame = GrayFrame(fw, fh);
        var overlay = new ViOverlay();
        overlay.Add(new ViOverlaySeg { X1 = fw / 2.0, Y1 = -200, X2 = fw / 2.0, Y2 = fh + 200 });   // 위아래로 걸친 선
        overlay.Add(new ViOverlaySeg { X1 = -200, Y1 = fh / 2.0, X2 = fw + 200, Y2 = fh / 2.0 });   // 좌우로 걸친 선
        overlay.Add(new ViOverlayLabel { Text = "outside", X = fw / 2.0, Y = -40 });
        overlay.Add(new ViOverlayLabel { Text = "outside", X = -60, Y = fh / 2.0 });
        var inside = new ViOverlay();
        inside.Add(new ViOverlayRect { CenterX = fw / 2.0, CenterY = fh / 2.0, Width = fw / 4.0, Height = fh / 4.0 });

        var bare = Render(ClipCtrl(frame, false), ClipBox, ClipBox);
        var b = GrayBox(bare);
        var wide = fw > fh;
        Check(b.X1 >= b.X0 && b.Y1 >= b.Y0 && (wide ? b.X1 - b.X0 > 300 && b.Y0 > 50 : b.Y1 - b.Y0 > 300 && b.X0 > 50),
            $"the detector found the image and the fit leaves {(wide ? "margins above and below" : "margins left and right")} (image {b})");

        var off = CountChanged(Render(ClipCtrl(frame, false, overlay), ClipBox, ClipBox), bare, b);
        var on = CountChanged(Render(ClipCtrl(frame, true, overlay), ClipBox, ClipBox), bare, b);
        Check(off.Margin > 0, $"by default the overlay still runs into the fit margin — the old behaviour is kept, and this proves the counter sees margins ({off.Margin}px)");
        Check(on.Margin == 0, $"with ClipOverlayToImage the margins stay clean ({on.Margin}px drawn there)");
        Check(on.Image > 0 && on.Image == off.Image,
            $"and exactly the same pixels are drawn inside the image (inside {on.Image}px on, {off.Image}px off)");

        var inOn = CountChanged(Render(ClipCtrl(frame, true, inside), ClipBox, ClipBox), bare, b);
        var inOff = CountChanged(Render(ClipCtrl(frame, false, inside), ClipBox, ClipBox), bare, b);
        Check(inOn.Image > 0 && inOn == inOff,
            $"geometry wholly inside the image draws the same pixels with the clip on as off ({inOn} vs {inOff})");
    });

    [Fact]
    public void ClipLeavesTheCornerHudAndEditShapesWhole() => RunSta(() =>
    {
        // 띠 모양으로 잘린 영역 — 화면에 그려진 이미지가 HUD 블록보다 낮다. HUD 글자는 화면 고정 크기라, 자르면 판정 사유
        // 줄이 사라지고 헤더 한 줄만 남아 온전한 HUD 처럼 보인다(게시 전 검토 실측: 83% 소실). 그래서 HUD 는 자르지 않는다.
        var frame = GrayFrame(1000, 60);
        var hud = new ViOverlay();
        hud.AddSummary(false, "PatternFind", "line 1", "line 2", "line 3", "line 4", "line 5");
        var h = (ViOverlayLabel)hud.Items[0];
        var plain = new ViOverlay();   // 같은 글자·자리에 HUD 표식만 없는 라벨 — 이 이미지에서 클립이 일한다는 대조군
        plain.Add(new ViOverlayLabel { Text = h.Text, X = h.X, Y = h.Y, FontSize = h.FontSize, Align = h.Align, Color = h.Color });

        var bare = Render(ClipCtrl(frame, false), ClipBox, ClipBox);
        var b = GrayBox(bare);
        Check(b.X1 > b.X0 && b.Y1 >= b.Y0 && b.Y1 - b.Y0 < 60, $"the strip is drawn low and wide (image {b})");

        var hudOff = CountChanged(Render(ClipCtrl(frame, false, hud), ClipBox, ClipBox), bare, b);
        var hudOn = CountChanged(Render(ClipCtrl(frame, true, hud), ClipBox, ClipBox), bare, b);
        var plainOn = CountChanged(Render(ClipCtrl(frame, true, plain), ClipBox, ClipBox), bare, b);
        Check(hudOff.Margin > 0, $"the HUD block is taller than the drawn strip and runs into the margin — else the next checks prove nothing ({hudOff.Margin}px)");
        Check(plainOn.Margin == 0, $"the same label without the HUD mark is clipped — the clip is at work on this image ({plainOn.Margin}px in the margin)");
        Check(hudOn == hudOff, $"the corner HUD is not clipped, so its reason lines stay readable ({hudOn} on vs {hudOff} off)");

        // 편집 도형 — 이미지 가장자리에 걸친 사각. 켜도 여백 쪽 핸들이 그려져야 잡을 수 있다.
        var rect = new CvEditRect { Label = "Search" };
        rect.Set(-100, -30, 400, 80);
        IReadOnlyList<CvEditShape> shapes = [rect];
        var shOff = CountChanged(Render(ClipCtrl(frame, false, null, shapes), ClipBox, ClipBox), bare, b);
        var shOn = CountChanged(Render(ClipCtrl(frame, true, null, shapes), ClipBox, ClipBox), bare, b);
        Check(shOn.Margin > 0 && shOn == shOff, $"edit shapes are not clipped to the image — edge handles stay grabbable ({shOn} on vs {shOff} off)");
    });

    // === 라벨 글자 배율(OverlayLabelScale) ===

    private static CvDispCtrl ScaleCtrl(CamFrame frame, ViOverlay? overlay, double scale, IReadOnlyList<CvEditShape>? shapes = null)
        => new() { IsToolbarVisible = false, Frame = frame, Overlay = overlay, Shapes = shapes, OverlayLabelScale = scale };

    /// <summary>오버레이 없는 화면과 달라진 화소의 외접 사각. 없으면 X1 &lt; X0.</summary>
    private static (int X0, int Y0, int X1, int Y1) ChangedBox(byte[] px, byte[] bare)
    {
        int x0 = ClipBox, y0 = ClipBox, x1 = -1, y1 = -1;
        for (int y = 0; y < ClipBox; y++)
            for (int x = 0; x < ClipBox; x++)
            {
                int i = (y * ClipBox + x) * 4;
                if (px[i] == bare[i] && px[i + 1] == bare[i + 1] && px[i + 2] == bare[i + 2] && px[i + 3] == bare[i + 3]) continue;
                x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
            }
        return (x0, y0, x1, y1);
    }

    /// <summary>라벨 배경 상자(회색 128 위에 반투명 검정 → 세 채널 모두 어두움)의 외접 사각. 글자(초록·빨강)는 한 채널이 밝아 섞이지 않는다.</summary>
    private static (int X0, int Y0, int X1, int Y1) DarkBox(byte[] px, byte[] bare)
    {
        int x0 = ClipBox, y0 = ClipBox, x1 = -1, y1 = -1;
        for (int y = 0; y < ClipBox; y++)
            for (int x = 0; x < ClipBox; x++)
            {
                int i = (y * ClipBox + x) * 4;
                if (bare[i] < 100 || px[i] >= 45 || px[i + 1] >= 45 || px[i + 2] >= 45) continue;
                x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
            }
        return (x0, y0, x1, y1);
    }

    /// <summary>가운데 열에서 오버레이 없는 화면과 달라진 화소 수 — 가로로 지나가는 선 하나의 화면 굵기다.</summary>
    private static int ChangedInMiddleColumn(byte[] px, byte[] bare)
    {
        int n = 0, x = ClipBox / 2;
        for (int y = 0; y < ClipBox; y++)
        {
            int i = (y * ClipBox + x) * 4;
            if (px[i] != bare[i] || px[i + 1] != bare[i + 1] || px[i + 2] != bare[i + 2]) n++;
        }
        return n;
    }

    [Fact]
    public void StrokeScaleThickensOneItemAtAnyZoom() => RunSta(() =>
    {
        // 소비자 요청: NG 위치 박스 하나만 "한 단계 굵게 — 멀리서도 보이게". 굵기는 화면 고정이라 배수도 줌과 무관해야 한다.
        // 세 갈래(선분·사각·폴리라인)가 각자 펜을 만든다 — 하나만 배수를 빠뜨려도 그 종류만 조용히 얇게 남으니 셋 다 본다.
        ViOverlay One(double scale, int imgSize, string kind)
        {
            var k = imgSize / 400.0;   // 큰 프레임은 좌표도 같은 비율로 — 화면에서는 같은 자리에 그려진다
            var o = new ViOverlay();
            o.Add(kind switch
            {
                "seg" => new ViOverlaySeg { X1 = 100 * k, Y1 = 120 * k, X2 = 300 * k, Y2 = 120 * k, Color = ViOverlayColor.Red, StrokeScale = scale },
                "rect" => new ViOverlayRect { CenterX = 200 * k, CenterY = 200 * k, Width = 200 * k, Height = 160 * k, Color = ViOverlayColor.Red, StrokeScale = scale },
                _ => new ViOverlayPoly { Points = [(100 * k, 120 * k), (300 * k, 120 * k)], IsClosed = false, Color = ViOverlayColor.Red, StrokeScale = scale },
            });
            return o;
        }
        foreach (var kind in new[] { "seg", "rect", "poly" })
        {
            var frame = GrayFrame(400, 400);
            var bare = Render(ClipCtrl(frame, false), ClipBox, ClipBox);
            int Width(double scale, CamFrame f, byte[] b) => ChangedInMiddleColumn(Render(ClipCtrl(f, false, One(scale, f.Width, kind)), ClipBox, ClipBox), b);

            var w1 = Width(1.0, frame, bare);
            var w3 = Width(3.0, frame, bare);
            Check(w1 > 0, $"{kind}: premise — the probe column crosses the line at scale 1 ({w1}px)");
            // 아래·위 둘 다 — 배수를 두 번 곱하면(9 배) 아래쪽 판정만으로는 통과한다
            Check(w3 >= w1 * 2 && w3 < w1 * 4, $"{kind}: StrokeScale 3 draws about three times the line — {w1}px at 1.0, {w3}px at 3.0");

            // 이상한 값은 기본 굵기 — 던지지도, 선을 지우지도 않는다(0 굵기 펜은 아무것도 안 그린다).
            foreach (var bad in new[] { 0.0, -2.0, double.NaN, double.PositiveInfinity })
                Check(Width(bad, frame, bare) == w1, $"{kind}: StrokeScale {bad} draws like 1.0 ({Width(bad, frame, bare)}px vs {w1}px)");
            // 범위 끝으로 자른다 — 아주 작은 배수가 선을 지우지 않고, 아주 큰 유한값이 무한대 굵기로 넘치지 않는다.
            var wMin = Width(0.25, frame, bare);
            Check(wMin > 0 && Width(1e-300, frame, bare) == wMin, $"{kind}: a vanishing StrokeScale draws like the 0.25 floor ({Width(1e-300, frame, bare)}px vs {wMin}px), not nothing");
            Check(Width(double.MaxValue, frame, bare) == Width(50, frame, bare), $"{kind}: double.MaxValue draws like the 50 ceiling instead of an infinite pen");

            // 줌과 무관 — 두 배 큰 프레임은 절반 배율로 맞춰지지만 선 굵기는 화면 px 그대로다.
            var big = GrayFrame(800, 800);
            var bareBig = Render(ClipCtrl(big, false), ClipBox, ClipBox);
            var w1Big = Width(1.0, big, bareBig);
            var w3Big = Width(3.0, big, bareBig);
            Check(Math.Abs(w1Big - w1) <= 1 && Math.Abs(w3Big - w3) <= 1,
                $"{kind}: the stroke is screen-fixed — at half the zoom 1.0 draws {w1Big}px (was {w1}px) and 3.0 draws {w3Big}px (was {w3}px)");
        }

        // 화살촉도 같은 배수로 — 굵은 선에 기본 크기 촉이면 촉이 선에 묻힌다.
        var f400 = GrayFrame(400, 400);
        var bare400 = Render(ClipCtrl(f400, false), ClipBox, ClipBox);
        (int X0, int Y0, int X1, int Y1) SegBox(double x1, double x2, bool arrow, double scale)
        {
            var o = new ViOverlay();
            o.Add(new ViOverlaySeg { X1 = x1, Y1 = 120, X2 = x2, Y2 = 120, HasEndArrow = arrow, Color = ViOverlayColor.Red, StrokeScale = scale });
            return ChangedBox(Render(ClipCtrl(f400, false, o), ClipBox, ClipBox), bare400);
        }
        int ArrowHeight(double scale) { var b = SegBox(100, 300, true, scale); return b.Y1 - b.Y0 + 1; }
        var a1 = ArrowHeight(1.0);
        var a3 = ArrowHeight(3.0);
        Check(a3 >= a1 * 2 && a3 < a1 * 4, $"the end arrow grows with the stroke — {a1}px tall at 1.0, {a3}px at 3.0");
        foreach (var bad in new[] { 0.0, -2.0, double.NaN, double.PositiveInfinity })
            Check(ArrowHeight(bad) == a1, $"the end arrow of StrokeScale {bad} draws like 1.0 ({ArrowHeight(bad)}px vs {a1}px) — not lost, not unbounded");

        // 짧은 선분 — 키운 촉(길이 36)이 선분(약 10px)보다 길면 시작점 뒤 빈 곳에서 튀어나왔다. 촉은 선분 안에 머문다.
        var shortLine = SegBox(195, 205, false, 3.0);
        var shortArrow = SegBox(195, 205, true, 3.0);
        Check(shortArrow.X0 >= shortLine.X0 - 1, $"a bold arrow on a short segment stays within it (arrow starts at x={shortArrow.X0}, the line at x={shortLine.X0})");

        // 굵게 한 사각의 바깥 모서리가 채워진다 — 변마다 따로 그리면 끝이 평평해 모서리 바깥 칸이 비었다(배수 3 에서 3×3).
        var boxOverlay = new ViOverlay();
        boxOverlay.Add(new ViOverlayRect { CenterX = 200, CenterY = 200, Width = 200, Height = 160, Color = ViOverlayColor.Red, StrokeScale = 3 });
        var boxPx = Render(ClipCtrl(f400, false, boxOverlay), ClipBox, ClipBox);
        var outer = ChangedBox(boxPx, bare400);
        foreach (var (cx, cy) in new[] { (outer.X0 + 1, outer.Y0 + 1), (outer.X1 - 1, outer.Y0 + 1), (outer.X1 - 1, outer.Y1 - 1), (outer.X0 + 1, outer.Y1 - 1) })
        {
            int i = (cy * ClipBox + cx) * 4;
            Check(boxPx[i + 2] > 150 && boxPx[i + 1] < 100, $"a bold rect's outer corner near ({cx},{cy}) is painted red, not notched (bgr {boxPx[i]},{boxPx[i + 1]},{boxPx[i + 2]})");
        }

        // 복사·자르기가 배수를 잇는다 — 사본에서 빠지면 잘라 보인 화면에서만 NG 박스가 다시 가늘어진다.
        var src = new ViOverlay();
        src.Add(new ViOverlayRect { CenterX = 50, CenterY = 50, Width = 20, Height = 20, StrokeScale = 2.5 });
        Check(src.CropTo(10, 10, 80, 80).Items[0].StrokeScale == 2.5, "CropTo keeps StrokeScale");
    });

    /// <summary>편집 사각 하나를 얹은 화면 — 렌더까지 돌려 화면 배율이 정해진 뒤 표면을 돌려준다.</summary>
    private static (CvDispCtrl Ctrl, CvInspect.Controls.CvDispSurface Surface) EditCtrl(CvEditRect rect)
    {
        var ctrl = new CvDispCtrl { IsToolbarVisible = false, Frame = GrayFrame(400, 400), Shapes = new List<CvEditShape> { rect } };
        Render(ctrl, ClipBox, ClipBox);
        return (ctrl, ctrl.Surface);
    }

    /// <summary>이미지 좌표에서 누르고 이미지 좌표까지 끌어 놓는다 — 사람이 하는 드래그 그대로(누름 판정 포함).</summary>
    private static bool DragImage(CvInspect.Controls.CvDispSurface s, (double X, double Y) from, (double X, double Y) to)
    {
        if (!s.PressAt(s.ImageToScreen(from.X, from.Y))) return false;
        s.DragTo(s.ImageToScreen(to.X, to.Y));
        s.ReleaseDrag();
        return true;
    }

    private static (double X, double Y) Rot(double cx, double cy, double deg, double lx, double ly)
    {
        var r = deg * Math.PI / 180;
        return (cx + Math.Cos(r) * lx - Math.Sin(r) * ly, cy + Math.Sin(r) * lx + Math.Cos(r) * ly);
    }

    [Fact]
    public void EdgeHandlesResizeOneSideEvenWhenRotated() => RunSta(() =>
    {
        // 사용자 요청: 꼭짓점만이 아니라 면 가운데를 잡아 그 방향(좌우·상하)만 넓히기 — 사선 사각도. 면 가운데를 조금 굵게 그린 짧은 선이 핸들이다.
        const double eps = 1e-6;

        // 축 정렬 — 오른쪽 면을 끌면 폭만 늘고 왼쪽 변은 그대로. 위 면은 높이만, 아래 변 그대로.
        var r = new CvEditRect();
        r.Set(100, 100, 160, 120);
        var (_, s) = EditCtrl(r);
        Check(DragImage(s, (260, 160), (300, 175)), "pressing the middle of the right edge grabs something");
        Check(Math.Abs(r.X - 100) < eps && Math.Abs(r.W - 200) < eps && Math.Abs(r.Y - 100) < eps && Math.Abs(r.H - 120) < eps,
            $"the right edge handle widens only to the right — the left side, the height and the vertical place stay, even with a 15 px vertical wobble (x={r.X}, w={r.W}, y={r.Y}, h={r.H})");
        Check(DragImage(s, (200, 100), (210, 60)), "pressing the middle of the top edge grabs something");
        Check(Math.Abs(r.Y - 60) < eps && Math.Abs(r.H - 160) < eps && Math.Abs(r.X - 100) < eps && Math.Abs(r.W - 200) < eps,
            $"the top edge handle grows only upward — the bottom side and the width stay (x={r.X}, w={r.W}, y={r.Y}, h={r.H})");
        // 아래·왼쪽 면도 — 네 갈래 식이 각자 부호를 갖는다(위·오른쪽만 보면 나머지 둘의 부호가 틀려도 초록이다).
        Check(DragImage(s, (200, 220), (190, 250)), "pressing the middle of the bottom edge grabs something");
        Check(Math.Abs(r.Y - 60) < eps && Math.Abs(r.H - 190) < eps && Math.Abs(r.X - 100) < eps && Math.Abs(r.W - 200) < eps,
            $"the bottom edge handle grows only downward — the top side and the width stay (x={r.X}, w={r.W}, y={r.Y}, h={r.H})");
        Check(DragImage(s, (100, 155), (70, 140)), "pressing the middle of the left edge grabs something");
        Check(Math.Abs(r.X - 70) < eps && Math.Abs(r.X + r.W - 300) < eps && Math.Abs(r.Y - 60) < eps && Math.Abs(r.H - 190) < eps,
            $"the left edge handle grows only to the left — the right side and the height stay (x={r.X}, w={r.W}, y={r.Y}, h={r.H})");
        // 띠 안쪽을 눌러도 변은 민 만큼만 — 커서 자리로 튀지 않는다(판정 띠가 변 양옆 8 px).
        Check(DragImage(s, (295, 155), (296, 155)) && Math.Abs(r.X + r.W - 301) < eps,
            $"pressing 5 px inside the right side and moving 1 moves the side by 1, not to the cursor (right side at {r.X + r.W}, was 300)");

        // 회전 30° — 오른쪽(로컬 +x) 면을 로컬 x 로 +40, 로컬 y 로 +15 흔들며 끈다. 기울기 그대로, 로컬 x 로만 40 늘고 왼쪽 면 가운데는 제자리.
        var rr = new CvEditRect { IsRotatable = true };
        rr.Set(120, 160, 160, 80);   // 중심 (200, 200)
        rr.AngleDeg = 30;
        var (_, sr) = EditCtrl(rr);
        var leftMidBefore = Rot(200, 200, 30, -80, 0);
        Check(DragImage(sr, Rot(200, 200, 30, 80, 0), Rot(200, 200, 30, 120, 15)), "pressing the middle of the rotated right edge grabs something");
        var c = (X: rr.X + rr.W / 2, Y: rr.Y + rr.H / 2);
        var leftMidAfter = Rot(c.X, c.Y, rr.AngleDeg, -rr.W / 2, 0);
        Check(Math.Abs(rr.AngleDeg - 30) < eps && Math.Abs(rr.W - 200) < 1e-6 && Math.Abs(rr.H - 80) < 1e-6,
            $"a rotated rect keeps its angle and grows only along its own x by the dragged 40 (angle={rr.AngleDeg}, w={rr.W}, h={rr.H})");
        Check(Math.Abs(leftMidAfter.X - leftMidBefore.X) < 1e-6 && Math.Abs(leftMidAfter.Y - leftMidBefore.Y) < 1e-6,
            $"the opposite (left) side of the rotated rect stays where it was ({leftMidBefore} -> {leftMidAfter})");

        // 모서리가 먼저다 — 꼭짓점을 잡으면 종전대로 두 축이 함께 바뀐다(면 핸들이 모서리 자리를 가로채지 않는다).
        var rc = new CvEditRect();
        rc.Set(100, 100, 160, 120);
        var (_, sc) = EditCtrl(rc);
        Check(DragImage(sc, (260, 220), (280, 250)) && Math.Abs(rc.W - 180) < eps && Math.Abs(rc.H - 150) < eps,
            $"a corner still resizes both axes (w={rc.W}, h={rc.H})");

        // 크기 고정 사각은 면 핸들도 없다 — 면 가운데를 끌면 이동이다.
        var rf = new CvEditRect { IsResizable = false };
        rf.Set(100, 100, 160, 120);
        var (_, sf) = EditCtrl(rf);
        Check(DragImage(sf, (260, 160), (280, 160)) && Math.Abs(rf.X - 120) < eps && Math.Abs(rf.W - 160) < eps,
            $"a fixed-size rect has no edge handles — dragging an edge middle moves it (x={rf.X}, w={rf.W})");

        // 화면에서 짧은 변은 면 핸들을 내지 않는다 — 모서리 핸들과 겹쳐 무엇을 잡았는지 모르게 된다. 짧은 변 가운데는 모서리 핸들의
        // 판정 범위 안이라 모서리로 잡힌다(두 축이 바뀐다).
        var rs = new CvEditRect();
        rs.Set(100, 100, 12, 160);   // 폭 12 이미지 px ≈ 화면 11 px — 면 핸들 하한(21 px) 아래
        var (_, ss) = EditCtrl(rs);
        Check(DragImage(ss, (106, 100), (106, 80)) && Math.Abs(rs.W - 12) > eps,
            $"on an edge too short on screen the press goes to a corner, not an edge handle (w {rs.W} changed)");
        // 하한 자체 — 폭 20 이미지 px ≈ 화면 18 px 이면 가운데가 모서리 칸(±8) 밖이라, 면 핸들이 있었다면 그것이 잡혔다.
        // 하한 아래라 면 핸들이 없으니 그 자리는 몸체 → 이동이다(높이 그대로, 위치만).
        var rn = new CvEditRect();
        rn.Set(100, 100, 20, 160);
        var (_, sn) = EditCtrl(rn);
        Check(DragImage(sn, (110, 101), (110, 81)) && Math.Abs(rn.Y - 80) < eps && Math.Abs(rn.H - 160) < eps && Math.Abs(rn.W - 20) < eps,
            $"below the length floor there is no side handle — the press moves the rect (y={rn.Y}, h={rn.H}, w={rn.W})");

        // 겹친 도형 — Search 가 Train 을 감싸고 여백이 화면 몇 px 일 때, Train 몸체를 누른 것은 Train 이다(Search 의 면 띠가 가로채지 않는다).
        var train = new CvEditRect { IsResizable = false };
        train.Set(150, 150, 100, 100);
        var search = new CvEditRect();
        search.Set(145, 145, 110, 110);
        var nested = new CvDispCtrl { IsToolbarVisible = false, Frame = GrayFrame(400, 400), Shapes = new List<CvEditShape> { train, search } };
        Render(nested, ClipBox, ClipBox);
        Check(DragImage(nested.Surface, (249, 200), (259, 200)) && Math.Abs(train.X - 160) < eps && Math.Abs(search.X - 145) < eps && Math.Abs(search.W - 110) < eps,
            $"a press on the inner shape's body near the outer side handle moves the inner shape (train x={train.X}; search x={search.X}, w={search.W})");

        // 얇은 띠 — 위·아래 면 띠가 겹치면 가까운 면. 띠 안 아래 변 쪽(아래 변 1 px, 위 변 5 px — 둘 다 판정 안)을 눌러 내리면
        // 아래 변이 내려간다(먼저 찾은 위 변이 잡히면 높이가 줄었다 뒤집힌다). 아래 변 바깥을 누르면 위 변이 판정 밖이라 이 갈래를 못 본다.
        var strip = new CvEditRect();
        strip.Set(100, 200, 200, 6);
        var (_, st) = EditCtrl(strip);
        Check(DragImage(st, (200, 205), (200, 215)) && Math.Abs(strip.Y - 200) < eps && Math.Abs(strip.H - 16) < eps,
            $"on a thin strip the nearer side is taken — pulling near the bottom grows it downward (y={strip.Y}, h={strip.H})");
    });

    [Fact]
    public void EdgeHandlesThickenOnlyTheMiddleOfEachSide() => RunSta(() =>
    {
        // 그리는 모양 — 변 가운데만 실선으로 조금 굵게, 길이는 변의 1/3 이되 화면 상한(24)이 있어 큰 사각이면 가운데만 짧게.
        int Thickness(byte[] px, byte[] bare, int x, int yMid)
        {
            // (x, yMid) 를 지나는 가로줄에서 바뀐 화소 수 — 세로 변 하나의 그려진 굵기
            int n = 0;
            for (int xx = Math.Max(0, x - 10); xx <= Math.Min(ClipBox - 1, x + 10); xx++)
            {
                int i = (yMid * ClipBox + xx) * 4;
                if (px[i] != bare[i] || px[i + 1] != bare[i + 1] || px[i + 2] != bare[i + 2]) n++;
            }
            return n;
        }
        var bare = Render(new CvDispCtrl { IsToolbarVisible = false, Frame = GrayFrame(400, 400) }, ClipBox, ClipBox);
        var big = new CvEditRect { Color = ViOverlayColor.Red };
        big.Set(20, 20, 360, 360);
        var (ctrl, s) = EditCtrl(big);
        var px = Render(ctrl, ClipBox, ClipBox);
        var right = s.ImageToScreen(380, 200);
        var mid = Thickness(px, bare, (int)Math.Round(right.X), (int)Math.Round(right.Y));
        // 기준선 — 변 1/4 지점의 얇은 외곽선 굵기. 외곽선이 점선이라 한 줄만 보면 빈 칸에 떨어질 수 있어 ±4 줄(점선 한 주기)의 최댓값.
        var q = s.ImageToScreen(380, 110);
        var quarter = Enumerable.Range(-4, 9).Max(o => Thickness(px, bare, (int)Math.Round(q.X), (int)Math.Round(q.Y) + o));
        Check(quarter > 0 && mid >= quarter + 2, $"the middle of the right side is drawn thicker than the rest ({mid}px vs {quarter}px at a quarter)");
        // 굵은 구간의 길이 — 상한 24 근처여야 한다(변 1/3 이면 화면 100 px 넘게)
        int thickRun = 0;
        // 모서리 핸들(사각 7 px)도 굵게 잡히므로 모서리 근처는 뺀다
        for (int y = (int)s.ImageToScreen(380, 40).Y; y <= (int)s.ImageToScreen(380, 360).Y; y++)
            if (Thickness(px, bare, (int)Math.Round(right.X), y) >= mid - 1) thickRun++;
        Check(thickRun is >= 20 and <= 30, $"on a large rect only a short middle part is thick — {thickRun}px long, capped near 24");
    });

    [Fact]
    public void OverlayLabelScaleGrowsOnlyTheNonHudLabelText() => RunSta(() =>
    {
        // 한 창에서만 검사 라벨 글자를 키우는 속성 — 소비자는 그동안 매 프레임 라벨 사본을 손으로 만들어 넣었다(라벨에 속성이 늘면 그 사본이
        // 조용히 빠뜨린다). 바라는 동작: 글자만 × 배율, 기준점·정렬 그대로, 배경 상자가 글자를 따라감, HUD·도형은 그대로(소비자 사본
        // 실측: 1.5배에서 상자 비율 ≈1.53, HUD 1.0). 라벨은 두 갈래로 그려진다 — 한 색 블록과 줄별 색 블록. 둘 다 본다.
        var frame = GrayFrame(400, 400);
        var bare = Render(ScaleCtrl(frame, null, 1.0), ClipBox, ClipBox);
        double Ratio((int X0, int Y0, int X1, int Y1) a, (int X0, int Y0, int X1, int Y1) b, bool horizontal)
            => horizontal ? (b.X1 - b.X0 + 1.0) / (a.X1 - a.X0 + 1.0) : (b.Y1 - b.Y0 + 1.0) / (a.Y1 - a.Y0 + 1.0);

        // 배경 없는 행은 글자 잉크만 잰다 — 배경 상자는 블록 전체 크기에서 잡혀, 줄별 색 갈래가 글자를 안 키우고 상자만 키워도 배경 있는
        // 행은 1.5 로 나온다(변이로 확인: 그 갈래만 옛 크기로 두어도 배경 있는 행만으로는 초록이었다).
        foreach (var (lineColors, background) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            var overlay = new ViOverlay();
            overlay.Add(new ViOverlayLabel
            {
                Text = "OK 12.3\nA 45", X = 60, Y = 80, FontSize = 14, Align = ViOverlayAlign.TopLeft, Color = ViOverlayColor.Green,
                LineColors = lineColors ? new ViOverlayColor?[] { ViOverlayColor.Red, null } : null, HasBackground = background,
            });
            var one = ChangedBox(Render(ScaleCtrl(frame, overlay, 1.0), ClipBox, ClipBox), bare);
            var big = ChangedBox(Render(ScaleCtrl(frame, overlay, 1.5), ClipBox, ClipBox), bare);
            var path = (lineColors ? "per-line colours" : "single colour") + (background ? ", text and background" : ", text only");
            Check(one.X1 > one.X0 && one.Y1 > one.Y0, $"{path}: premise — the label is drawn at scale 1.0 ({one})");
            var rw = Ratio(one, big, true);
            var rh = Ratio(one, big, false);
            Check(rw is > 1.35 and < 1.7 && rh is > 1.35 and < 1.7,
                $"{path}: at 1.5 the drawn label grows about 1.5x — width {rw:F2}, height {rh:F2} ({one} -> {big})");
            // 위쪽은 잉크 윗선이라 글자가 커지면 행간만큼 내려온다(1.5 배에서 2~3 px) — 기준점이 같이 커지면 28 px 넘게 움직인다
            Check(Math.Abs(big.X0 - one.X0) <= 3 && Math.Abs(big.Y0 - one.Y0) <= 8,
                $"{path}: the anchor stays put — a top-left label grows to the right and down ({one} -> {big})");
            if (background)
            {
                // 상자만 따로 잰다 — 위 외접 사각은 상자와 글자의 합이라, 상자가 옛 크기로 남고 글자만 커져도 ≈1.5 가 나왔다(검토 변이)
                var boxOne = DarkBox(Render(ScaleCtrl(frame, overlay, 1.0), ClipBox, ClipBox), bare);
                var boxBig = DarkBox(Render(ScaleCtrl(frame, overlay, 1.5), ClipBox, ClipBox), bare);
                var bw = Ratio(boxOne, boxBig, true);
                var bh = Ratio(boxOne, boxBig, false);
                Check(boxOne.X1 > boxOne.X0 && bw is > 1.35 and < 1.7 && bh is > 1.35 and < 1.7,
                    $"{path}: the background box itself follows the text — width {bw:F2}, height {bh:F2} ({boxOne} -> {boxBig})");
            }
        }

        // 기준점은 정렬마다 다른 변이다 — 오른쪽 정렬은 오른쪽 끝, 가운데 정렬은 가운데, 아래 정렬은 아래쪽이 기준점에 붙어 있어야 한다.
        // 왼쪽 위만 보면 오른쪽·아래 오프셋(글자 폭·높이를 빼는 자리)이 배율을 잘못 써도 초록이다(검토 변이).
        foreach (var align in new[] { ViOverlayAlign.TopRight, ViOverlayAlign.BottomCenter, ViOverlayAlign.BottomRight })
        {
            var overlay = new ViOverlay();
            overlay.Add(new ViOverlayLabel { Text = "OK 12.3", X = 200, Y = 200, FontSize = 14, Align = align, HasBackground = false });
            var one = ChangedBox(Render(ScaleCtrl(frame, overlay, 1.0), ClipBox, ClipBox), bare);
            var big = ChangedBox(Render(ScaleCtrl(frame, overlay, 1.5), ClipBox, ClipBox), bare);
            Check(Ratio(one, big, true) is > 1.35 and < 1.7, $"{align}: premise — the label grows ({one} -> {big})");
            var anchorX = align == ViOverlayAlign.BottomCenter
                ? Math.Abs((big.X0 + big.X1) / 2.0 - (one.X0 + one.X1) / 2.0) <= 3
                : Math.Abs(big.X1 - one.X1) <= 4;
            // 아래 정렬은 잉크 아랫선과 기준점 사이 하강부가 글자와 함께 커진다(문서화) — 그만큼은 허용한다
            var anchorY = align == ViOverlayAlign.TopRight ? Math.Abs(big.Y0 - one.Y0) <= 8 : Math.Abs(big.Y1 - one.Y1) <= 8;
            Check(anchorX && anchorY, $"{align}: the edge on the anchor side stays at the anchor ({one} -> {big})");
        }

        // 그대로인 것 — 모서리 요약 HUD, 도형·선, 편집 도형의 이름표. 바이트까지 같아야 한다.
        var hud = new ViOverlay();
        hud.AddSummary(false, "PatternFind", "line 1", "line 2");
        var shapes = new ViOverlay();
        shapes.Add(new ViOverlaySeg { X1 = 20, Y1 = 300, X2 = 380, Y2 = 320, HasEndArrow = true });
        shapes.Add(new ViOverlayRect { CenterX = 200, CenterY = 200, Width = 120, Height = 60, AngleDeg = 15 });
        shapes.Add(new ViOverlayPoly { Points = [(30, 30), (90, 40), (60, 90)] });
        var rect = new CvEditRect { Label = "Search" };
        rect.Set(150, 250, 100, 60);
        IReadOnlyList<CvEditShape> edit = [rect];
        var hudDiff = Diff(Render(ScaleCtrl(frame, hud, 1.0), ClipBox, ClipBox), Render(ScaleCtrl(frame, hud, 1.5), ClipBox, ClipBox));
        var shapeDiff = Diff(Render(ScaleCtrl(frame, shapes, 1.0, edit), ClipBox, ClipBox), Render(ScaleCtrl(frame, shapes, 1.5, edit), ClipBox, ClipBox));
        Check(ChangedBox(Render(ScaleCtrl(frame, hud, 1.0), ClipBox, ClipBox), bare).X1 >= 0, "premise: the HUD is drawn");
        Check(hudDiff == 0, $"the summary HUD keeps its size — it covers an image corner and would hide more of the image ({hudDiff} bytes differ)");
        Check(shapeDiff == 0, $"shapes, lines and edit-shape name tags are not scaled ({shapeDiff} bytes differ)");
        // HUD 표식이 있어도 모서리 정렬이 아니면 붙은 모서리가 없다 — 자르기·CropTo 와 같이 다른 라벨처럼 다룬다(판정을 못 박는다)
        var centredHud = new ViOverlay();
        centredHud.Add(new ViOverlayLabel { Text = "[NG] centred", X = 200, Y = 120, FontSize = 14, Align = ViOverlayAlign.TopCenter, IsHud = true });
        var chOne = ChangedBox(Render(ScaleCtrl(frame, centredHud, 1.0), ClipBox, ClipBox), bare);
        var chBig = ChangedBox(Render(ScaleCtrl(frame, centredHud, 1.5), ClipBox, ClipBox), bare);
        Check(Ratio(chOne, chBig, true) is > 1.35 and < 1.7,
            $"a HUD-marked label that is not corner-aligned scales like any label — the same corner-HUD test as clipping and CropTo ({chOne} -> {chBig})");

        // 이미 떠 있는 화면에서 배율을 바꾸면 다시 그린다 — 멈춘 라인(새 프레임·오버레이가 안 오는 화면)에서도 바로 바뀌어야 한다
        var live = new ViOverlay();
        live.Add(new ViOverlayLabel { Text = "OK 12.3", X = 60, Y = 80, FontSize = 14, Align = ViOverlayAlign.TopLeft });
        var shown = ScaleCtrl(frame, live, 1.0);
        Render(shown, ClipBox, ClipBox);
        shown.OverlayLabelScale = 1.5;
        var redrawn = Diff(Render(shown, ClipBox, ClipBox), Render(ScaleCtrl(frame, live, 1.5), ClipBox, ClipBox));
        Check(redrawn == 0, $"changing the scale on a control already on screen redraws it at once ({redrawn} bytes differ from a fresh control at 1.5)");

        // 거절하지 않는 극단 값과 글자 크기 0 은 그리는 자리에서 잘려 예외 없이 그려진다 — FormattedText 의 예외는 OnRender 안이라 앱의
        // 처리되지 않은 예외로 올라간다(검토 실측: 20 pt 라벨이 배율 1500 에서, 아주 작은 배율은 float 0 으로 던졌다)
        foreach (var (scale, size) in new[] { (5000.0, 20f), (1e-300, 20f), (1.0, 0f) })
        {
            var extreme = new ViOverlay();
            extreme.Add(new ViOverlayLabel { Text = "x\ny", X = 60, Y = 80, FontSize = size, LineColors = new ViOverlayColor?[] { null, ViOverlayColor.Red } });
            extreme.Add(new ViOverlayLabel { Text = "x", X = 60, Y = 200, FontSize = size });
            Exception? err = null;
            try { Render(ScaleCtrl(frame, extreme, scale), ClipBox, ClipBox); } catch (Exception ex) { err = ex; }
            Check(err is null, $"scale {scale}, font size {size}: renders without throwing (got {err?.GetType().Name}: {err?.Message})");
        }

        // 기본값은 종전 동작이고, 쓸 수 없는 값은 거절한다
        Check(new CvDispCtrl().OverlayLabelScale == 1.0, "the default is 1.0 — unchanged rendering unless a host opts in");
        foreach (var bad in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
        {
            var threw = false;
            try { new CvDispCtrl().OverlayLabelScale = bad; } catch (ArgumentException) { threw = true; }
            Check(threw, $"OverlayLabelScale rejects {bad} with ArgumentException");
        }
    });

    // === 테마 색 — 툴바·상태 줄 바탕, 상태 줄 글자, 구분선, ⏯·✋ 강조 ===
    // 앞의 넷은 코드가 고정 브러시를 직접 넣어(값 출처 Local) 호스트가 리소스로 덮을 길이 없었다 — 다크 테마에서도 흰 띠로
    // 남았다(소비자 실측, 0.29.4). 호스트는 키(색 사전 한 곳에서 모든 인스턴스)나 속성(인스턴스마다)으로 넣고, 실행 중 사전
    // 교체를 따라온다. 교체는 부모 요소의 Resources 로 잰다 — 이 프로세스의 Application 은 하나뿐이고 전역이라 교체 실험에
    // 쓰지 않는다. 앱 사전에서만 닿는 길(생성자 안의 스타일·키)은 맨 아래 절 하나가 잰다. 앱 사전을 실행 중에 갈아 끼우는 것은
    // 이 스위트가 재지 않는다 — 리소스 탐색은 부모 요소와 같은 길로 앱 사전에 닿는다.
    // 레이아웃 반올림을 켜고, 시스템 배율대로 그린다 — 1px 선이 장치 화소에 걸쳐 두 줄로 번지면 정확한 색 대조가 안 된다.

    /// <summary>호스트가 고르는 네 색 — 툴바 바탕, 상태 줄 바탕, 상태 줄 글자, 구분선.</summary>
    private readonly record struct Look(Color Bar, Color Status, Color Text, Color Sep);

    private static readonly Look Default = new(Color.FromRgb(0xFA, 0xFA, 0xFA), Color.FromRgb(0xFA, 0xFA, 0xFA), Color.FromRgb(0x55, 0x55, 0x55), Color.FromRgb(0xDD, 0xDD, 0xDD));

    // 호스트 색 두 벌 — 서로·기본색·영상 영역(0x2A)·버튼 바탕(0xDD)과 겹치지 않게 고른다.
    private static readonly Look ThemeA = new(Color.FromRgb(0x10, 0x40, 0x80), Color.FromRgb(0x00, 0x00, 0x40), Color.FromRgb(0xFF, 0x00, 0x00), Color.FromRgb(0xF1, 0x07, 0xE3));
    private static readonly Look ThemeB = new(Color.FromRgb(0x20, 0x80, 0x20), Color.FromRgb(0x40, 0x00, 0x00), Color.FromRgb(0x00, 0xFF, 0x00), Color.FromRgb(0x07, 0xE3, 0xF1));

    /// <summary>호스트 색 사전. 키는 호스트가 XAML 에 그대로 적는 글자라 상수를 거치지 않고 글자로 둔다 — 이름이 바뀌면 여기서 깨져야 한다.</summary>
    private static ResourceDictionary HostChrome(Look t) => new()
    {
        ["CvDispToolbarBackgroundBrush"] = new SolidColorBrush(t.Bar),
        ["CvDispStatusBarBackgroundBrush"] = new SolidColorBrush(t.Status),
        ["CvDispStatusBarForegroundBrush"] = new SolidColorBrush(t.Text),
        ["CvDispSeparatorBrush"] = new SolidColorBrush(t.Sep),
    };

    /// <summary>상태 줄에 글자가 있게 프레임을 넣은 컨트롤을 부모 Grid 에 얹는다 — 부모의 Resources 가 호스트 사전 자리다.</summary>
    private static (System.Windows.Controls.Grid Host, CvDispCtrl Ctrl) Hosted()
    {
        var ctrl = new CvDispCtrl { Frame = Gradient(W * 3) };
        var host = new System.Windows.Controls.Grid { UseLayoutRounding = true };
        host.Children.Add(ctrl);
        return (host, ctrl);
    }

    /// <summary>장치 화소로 그린 한 장. S 는 배율(DIP 하나가 몇 화소인가).</summary>
    private readonly record struct Shot(byte[] Px, int W, int H, double S)
    {
        public Color At(int x, int y)
        {
            int i = (y * W + x) * 4;
            return Color.FromRgb(Px[i + 2], Px[i + 1], Px[i]);
        }
    }

    /// <summary>시스템 배율대로 그린다. 레이아웃 반올림은 시스템 DPI 의 장치 화소에 선을 맞추므로, 96 DPI 로 그리면 125%·150% PC 에서
    /// 1px 선이 두 화소에 걸쳐 섞여 코드가 멀쩡해도 시험이 빨갛다(배율을 흉내 내 실측). 배율 1 이면 Render 와 같다.</summary>
    private static Shot Shoot(FrameworkElement el)
    {
        el.Measure(new Size(VW, VH));
        el.Arrange(new Rect(0, 0, VW, VH));
        el.UpdateLayout();
        Pump();
        var dpi = VisualTreeHelper.GetDpi(el);
        int pw = (int)Math.Round(VW * dpi.DpiScaleX), ph = (int)Math.Round(VH * dpi.DpiScaleY);
        var rtb = new RenderTargetBitmap(pw, ph, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(el);
        var buf = new byte[pw * ph * 4];
        rtb.CopyPixels(buf, pw * 4, 0);
        return new Shot(buf, pw, ph, dpi.DpiScaleX);
    }

    /// <summary>상태 줄의 두 글자 블록(좌: 커서 좌표·픽셀 값, 우: 크기·배율). 시험 화면에는 커서가 없어 왼쪽은 비어 있으므로 픽셀로는
    /// 오른쪽만 보인다 — 그래서 두 블록의 글자색은 속성으로 직접 본다. 고정폭 글꼴로 다른 글자와 가린다.</summary>
    private static List<System.Windows.Controls.TextBlock> StatusTexts(DependencyObject root)
    {
        var found = new List<System.Windows.Controls.TextBlock>();
        void Walk(DependencyObject d)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                if (c is System.Windows.Controls.TextBlock t && t.FontFamily.Source == "Consolas") found.Add(t);
                Walk(c);
            }
        }
        Walk(root);
        return found;
    }

    private static int Dist2(Color a, Color b) => (a.R - b.R) * (a.R - b.R) + (a.G - b.G) * (a.G - b.G) + (a.B - b.B) * (a.B - b.B);

    /// <summary>렌더 결과와 상태 줄 글자 속성을 읽어 한 줄로 적는다(실패 메시지에 그대로 싣는다). 다 맞으면 ok.
    /// 바탕은 버튼·글자가 없는 패딩 자리에서 읽는다 — 툴바는 위 가장자리 안쪽(가운데, 2 DIP), 상태 줄은 맨 아래 줄.
    /// 구분선은 가운데 세로줄에서 툴바 아래 가장자리와 상태 줄 위 가장자리를 각각 찾고, 툴바 높이 가운데에서 선 폭(1 DIP)·위아래로
    /// 17 DIP 이상 이어지는 세로 구분선 둘을 센다(폭과 길이로 거르는 이유: 기본 테마의 버튼 바탕도 0xDD 라 넓은 덩어리로 나오고,
    /// 이모지 획 사이 틈은 좁지만 짧다 — 폭만 보면 다섯이 잡혔다).
    /// 글자는 상태 줄 띠에서 바탕보다 글자색에 가까운 픽셀 수(그려졌다는 증거)와, 두 글자 블록의 Foreground 가 정확히 그 색인지(어느 색인가).
    /// 띠는 찾은 위 가장자리 아래 — 못 찾으면 맨 아래 18 DIP 로 세고 그렇다고 적는다(구분선만 틀린 것을 글자도 없다고 적지 않게).</summary>
    private static string Chrome(Shot s, DependencyObject root, Look want, out bool ok)
    {
        int cx = s.W / 2;
        var tb = s.At(cx, (int)(2 * s.S));
        var st = s.At(cx, s.H - 1);
        int topEdge = -1, bottomEdge = -1;
        for (int y = 0; y < s.H / 2 && topEdge < 0; y++) if (s.At(cx, y) == want.Sep) topEdge = y;
        for (int y = s.H - 1; y >= s.H / 2 && bottomEdge < 0; y--) if (s.At(cx, y) == want.Sep) bottomEdge = y;
        var groupSepX = new List<int>();
        if (topEdge > 0)
        {
            // 1 DIP 선은 배율에 따라 1~2화소 폭으로 반올림된다(150%·200% 에서 2화소) — 같은 색이 이어진 폭이 그 이하인 줄만 센다.
            int mid = topEdge / 2, half = (int)(8 * s.S), maxW = (int)Math.Ceiling(s.S);
            for (int x = 0; x < s.W; x++)
            {
                if (s.At(x, mid) != want.Sep) continue;
                int x1 = x;
                while (x1 + 1 < s.W && s.At(x1 + 1, mid) == want.Sep) x1++;
                bool tall = x1 - x + 1 <= maxW;
                for (int cx2 = x; cx2 <= x1 && tall; cx2++)
                    for (int y = mid - half; y <= mid + half && tall; y++) tall = s.At(cx2, y) == want.Sep;
                if (tall) groupSepX.Add(x);
                x = x1;
            }
        }
        int bandTop = bottomEdge > 0 ? bottomEdge + 1 : s.H - (int)(18 * s.S);
        int text = 0;
        for (int y = bandTop; y < s.H; y++)
            for (int x = 0; x < s.W; x++)
            {
                var c = s.At(x, y);
                if (Dist2(c, want.Text) < Dist2(c, want.Status)) text++;
            }
        var fgs = StatusTexts(root).Select(t => t.Foreground is SolidColorBrush b ? b.Color.ToString() : t.Foreground?.ToString() ?? "null").ToList();
        bool fgOk = fgs.Count == 2 && fgs.All(f => f == want.Text.ToString());
        ok = tb == want.Bar && st == want.Status && topEdge > 0 && bottomEdge > 0 && groupSepX.Count == 2 && text > 0 && fgOk;
        return $"scale={s.S}, toolbar={tb} (want {want.Bar}), status={st} (want {want.Status}), separator rows top={topEdge} bottom={bottomEdge}, " +
               $"group separators={groupSepX.Count} at x=[{string.Join(",", groupSepX)}] (want 2), " +
               $"text pixels={text}{(bottomEdge > 0 ? "" : " (status edge missing — counted in the bottom band)")}, " +
               $"status text brushes=[{string.Join(",", fgs)}] (want 2 × {want.Text})";
    }

    /// <summary>호스트가 아무 색도 주지 않으면 종전 색 그대로다(0.29.4 의 고정색). 고치기 전에도 통과하는 대조군이자 표본 자리가 맞다는
    /// 증거다 — 표본 좌표가 바탕이 아닌 곳을 찍고 있으면 여기서 깨진다(그러면 아래 절들이 거짓으로 통과할 수 있다).</summary>
    [Fact]
    public void ChromeKeepsItsColorsWithoutAHostTheme() => RunSta(() =>
    {
        var (host, _) = Hosted();
        var r = Chrome(Shoot(host), host, Default, out var ok);
        Check(ok, $"without host keys the toolbar and status bar keep their fixed colors: {r}");
    });

    /// <summary>호스트가 키를 두면 툴바·상태 줄·글자·구분선이 그 색이다 — 종전에는 코드가 넣은 Local 값이라 어떤 리소스로도 못 덮었다.</summary>
    [Fact]
    public void ChromeFollowsHostKeys() => RunSta(() =>
    {
        var (host, _) = Hosted();
        host.Resources = HostChrome(ThemeA);
        var r = Chrome(Shoot(host), host, ThemeA, out var ok);
        Check(ok, $"host keys paint the toolbar, the status bar and both of its text blocks, and the separators: {r}");
    });

    /// <summary>실행 중 사전을 갈아 끼우면 다시 띄우지 않아도 따라 바뀌고, 키를 걷으면 종전 색으로 돌아간다 — 한 번 읽어 얼린 브러시면
    /// 첫 색에 굳는다(소비자는 앱 색 사전 교체로 라이트/다크를 바꾼다).</summary>
    [Fact]
    public void ChromeFollowsARuntimeThemeSwap() => RunSta(() =>
    {
        var (host, _) = Hosted();
        host.Resources.MergedDictionaries.Add(HostChrome(ThemeA));
        var ra = Chrome(Shoot(host), host, ThemeA, out var okA);
        Check(okA, $"first theme applies: {ra}");
        host.Resources.MergedDictionaries[0] = HostChrome(ThemeB);
        var rb = Chrome(Shoot(host), host, ThemeB, out var okB);
        Check(okB, $"swapping the host dictionary at run time repaints without a restart: {rb}");
        host.Resources.MergedDictionaries.Clear();
        var rd = Chrome(Shoot(host), host, Default, out var okD);
        Check(okD, $"removing the host keys brings the fixed colors back: {rd}");
    });

    /// <summary>키 이름 상수는 시험이 글자로 적은 키와 같아야 한다 — x:Static 으로 적은 호스트와 글자로 적은 호스트가 같은 곳을 가리킨다.</summary>
    [Fact]
    public void KeyConstantsSpellTheDocumentedStrings()
    {
        Check(CvDispCtrl.ToolbarBackgroundKey == "CvDispToolbarBackgroundBrush" && CvDispCtrl.StatusBarBackgroundKey == "CvDispStatusBarBackgroundBrush"
              && CvDispCtrl.StatusBarForegroundKey == "CvDispStatusBarForegroundBrush" && CvDispCtrl.SeparatorBrushKey == "CvDispSeparatorBrush",
            "key constants spell the documented strings");
    }

    /// <summary>인스턴스 속성은 키를 이기고, 걷으면 다음 출처로 돌아간다 — 스타일 세터가 있으면 그것, 없으면 키. 키 참조를 공개 속성 자체에
    /// 걸면 호스트가 값을 줬다 걷는 순간 참조까지 지워져 기본색에 머물고, 스타일 세터는 그 참조(Local)에 조용히 진다. 넷 다 같은 틀로 본다.</summary>
    [Theory]
    [InlineData(nameof(CvDispCtrl.ToolbarBackground))]
    [InlineData(nameof(CvDispCtrl.StatusBarBackground))]
    [InlineData(nameof(CvDispCtrl.StatusBarForeground))]
    [InlineData(nameof(CvDispCtrl.SeparatorBrush))]
    public void InstancePropertiesBeatTheKeysAndFallBackToThem(string prop) => RunSta(() =>
    {
        var (dp, own, styled) = prop switch
        {
            nameof(CvDispCtrl.ToolbarBackground) => (CvDispCtrl.ToolbarBackgroundProperty, Color.FromRgb(0x12, 0x34, 0x56), Color.FromRgb(0x33, 0x66, 0x99)),
            nameof(CvDispCtrl.StatusBarBackground) => (CvDispCtrl.StatusBarBackgroundProperty, Color.FromRgb(0x12, 0x34, 0x56), Color.FromRgb(0x33, 0x66, 0x99)),
            nameof(CvDispCtrl.StatusBarForeground) => (CvDispCtrl.StatusBarForegroundProperty, Color.FromRgb(0xFF, 0xFF, 0x00), Color.FromRgb(0x00, 0xFF, 0xFF)),
            _ => (CvDispCtrl.SeparatorBrushProperty, Color.FromRgb(0x0B, 0xF5, 0x0B), Color.FromRgb(0xF5, 0xA0, 0x0B)),
        };
        Look With(Color c) => prop switch
        {
            nameof(CvDispCtrl.ToolbarBackground) => ThemeA with { Bar = c },
            nameof(CvDispCtrl.StatusBarBackground) => ThemeA with { Status = c },
            nameof(CvDispCtrl.StatusBarForeground) => ThemeA with { Text = c },
            _ => ThemeA with { Sep = c },
        };

        var (host, ctrl) = Hosted();
        host.Resources = HostChrome(ThemeA);
        ctrl.SetValue(dp, new SolidColorBrush(own));
        var r1 = Chrome(Shoot(host), host, With(own), out var ok1);
        Check(ok1, $"{prop}: a value set on the instance beats the host key, the other three still follow the keys: {r1}");

        ctrl.ClearValue(dp);
        var r2 = Chrome(Shoot(host), host, ThemeA, out var ok2);
        Check(ok2, $"{prop}: clearing the instance value goes back to the host key, not to the fixed color: {r2}");

        var style = new Style(typeof(CvDispCtrl));
        style.Setters.Add(new Setter(dp, new SolidColorBrush(styled)));
        host.Resources.Add(typeof(CvDispCtrl), style);
        var r3 = Chrome(Shoot(host), host, With(styled), out var ok3);
        Check(ok3, $"{prop}: an implicit style setter from the host beats the key: {r3}");

        ctrl.SetValue(dp, new SolidColorBrush(own));
        ctrl.ClearValue(dp);
        var r4 = Chrome(Shoot(host), host, With(styled), out var ok4);
        Check(ok4, $"{prop}: with a style setter present, clearing the instance value goes back to the style, not the key: {r4}");

        host.Resources.Remove(typeof(CvDispCtrl));
        var r5 = Chrome(Shoot(host), host, ThemeA, out var ok5);
        Check(ok5, $"{prop}: removing the style goes back to the key: {r5}");
    });

    private static Color ColorOf(Brush? b) => b is SolidColorBrush s ? s.Color : Colors.Transparent;

    /// <summary>우리 키에 Brush 가 아닌 값(흔한 실수: Color)을 두면 기본색으로 남되 경고로 알린다 — 말없이 두면 "키가 안 먹는다" 로만 보인다.
    /// 네 키 각각이 이름으로 불려야 한다. 강조색 키(PrimaryBrush 등)는 호스트 라이브러리의 이름이라 다른 형이어도 정상 구성일 수 있어 조용하고,
    /// 그 자리는 고정색이다. 어느 쪽도 컨트롤을 깨뜨리지 않는다.</summary>
    [Fact]
    public void ANonBrushUnderAKeyIsIgnoredWithAWarning() => RunSta(() =>
    {
        var warnings = new List<string>();
        var before = CvLog.Sink;
        CvLog.Sink = (level, src, msg, ex) => { if (level == CvLogLevel.Warning) lock (warnings) warnings.Add($"{src}: {msg}"); };
        lock (warnings) warnings.Clear();   // 붙는 순간 그동안 붙잡혀 있던 줄이 흘러든다 — 세는 것은 그 뒤의 것이다
        try
        {
            var (host, ctrl) = Hosted();
            host.Resources = new ResourceDictionary
            {
                ["CvDispToolbarBackgroundBrush"] = Color.FromRgb(1, 2, 3),
                ["CvDispStatusBarBackgroundBrush"] = Color.FromRgb(1, 2, 4),
                ["CvDispStatusBarForegroundBrush"] = Color.FromRgb(1, 2, 5),
                ["CvDispSeparatorBrush"] = Color.FromRgb(1, 2, 6),
                ["DangerBrush"] = Color.FromRgb(4, 5, 6),
            };
            var r = Chrome(Shoot(host), host, Default, out var ok);
            Check(ok, $"a Color under each key leaves the fixed colors in place instead of breaking the control: {r}");
            var play = FindToggle(ctrl, "Play / Stop")!;
            Check(ColorOf(play.Background) == Color.FromRgb(0xC6, 0x28, 0x28), $"a non-Brush DangerBrush leaves the stopped ⏯ at its fixed color: {ColorOf(play.Background)}");
            lock (warnings)
            {
                var all = string.Join(" | ", warnings);
                foreach (var key in new[] { "CvDispToolbarBackgroundBrush", "CvDispStatusBarBackgroundBrush", "CvDispStatusBarForegroundBrush", "CvDispSeparatorBrush" })
                    Check(warnings.Any(w => w.Contains("'" + key + "'")), $"the misplaced key {key} is named in a warning: [{all}]");
                Check(!warnings.Any(w => w.Contains("DangerBrush")), $"a host library key of another type stays quiet: [{all}]");
            }
        }
        finally { CvLog.Sink = before; }
    });

    /// <summary>⏯·✋ 강조색은 호스트 테마 키(PrimaryBrush / SuccessBrush / DangerBrush)를 따르고, 키가 없으면 고정색이다.
    /// 종전에는 그 키를 생성·토글 때 읽어 브러시를 박아 두어, 부모 요소에 둔 키는 생성 시점에 보이지 않았고 실행 중 테마를
    /// 바꾸면 다음 토글까지 옛 색이었다. 풀린 ✋ 는 강조색을 갖지 않는다 — 풀린 뒤 테마가 바뀌어도 다시 칠해지지 않아야 한다.</summary>
    [Fact]
    public void AccentsFollowHostKeysAtRunTime() => RunSta(() =>
    {
        var (host, ctrl) = Hosted();
        ResourceDictionary Accents(byte v) => new()
        {
            ["PrimaryBrush"] = new SolidColorBrush(Color.FromRgb(v, 1, 1)),
            ["SuccessBrush"] = new SolidColorBrush(Color.FromRgb(v, 2, 2)),
            ["DangerBrush"] = new SolidColorBrush(Color.FromRgb(v, 3, 3)),
        };
        host.Resources.MergedDictionaries.Add(Accents(0x40));
        Render(host);
        var play = FindToggle(ctrl, "Play / Stop")!;
        var pan = FindToggle(ctrl, "Pan")!;
        Check(ColorOf(play.Background) == Color.FromRgb(0x40, 3, 3), $"a stopped ⏯ shows the host DangerBrush from a parent element: {ColorOf(play.Background)}");

        host.Resources.MergedDictionaries[0] = Accents(0x80);
        Render(host);
        Check(ColorOf(play.Background) == Color.FromRgb(0x80, 3, 3), $"a theme swap repaints ⏯ without a toggle: {ColorOf(play.Background)}");
        ctrl.IsRunning = true;
        Check(ColorOf(play.Background) == Color.FromRgb(0x80, 2, 2), $"a live ⏯ shows the host SuccessBrush: {ColorOf(play.Background)}");
        pan.IsChecked = true;
        Check(ColorOf(pan.Background) == Color.FromRgb(0x80, 1, 1), $"a pressed ✋ shows the host PrimaryBrush: {ColorOf(pan.Background)}");
        host.Resources.MergedDictionaries[0] = Accents(0xC0);
        Render(host);
        Check(ColorOf(pan.Background) == Color.FromRgb(0xC0, 1, 1) && ColorOf(play.Background) == Color.FromRgb(0xC0, 2, 2),
            $"pressed accents follow a swap too: pan={ColorOf(pan.Background)} play={ColorOf(play.Background)}");

        host.Resources.MergedDictionaries.Clear();
        Render(host);
        Check(ColorOf(play.Background) == Color.FromRgb(0x2E, 0x7D, 0x32) && ColorOf(pan.Background) == Color.FromRgb(0x15, 0x65, 0xC0),
            $"without the keys the accents fall back to the fixed colors: play={ColorOf(play.Background)} pan={ColorOf(pan.Background)}");
        pan.IsChecked = false;
        Check(pan.ReadLocalValue(System.Windows.Controls.Control.BackgroundProperty) == DependencyProperty.UnsetValue,
            "a released ✋ goes back to the button style, not a stale accent");

        host.Resources.MergedDictionaries.Add(Accents(0xE0));
        Render(host);
        Check(pan.ReadLocalValue(System.Windows.Controls.Control.BackgroundProperty) == DependencyProperty.UnsetValue,
            $"a theme swap after release does not paint the released ✋ again: {pan.ReadLocalValue(System.Windows.Controls.Control.BackgroundProperty)}");
        Check(ColorOf(play.Background) == Color.FromRgb(0xE0, 2, 2), $"...while the live ⏯ follows it: {ColorOf(play.Background)}");
        ctrl.IsRunning = false;
        Check(ColorOf(play.Background) == Color.FromRgb(0xE0, 3, 3), $"stopping shows the host DangerBrush: {ColorOf(play.Background)}");
        host.Resources.MergedDictionaries.Clear();
        Render(host);
        Check(ColorOf(play.Background) == Color.FromRgb(0xC6, 0x28, 0x28), $"a stopped ⏯ without the key falls back to its fixed color: {ColorOf(play.Background)}");
    });

    /// <summary>앱 사전에서만 닿는 길 — 생성자 안에서 도는 것들. 이 컨트롤의 첫 논리 자식이 붙는 순간 WPF 가 생성자 안에서 암시 스타일을
    /// 적용하고(그때 보이는 것은 앱 사전뿐이다), 앱 사전의 키는 슬롯을 묶는 순간 풀린다. 행·자식을 생성자 첫머리에서 붙이던 판은 앱 사전의
    /// CvDispCtrl 스타일이 새 색 속성이나 ToolbarDock·IsToolbarVisible·IsCamControlVisible·IsRunning·LoadFrameCommand 를 주면 생성자에서
    /// NullReferenceException(ToolbarDock 은 ArgumentNullException)이 나 그 화면이 뜨지 않았다(실측 — 호스트는 전역 스타일을 앱 사전에
    /// 병합한다). 스타일은 콜백이 만지는 필드 가운데 가장 늦게 만들어지는 것을 건드리는 속성까지 준다(불러오기 메뉴 항목 — LoadFrameCommand,
    /// 툴바 짝 메뉴 항목 — IsToolbarVisible). 행 정의만 상태 줄 뒤로 옮기는 부분 되돌림도 여기서 떨어져야 한다. 툴바를 접는 스타일은 렌더
    /// 단언과 섞이지 않게 따로 만든다. 부모 요소의 Resources 로는 이 길에 닿지 않는다: 부모 키는 생성자가 끝난 뒤 트리에 붙을 때 풀린다.
    /// Application 은 프로세스에 하나라 한 번 만들면 그 Dispatcher(이 시험 스레드의 것)와 함께 남는다. 이 절은 들어올 때의 앱 사전을 되돌려
    /// 놓을 뿐이다 — Application.Current.Dispatcher 에 기대는 시험을 더하면, 이 절 뒤에서는 그 Dispatcher 의 스레드가 죽어 있다.</summary>
    [Fact]
    public void AppLevelStyleAndKeysWorkInsideTheConstructor() => RunSta(() =>
    {
        var app = Application.Current ?? new Application();
        var before = app.Resources;
        var bar = Color.FromRgb(0x21, 0x43, 0x65);
        int commandRuns = 0;
        var counting = new Cmd(() => commandRuns++);
        var style = new Style(typeof(CvDispCtrl));
        style.Setters.Add(new Setter(CvDispCtrl.ToolbarBackgroundProperty, new SolidColorBrush(bar)));
        style.Setters.Add(new Setter(CvDispCtrl.ToolbarDockProperty, System.Windows.Controls.Dock.Left));
        style.Setters.Add(new Setter(CvDispCtrl.IsCamControlVisibleProperty, false));
        style.Setters.Add(new Setter(CvDispCtrl.IsRunningProperty, true));
        style.Setters.Add(new Setter(CvDispCtrl.LoadFrameCommandProperty, counting));
        style.Setters.Add(new Setter(CvDispCtrl.ContinuousCommandProperty, counting));
        style.Setters.Add(new Setter(CvDispCtrl.StopCommandProperty, counting));
        var rd = HostChrome(ThemeA);
        rd["SuccessBrush"] = new SolidColorBrush(Color.FromRgb(0x40, 2, 2));
        rd.Add(typeof(CvDispCtrl), style);
        app.Resources = rd;
        try
        {
            CvDispCtrl? ctrl = null;
            var ex = Record.Exception(() => ctrl = new CvDispCtrl());
            Check(ex is null, $"an application-level style setting the colors, ToolbarDock, IsCamControlVisible, IsRunning and the commands does not break construction: {ex}");
            var play = FindToggle(ctrl!, "Play / Stop")!;
            Check(play.IsChecked == true && ColorOf(play.Background) == Color.FromRgb(0x40, 2, 2),
                $"a styled IsRunning shows a live ⏯ in the application's SuccessBrush, picked up inside the constructor: checked={play.IsChecked} {ColorOf(play.Background)}");
            Check(commandRuns == 0, $"applying the style runs no command: {commandRuns}");
            Check(ctrl!.ContextMenu.Items[0] is System.Windows.Controls.MenuItem load && load.Visibility == Visibility.Visible,
                "a styled LoadFrameCommand shows the load entry in the context menu");
            Check(ctrl.ToolbarBackground is SolidColorBrush b && b.Color == bar && ctrl.ToolbarDock == System.Windows.Controls.Dock.Left && !ctrl.IsCamControlVisible,
                "the application style was applied");

            var shot = Shoot(ctrl);
            var tb = shot.At((int)(2 * shot.S), shot.H / 3);
            var st = shot.At(shot.W / 2, shot.H - 1);
            Check(tb == bar && st == ThemeA.Status, $"the left-docked toolbar shows the style color and the status bar the application key: toolbar={tb} status={st}");

            var hide = new Style(typeof(CvDispCtrl));
            hide.Setters.Add(new Setter(CvDispCtrl.IsToolbarVisibleProperty, false));
            app.Resources = new ResourceDictionary { [typeof(CvDispCtrl)] = hide };
            CvDispCtrl? hidden = null;
            var ex2 = Record.Exception(() => hidden = new CvDispCtrl());
            Check(ex2 is null && hidden!.IsToolbarVisible == false, $"an application-level style hiding the toolbar does not break construction: {ex2}");
            Check(hidden!.ContextMenu.Items[hidden.ContextMenu.Items.Count - 1] is System.Windows.Controls.MenuItem clear && clear.Visibility == Visibility.Collapsed,
                "...and the toolbar-paired context-menu entry is hidden with it");
        }
        finally { app.Resources = before; }
    });

    /// <summary>툴바 버튼을 툴팁 문구로 찾는다(버튼은 내부에서 만들어져 이름이 없다).</summary>
    /// <summary>⏯ 토글은 누른 사람의 뜻이 아니라 IsRunning(VM 의 카메라 상태)을 보인다. 명령이 라이브를 켜지 못하면 IsRunning 이
    /// 그대로라 속성 콜백이 안 돌고 버튼이 눌린 채 굳었다 — VM 이 같은 값을 다시 알려도 안 풀린다(소비자 독해, Mvs3 가 자기 토글에서
    /// 같은 모양을 실측으로 고친 적이 있다). 대조: 명령이 동기로 켜면 눌린 채 남고, 나중에 켜지면 그때 눌린다.</summary>
    [Fact]
    public void PlayToggleFollowsIsRunningNotTheClick() => RunSta(() =>
    {
        var ctrl = new CvDispCtrl();
        ctrl.Measure(new Size(VW, VH));
        ctrl.Arrange(new Rect(0, 0, VW, VH));
        ctrl.UpdateLayout();
        var play = FindToggle(ctrl, "Play / Stop");
        Check(play is not null, "the ⏯ toggle is found");
        void Click() => ((System.Windows.Automation.Provider.IToggleProvider)
            new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(play!)).Toggle();

        // 시작하지 못하는 명령 — 버튼이 풀려 있어야 한다
        ctrl.ContinuousCommand = new Cmd(() => { });
        Click();
        Check(play!.IsChecked == false, "a start that did not happen leaves the toggle released, not stuck pressed");

        // 대조: 동기로 켜는 명령 — 눌린 채 남는다
        ctrl.ContinuousCommand = new Cmd(() => ctrl.IsRunning = true);
        Click();
        Check(play.IsChecked == true && ctrl.IsRunning, "control: a start that did happen leaves the toggle pressed");

        // 정지하지 못하는 명령 — 라이브가 도는 동안 버튼은 눌린 채
        ctrl.StopCommand = new Cmd(() => { });
        Click();
        Check(play.IsChecked == true, "a stop that did not happen leaves the toggle pressed while live runs");

        // 나중에(비동기로) 켜지는 경우 — 누른 직후에는 풀려 있다가 켜지는 순간 눌린다
        ctrl.StopCommand = new Cmd(() => ctrl.IsRunning = false);
        Click();
        Check(play.IsChecked == false, "control: a stop that did happen releases the toggle");
        ctrl.ContinuousCommand = new Cmd(() => { });
        Click();
        Check(play.IsChecked == false, "a start still in progress shows released");
        ctrl.IsRunning = true;
        Check(play.IsChecked == true, "...and pressed once the view-model reports live");
    });

    private sealed class Cmd(Action run) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
    }

    private static System.Windows.Controls.Primitives.ToggleButton? FindToggle(DependencyObject root, string tooltip)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is System.Windows.Controls.Primitives.ToggleButton t && Equals(t.ToolTip, tooltip)) return t;
            if (FindToggle(c, tooltip) is { } found) return found;
        }
        return null;
    }

    private static System.Windows.Controls.Button? FindButton(DependencyObject root, string tooltip)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is System.Windows.Controls.Button b && Equals(b.ToolTip, tooltip)) return b;
            if (FindButton(c, tooltip) is { } found) return found;
        }
        return null;
    }
}
