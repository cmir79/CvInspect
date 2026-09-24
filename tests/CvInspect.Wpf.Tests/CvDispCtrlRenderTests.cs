// CvDispCtrl 오프스크린 렌더 실증 — 창 없이 RenderTargetBitmap 으로 그려 픽셀을 센다.
// 각 절은 계약 하나를 못 박는다: Frame 에 ICvPixelSource 를 직접 넣은 렌더가 Mat 경로와 같은지, 대입 뒤 원본
// 배열을 고쳐도 화면이 그대로인지(픽셀은 대입 시점에 백버퍼로 옮겨진다), 행 끝 패딩 버퍼가 제대로 걸리는지,
// 계약을 어긴 값이 예외 대신 빈 화면 + 경고로 끝나는지, 크기가 바뀌면 새 프레임 없이도 그 크기에 맞추는지.
// WPF 요소는 STA 스레드에서만 만들 수 있어 본문을 STA 로 감싼다.
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CvInspect.Controls;
using CvInspect.Imaging;
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
    public void ClipOverlayToImageKeepsTheFitMarginsClean() => RunSta(() =>
    {
        // 잘린 프레임은 칸과 비율이 달라 맞춤 여백이 크다. 오버레이를 표면 경계로만 자르면 영역에 걸친 도형과 영역 밖
        // 라벨이 그 여백에 그려져 이미지가 이어지는 것처럼 보인다(소비자 실측 0.26.3, 400×150 → 400×400: 여백 6305px).
        // 켜면 여백은 비고 이미지 안은 그대로 그려져야 한다. 끄면(기본) 종전대로 여백에도 그린다 — 가장자리 라벨이 읽히게.
        var gray = new byte[400 * 150];
        Array.Fill(gray, (byte)128);
        var frame = new CamFrame(gray, 400, 150, 400, CamPixelFormat.Mono8);
        var overlay = new CvInspect.Vision.Overlay.ViOverlay();
        overlay.Add(new CvInspect.Vision.Overlay.ViOverlaySeg { X1 = 200, Y1 = -200, X2 = 200, Y2 = 350 });   // 영역에 걸친 선
        overlay.Add(new CvInspect.Vision.Overlay.ViOverlayLabel { Text = "outside", X = 200, Y = -40 });   // 영역 밖 라벨
        var inside = new CvInspect.Vision.Overlay.ViOverlay();
        inside.Add(new CvInspect.Vision.Overlay.ViOverlayRect { CenterX = 100, CenterY = 75, Width = 60, Height = 40 });

        var bare = Render(Ctrl(frame), 400, 400);
        // 이미지가 그려진 사각 — 회색 화소의 외접 사각에서 한 칸씩 더 뗀 바깥을 여백으로 센다(경계 행의 섞인 화소 제외).
        // 맨 아래 상태 줄은 회색 계열이라 훑지 않는다(표면 밖이라 오버레이도 거기엔 못 그린다).
        int x0 = 400, y0 = 400, x1 = -1, y1 = -1;
        for (int y = 0; y < 360; y++)
            for (int x = 0; x < 400; x++)
            {
                int i = (y * 400 + x) * 4;
                if (Math.Abs(bare[i] - 128) < 4 && Math.Abs(bare[i + 1] - 128) < 4 && Math.Abs(bare[i + 2] - 128) < 4)
                {
                    x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                }
            }
        Check(y1 - y0 < 200 && y0 > 50, $"the frame is letterboxed, leaving margins above and below (image rows {y0}..{y1})");

        (int Margin, int Image) Count(byte[] px)
        {
            int margin = 0, image = 0;
            for (int y = 0; y < 400; y++)
                for (int x = 0; x < 400; x++)
                {
                    int i = (y * 400 + x) * 4;
                    if (px[i] == bare[i] && px[i + 1] == bare[i + 1] && px[i + 2] == bare[i + 2] && px[i + 3] == bare[i + 3]) continue;
                    if (x >= x0 && x <= x1 && y >= y0 && y <= y1) image++;
                    else if (x < x0 - 1 || x > x1 + 1 || y < y0 - 1 || y > y1 + 1) margin++;
                }
            return (margin, image);
        }

        var off = Count(Render(new CvDispCtrl { IsToolbarVisible = false, Frame = frame, Overlay = overlay }, 400, 400));
        var on = Count(Render(new CvDispCtrl { IsToolbarVisible = false, Frame = frame, Overlay = overlay, ClipOverlayToImage = true }, 400, 400));
        var inOnly = Count(Render(new CvDispCtrl { IsToolbarVisible = false, Frame = frame, Overlay = inside, ClipOverlayToImage = true }, 400, 400));

        Check(off.Margin > 0, $"by default the overlay still runs into the fit margin — the old behaviour is kept, and this proves the counter sees margins ({off.Margin}px)");
        Check(on.Margin == 0, $"with ClipOverlayToImage the margins stay clean ({on.Margin}px drawn there)");
        Check(on.Image > 0 && Math.Abs(on.Image - off.Image) <= 4,
            $"and the part inside the image is still drawn as before (inside {on.Image}px on, {off.Image}px off)");
        Check(inOnly.Margin == 0 && inOnly.Image > 0, $"geometry wholly inside the image is untouched by the clip ({inOnly.Image}px inside)");
    });

    /// <summary>툴바 버튼을 툴팁 문구로 찾는다(버튼은 내부에서 만들어져 이름이 없다).</summary>
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
