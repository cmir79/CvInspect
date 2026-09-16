// CvDispCtrl 오프스크린 렌더 실증 — 창 없이 RenderTargetBitmap 으로 그려 픽셀을 센다.
// 각 절은 계약 하나를 못 박는다: Frame 에 ICvPixelSource 를 직접 넣은 렌더가 Mat 경로와 같은지, 대입 뒤 원본
// 배열을 고쳐도 화면이 그대로인지(픽셀은 대입 시점에 백버퍼로 옮겨진다), 행 끝 패딩 버퍼가 제대로 걸리는지,
// 계약을 어긴 값이 예외 대신 빈 화면 + 경고로 끝나는지. WPF 요소는 STA 스레드에서만 만들 수 있어 본문을 STA 로 감싼다.
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
    private static byte[] Render(CvDispCtrl ctrl, int w = VW, int h = VH)
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
}
