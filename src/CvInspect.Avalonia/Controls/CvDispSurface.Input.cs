// CvDispSurface 의 포인터 조작 — Avalonia 포인터 이벤트를 받아 UI 중립 계층(CvEditDrag·CvViewXform)에 넘긴다.
// 누름 판정·끌기 적용의 계산은 전부 CvInspect.Display 에 있다. 여기는 포인터 잡기·휠 누적·다시 그리기·상태 통지만.

using Avalonia;
using Avalonia.Input;
using CvInspect.Display;

namespace CvInspect.Avalonia.Controls;

internal sealed partial class CvDispSurface
{
    private readonly CvEditDrag _editDrag = new();

    // 휠 누적 — 마우스 휠은 한 칸에 1.0 이 오지만 터치패드·정밀 휠은 소수 Delta 가 여러 번 온다. 통지마다 한 단계씩 줌하면
    // 터치패드 한 번 쓸기가 수십 단계가 되므로 모아서 1.0 마다 한 단계(ZoomStep)씩 한다. 반대 방향이 오면 모인 값에서 깎인다.
    private double _wheelAcc;

    // 누적 비교의 부동소수 여유 — +0.4 세 번(한 칸 소비), −1.0, −0.2 를 보내면 누적이 −0.9999999999999998 이 되어,
    // 여유가 없으면 −1 칸이 한 통지 늦게 찬다(헤드리스 스모크 실측).
    private const double WheelEps = 1e-9;

    // === 인터랙션 ===

    // 규칙: 줌으로 쓴 입력만 Handled 를 세운다. 세로 성분이 없는 통지(가로 스크롤)와, 그 방향으로 더 못 가는 줌 한계에서의
    // 통지는 누적을 버리고 Handled 없이 넘긴다 — 부모 스크롤이 받게. 한계에서 소수는 삼키고 정수 칸은 내보내는 식의 어긋남을 없앤다.
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_bitmap is null) return;
        var dy = e.Delta.Y;
        if (dy == 0) return;   // 가로 스크롤 — 줌 입력이 아니다. 누적도 건드리지 않는다
        var pos = e.GetPosition(this);
        if (!_view.TryZoomAt(CvViewXform.WheelFactor(dy), pos.X, pos.Y, out _))
        {
            _wheelAcc = 0;   // 이 방향 끝값이다 — 모인 값을 들고 있으면 반대로 돌아올 때 첫 칸이 늦는다
            return;
        }
        _wheelAcc += dy;
        var steps = (int)Math.Truncate(_wheelAcc + Math.CopySign(WheelEps, _wheelAcc));   // 0 쪽으로 자른다 — 남은 소수는 다음 통지로 넘긴다
        e.Handled = true;   // 이 방향으로 줌이 가능하니 한 칸이 안 찼어도 줌 입력이다 — 바깥 스크롤로 새지 않게
        if (steps == 0) return;
        _wheelAcc -= steps;
        if (Math.Abs(_wheelAcc) < WheelEps) _wheelAcc = 0;
        var factor = CvViewXform.WheelFactor(steps);
        var moved = false;
        for (var i = Math.Abs(steps); i > 0; i--)
        {
            if (!_view.TryZoomAt(factor, pos.X, pos.Y, out var next))
            {
                _wheelAcc = 0;   // 가는 도중 끝값에 닿았다 — 남은 칸과 소수를 버린다
                break;
            }
            _view = next;
            moved = true;
        }
        if (!moved) return;
        InvalidateVisual();
        RaiseStatus();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // 이번 눌림이 왼쪽인지로 판정한다 — 현재 상태(IsLeftButtonPressed)로 보면 왼쪽 드래그 중 다른 버튼 눌림도 참이라 드래그를 다시 시작한다.
        // 우클릭은 놓을 때 메뉴 요청으로 올라가야 한다 — 건드리지 않는다.
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;
        if (!PressAt(e.GetPosition(this))) return;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        DragTo(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        // 기본 처리가 먼저 — 우클릭이고 처리되지 않았으면 여기서 메뉴 요청(ContextRequested)이 올라간다. 그래서 우클릭에서는 Handled 를 세우지 않는다.
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (!ReleaseDrag()) return;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        // 놓기 전에 캡처를 잃으면(창 전환·다른 요소가 가져감) 놓기 통지가 안 온다 — 드래그를 여기서 끝내 다음 이동이 옛 드래그를 잇지 않게.
        // 이미 반영된 도형 변경은 그대로 둔다(놓기와 같은 끝). 우리가 놓기에서 캡처를 풀 때도 여기로 오지만 그때는 드래그가 이미 없다.
        ReleaseDrag();
    }

    /// <summary>이미지 좌표 → 화면 좌표 — 회귀가 핸들 자리를 찾는 데 쓴다.</summary>
    internal Point ImageToScreen(double x, double y) => ToScreen(x, y);

    /// <summary>왼쪽 누름 — 잡을 것이 있으면(핸들·도형 몸체·팬 모드) 드래그를 시작하고 true(호출자가 포인터를 잡는다).
    /// 포인터 장치와 떼어 둔 것은 회귀가 좌표로 부르기 위해서다.</summary>
    internal bool PressAt(Point screen)
        => _bitmap is not null && _editDrag.Press(_shapeSub.Shapes, (screen.X, screen.Y), _view, IsPanMode);

    /// <summary>끌기 — 진행 중인 드래그(팬·이동·핸들)를 이 화면 좌표까지. 드래그가 없으면 무동작.</summary>
    internal void DragTo(Point screen)
    {
        if (_editDrag.MoveTo((screen.X, screen.Y), ref _view)) InvalidateVisual();   // 팬 — 도형 변경은 도형의 Changed 가 다시 그린다
    }

    /// <summary>놓기 — 드래그 중이었으면 끝내고 true(호출자가 포인터를 놓는다).</summary>
    internal bool ReleaseDrag() => _editDrag.Release();
}
