// 시험 공용 도구 — 헤드리스 창에 올려 그린 화면을 바이트로 읽고, 툴바 버튼을 찾고, 자동화 피어로 사람처럼 누른다.
// 화면 바이트는 B,G,R,A 순서로 맞춰 돌려준다 — 헤드리스 프레임 버퍼의 기본 형식은 Rgba8888 이라 그대로 읽으면 빨강과 파랑이 뒤바뀐다.

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace CvInspect.Avalonia.Tests;

/// <summary>그린 한 장 — 장치 화소 W×H 의 B,G,R,A 바이트. S 는 렌더 배율(DIP 하나가 몇 화소인가).</summary>
internal readonly record struct Shot(byte[] Px, int W, int H, double S)
{
    public Color At(int x, int y)
    {
        int i = (y * W + x) * 4;
        return Color.FromRgb(Px[i + 2], Px[i + 1], Px[i]);
    }
}

internal static class AvaTest
{
    // 단언 기록 — 환경 변수 CVI_CHECK_LOG 에 파일 경로를 주면 통과한 단언까지 메시지(잰 값이 들어 있다)를 한 줄씩 남긴다.
    // 픽셀 임계값은 Skia 래스터라이저·OS 글꼴에 따라 달라 CI 러너에서 실제로 잰 값을 봐야 다시 정할 수 있는데, 통과한 시험은
    // 아무 값도 남기지 않는다. CI 가 이 파일을 시험 결과와 함께 올린다.
    private static readonly string? CheckLog = Environment.GetEnvironmentVariable("CVI_CHECK_LOG");
    private static readonly object CheckLogLock = new();

    /// <summary>단언 하나 — 실패 메시지가 곧 그 회귀가 존재하는 이유다.</summary>
    internal static void Check(bool cond, string name)
    {
        if (CheckLog is not null)
        {
            // 기록은 진단 보조다 — 쓰지 못해도(경로 없음 등) 단언 자체는 그대로 판정한다.
            try { lock (CheckLogLock) File.AppendAllText(CheckLog, (cond ? "OK   " : "FAIL ") + name + Environment.NewLine); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Assert.True(cond, name);
    }

    /// <summary>UI 디스패처 큐를 비운다 — 다른 스레드에서 넘어온 갱신, 미뤄 둔 상태 통지, 자동화 피어의 클릭이 여기서 돈다.</summary>
    internal static void Pump() => Dispatcher.UIThread.RunJobs();

    /// <summary>root 를 담은 창 — 이미 담겨 있으면 그 창의 크기·배율만 바꾼다(같은 컨트롤을 다른 크기로 다시 그리는 절이 있다).</summary>
    internal static Window WindowOf(Control root, double w, double h, double scaling = 1.0)
    {
        if (root.Parent is not Window win)
        {
            win = new Window { Width = w, Height = h, SizeToContent = SizeToContent.Manual, Content = root };
            win.Show();
        }
        win.Width = w;
        win.Height = h;
        win.SetRenderScaling(scaling);
        return win;
    }

    /// <summary>배치·미뤄 둔 통지·다시 그리기를 소화한다. 상태 통지는 그리기 뒤에 디스패처로 미뤄지고(그리기 중 통지는 던진다) 그 통지가
    /// 글자를 바꿔 다시 배치를 부르므로 두 번 돈다.</summary>
    internal static void Settle(Window win)
    {
        for (int i = 0; i < 2; i++)
        {
            Pump();
            win.CaptureRenderedFrame();
            Pump();
        }
    }

    /// <summary>root 를 w×h DIP 창에 올려 그린 한 장.</summary>
    internal static Shot Shoot(Control root, double w, double h, double scaling = 1.0)
    {
        var win = WindowOf(root, w, h, scaling);
        Settle(win);
        return Capture(win);
    }

    /// <summary>창이 지금 그리는 한 장을 B,G,R,A 로 읽는다. 행 보폭은 버퍼가 정한다(폭×4 보다 클 수 있다).</summary>
    internal static Shot Capture(Window win)
    {
        var bmp = win.CaptureRenderedFrame() ?? throw new InvalidOperationException("the headless window returned no frame");
        using var fb = bmp.Lock();
        int w = fb.Size.Width, h = fb.Size.Height;
        var row = new byte[fb.RowBytes];
        var px = new byte[w * h * 4];
        bool rgba = fb.Format == PixelFormat.Rgba8888;
        for (int y = 0; y < h; y++)
        {
            Marshal.Copy(fb.Address + y * fb.RowBytes, row, 0, fb.RowBytes);
            for (int x = 0; x < w; x++)
            {
                int s = x * 4, d = (y * w + x) * 4;
                px[d] = rgba ? row[s + 2] : row[s];
                px[d + 1] = row[s + 1];
                px[d + 2] = rgba ? row[s] : row[s + 2];
                px[d + 3] = row[s + 3];
            }
        }
        return new Shot(px, w, h, win.RenderScaling);
    }

    /// <summary>바이트만 — 크기가 같은 두 화면을 바이트로 견주는 절에 쓴다.</summary>
    internal static byte[] Render(Control root, int w, int h) => Shoot(root, w, h).Px;

    /// <summary>툴바 버튼을 툴팁 문구로 찾는다(버튼은 내부에서 만들어져 이름이 없다 — 툴팁은 번역하지 않는 고정 영어다).</summary>
    internal static T? FindByTip<T>(Visual root, string tip) where T : Button
        => root.GetVisualDescendants().OfType<T>().FirstOrDefault(b => b.GetType() == typeof(T) && Equals(ToolTip.GetTip(b), tip));

    internal static Button? FindButton(Visual root, string tip) => FindByTip<Button>(root, tip);

    internal static ToggleButton? FindToggle(Visual root, string tip) => FindByTip<ToggleButton>(root, tip);

    /// <summary>사람의 클릭 — 자동화 피어는 회색 버튼이면 <see cref="ElementNotEnabledException"/> 을 던진다. 큐까지 비운다.</summary>
    internal static Exception? PeerClick(Button b)
    {
        var ex = Record.Exception(() => ((IInvokeProvider)new ButtonAutomationPeer(b)).Invoke());
        Pump();
        return ex;
    }

    /// <summary>사람의 토글 — 회색이면 던진다.</summary>
    internal static Exception? PeerToggle(ToggleButton t)
    {
        var ex = Record.Exception(() => ((IToggleProvider)new ToggleButtonAutomationPeer(t)).Toggle());
        Pump();
        return ex;
    }

    /// <summary>회색 여부를 건너뛰고 클릭 처리기에 바로 닿는 클릭 — 버튼이 회색이 되기 전에 이미 들어와 있던 클릭이 지나는 길이다.</summary>
    internal static void RawClick(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    internal static string Name(Exception? ex) => ex?.GetType().Name ?? "nothing";

    internal static Color ColorOf(IBrush? b) => b is ISolidColorBrush s ? s.Color : Colors.Transparent;
}
