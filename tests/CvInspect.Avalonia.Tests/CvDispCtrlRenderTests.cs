// CvDispCtrl 렌더 실증 — 헤드리스 창(Skia)에 그려 픽셀을 센다. 계약은 CvInspect.Wpf 판과 같고, 판정 기준 화면만 Skia 래스터라이저다:
// Frame 에 ICvPixelSource 를 직접 넣은 렌더가 Mat 경로와 같은지, 대입 뒤 원본 배열을 고쳐도 화면이 그대로인지, 행 끝 패딩이 걸리는지,
// 계약을 어긴 값이 예외 대신 빈 화면 + 경고로 끝나는지, 크기가 바뀌면 새 프레임 없이 맞추는지, 오버레이 자르기·굵기·라벨 배율,
// 테마 색(키·속성·실행 중 교체)과 ⏯·✋ 강조색.
// 임계값은 WPF 판 값을 두고 Skia 에서 잰 값이 그 안에 드는지 확인했다(Windows·Consolas, 2026-10-27 — 잰 값은 각 절 주석).
// Linux 러너는 데이터 글꼴이 다르다(DejaVu Sans Mono 등) — 글자를 재는 판정(라벨 배율·HUD·상태 줄 글자·무이미지 안내)은 CI 첫 회차의
// 단언 기록(CVI_CHECK_LOG)을 보고 재조정한다. 글자 없는 판정(맞춤 폭·선 굵기·화살촉·면 핸들·여백 대조)은 래스터 규칙만 탄다.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using CvInspect.Avalonia.Controls;
using CvInspect.Imaging;
using CvInspect.Vision.Edit;
using CvInspect.Vision.Overlay;
using Xunit;
using static CvInspect.Avalonia.Tests.AvaTest;

namespace CvInspect.Avalonia.Tests;

public class CvDispCtrlRenderTests
{
    private const int W = 1280, H = 960;      // 소스 프레임
    private const int VW = 800, VH = 600;     // 뷰포트 — 소스보다 작아 Fit 축소까지 지난 결과를 본다

    private static byte[] Render(Control ctrl, int w = VW, int h = VH) => AvaTest.Render(ctrl, w, h);

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

    /// <summary>포맷별 그라디언트. pad 는 행 끝 패딩 바이트 수. Bgra32 는 알파를 255 로 고정한다 — 알파가 섞이면 합성 결과가
    /// 소스 픽셀과 달라져 "같다" 판정이 흔들린다.</summary>
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

    [AvaloniaFact]
    public void PixelSourcePathMatchesMatPath()
    {
        var cam = Gradient(W * 3);
        var viaSource = Render(Ctrl(cam));
        Check(DistinctColors(viaSource) > 1000, $"ICvPixelSource path actually draws: distinct colors={DistinctColors(viaSource)}");

        var blank = Render(Ctrl());
        Check(Diff(viaSource, blank) > 0, "ICvPixelSource path differs from the empty control");

        using var mat = cam.AsMat();
        var viaMat = Render(Ctrl(mat));
        Check(Diff(viaSource, viaMat) == 0, $"ICvPixelSource render == Mat render (differing bytes={Diff(viaSource, viaMat)})");
    }

    [AvaloniaFact]
    public void PixelsAreMovedToTheBackBufferAtAssignment()
    {
        // 계약("발행 뒤 불변")을 어겼을 때 깨지는 것은 화면이 아니다 — 픽셀은 대입 시점에 화면 비트맵으로 옮겨지고
        // 렌더는 그 비트맵만 그린다. 여기서 화면이 바뀌면 그 문서가 틀린 것이다.
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
    }

    [AvaloniaTheory]
    [InlineData(CamPixelFormat.Mono8)]
    [InlineData(CamPixelFormat.Bgr24)]
    [InlineData(CamPixelFormat.Bgra32)]
    public void EveryFormatMatchesMatPath(CamPixelFormat fmt)
    {
        // 세 포맷 모두 ICvPixelSource 경로(채널 수)와 Mat 경로(MatType)가 같은 그림이어야 한다.
        var cam = Gradient(fmt, 0);
        var viaSource = Render(Ctrl(cam));
        using var mat = cam.AsMat();
        var viaMat = Render(Ctrl(mat));
        Check(DistinctColors(viaSource) > 100, $"{fmt}: ICvPixelSource path draws (distinct colors={DistinctColors(viaSource)})");
        Check(Diff(viaSource, viaMat) == 0, $"{fmt}: ICvPixelSource render == Mat render (diff={Diff(viaSource, viaMat)})");
    }

    [AvaloniaFact]
    public void RowPaddingIsHonoured()
    {
        // 공급자 버퍼의 stride 가 폭×채널보다 클 때(행 끝 패딩) 타이트한 버퍼와 같은 그림이 나와야 한다.
        var tight = Render(Ctrl(Gradient(W * 3)));
        var padded = Render(Ctrl(Gradient(W * 3 + 64)));
        Check(Diff(tight, padded) == 0, $"padded-stride source renders identically to tight source (diff={Diff(tight, padded)})");
    }

    [AvaloniaFact]
    public void ContractViolationsFallBackToBlankWithWarning()
    {
        var warnings = new List<string>();
        var prev = CvLog.Sink;
        CvLog.Sink = (level, src, msg, ex) => { if (level == CvLogLevel.Warning) warnings.Add($"{src}: {msg}"); };
        warnings.Clear();   // 붙는 순간 그동안 붙잡혀 있던 줄이 흘러든다 — 이 절이 세는 것은 그 뒤의 것이다
        try
        {
            var blank = Render(Ctrl());

            // 길이가 stride×height 에 못 미치는 버퍼 — 화면 비트맵으로 옮기기 전에 걸러야 한다.
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
    }

    [AvaloniaTheory]
    [InlineData(800, 1400)]   // 커지면 여백이 남았다
    [InlineData(1400, 800)]   // 작아지면 잘린 채 남았다
    public void ResizeRefitsWithoutANewFrame(int fromW, int toW)
    {
        // 프레임이 드문 화면(관제·라인 정지 중)에서 창 크기가 바뀌면 다음 프레임이 올 때까지 옛 배율로 남아 여백이 생기거나 잘렸다.
        // 기준은 처음부터 그 크기로 뜬 컨트롤이다 — 맞춤 식을 여기 다시 적으면 식이 바뀔 때 같이 틀린다.
        var gray = new byte[1000 * 500];
        Array.Fill(gray, (byte)128);
        CamFrame Frame() => new(gray, 1000, 500, 1000, CamPixelFormat.Mono8);   // 대입마다 새 값이어야 교체로 친다

        var ctrl = Ctrl(Frame());
        Render(ctrl, fromW, 600);
        // 한 장 더 받고 선 화면 — 첫 배치의 크기 변경이 남긴 맞춤 예약을 이 프레임이 소화한다. 이 단계를 빼면 남은
        // 예약이 리사이즈 뒤 그리기에서 쓰여 결함이 가려진다.
        ctrl.Frame = Frame();
        var before = GrayWidth(Render(ctrl, fromW, 600), fromW, 600);
        var resized = GrayWidth(Render(ctrl, toW, 600), toW, 600);   // 새 프레임 없이 크기만 바꾼다
        var born = GrayWidth(Render(Ctrl(Frame()), toW, 600), toW, 600);

        Check(before > 0 && born > 0, $"the probe sees the picture at all ({fromW} wide={before}px, {toW} wide={born}px)");
        Check(Math.Abs(born - before) > 50, $"a control of the other width fits the picture differently — else the next check proves nothing ({fromW}={before}px, {toW}={born}px)");
        Check(resized == born,
            $"resizing {fromW}->{toW} refits at once without a new frame: drew {resized}px, a control born at that size draws {born}px (the old fit was {before}px)");
    }

    /// <summary>툴바의 축소 버튼 — 사람이 하는 줌 그대로 버튼으로 준다.</summary>
    private static void ZoomOut(CvDispCtrl c)
    {
        var b = FindButton(c, "Zoom Out");
        Check(b is not null, "the toolbar's Zoom Out button is found");
        RawClick(b!);
    }

    [AvaloniaFact]
    public void HidingAndShowingKeepsTheZoom()
    {
        // README 가 두 쪽을 다 약속한다 — 크기가 바뀌면 수동 줌 대신 맞추고, 숨겼다 다시 보이는 것(탭 전환)은 크기 변경이 아니라 줌이 남는다.
        // 뒤쪽만 틀리면 탭을 오갈 때마다 줌이 풀리는데, 그렇게 문서를 읽은 호스트는 탭 구조를 피하게 된다.
        // 숨김은 IsVisible=false 다 — 이 프레임워크에는 "자리는 남고 그리지만 않는" 숨김이 따로 없다.
        var gray = new byte[1000 * 500];
        Array.Fill(gray, (byte)128);
        CamFrame Frame() => new(gray, 1000, 500, 1000, CamPixelFormat.Mono8);

        var ctrl = new CvDispCtrl { Frame = Frame() };   // 툴바를 켠다 — 버튼으로 줌을 준다
        var page = new Grid();   // 탭 페이지 자리 — 숨겼다 보이는 것은 이쪽이다
        page.Children.Add(ctrl);
        var fit = GrayWidth(Render(page, 800, 600), 800, 600);

        ZoomOut(ctrl);
        var zoomed = GrayWidth(Render(page, 800, 600), 800, 600);
        Check(fit > 0 && Math.Abs(zoomed * 1.25 - fit) <= 3,
            $"the zoom took effect — else the next check proves nothing (fit {fit}px, one step out {zoomed}px)");

        page.IsVisible = false;
        Render(page, 800, 600);
        page.IsVisible = true;
        var shown = GrayWidth(Render(page, 800, 600), 800, 600);
        Check(shown == zoomed, $"hiding and showing the page keeps the zoom: drew {shown}px, zoomed was {zoomed}px (fit is {fit}px)");

        // 대조군 — 같은 컨트롤에서 크기가 바뀌면 줌이 맞춤으로 바뀐다(앞 절과 같은 계약, 이 절의 판정이 살아 있음을 보인다).
        var resized = GrayWidth(Render(page, 1400, 600), 1400, 600);
        var bornPage = new Grid();
        bornPage.Children.Add(new CvDispCtrl { Frame = Frame() });
        var born = GrayWidth(Render(bornPage, 1400, 600), 1400, 600);
        Check(resized == born, $"a resize still replaces the zoom with a fit: drew {resized}px, a control born at that size draws {born}px");

        // 숨긴 사이에 크기가 바뀌면(탭이 가려진 동안 창 크기 변경) 다시 보일 때 맞춘다 — README 의 단서.
        ZoomOut(ctrl);
        var zoomedWide = GrayWidth(Render(page, 1400, 600), 1400, 600);
        Check(zoomedWide != born, $"zoomed again before hiding (drew {zoomedWide}px, fit is {born}px)");
        page.IsVisible = false;
        Render(page, 1400, 600);
        Render(page, 1000, 600);   // 가려진 채로 크기가 바뀐다
        page.IsVisible = true;
        var reshown = GrayWidth(Render(page, 1000, 600), 1000, 600);
        var bornMid = new Grid();
        bornMid.Children.Add(new CvDispCtrl { Frame = Frame() });
        var fitMid = GrayWidth(Render(bornMid, 1000, 600), 1000, 600);
        Check(reshown == fitMid, $"a size change while hidden refits on show: drew {reshown}px, a control born at that size draws {fitMid}px");
    }

    [AvaloniaFact]
    public void FitToViewRestoresTheFitEvenWhileHidden()
    {
        // 숨겼다 보이는 것은 줌을 남긴다(앞 절). 페이지를 오갈 때 첫 화면으로 돌아오고 싶은 호스트는 그 계약을 깰 수 없어
        // 프레임을 비웠다 다시 넣는 우회를 썼다(소비자 보고 — 맞춤이 내부에만 있었다). FitToView 가 그 자리다.
        var gray = new byte[1000 * 500];
        Array.Fill(gray, (byte)128);
        CamFrame Frame() => new(gray, 1000, 500, 1000, CamPixelFormat.Mono8);

        var ctrl = new CvDispCtrl { Frame = Frame() };
        var page = new Grid();
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
        ZoomOut(ctrl);
        Render(page, 800, 600);
        page.IsVisible = false;
        Render(page, 800, 600);
        ctrl.FitToView();
        Render(page, 800, 600);
        page.IsVisible = true;
        var shown = GrayWidth(Render(page, 800, 600), 800, 600);
        Check(shown == fit, $"FitToView called while hidden shows the fit on return: drew {shown}px, fit is {fit}px");

        ZoomOut(ctrl);
        var zoomedAgain = GrayWidth(Render(page, 800, 600), 800, 600);
        page.IsVisible = false;
        Render(page, 800, 600);
        page.IsVisible = true;
        var kept = GrayWidth(Render(page, 800, 600), 800, 600);
        Check(kept == zoomedAgain && kept != fit, $"control: without FitToView the same hide/show keeps the zoom ({kept}px, zoomed {zoomedAgain}px, fit {fit}px)");

        // 프레임이 오기 전에 불러도 던지지 않는다. 첫 프레임이 맞춰 나오는 것은 FitToView 덕이 아니다(첫 프레임은 늘 맞춘다) —
        // 이 절은 "부르면 안 되는 때" 가 없음만 못 박는다.
        var empty = new CvDispCtrl();
        var emptyPage = new Grid();
        emptyPage.Children.Add(empty);
        Render(emptyPage, 800, 600);
        empty.FitToView();
        empty.Frame = Frame();
        var first = GrayWidth(Render(emptyPage, 800, 600), 800, 600);
        Check(first == fit, $"FitToView before any frame is harmless and the first frame is fitted: drew {first}px, fit is {fit}px");
    }

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
    /// 그 열에 닿지 않는다). 못 찾으면 X1 &lt; X0 이다.</summary>
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

    [AvaloniaTheory]
    [InlineData(400, 150)]   // 위아래 여백
    [InlineData(150, 400)]   // 좌우 여백
    public void ClipOverlayToImageKeepsTheFitMarginsClean(int fw, int fh)
    {
        // 잘린 프레임은 칸과 비율이 달라 맞춤 여백이 크다. 오버레이를 표면 경계로만 자르면 영역에 걸친 도형과 영역 밖
        // 라벨이 그 여백에 그려져 이미지가 이어지는 것처럼 보인다. 켜면 여백은 비고 이미지 안은 그대로 그려져야 한다.
        // 끄면(기본) 종전대로 여백에도 그린다.
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
    }

    [AvaloniaFact]
    public void ClipLeavesTheCornerHudAndEditShapesWhole()
    {
        // 띠 모양으로 잘린 영역 — 화면에 그려진 이미지가 HUD 블록보다 낮다. HUD 글자는 화면 고정 크기라, 자르면 판정 사유
        // 줄이 사라지고 헤더 한 줄만 남아 온전한 HUD 처럼 보인다. 그래서 HUD 는 자르지 않는다.
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
    }

    // === 라벨 글자 배율(OverlayLabelScale) ===

    private static CvDispCtrl ScaleCtrl(CamFrame frame, ViOverlay? overlay, double scale, IReadOnlyList<CvEditShape>? shapes = null)
        => new() { IsToolbarVisible = false, Frame = frame, Overlay = overlay, Shapes = shapes, OverlayLabelScale = scale };

    /// <summary>오버레이 없는 화면과 달라진 화소의 외접 사각. 없으면 X1 &lt; X0.</summary>
    private static (int X0, int Y0, int X1, int Y1) ChangedBox(byte[] px, byte[] bare, int size = ClipBox)
    {
        int x0 = size, y0 = size, x1 = -1, y1 = -1;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
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

    [AvaloniaFact]
    public void StrokeScaleThickensOneItemAtAnyZoom()
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
            // 아래·위 둘 다 — 배수를 두 번 곱하면(9 배) 아래쪽 판정만으로는 통과한다. Skia 실측(가운데 열의 바뀐 화소): 선분 3→7, 사각 6→14,
            // 폴리라인 2→4 — 폴리라인은 아래 경계(2배)에 딱 닿는다(안티앨리어싱 한 줄이 1.0 쪽에만 걸린 모양). 화살촉 높이 10→28.
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
    }

    /// <summary>편집 사각 하나를 얹은 화면 — 렌더까지 돌려 화면 배율이 정해진 뒤 표면을 돌려준다.</summary>
    private static (CvDispCtrl Ctrl, CvDispSurface Surface) EditCtrl(CvEditRect rect)
    {
        var ctrl = new CvDispCtrl { IsToolbarVisible = false, Frame = GrayFrame(400, 400), Shapes = new List<CvEditShape> { rect } };
        Render(ctrl, ClipBox, ClipBox);
        return (ctrl, ctrl.Surface);
    }

    /// <summary>이미지 좌표에서 누르고 이미지 좌표까지 끌어 놓는다 — 사람이 하는 드래그 그대로(누름 판정 포함).</summary>
    private static bool DragImage(CvDispSurface s, (double X, double Y) from, (double X, double Y) to)
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

    [AvaloniaFact]
    public void EdgeHandlesResizeOneSideEvenWhenRotated()
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
        // 아래 변이 내려간다(먼저 찾은 위 변이 잡히면 높이가 줄었다 뒤집힌다).
        var strip = new CvEditRect();
        strip.Set(100, 200, 200, 6);
        var (_, st) = EditCtrl(strip);
        Check(DragImage(st, (200, 205), (200, 215)) && Math.Abs(strip.Y - 200) < eps && Math.Abs(strip.H - 16) < eps,
            $"on a thin strip the nearer side is taken — pulling near the bottom grows it downward (y={strip.Y}, h={strip.H})");
    }

    [AvaloniaFact]
    public void EdgeHandlesThickenOnlyTheMiddleOfEachSide()
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
    }

    [AvaloniaFact]
    public void OverlayLabelScaleGrowsOnlyTheNonHudLabelText()
    {
        // 한 창에서만 검사 라벨 글자를 키우는 속성 — 바라는 동작: 글자만 × 배율, 기준점·정렬 그대로, 배경 상자가 글자를 따라감,
        // HUD·도형은 그대로. 라벨은 두 갈래로 그려진다 — 한 색 블록과 줄별 색 블록. 둘 다 본다.
        // 비율 구간(1.35~1.7)과 기준점 여유(3·4·8 px)는 WPF 판 값이다. Skia·Consolas 실측(Windows): 잉크 폭 1.48·높이 1.47, 배경 상자 1.47·1.42,
        // 기준점 이동 X 0·Y 2 px, 오른쪽·아래 정렬의 기준 변 이동 1~4 px — 구간 안이다. Linux 글꼴은 CI 첫 회차에서 재조정.
        var frame = GrayFrame(400, 400);
        var bare = Render(ScaleCtrl(frame, null, 1.0), ClipBox, ClipBox);
        double Ratio((int X0, int Y0, int X1, int Y1) a, (int X0, int Y0, int X1, int Y1) b, bool horizontal)
            => horizontal ? (b.X1 - b.X0 + 1.0) / (a.X1 - a.X0 + 1.0) : (b.Y1 - b.Y0 + 1.0) / (a.Y1 - a.Y0 + 1.0);

        // 배경 없는 행은 글자 잉크만 잰다 — 배경 상자는 블록 전체 크기에서 잡혀, 줄별 색 갈래가 글자를 안 키우고 상자만 키워도 배경 있는
        // 행은 1.5 로 나온다.
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
            // 위쪽은 잉크 윗선이라 글자가 커지면 행간만큼 내려온다 — 기준점이 같이 커지면 28 px 넘게 움직인다
            Check(Math.Abs(big.X0 - one.X0) <= 3 && Math.Abs(big.Y0 - one.Y0) <= 8,
                $"{path}: the anchor stays put — a top-left label grows to the right and down ({one} -> {big})");
            if (background)
            {
                // 상자만 따로 잰다 — 위 외접 사각은 상자와 글자의 합이라, 상자가 옛 크기로 남고 글자만 커져도 ≈1.5 가 나온다
                var boxOne = DarkBox(Render(ScaleCtrl(frame, overlay, 1.0), ClipBox, ClipBox), bare);
                var boxBig = DarkBox(Render(ScaleCtrl(frame, overlay, 1.5), ClipBox, ClipBox), bare);
                var bw = Ratio(boxOne, boxBig, true);
                var bh = Ratio(boxOne, boxBig, false);
                Check(boxOne.X1 > boxOne.X0 && bw is > 1.35 and < 1.7 && bh is > 1.35 and < 1.7,
                    $"{path}: the background box itself follows the text — width {bw:F2}, height {bh:F2} ({boxOne} -> {boxBig})");
            }
        }

        // 기준점은 정렬마다 다른 변이다 — 오른쪽 정렬은 오른쪽 끝, 가운데 정렬은 가운데, 아래 정렬은 아래쪽이 기준점에 붙어 있어야 한다.
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
        // HUD 표식이 있어도 모서리 정렬이 아니면 붙은 모서리가 없다 — 자르기·CropTo 와 같이 다른 라벨처럼 다룬다
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

        // 거절하지 않는 극단 값과 글자 크기 0 은 그리는 자리에서 잘려 예외 없이 그려진다 — 그리기 안의 예외는 앱의 처리되지 않은 예외로 올라간다
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
    }

    /// <summary>바인딩이 낸 잘못된 OverlayLabelScale 은 던지지 않고 기본값 1.0 으로 떨어진다 — 직전 값도, 스타일 우선순위로 깔린 값도 아니다
    /// (README "Differences" 13). 코드 대입이 던지는 것과 다른 길이라 따로 못 박는다 — 바인딩 값이 던지면 앱 전체 바인딩 오류가 된다.</summary>
    [AvaloniaFact]
    public void AnInvalidOverlayLabelScaleFromABindingFallsBackToTheDefault()
    {
        var ctrl = new CvDispCtrl();
        var src = new Subject();
        ctrl.Bind(CvDispCtrl.OverlayLabelScaleProperty, src);
        src.Push(2.0);
        Check(ctrl.OverlayLabelScale == 2.0, $"premise: a valid bound value applies ({ctrl.OverlayLabelScale})");
        foreach (var bad in new object[] { -1.0, 0.0, double.NaN, double.PositiveInfinity })
        {
            src.Push(2.0);
            var ex = Record.Exception(() => src.Push(bad));
            Check(ex is null && ctrl.OverlayLabelScale == 1.0,
                $"a bound {bad} throws nothing and falls back to the default 1.0, not to the previous 2.0 (threw {Name(ex)}, value {ctrl.OverlayLabelScale})");
        }
        ctrl.SetValue(CvDispCtrl.OverlayLabelScaleProperty, 3.0, BindingPriority.Style);
        src.Push(2.0);
        src.Push(-1.0);
        Check(ctrl.OverlayLabelScale == 1.0, $"...even with a style-priority 3.0 underneath it falls to 1.0 ({ctrl.OverlayLabelScale})");
    }

    /// <summary>값을 손으로 밀어 넣는 관찰 대상 — 바인딩 원천.</summary>
    private sealed class Subject : IObservable<object?>
    {
        private readonly List<IObserver<object?>> _obs = new();

        public IDisposable Subscribe(IObserver<object?> observer)
        {
            _obs.Add(observer);
            return new Unsub(() => _obs.Remove(observer));
        }

        public void Push(object? v)
        {
            foreach (var o in _obs.ToArray()) o.OnNext(v);
            Pump();
        }

        private sealed class Unsub(Action a) : IDisposable
        {
            public void Dispose() => a();
        }
    }

    // === 렌더 배율 — 화면 배율(125%·150% 모니터)에서도 확대가 최근접이고, 글자가 배율만큼 커지는지 ===

    /// <summary>작은 그레이 패턴 — 이웃 화소끼리 값이 달라 번지면(보간) 화면에 원본에 없는 값이 나온다.</summary>
    private static CamFrame Pattern(int w, int h)
    {
        var px = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = (byte)((x * 7 + y * 13 + 5) % 256);
        return new CamFrame(px, w, h, w, CamPixelFormat.Mono8);
    }

    /// <summary>표면 안의 장치 화소마다 뷰 역변환으로 원본 화소를 찾아 견준다. 장치 화소 중심이 원본 화소 경계에서 장치 화소 0.5 안이면
    /// 이웃 화소도 허용한다(래스터 반올림). 이미지 사각 경계가 지나는 화소(부분 덮임)는 세지 않는다. want 는 원본 값을 바꿔 끼울 자리 —
    /// 살아 있음 대조에서 일부러 틀린 기대를 준다.</summary>
    private static (int InImage, int Bad, string Sample) CompareNearest(Shot shot, Window win, CvDispSurface s, int w, int h, Func<int, int, byte> want)
    {
        var o = s.TranslatePoint(new Point(0, 0), win) ?? new Point(double.NaN, double.NaN);
        var org = s.ImageToScreen(0, 0);
        var scale = s.ViewScale;
        var k = shot.S;
        double x0 = (o.X + org.X) * k, y0 = (o.Y + org.Y) * k, x1 = x0 + scale * w * k, y1 = y0 + scale * h * k;
        int inImg = 0, bad = 0;
        string sample = "";
        int sx0 = (int)Math.Floor(o.X * k), sy0 = (int)Math.Floor(o.Y * k);
        int sx1 = (int)Math.Ceiling((o.X + s.Bounds.Width) * k), sy1 = (int)Math.Ceiling((o.Y + s.Bounds.Height) * k);
        for (int dy = Math.Max(0, sy0); dy < Math.Min(shot.H, sy1); dy++)
            for (int dx = Math.Max(0, sx0); dx < Math.Min(shot.W, sx1); dx++)
            {
                bool cross(double e, int a) => e > a && e < a + 1;
                if ((cross(x0, dx) || cross(x1, dx)) && dy + 1 > y0 && dy < y1 || (cross(y0, dy) || cross(y1, dy)) && dx + 1 > x0 && dx < x1) continue;
                var ix = (dx + 0.5 - x0) / (scale * k);
                var iy = (dy + 0.5 - y0) / (scale * k);
                if (ix < 0 || iy < 0 || ix >= w || iy >= h) continue;
                inImg++;
                int xi = (int)Math.Floor(ix), yi = (int)Math.Floor(iy);
                var c = shot.At(dx, dy);
                bool Matches(int cx, int cy) => cx >= 0 && cy >= 0 && cx < w && cy < h && c.R == want(cx, cy) && c.G == c.R && c.B == c.R;
                if (Matches(xi, yi)) continue;
                var near = 0.5 / (scale * k);
                var cands = new List<(int, int)>();
                foreach (var ddx in new[] { ix - xi < near ? -1 : 0, 0, xi + 1 - ix < near ? 1 : 0 })
                    foreach (var ddy in new[] { iy - yi < near ? -1 : 0, 0, yi + 1 - iy < near ? 1 : 0 })
                        cands.Add((xi + ddx, yi + ddy));
                if (cands.Any(p => Matches(p.Item1, p.Item2))) continue;
                bad++;
                if (sample.Length < 300) sample += $" dev({dx},{dy})->img({ix:F2},{iy:F2}) got {c} want {want(xi, yi)};";
            }
        return (inImg, bad, sample);
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void ZoomStaysNearestNeighbourAtEveryRenderScaling(double scaling)
    {
        // 확대 보간의 기본값은 확대한 화소를 번지게 한다 — 검사 화면에서 화소 경계가 흐려지면 "몇 화소 결함인가" 를 눈으로 셀 수 없다.
        // 125%·150% 모니터에서는 장치 화소가 DIP 와 어긋나 번짐이 생기기 쉬운 자리라 세 배율을 다 본다.
        const int fw = 37, fh = 23;
        var frame = Pattern(fw, fh);
        var ctrl = new CvDispCtrl { IsToolbarVisible = false, Frame = frame };
        var shot = Shoot(ctrl, 400, 300, scaling);
        var win = (Window)ctrl.Parent!;
        Check(shot.W == (int)Math.Round(400 * scaling), $"premise: the window draws at render scaling {scaling} ({shot.W}x{shot.H} device pixels, scaling {shot.S})");
        var px = frame.Pixels;
        var (inImg, bad, sample) = CompareNearest(shot, win, ctrl.Surface, fw, fh, (x, y) => px[y * fw + x]);
        Check(inImg > 1000 && bad == 0,
            $"at render scaling {scaling} every device pixel inside the image shows exactly one source pixel — no smoothing ({inImg} compared, {bad} off:{sample})");
        // 살아 있음 — 일부러 틀린 기대(반전)를 주면 거의 다 어긋나야 한다. 0 이면 비교기가 죽은 것이다.
        var (_, badInv, _) = CompareNearest(shot, win, ctrl.Surface, fw, fh, (x, y) => (byte)(255 - px[y * fw + x]));
        Check(badInv > inImg / 2, $"control: the same comparison against an inverted expectation fails ({badInv} of {inImg} off)");
    }

    [AvaloniaTheory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void LabelTextGrowsWithTheRenderScaling(double scaling)
    {
        // 글자 크기는 DIP 로 정해 넘기고 화면 배율은 렌더러가 곱한다 — 그리는 쪽에 화소/DIP 배율 인자가 없다. 그러니 장치 화소로 잰
        // 라벨 폭·높이는 배율 1 의 값 × 배율이어야 한다(배율을 두 번 곱하거나 빠뜨리면 1.25 에서 약 ±25% 어긋난다).
        // 여유(2 장치 화소 + 8%)는 글자 안티앨리어싱 몫으로 잡은 보수적 값이다. Skia·Consolas 실측(Windows): 72×12 → 1.25 에서 89×15(기대 90×15),
        // 1.5 에서 108×18(기대 108×18). Linux 글꼴은 CI 첫 회차에서 재조정.
        var frame = GrayFrame(400, 400);
        var overlay = new ViOverlay();
        overlay.Add(new ViOverlayLabel { Text = "OK 12.3", X = 60, Y = 80, FontSize = 14, Align = ViOverlayAlign.TopLeft, Color = ViOverlayColor.Green, HasBackground = false });
        (int W, int H) Ink(double sc)
        {
            var bare = Shoot(new CvDispCtrl { IsToolbarVisible = false, Frame = frame }, ClipBox, ClipBox, sc);
            var lab = Shoot(new CvDispCtrl { IsToolbarVisible = false, Frame = frame, Overlay = overlay }, ClipBox, ClipBox, sc);
            var b = ChangedBox(lab.Px, bare.Px, lab.W);
            return (b.X1 - b.X0 + 1, b.Y1 - b.Y0 + 1);
        }
        var one = Ink(1.0);
        var big = Ink(scaling);
        Check(one.W > 10 && one.H > 5, $"premise: the label is drawn at scaling 1.0 ({one.W}x{one.H} px)");
        bool Near(int got, int at1) => Math.Abs(got - at1 * scaling) <= 2 + 0.08 * at1 * scaling;
        Check(Near(big.W, one.W) && Near(big.H, one.H),
            $"at render scaling {scaling} the label ink is {scaling}x its size at 1.0 — {one.W}x{one.H} -> {big.W}x{big.H} (want about {one.W * scaling:F0}x{one.H * scaling:F0})");
    }

    // === 테마 색 — 툴바·상태 줄 바탕, 상태 줄 글자, 구분선, 영상 영역 바탕, ⏯·✋ 강조 ===
    // 호스트는 키(색 사전 한 곳에서 모든 인스턴스)나 속성(인스턴스마다)으로 넣고, 실행 중 사전 교체를 따라온다. 교체는 부모 요소의
    // Resources 로 잰다 — 리소스 탐색은 부모 요소와 같은 길로 앱 사전에 닿는다.

    /// <summary>호스트가 고르는 다섯 색 — 툴바 바탕, 상태 줄 바탕, 상태 줄 글자, 구분선, 영상 영역 바탕(이미지 바깥 여백).</summary>
    private readonly record struct Look(Color Bar, Color Status, Color Text, Color Sep, Color View);

    /// <summary>컨트롤 기본 Background — 테마가 없으면 영상 영역에 이것이 비친다.</summary>
    private static readonly Color CtrlBackground = Color.FromRgb(0x2A, 0x2A, 0x2A);

    private static readonly Look Default = new(Color.FromRgb(0xFA, 0xFA, 0xFA), Color.FromRgb(0xFA, 0xFA, 0xFA), Color.FromRgb(0x55, 0x55, 0x55), Color.FromRgb(0xDD, 0xDD, 0xDD), CtrlBackground);

    // 호스트 색 두 벌 — 서로·기본색·영상 영역(0x2A)·무이미지 안내 글자(0x9E)와 겹치지 않게 고른다.
    private static readonly Look ThemeA = new(Color.FromRgb(0x10, 0x40, 0x80), Color.FromRgb(0x00, 0x00, 0x40), Color.FromRgb(0xFF, 0x00, 0x00), Color.FromRgb(0xF1, 0x07, 0xE3), Color.FromRgb(0x60, 0x30, 0x10));
    private static readonly Look ThemeB = new(Color.FromRgb(0x20, 0x80, 0x20), Color.FromRgb(0x40, 0x00, 0x00), Color.FromRgb(0x00, 0xFF, 0x00), Color.FromRgb(0x07, 0xE3, 0xF1), Color.FromRgb(0x10, 0x60, 0x60));

    /// <summary>호스트 색 사전. 키는 호스트가 AXAML 에 그대로 적는 글자라 상수를 거치지 않고 글자로 둔다 — 이름이 바뀌면 여기서 깨져야 한다.</summary>
    private static ResourceDictionary HostChrome(Look t) => new()
    {
        ["CvDispToolbarBackgroundBrush"] = new SolidColorBrush(t.Bar),
        ["CvDispStatusBarBackgroundBrush"] = new SolidColorBrush(t.Status),
        ["CvDispStatusBarForegroundBrush"] = new SolidColorBrush(t.Text),
        ["CvDispSeparatorBrush"] = new SolidColorBrush(t.Sep),
        ["CvDispViewBackgroundBrush"] = new SolidColorBrush(t.View),
    };

    /// <summary>상태 줄에 글자가 있게 프레임을 넣은 컨트롤을 부모 Grid 에 얹는다 — 부모의 Resources 가 호스트 사전 자리다.
    /// withFrame=false 면 무이미지 화면(안내 글자만 있고 상태 줄 글자는 비어 있다 — <see cref="Chrome"/> 이 아니라 <see cref="ViewAt"/> 로 본다).</summary>
    private static (Grid Host, CvDispCtrl Ctrl) Hosted(bool withFrame = true)
    {
        var ctrl = new CvDispCtrl { Frame = withFrame ? Gradient(W * 3) : null };
        var host = new Grid();
        host.Children.Add(ctrl);
        return (host, ctrl);
    }

    private static Shot Shoot(Control el, double w = VW, double h = VH, double scaling = 1.0) => AvaTest.Shoot(el, w, h, scaling);

    /// <summary>상태 줄의 두 글자 블록(좌: 커서 좌표·픽셀 값, 우: 크기·배율). 시험 화면에는 포인터가 없어 왼쪽은 비어 있으므로 픽셀로는
    /// 오른쪽만 보인다 — 그래서 두 블록의 글자색은 속성으로 직접 본다. 고정폭 글꼴 목록으로 다른 글자와 가린다.</summary>
    private static List<TextBlock> StatusTexts(Visual root)
        => root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.FontFamily.Name == "Consolas").ToList();

    private static int Dist2(Color a, Color b) => (a.R - b.R) * (a.R - b.R) + (a.G - b.G) * (a.G - b.G) + (a.B - b.B) * (a.B - b.B);

    /// <summary>영상 표면(툴바·상태 줄 사이) — 표본 좌표를 레이아웃에서 읽는다(툴바 높이를 숫자로 박지 않는다). 반환은 root 기준 DIP 사각.</summary>
    private static Rect SurfaceRect(Visual root)
    {
        var surface = root.GetVisualDescendants().OfType<CvDispSurface>().FirstOrDefault()
                      ?? throw new InvalidOperationException("no CvDispSurface under the root");
        var tl = surface.TranslatePoint(new Point(0, 0), root) ?? throw new InvalidOperationException("the surface is not under the root");
        return new Rect(tl, surface.Bounds.Size);
    }

    /// <summary>영상 영역 바탕 표본 — 표면 왼쪽 가장자리에서 8 DIP 안쪽, 세로 가운데. 무이미지 안내 글자는 표면 가운데에 놓여
    /// 왼쪽 끝에 닿지 않고, 이미지가 있으면 맞춤(여백 3%)이 4:3 프레임을 높이에 맞추므로 이 뷰포트에서 좌우 여백이 50 DIP 남짓이라
    /// 8 DIP 는 이미지 바깥이다. 툴바·상태 줄과도 떨어져 있다.</summary>
    private static (int X, int Y) ViewPoint(Shot s, Visual root)
    {
        var r = SurfaceRect(root);
        return ((int)Math.Round((r.Left + 8) * s.S), (int)Math.Round((r.Top + r.Height / 2) * s.S));
    }

    private static Color ViewAt(Shot s, Visual root)
    {
        var (x, y) = ViewPoint(s, root);
        return s.At(x, y);
    }

    /// <summary>렌더 결과와 상태 줄 글자 속성을 읽어 한 줄로 적는다(실패 메시지에 그대로 싣는다). 다 맞으면 ok.
    /// 바탕은 버튼·글자가 없는 패딩 자리에서 읽는다 — 툴바는 위 가장자리 안쪽(가운데, 2 DIP), 상태 줄은 맨 아래 줄.
    /// 구분선은 가운데 세로줄에서 툴바 아래 가장자리와 상태 줄 위 가장자리를 각각 찾고, 툴바 높이 가운데에서 선 폭(1 DIP)·위아래로
    /// 17 DIP 이상 이어지는 세로 구분선 둘을 센다(폭과 길이로 거르는 이유: 버튼 바탕·아이콘 획 사이 틈이 같은 색으로 잡히는 일을 거른다).
    /// 글자는 상태 줄 띠에서 바탕보다 글자색에 가까운 픽셀 수(그려졌다는 증거)와, 두 글자 블록의 Foreground 가 정확히 그 색인지(어느 색인가).
    /// 영상 영역 바탕은 <see cref="ViewPoint"/> 한 점(이미지 바깥 맞춤 여백)에서 읽는다.</summary>
    private static string Chrome(Shot s, Visual root, Look want, out bool ok)
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
        var fgs = StatusTexts(root).Select(t => t.Foreground is ISolidColorBrush b ? b.Color.ToString() : t.Foreground?.ToString() ?? "null").ToList();
        bool fgOk = fgs.Count == 2 && fgs.All(f => f == want.Text.ToString());
        var view = ViewAt(s, root);
        ok = tb == want.Bar && st == want.Status && topEdge > 0 && bottomEdge > 0 && groupSepX.Count == 2 && text > 0 && fgOk && view == want.View;
        return $"scale={s.S}, toolbar={tb} (want {want.Bar}), status={st} (want {want.Status}), view margin={view} (want {want.View}), separator rows top={topEdge} bottom={bottomEdge}, " +
               $"group separators={groupSepX.Count} at x=[{string.Join(",", groupSepX)}] (want 2), " +
               $"text pixels={text}{(bottomEdge > 0 ? "" : " (status edge missing — counted in the bottom band)")}, " +
               $"status text brushes=[{string.Join(",", fgs)}] (want 2 × {want.Text})";
    }

    /// <summary>호스트가 아무 색도 주지 않으면 고정색 그대로다. 대조군이자 표본 자리가 맞다는 증거다 — 표본 좌표가 바탕이 아닌 곳을
    /// 찍고 있으면 여기서 깨진다(그러면 아래 절들이 거짓으로 통과할 수 있다).</summary>
    [AvaloniaFact]
    public void ChromeKeepsItsColorsWithoutAHostTheme()
    {
        var (host, _) = Hosted();
        var r = Chrome(Shoot(host), host, Default, out var ok);
        Check(ok, $"without host keys the toolbar and status bar keep their fixed colors and the view margin shows the control's #2A2A2A: {r}");
    }

    /// <summary>호스트가 키를 두면 툴바·상태 줄·글자·구분선·영상 영역 바탕이 그 색이다.</summary>
    [AvaloniaFact]
    public void ChromeFollowsHostKeys()
    {
        var (host, _) = Hosted();
        host.Resources.MergedDictionaries.Add(HostChrome(ThemeA));
        var r = Chrome(Shoot(host), host, ThemeA, out var ok);
        Check(ok, $"host keys paint the toolbar, the status bar and both of its text blocks, the separators, and the view margin: {r}");
    }

    /// <summary>실행 중 사전을 갈아 끼우면 다시 띄우지 않아도 따라 바뀌고, 키를 걷으면 고정색으로 돌아간다 — 한 번 읽어 얼린 브러시면
    /// 첫 색에 굳는다(소비자는 앱 색 사전 교체로 라이트/다크를 바꾼다).</summary>
    [AvaloniaFact]
    public void ChromeFollowsARuntimeThemeSwap()
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
    }

    /// <summary>테마 변형(밝음·어둠)을 바꾸면 변형별 사전의 키를 따라온다 — 이 프레임워크에서는 사전 교체 말고 이 길로도 테마가 바뀐다
    /// (README "Differences" 3).</summary>
    [AvaloniaFact]
    public void ChromeFollowsAThemeVariantSwitch()
    {
        var (host, _) = Hosted();
        host.Resources.ThemeDictionaries[ThemeVariant.Light] = HostChrome(ThemeA);
        host.Resources.ThemeDictionaries[ThemeVariant.Dark] = HostChrome(ThemeB);
        var scope = new ThemeVariantScope { RequestedThemeVariant = ThemeVariant.Light, Child = host };
        var ra = Chrome(Shoot(scope), host, ThemeA, out var okA);
        Check(okA, $"the light variant's keys apply: {ra}");
        scope.RequestedThemeVariant = ThemeVariant.Dark;
        var rb = Chrome(Shoot(scope), host, ThemeB, out var okB);
        Check(okB, $"switching the theme variant repaints from the dark dictionary without a restart: {rb}");
    }

    /// <summary>키 이름 상수는 시험이 글자로 적은 키와 같아야 한다 — x:Static 으로 적은 호스트와 글자로 적은 호스트가 같은 곳을 가리킨다.</summary>
    [Fact]
    public void KeyConstantsSpellTheDocumentedStrings()
    {
        Check(CvDispCtrl.ToolbarBackgroundKey == "CvDispToolbarBackgroundBrush" && CvDispCtrl.StatusBarBackgroundKey == "CvDispStatusBarBackgroundBrush"
              && CvDispCtrl.StatusBarForegroundKey == "CvDispStatusBarForegroundBrush" && CvDispCtrl.SeparatorBrushKey == "CvDispSeparatorBrush",
            "key constants spell the documented strings");
        Check(CvDispCtrl.ViewBackgroundKey == "CvDispViewBackgroundBrush",
            $"the view background key spells the README string, so x:Static and a typed key reach the same slot: {CvDispCtrl.ViewBackgroundKey}");
        Check(CvDispCtrl.PlaceholderForegroundKey == "CvDispPlaceholderForegroundBrush",
            $"the placeholder text key spells the README string: {CvDispCtrl.PlaceholderForegroundKey}");
        Check(CvDispCtrl.ToolbarForegroundKey == "CvDispToolbarForegroundBrush",
            $"the toolbar icon key spells the README string, the same as the WPF control's: {CvDispCtrl.ToolbarForegroundKey}");
    }

    /// <summary>인스턴스 속성은 키를 이기고, 걷으면 다음 출처로 돌아간다 — 스타일 세터가 있으면 그것, 없으면 키. 키 관찰을 공개 속성 자체에
    /// 묶으면 호스트가 값을 줬다 걷는 순간 관찰까지 지워져 기본색에 머물고, 스타일 세터는 그 관찰(지역 값)에 조용히 진다. 다섯 다 같은 틀로 본다.</summary>
    [AvaloniaTheory]
    [InlineData(nameof(CvDispCtrl.ToolbarBackground))]
    [InlineData(nameof(CvDispCtrl.StatusBarBackground))]
    [InlineData(nameof(CvDispCtrl.StatusBarForeground))]
    [InlineData(nameof(CvDispCtrl.SeparatorBrush))]
    [InlineData(nameof(CvDispCtrl.ViewBackground))]
    public void InstancePropertiesBeatTheKeysAndFallBackToThem(string prop)
    {
        var (dp, own, styled) = prop switch
        {
            nameof(CvDispCtrl.ToolbarBackground) => (CvDispCtrl.ToolbarBackgroundProperty, Color.FromRgb(0x12, 0x34, 0x56), Color.FromRgb(0x33, 0x66, 0x99)),
            nameof(CvDispCtrl.StatusBarBackground) => (CvDispCtrl.StatusBarBackgroundProperty, Color.FromRgb(0x12, 0x34, 0x56), Color.FromRgb(0x33, 0x66, 0x99)),
            nameof(CvDispCtrl.StatusBarForeground) => (CvDispCtrl.StatusBarForegroundProperty, Color.FromRgb(0xFF, 0xFF, 0x00), Color.FromRgb(0x00, 0xFF, 0xFF)),
            nameof(CvDispCtrl.ViewBackground) => (CvDispCtrl.ViewBackgroundProperty, Color.FromRgb(0x5A, 0x12, 0x34), Color.FromRgb(0x34, 0x5A, 0x12)),
            _ => (CvDispCtrl.SeparatorBrushProperty, Color.FromRgb(0x0B, 0xF5, 0x0B), Color.FromRgb(0xF5, 0xA0, 0x0B)),
        };
        Look With(Color c) => prop switch
        {
            nameof(CvDispCtrl.ToolbarBackground) => ThemeA with { Bar = c },
            nameof(CvDispCtrl.StatusBarBackground) => ThemeA with { Status = c },
            nameof(CvDispCtrl.StatusBarForeground) => ThemeA with { Text = c },
            nameof(CvDispCtrl.ViewBackground) => ThemeA with { View = c },
            _ => ThemeA with { Sep = c },
        };

        var (host, ctrl) = Hosted();
        host.Resources.MergedDictionaries.Add(HostChrome(ThemeA));
        ctrl.SetValue(dp, new SolidColorBrush(own));
        var r1 = Chrome(Shoot(host), host, With(own), out var ok1);
        Check(ok1, $"{prop}: a value set on the instance beats the host key, the other colors still follow the keys: {r1}");

        ctrl.ClearValue(dp);
        var r2 = Chrome(Shoot(host), host, ThemeA, out var ok2);
        Check(ok2, $"{prop}: clearing the instance value goes back to the host key, not to the fixed color: {r2}");

        var style = new Style(x => x.OfType<CvDispCtrl>());
        style.Setters.Add(new Setter(dp, new SolidColorBrush(styled)));
        host.Styles.Add(style);
        var r3 = Chrome(Shoot(host), host, With(styled), out var ok3);
        Check(ok3, $"{prop}: a style setter from the host beats the key: {r3}");

        ctrl.SetValue(dp, new SolidColorBrush(own));
        ctrl.ClearValue(dp);
        var r4 = Chrome(Shoot(host), host, With(styled), out var ok4);
        Check(ok4, $"{prop}: with a style setter present, clearing the instance value goes back to the style, not the key: {r4}");

        host.Styles.Remove(style);
        var r5 = Chrome(Shoot(host), host, ThemeA, out var ok5);
        Check(ok5, $"{prop}: removing the style goes back to the key: {r5}");
    }

    /// <summary>우리 키에 브러시가 아닌 값(흔한 실수: Color)을 두면 기본색으로 남되 경고로 알린다 — 말없이 두면 "키가 안 먹는다" 로만 보인다.
    /// 키 각각이 이름으로 불려야 한다. 강조색 키(PrimaryBrush 등)는 호스트 라이브러리의 이름이라 다른 형이어도 정상 구성일 수 있어 조용하고,
    /// 그 자리는 고정색이다. 어느 쪽도 컨트롤을 깨뜨리지 않는다.</summary>
    [AvaloniaFact]
    public void ANonBrushUnderAKeyIsIgnoredWithAWarning()
    {
        var warnings = new List<string>();
        var before = CvLog.Sink;
        CvLog.Sink = (level, src, msg, ex) => { if (level == CvLogLevel.Warning) lock (warnings) warnings.Add($"{src}: {msg}"); };
        lock (warnings) warnings.Clear();   // 붙는 순간 그동안 붙잡혀 있던 줄이 흘러든다 — 세는 것은 그 뒤의 것이다
        try
        {
            var (host, ctrl) = Hosted();
            host.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                ["CvDispToolbarBackgroundBrush"] = Color.FromRgb(1, 2, 3),
                ["CvDispStatusBarBackgroundBrush"] = Color.FromRgb(1, 2, 4),
                ["CvDispStatusBarForegroundBrush"] = Color.FromRgb(1, 2, 5),
                ["CvDispSeparatorBrush"] = Color.FromRgb(1, 2, 6),
                ["CvDispViewBackgroundBrush"] = Color.FromRgb(1, 2, 7),
                ["CvDispPlaceholderForegroundBrush"] = Color.FromRgb(1, 2, 8),
                ["DangerBrush"] = Color.FromRgb(4, 5, 6),
            });
            var r = Chrome(Shoot(host), host, Default, out var ok);
            Check(ok, $"a Color under each key leaves the fixed colors (and the control's #2A2A2A in the view margin) in place instead of breaking the control: {r}");
            var play = FindToggle(ctrl, "Play / Stop")!;
            Check(ColorOf(play.Background) == Color.FromRgb(0xC6, 0x28, 0x28), $"a non-Brush DangerBrush leaves the stopped ⏯ at its fixed color: {ColorOf(play.Background)}");
            lock (warnings)
            {
                var all = string.Join(" | ", warnings);
                foreach (var key in new[] { "CvDispToolbarBackgroundBrush", "CvDispStatusBarBackgroundBrush", "CvDispStatusBarForegroundBrush", "CvDispSeparatorBrush", "CvDispViewBackgroundBrush", "CvDispPlaceholderForegroundBrush" })
                    Check(warnings.Any(w => w.Contains("'" + key + "'")), $"the misplaced key {key} is named in a warning: [{all}]");
                Check(!warnings.Any(w => w.Contains("DangerBrush")), $"a host library key of another type stays quiet: [{all}]");
            }
        }
        finally { CvLog.Sink = before; }
    }

    /// <summary>무이미지 화면의 영상 영역 바탕 — 키가 없으면 컨트롤의 #2A2A2A(대조군이자 표본 자리가 안내 글자를 피해 바탕을 찍는다는 증거),
    /// 키를 두면 그 색, 실행 중 사전을 갈아 끼우면 따라가고, 걷으면 #2A2A2A 로 돌아온다. 인스턴스 속성은 키를 이기고 ClearValue 로 키에 돌아간다.</summary>
    [AvaloniaFact]
    public void ViewBackgroundFollowsKeysWithoutAnImage()
    {
        var (host, ctrl) = Hosted(withFrame: false);
        var s0 = Shoot(host);
        var surf = SurfaceRect(host);
        int hint = 0;
        for (int y = (int)(surf.Top * s0.S); y < (int)(surf.Bottom * s0.S); y++)
            for (int x = (int)(surf.Left * s0.S); x < (int)(surf.Right * s0.S); x++)
                if (Dist2(s0.At(x, y), Color.FromRgb(0x9E, 0x9E, 0x9E)) < Dist2(s0.At(x, y), CtrlBackground)) hint++;
        Check(hint > 0, $"premise: the no-image placeholder text is drawn, so this is the no-image screen: hint pixels={hint}");
        Check(ViewAt(s0, host) == CtrlBackground,
            $"without a key or property the empty view shows the control's #2A2A2A (also proves the sample point misses the placeholder text): {ViewAt(s0, host)} at {ViewPoint(s0, host)}");

        host.Resources.MergedDictionaries.Add(HostChrome(ThemeA));
        var sa = Shoot(host);
        Check(ViewAt(sa, host) == ThemeA.View, $"the host key paints the empty view — a light theme no longer leaves a dark slab: {ViewAt(sa, host)} (want {ThemeA.View})");

        host.Resources.MergedDictionaries[0] = HostChrome(ThemeB);
        var sb = Shoot(host);
        Check(ViewAt(sb, host) == ThemeB.View, $"swapping the host dictionary at run time repaints the empty view without a restart: {ViewAt(sb, host)} (want {ThemeB.View})");

        var own = Color.FromRgb(0x5A, 0x12, 0x34);
        ctrl.ViewBackground = new SolidColorBrush(own);
        var sp = Shoot(host);
        Check(ViewAt(sp, host) == own, $"the instance ViewBackground beats the host key: {ViewAt(sp, host)} (want {own})");
        ctrl.ClearValue(CvDispCtrl.ViewBackgroundProperty);
        var sc = Shoot(host);
        Check(ViewAt(sc, host) == ThemeB.View, $"clearing the instance value goes back to the host key, not to #2A2A2A: {ViewAt(sc, host)} (want {ThemeB.View})");

        host.Resources.MergedDictionaries.Clear();
        var sd = Shoot(host);
        Check(ViewAt(sd, host) == CtrlBackground, $"removing the host key brings the control's #2A2A2A back: {ViewAt(sd, host)}");
    }

    /// <summary>무이미지 안내 글자 색은 키·속성을 따른다 — 밝은 바탕에서 기본 #9E9E9E 는 대비가 낮아 바꿀 길이 필요하다.
    /// 글자 화소는 "기본 회색보다 지정 색에 가까운 화소" 로 센다(경계는 바탕과 섞이므로 정확히 같은 색만 세면 0 이 될 수 있다).
    /// 살아 있음: 키가 없을 때 같은 계수는 0 이어야 하고(지정 색이 기본 화면에 없다), 키를 두면 0 보다 커야 한다.
    /// 단색이 아닌 브러시는 기본색으로 남고 경고한다.</summary>
    [AvaloniaFact]
    public void PlaceholderTextFollowsItsKeyAndProperty()
    {
        var warnings = new List<string>();
        var before = CvLog.Sink;
        CvLog.Sink = (level, src, msg, ex) => { if (level == CvLogLevel.Warning) lock (warnings) warnings.Add(msg); };
        lock (warnings) warnings.Clear();
        try
        {
            var gray = Color.FromRgb(0x9E, 0x9E, 0x9E);
            var keyed = Color.FromRgb(0x10, 0xD0, 0x30);
            var own = Color.FromRgb(0xD0, 0x20, 0xC0);
            var (host, ctrl) = Hosted(withFrame: false);
            int Count(Color want)
            {
                var s = Shoot(host);
                var surf = SurfaceRect(host);
                int n = 0;
                for (int y = (int)(surf.Top * s.S); y < (int)(surf.Bottom * s.S); y++)
                    for (int x = (int)(surf.Left * s.S); x < (int)(surf.Right * s.S); x++)
                    {
                        var c = s.At(x, y);
                        if (Dist2(c, want) < Dist2(c, gray) && Dist2(c, want) < Dist2(c, CtrlBackground)) n++;
                    }
                return n;
            }

            Check(Count(keyed) == 0, "control: without the key no pixel of the empty view is closer to the keyed color than to the default gray");
            host.Resources.MergedDictionaries.Add(new ResourceDictionary { [CvDispCtrl.PlaceholderForegroundKey] = new SolidColorBrush(keyed) });
            var nKey = Count(keyed);
            Check(nKey > 0, $"the host key colors the no-image text: keyed pixels={nKey}");

            ctrl.PlaceholderForeground = new SolidColorBrush(own);
            Check(Count(own) > 0 && Count(keyed) == 0, "the instance PlaceholderForeground beats the host key");
            ctrl.ClearValue(CvDispCtrl.PlaceholderForegroundProperty);
            Check(Count(keyed) > 0, "clearing the instance value goes back to the host key");

            host.Resources.MergedDictionaries.Clear();
            Check(Count(keyed) == 0, "removing the key brings the default gray back");

            ctrl.PlaceholderForeground = new LinearGradientBrush
            {
                GradientStops = { new GradientStop(own, 0), new GradientStop(keyed, 1) },
            };
            Check(Count(own) == 0 && Count(keyed) == 0, "a non-solid brush keeps the default gray instead of breaking the draw");
            lock (warnings)
                Check(warnings.Count(w => w.Contains("Placeholder text brush is a LinearGradientBrush")) == 1,
                    $"a non-solid placeholder brush is named once in a warning, not on every repaint: [{string.Join(" | ", warnings)}]");
        }
        finally { CvLog.Sink = before; }
    }

    /// <summary>키·속성이 없으면 영상 영역은 컨트롤의 Background 가 비친다 — 호스트가 컨트롤에 직접 준 Background 도 보여야 한다.
    /// 영상 영역 바탕을 고정 기본색(#2A2A2A)으로 떨어뜨리면 그 호스트의 화면이 말없이 바뀐다. 키를 두면 키가 이기고, 걷으면 호스트 색으로 돌아온다.
    /// 무이미지와 이미지 있는 화면(맞춤 여백) 둘 다 본다.</summary>
    [AvaloniaFact]
    public void AHostSetControlBackgroundStillShowsWithoutTheViewKey()
    {
        var hostColor = Color.FromRgb(0x44, 0x22, 0x66);
        foreach (var withFrame in new[] { false, true })
        {
            var (host, ctrl) = Hosted(withFrame);
            ctrl.Background = new SolidColorBrush(hostColor);
            var s1 = Shoot(host);
            Check(ViewAt(s1, host) == hostColor,
                $"frame={withFrame}: without the view key a Background the host set on the control still shows: {ViewAt(s1, host)} (want {hostColor})");

            host.Resources.MergedDictionaries.Add(HostChrome(ThemeA));
            var s2 = Shoot(host);
            Check(ViewAt(s2, host) == ThemeA.View, $"frame={withFrame}: the view key wins over the control Background: {ViewAt(s2, host)} (want {ThemeA.View})");

            host.Resources.MergedDictionaries.Clear();
            var s3 = Shoot(host);
            Check(ViewAt(s3, host) == hostColor, $"frame={withFrame}: removing the key goes back to the host's control Background, not a fixed color: {ViewAt(s3, host)} (want {hostColor})");
        }
    }

    /// <summary>영상 영역 바탕은 이미지 바깥(맞춤 여백)만 칠하고 이미지 픽셀은 덮지 않는다 — 바탕을 이미지 위에 칠하면 검사 화면이 단색 판이 된다.
    /// 표면 가운데 200 DIP 사각(맞춤 이미지 안쪽)을 키 없는 화면과 바이트로 대조하고, 같은 런에서 여백 한 점은 달라져야 한다(대조가 살아 있다는 증거).</summary>
    [AvaloniaFact]
    public void ViewBackgroundPaintsTheFitMarginButNotTheImage()
    {
        var (host, _) = Hosted();
        var bare = Shoot(host);
        host.Resources.MergedDictionaries.Add(HostChrome(ThemeA));
        var themed = Shoot(host);
        Check(ViewAt(bare, host) == CtrlBackground && ViewAt(themed, host) == ThemeA.View,
            $"the fit margin follows the key: without={ViewAt(bare, host)} with={ViewAt(themed, host)} (want {CtrlBackground} then {ThemeA.View})");

        var surf = SurfaceRect(host);
        int x0 = (int)((surf.Left + surf.Width / 2 - 100) * bare.S), x1 = (int)((surf.Left + surf.Width / 2 + 100) * bare.S);
        int y0 = (int)((surf.Top + surf.Height / 2 - 100) * bare.S), y1 = (int)((surf.Top + surf.Height / 2 + 100) * bare.S);
        var colors = new HashSet<Color>();
        int differ = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                colors.Add(bare.At(x, y));
                if (bare.At(x, y) != themed.At(x, y)) differ++;
            }
        Check(colors.Count > 100, $"premise: the sampled square is image (gradient), not background: distinct colors={colors.Count}");
        Check(differ == 0, $"the view background does not cover the image: {differ} image pixels changed under the key");
    }

    /// <summary>⏯·✋ 강조색은 호스트 테마 키(PrimaryBrush / SuccessBrush / DangerBrush)를 따르고, 키가 없으면 고정색이다. 실행 중 테마를 바꾸면
    /// 다음 토글을 기다리지 않고 따라온다. 풀린 ✋ 는 강조색을 갖지 않는다 — 풀린 뒤 테마가 바뀌어도 다시 칠해지지 않아야 한다.
    /// 풀린 ✋ 의 기준은 같은 자리의 표준 토글 버튼이 테마에서 받는 바탕이다.</summary>
    [AvaloniaFact]
    public void AccentsFollowHostKeysAtRunTime()
    {
        var (host, ctrl) = Hosted();
        var plain = new ToggleButton { Content = "x" };   // 테마가 주는 바탕의 기준
        host.Children.Add(plain);
        ResourceDictionary Accents(byte v) => new()
        {
            ["PrimaryBrush"] = new SolidColorBrush(Color.FromRgb(v, 1, 1)),
            ["SuccessBrush"] = new SolidColorBrush(Color.FromRgb(v, 2, 2)),
            ["DangerBrush"] = new SolidColorBrush(Color.FromRgb(v, 3, 3)),
        };
        host.Resources.MergedDictionaries.Add(Accents(0x40));
        Shoot(host);
        var play = FindToggle(ctrl, "Play / Stop")!;
        var pan = FindToggle(ctrl, "Pan")!;
        Check(ColorOf(play.Background) == Color.FromRgb(0x40, 3, 3), $"a stopped ⏯ shows the host DangerBrush from a parent element: {ColorOf(play.Background)}");

        host.Resources.MergedDictionaries[0] = Accents(0x80);
        Shoot(host);
        Check(ColorOf(play.Background) == Color.FromRgb(0x80, 3, 3), $"a theme swap repaints ⏯ without a toggle: {ColorOf(play.Background)}");
        ctrl.IsRunning = true;
        Check(ColorOf(play.Background) == Color.FromRgb(0x80, 2, 2), $"a live ⏯ shows the host SuccessBrush: {ColorOf(play.Background)}");
        pan.IsChecked = true;
        Check(ColorOf(pan.Background) == Color.FromRgb(0x80, 1, 1), $"a pressed ✋ shows the host PrimaryBrush: {ColorOf(pan.Background)}");
        host.Resources.MergedDictionaries[0] = Accents(0xC0);
        Shoot(host);
        Check(ColorOf(pan.Background) == Color.FromRgb(0xC0, 1, 1) && ColorOf(play.Background) == Color.FromRgb(0xC0, 2, 2),
            $"pressed accents follow a swap too: pan={ColorOf(pan.Background)} play={ColorOf(play.Background)}");

        host.Resources.MergedDictionaries.Clear();
        Shoot(host);
        Check(ColorOf(play.Background) == Color.FromRgb(0x2E, 0x7D, 0x32) && ColorOf(pan.Background) == Color.FromRgb(0x15, 0x65, 0xC0),
            $"without the keys the accents fall back to the fixed colors: play={ColorOf(play.Background)} pan={ColorOf(pan.Background)}");
        pan.IsChecked = false;
        Shoot(host);
        Check(ReferenceEquals(pan.Background, plain.Background),
            $"a released ✋ goes back to the theme's toggle background, not a stale accent: {ColorOf(pan.Background)} (theme {ColorOf(plain.Background)})");

        host.Resources.MergedDictionaries.Add(Accents(0xE0));
        Shoot(host);
        Check(ReferenceEquals(pan.Background, plain.Background),
            $"a theme swap after release does not paint the released ✋ again: {ColorOf(pan.Background)} (theme {ColorOf(plain.Background)})");
        Check(ColorOf(play.Background) == Color.FromRgb(0xE0, 2, 2), $"...while the live ⏯ follows it: {ColorOf(play.Background)}");
        ctrl.IsRunning = false;
        Check(ColorOf(play.Background) == Color.FromRgb(0xE0, 3, 3), $"stopping shows the host DangerBrush: {ColorOf(play.Background)}");
        host.Resources.MergedDictionaries.Clear();
        Shoot(host);
        Check(ColorOf(play.Background) == Color.FromRgb(0xC6, 0x28, 0x28), $"a stopped ⏯ without the key falls back to its fixed color: {ColorOf(play.Background)}");
    }

    /// <summary>Fluent 테마는 토글 버튼의 눌림·포인터 올림·눌린 상태에서 템플릿 안쪽(PART_ContentPresenter) 바탕을 직접 칠한다 — 그대로 두면
    /// ⏯ 의 라이브(초록)·정지(빨강)와 눌린 ✋ 의 강조색이 그 상태에서 테마 강조색에 가려진다. 컨트롤은 그 안쪽 바탕을 버튼 Background 에
    /// 묶는다. 대조: 묶음 없는 표준 토글은 같은 테마에서 눌리면 안쪽 바탕이 버튼 Background 와 달라진다(이 절이 테마의 덮어쓰기를 볼 수
    /// 있다는 증거). 풀린 ✋ 는 묶지 않는다 — 테마의 평소·포인터 올림 색을 그대로 쓴다.</summary>
    [AvaloniaFact]
    public void AccentsSurviveTheFluentToggleStates()
    {
        var (host, ctrl) = Hosted();
        var bare = new ToggleButton { Content = "x", Background = new SolidColorBrush(Colors.Red) };
        host.Children.Add(bare);
        Shoot(host);
        var play = FindToggle(ctrl, "Play / Stop")!;
        var pan = FindToggle(ctrl, "Pan")!;
        var win = (Window)host.Parent!;

        ContentPresenter? Part(ToggleButton b) => b.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault(c => c.Name == "PART_ContentPresenter");
        void Pseudo(ToggleButton b, string pc, bool on)
            => ((IPseudoClasses)typeof(StyledElement).GetProperty("PseudoClasses", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(b)!).Set(pc, on);
        string Probe(ToggleButton b, out bool same)
        {
            Settle(win);
            var inner = Part(b)?.Background;
            same = inner is not null && ColorOf(inner) == ColorOf(b.Background) && ColorOf(b.Background) != Colors.Transparent;
            return $"button={ColorOf(b.Background)} inner={(inner is null ? "no PART" : ColorOf(inner).ToString())}";
        }

        bare.IsChecked = true;
        var rc = Probe(bare, out var bareSame);
        Check(!bareSame, $"control: Fluent repaints the inside of a checked standard toggle, so the check below can see the override ({rc})");

        var r1 = Probe(play, out var s1);
        Check(s1, $"a stopped ⏯ shows its accent inside the template ({r1})");
        ctrl.IsRunning = true;
        var r2 = Probe(play, out var s2);
        Check(play.IsChecked == true && s2, $"a live (checked) ⏯ shows its accent inside the template, not the theme's checked color ({r2})");
        Pseudo(play, ":pointerover", true);
        var r3 = Probe(play, out var s3);
        Check(s3, $"...and with the pointer over it ({r3})");
        Pseudo(play, ":pressed", true);
        var r4 = Probe(play, out var s4);
        Check(s4, $"...and while pressed ({r4})");
        Pseudo(play, ":pressed", false);
        Pseudo(play, ":pointerover", false);

        pan.IsChecked = true;
        var p1 = Probe(pan, out var t1);
        Check(t1, $"a checked ✋ shows its accent inside the template ({p1})");
        Pseudo(pan, ":pointerover", true);
        var p2 = Probe(pan, out var t2);
        Check(t2, $"...and with the pointer over it ({p2})");
        var accent = ColorOf(pan.Background);
        pan.IsChecked = false;
        Settle(win);
        var inner = Part(pan)?.Background;
        // PART 을 못 찾으면 ColorOf(null) 이 투명이라 "강조색과 다르다" 가 공짜로 참이 된다 — 있는지부터 본다.
        Check(inner is not null && ColorOf(inner) != accent,
            $"a released ✋ under the pointer takes the theme's hover color, not the stale accent ({(inner is null ? "no PART" : ColorOf(inner).ToString())} vs accent {accent})");
        Pseudo(pan, ":pointerover", false);
    }

    /// <summary>⏯ 토글은 누른 사람의 뜻이 아니라 IsRunning(VM 의 카메라 상태)을 보인다. 명령이 라이브를 켜지 못하면 IsRunning 이
    /// 그대로라 속성 콜백이 안 돌고 버튼이 눌린 채 굳는다 — VM 이 같은 값을 다시 알려도 안 풀린다. 대조: 명령이 동기로 켜면 눌린 채 남고,
    /// 나중에 켜지면 그때 눌린다.</summary>
    [AvaloniaFact]
    public void PlayToggleFollowsIsRunningNotTheClick()
    {
        var ctrl = new CvDispCtrl();
        Shoot(ctrl);
        var play = FindToggle(ctrl, "Play / Stop");
        Check(play is not null, "the ⏯ toggle is found");
        void Click() => PeerToggle(play!);

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
    }

    /// <summary>앱 스타일은 컨트롤이 논리 트리에 붙을 때 적용된다 — 생성자 안에서 적용되지 않으므로, 앱 전역 스타일이 콜백이 만지는
    /// 요소가 다 만들어지기 전에 끼어들 길이 없다(README "Differences" 7). 스타일이 색·도킹(이 프레임워크의 Dock 형)·카메라 패널·IsRunning·
    /// 명령·툴바 숨김을 다 주어도 생성은 던지지 않고, 창에 붙은 뒤 그 값이 보이며, 스타일 적용이 명령을 실행하지 않는다.</summary>
    [AvaloniaFact]
    public void AnAppLevelStyleAppliesWhenTheControlJoinsTheTree()
    {
        var app = Application.Current!;
        var bar = Color.FromRgb(0x21, 0x43, 0x65);
        int commandRuns = 0;
        var counting = new Cmd(() => commandRuns++);
        var style = new Style(x => x.OfType<CvDispCtrl>());
        style.Setters.Add(new Setter(CvDispCtrl.ToolbarBackgroundProperty, new SolidColorBrush(bar)));
        style.Setters.Add(new Setter(CvDispCtrl.ToolbarDockProperty, Dock.Left));
        style.Setters.Add(new Setter(CvDispCtrl.IsCamControlVisibleProperty, false));
        style.Setters.Add(new Setter(CvDispCtrl.IsRunningProperty, true));
        style.Setters.Add(new Setter(CvDispCtrl.ContinuousCommandProperty, counting));
        style.Setters.Add(new Setter(CvDispCtrl.StopCommandProperty, counting));
        app.Styles.Add(style);
        try
        {
            CvDispCtrl? ctrl = null;
            var ex = Record.Exception(() => ctrl = new CvDispCtrl());
            Check(ex is null, $"constructing under an application style that sets the colors, ToolbarDock, IsCamControlVisible, IsRunning and the commands does not throw: {ex}");
            Check(ctrl!.ToolbarDock == Dock.Top, $"the application style is not applied inside the constructor — only when the control joins the tree (dock={ctrl.ToolbarDock})");
            var shot = Shoot(ctrl);
            var play = FindToggle(ctrl, "Play / Stop")!;
            Check(ctrl.ToolbarDock == Dock.Left && !ctrl.IsCamControlVisible && play.IsChecked == true,
                $"once in a window the style applies: dock={ctrl.ToolbarDock}, cam panel visible={ctrl.IsCamControlVisible}, ⏯ checked={play.IsChecked}");
            Check(commandRuns == 0, $"applying the style runs no command: {commandRuns}");
            var tb = shot.At((int)(2 * shot.S), shot.H / 3);
            Check(tb == bar, $"the left-docked toolbar shows the style color: {tb} (want {bar})");
        }
        finally { app.Styles.Remove(style); }
    }

    /// <summary>영상 없는 화면에 떠 있는 채 언어를 바꾸면 무이미지 안내가 곧바로 새 언어로 다시 그려진다 — 종전에는 그릴 때만 번역을 읽어
    /// 다음 그리기(테마 전환·영상 도착)까지 옛 언어로 남았다(소비자 실제 창에서 본 결함). 기준 화면은 처음부터 새 언어로 띄운 컨트롤이다.</summary>
    [AvaloniaFact]
    public void ThePlaceholderRepaintsWhenTheCultureChanges()
    {
        var before = CvLoc.Culture;
        try
        {
            CvLoc.Culture = "en";
            var live = new CvDispCtrl();
            var en = Shoot(live, VW, VH);

            CvLoc.Culture = "ko";
            var fresh = Shoot(new CvDispCtrl(), VW, VH);
            Check(Diff(en.Px, fresh.Px) > 0, "control: the English and Korean placeholders draw differently, so comparing them can tell the two apart");

            var win = (Window)live.Parent!;
            Settle(win);   // 다시 그리기 요청은 하지 않는다 — 언어 전환 통지만으로 다시 그려져야 한다
            var after = Capture(win);
            Check(Diff(after.Px, fresh.Px) == 0,
                $"switching CvLoc.Culture repaints the shown placeholder in the new language without any other trigger ({Diff(after.Px, en.Px)} bytes changed, {Diff(after.Px, fresh.Px)} still differ from a control opened in Korean)");
        }
        finally { CvLoc.Culture = before; }
    }

    /// <summary>툴바 아이콘 색 — 강조색을 쓰지 않는 버튼의 아이콘은 버튼 테마의 글자색이 아니라 컨트롤이 물려받는 본문 글자색으로 그린다(포인터 올림 포함).
    /// 버튼 글자를 브랜드 색으로 칠하는 테마(Semi)에서 아이콘 일곱 개가 전부 강조색이 되어 툴바가 튀었다(소비자 실제 창). 그 테마를 여기서는 호스트 스타일
    /// 두 줄(버튼 Foreground·포인터 올림 때 안쪽 글자)로 흉내 내고, 같은 스타일을 받은 맨 버튼이 실제로 그 색이 되는 것을 대조군으로 둔다.
    /// 출처 순서는 다른 색 표면과 같다(속성 → 키 → 기본). 비활성은 테마 모양, 눌린 ✋ 와 ⏯ 는 흰색.</summary>
    [AvaloniaFact]
    public void ToolbarIconsFollowTheTextColorNotTheButtonTheme()
    {
        var text = Color.FromRgb(0x12, 0x34, 0x56);
        var brand = Color.FromRgb(0x00, 0x41, 0x99);
        var ctrl = new CvDispCtrl();   // 아이콘만 본다 — 프레임은 필요 없다
        var host = new Grid();
        host.Children.Add(ctrl);
        host.SetValue(global::Avalonia.Controls.Documents.TextElement.ForegroundProperty, new SolidColorBrush(text));
        host.Styles.Add(new Style(x => x.OfType<Button>()) { Setters = { new Setter(TemplatedControl.ForegroundProperty, new SolidColorBrush(brand)) } });
        host.Styles.Add(new Style(x => x.OfType<ToggleButton>()) { Setters = { new Setter(TemplatedControl.ForegroundProperty, new SolidColorBrush(brand)) } });
        host.Styles.Add(new Style(x => x.OfType<Button>().Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters = { new Setter(ContentPresenter.ForegroundProperty, new SolidColorBrush(brand)) },
        });
        var bare = new Button { Content = new CvToolIcon(CvToolIconKind.Fit) };
        host.Children.Add(bare);
        Shoot(host);
        var win = (Window)host.Parent!;

        static Color IconFg(Button b) => ColorOf(b.GetVisualDescendants().OfType<CvToolIcon>().First()
            .GetValue(global::Avalonia.Controls.Documents.TextElement.ForegroundProperty));
        void Pseudo(Button b, string pc, bool on)
            => ((IPseudoClasses)typeof(StyledElement).GetProperty("PseudoClasses", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(b)!).Set(pc, on);
        string[] plain = ["Grab", "Fit", "Zoom In", "Zoom Out", "Clear"];
        List<Button> Plain() => [.. plain.Select(t => FindButton(ctrl, t)!), FindToggle(ctrl, "Pan")!];
        string All(out bool allAre, Color want)
        {
            Settle(win);
            var got = Plain().Select(b => (ToolTip.GetTip(b), IconFg(b))).ToList();
            allAre = got.All(g => g.Item2 == want);
            return string.Join(", ", got.Select(g => $"{g.Item1}={g.Item2}")) + $" (want {want})";
        }

        Check(IconFg(bare) == brand, $"control: the host styles do paint a plain button's icon with the brand color ({IconFg(bare)})");
        Pseudo(bare, ":pointerover", true);
        Settle(win);
        Check(IconFg(bare) == brand, $"control: ...also with the pointer over it ({IconFg(bare)})");

        var r0 = All(out var ok0, text);
        Check(ok0, $"with no key or property the icons use the inherited text color, not the button theme's brand color: {r0}");
        var fit = FindButton(ctrl, "Fit")!;
        Pseudo(fit, ":pointerover", true);
        Settle(win);
        Check(IconFg(fit) == text, $"...also with the pointer over a button, where the theme paints the inside of the template directly ({IconFg(fit)})");
        Pseudo(fit, ":pointerover", false);
        var play = FindToggle(ctrl, "Play / Stop")!;
        Check(IconFg(play) == Colors.White, $"⏯ keeps its white icon on the accent ({IconFg(play)})");

        var keyed = Color.FromRgb(0x00, 0xAA, 0x00);
        host.Resources[CvDispCtrl.ToolbarForegroundKey] = new SolidColorBrush(keyed);
        var r1 = All(out var ok1, keyed);
        Check(ok1, $"the host key beats the inherited text color: {r1}");
        var own = Color.FromRgb(0xAA, 0x00, 0xAA);
        ctrl.ToolbarForeground = new SolidColorBrush(own);
        var r2 = All(out var ok2, own);
        Check(ok2, $"the instance property beats the key: {r2}");
        ctrl.ClearValue(CvDispCtrl.ToolbarForegroundProperty);
        var r3 = All(out var ok3, keyed);
        Check(ok3, $"clearing the property goes back to the key: {r3}");
        host.Resources.Remove(CvDispCtrl.ToolbarForegroundKey);
        var r4 = All(out var ok4, text);
        Check(ok4, $"removing the key goes back to the inherited text color: {r4}");
        var text2 = Color.FromRgb(0x65, 0x43, 0x21);
        host.SetValue(global::Avalonia.Controls.Documents.TextElement.ForegroundProperty, new SolidColorBrush(text2));
        var r5 = All(out var ok5, text2);
        Check(ok5, $"changing the inherited text color at run time (a theme variant switch does this) repaints the icons: {r5}");

        var pan = FindToggle(ctrl, "Pan")!;
        pan.IsChecked = true;
        Settle(win);
        Check(IconFg(pan) == Colors.White, $"a checked ✋ shows a white icon on its accent ({IconFg(pan)})");
        pan.IsChecked = false;
        Settle(win);
        Check(IconFg(pan) == text2, $"releasing ✋ brings its icon back to the toolbar icon color, not the button theme's ({IconFg(pan)})");

        ctrl.GrabCommand = new NeverCmd();
        var grab = FindButton(ctrl, "Grab")!;
        Settle(win);
        // 비활성을 버튼 자신의 Foreground 로 칠하는 테마 — Fluent 는 안쪽 글자를 직접 칠해 지역 값이 있어도 흐리게 나오므로(대조 없이는 이 절이
        // 고치기 전에도 통과했다), 안쪽을 버튼 Foreground 에 잇는 호스트 스타일을 더해 그 테마 모양을 만든다.
        var dim = Color.FromRgb(0x99, 0x99, 0x99);
        host.Styles.Add(new Style(x => x.OfType<Button>().Class(":disabled")) { Setters = { new Setter(TemplatedControl.ForegroundProperty, new SolidColorBrush(dim)) } });
        host.Styles.Add(new Style(x => x.OfType<Button>().Class(":disabled").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters = { new Setter(ContentPresenter.ForegroundProperty, new TemplateBinding(TemplatedControl.ForegroundProperty)) },
        });
        bare.IsEnabled = false;
        Pseudo(bare, ":pointerover", false);
        Settle(win);
        Check(IconFg(bare) == dim, $"control: a disabled plain button under these styles shows the theme's disabled color ({IconFg(bare)})");
        Check(!grab.IsEnabled && IconFg(grab) == dim,
            $"a disabled toolbar button keeps the theme's disabled color — no local icon color, which would beat the theme's :disabled setter on the button " +
            $"(enabled={grab.IsEnabled}, icon={IconFg(grab)}, want {dim})");
        ctrl.GrabCommand = null;
        Settle(win);
        Check(grab.IsEnabled && IconFg(grab) == text2, $"re-enabled, the button takes the toolbar icon color again ({IconFg(grab)})");
    }

    private sealed class NeverCmd : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => false;
        public void Execute(object? parameter) { }
    }

    private sealed class Cmd(Action run) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
    }
}
