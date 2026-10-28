// CvDispCtrl 포인터 입력 — 헤드리스 창에 실제 마우스 통지(누름·끌기·놓기·휠)를 보내 표면이 받는 그대로 잰다.
// 이 판에만 있는 규칙(README "Differences" 8): 휠의 소수 Delta 는 모아 1.0 마다 한 단계 줌하고, 세로 성분이 없는 통지(가로 스크롤)와
// 그 방향으로 더 못 가는 줌 한계의 통지는 처리하지 않고 넘겨 부모 스크롤이 받게 한다.
// 그리고 왼쪽 드래그 중 다른 버튼을 눌러도 드래그를 다시 시작하지 않는다 — 버튼 상태(왼쪽이 눌려 있나)가 아니라 이번 눌림이 왼쪽인지로 본다.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CvInspect.Avalonia.Controls;
using CvInspect.Display;
using CvInspect.Imaging;
using CvInspect.Vision.Edit;
using Xunit;
using static CvInspect.Avalonia.Tests.AvaTest;

namespace CvInspect.Avalonia.Tests;

public class CvDispCtrlInputTests
{
    private static CamFrame GrayFrame(int w, int h)
    {
        var px = new byte[w * h];
        Array.Fill(px, (byte)128);
        return new CamFrame(px, w, h, w, CamPixelFormat.Mono8);
    }

    /// <summary>창 좌표로 본 표면 위 한 점(이미지 좌표 → 화면).</summary>
    private static Point WinAt(Window win, CvDispSurface s, double imgX, double imgY)
    {
        var o = s.TranslatePoint(new Point(0, 0), win) ?? throw new InvalidOperationException("the surface is not in the window");
        var p = s.ImageToScreen(imgX, imgY);
        return new Point(o.X + p.X, o.Y + p.Y);
    }

    [AvaloniaFact]
    public void WheelLeavesHorizontalAndExhaustedNotchesToTheParent()
    {
        var ctrl = new CvDispCtrl { IsToolbarVisible = false, Frame = GrayFrame(400, 300) };
        Shoot(ctrl, 500, 400);
        var win = (Window)ctrl.Parent!;
        var s = ctrl.Surface;
        // 표면이 처리하지 않은 휠만 창까지 올라온다 — 부모 스크롤이 받는 자리다.
        int reachedParent = 0;
        win.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) => { if (!e.Handled) reachedParent++; });
        var p = WinAt(win, s, 200, 150);
        win.MouseMove(p, RawInputModifiers.None);
        Settle(win);
        void Wheel(double dx, double dy)
        {
            win.MouseWheel(p, new Vector(dx, dy), RawInputModifiers.None);
            Settle(win);
        }

        // 대조 — 한 칸은 줌이고 부모로 가지 않는다
        var s0 = s.ViewScale;
        Wheel(0, 1);
        Check(Math.Abs(s.ViewScale / s0 - 1.25) < 1e-9 && reachedParent == 0,
            $"control: one vertical notch zooms x1.25 and is consumed (scale {s0:F4} -> {s.ViewScale:F4}, reached parent {reachedParent}x)");

        // 가로 스크롤 — 줌이 아니다. 부모로 넘기고, 모아 둔 소수는 그대로 둔다(0.6 + 가로 + 0.4 = 한 칸).
        var s1 = s.ViewScale;
        Wheel(0, 0.6);
        Check(s.ViewScale == s1 && reachedParent == 0, $"a fractional notch is accumulated, not zoomed, and still consumed (scale {s.ViewScale:F4}, reached parent {reachedParent}x)");
        Wheel(1, 0);
        Check(s.ViewScale == s1 && reachedParent == 1,
            $"a horizontal-only wheel is not a zoom: the scale stays and the event goes on to the parent scroll viewer (scale {s.ViewScale:F4}, reached parent {reachedParent}x)");
        Wheel(0, 0.4);
        Check(Math.Abs(s.ViewScale / s1 - 1.25) < 1e-9,
            $"the fraction gathered before the horizontal scroll is kept — 0.6 then 0.4 makes one notch (scale {s1:F4} -> {s.ViewScale:F4})");

        // 줌 한계 — 그 방향으로 더 못 가는 통지는 부모로 넘기고, 모아 둔 소수를 버린다. 버리는 것을 재려면 한계에 닿을 때 소수가
        // 남아 있어야 한다: 한계 한 칸 앞에서 0.5 를 모으고 1.0 으로 한계에 닿으면(1.5 → 한 칸, 0.5 남음) 그 0.5 를 들고 한계에 선다.
        // 정수 칸만으로 한계에 가면 남은 소수가 0 이라 "버렸다" 와 "남겼다" 가 같은 화면이 된다(변이로 확인: 버리는 줄을 지워도 초록이었다).
        for (int i = 0; i < 60 && s.ViewScale * CvViewXform.ZoomStep < CvViewXform.MaxScale; i++) Wheel(0, 1);
        Wheel(0, 0.5);
        var nearTop = s.ViewScale;
        Wheel(0, 1);
        Check(nearTop < CvViewXform.MaxScale && s.ViewScale == CvViewXform.MaxScale,
            $"premise: 0.5 gathered one notch below the zoom-in limit, then one notch reaches it with 0.5 left over ({nearTop} -> {s.ViewScale})");
        var before = reachedParent;
        Wheel(0, 1);
        Wheel(0, 0.5);
        Check(reachedParent == before + 2 && s.ViewScale == CvViewXform.MaxScale,
            $"notches past the zoom limit are left to the parent, whole or fractional (reached parent {reachedParent - before}x of 2, scale {s.ViewScale})");
        // 버린 것이 맞는지 — 남겨 뒀다면 +0.5 위에 −1.0 은 −0.5 라 한 칸이 안 찬다
        Wheel(0, -1);
        Check(s.ViewScale < CvViewXform.MaxScale,
            $"the fraction held at the limit was dropped, so the first notch back zooms out at once (scale {s.ViewScale})");
    }

    /// <summary>큰 사각 A 를 끌어 작은 사각 B 위로 가져간 채 다른 버튼을 누른다. 그 눌림을 왼쪽 누름으로 읽으면 그 자리에서 드래그를 새로
    /// 시작한다 — 겹친 곳에서는 작은 몸체가 이기므로 이후 이동이 B 를 끈다. 맞는 동작: 다른 버튼은 무시되고 A 가 끝까지 따라온다.
    /// 두 길로 누른다. 헤드리스 마우스(mouse) — 이 길의 두 번째 버튼 눌림은 표면의 누름 판정 줄을 시험하지 못한다(변이로 확인: 판정을
    /// "왼쪽이 눌려 있나" 로 되돌려도 초록이었다. 마우스 장치가 이미 눌린 버튼이 있을 때 다른 버튼 눌림을 누름이 아니라 이동으로 올리는
    /// 것으로 보인다 — 프레임워크 원문은 확인하지 않았다). 그래서 이 길은 끝 상태만 본다. 누름 통지 직접(direct) — 왼쪽이 눌린 채 다른
    /// 버튼의 누름 통지가 표면에 오는 경우를 만들어 그 줄을 잰다(다른 입력 장치·플랫폼이 그렇게 올릴 수 있다).</summary>
    [AvaloniaTheory]
    [InlineData(MouseButton.Right, false)]
    [InlineData(MouseButton.Middle, false)]
    [InlineData(MouseButton.Right, true)]
    [InlineData(MouseButton.Middle, true)]
    public void ALeftDragIgnoresAnotherButtonPressedMeanwhile(MouseButton other, bool direct)
    {
        var a = new CvEditRect { IsResizable = false };
        a.Set(100, 100, 160, 120);
        var b = new CvEditRect { IsResizable = false };
        b.Set(300, 150, 40, 40);
        var ctrl = new CvDispCtrl { IsToolbarVisible = false, Frame = GrayFrame(400, 400), Shapes = new List<CvEditShape> { a, b } };
        Shoot(ctrl, 500, 500);
        var win = (Window)ctrl.Parent!;
        var s = ctrl.Surface;

        var start = WinAt(win, s, 180, 160);           // A 몸체 가운데
        var over = WinAt(win, s, 320, 170);            // A 를 끌어 온 자리 — B 가 그 아래에 있다
        var end = WinAt(win, s, 330, 180);
        var held = other == MouseButton.Right ? RawInputModifiers.RightMouseButton : RawInputModifiers.MiddleMouseButton;
        win.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
        win.MouseMove(over, RawInputModifiers.LeftMouseButton);
        var movedTo = (a.X, a.Y);
        if (direct)
        {
            var kind = other == MouseButton.Right ? PointerUpdateKind.RightButtonPressed : PointerUpdateKind.MiddleButtonPressed;
            using var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            s.RaiseEvent(new PointerPressedEventArgs(s, pointer, win, over, 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton | held, kind), KeyModifiers.None));
        }
        else win.MouseDown(over, other, RawInputModifiers.LeftMouseButton);
        win.MouseMove(end, RawInputModifiers.LeftMouseButton | held);
        win.MouseUp(end, MouseButton.Left, held);
        win.MouseUp(end, other, RawInputModifiers.None);
        ctrl.ContextMenu?.Close();
        Settle(win);

        Check(Math.Abs(movedTo.X - 240) < 1e-6 && Math.Abs(movedTo.Y - 110) < 1e-6,
            $"premise: the left drag moved A under the pointer, onto B (A at {movedTo}, want (240, 110))");
        Check(Math.Abs(b.X - 300) < 1e-6 && Math.Abs(b.Y - 150) < 1e-6,
            $"pressing {other} during a left drag ({(direct ? "press raised directly" : "headless mouse")}) does not start a new drag — B under the pointer stays put (B at ({b.X}, {b.Y}))");
        Check(Math.Abs(a.X - 250) < 1e-6 && Math.Abs(a.Y - 120) < 1e-6,
            $"...and A keeps following the left drag to its end (A at ({a.X}, {a.Y}), want (250, 120))");
    }
}
