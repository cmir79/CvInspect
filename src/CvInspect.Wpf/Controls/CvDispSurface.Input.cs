// CvDispSurface 의 마우스 조작 — WPF 마우스 이벤트를 받아 UI 중립 계층(CvEditDrag·CvViewXform)에 넘긴다.
// 누름 판정·끌기 적용의 계산은 전부 CvInspect.Display 에 있다. 여기는 마우스 잡기·다시 그리기·상태 통지만.

using System.Windows.Input;
using CvInspect.Display;

namespace CvInspect.Controls;

internal sealed partial class CvDispSurface
{
    private readonly CvEditDrag _editDrag = new();

    // === 인터랙션 ===

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_bitmap is null) return;
        var pos = e.GetPosition(this);
        var factor = e.Delta > 0 ? CvViewXform.ZoomStep : 1 / CvViewXform.ZoomStep;
        if (!_view.TryZoomAt(factor, pos.X, pos.Y, out var next)) return;
        _view = next;
        InvalidateVisual();
        StatusChanged?.Invoke();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!PressAt(e.GetPosition(this))) return;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e) => DragTo(e.GetPosition(this));

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!ReleaseDrag()) return;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <summary>이미지 좌표 → 화면 좌표 — 회귀가 핸들 자리를 찾는 데 쓴다.</summary>
    internal System.Windows.Point ImageToScreen(double x, double y) => ToScreen(x, y);

    /// <summary>왼쪽 누름 — 잡을 것이 있으면(핸들·도형 몸체·팬 모드) 드래그를 시작하고 true(호출자가 마우스를 잡는다).
    /// 마우스 장치와 떼어 둔 것은 회귀가 좌표로 부르기 위해서다.</summary>
    internal bool PressAt(System.Windows.Point screen)
        => _bitmap is not null && _editDrag.Press(_shapes, (screen.X, screen.Y), _view, IsPanMode);

    /// <summary>끌기 — 진행 중인 드래그(팬·이동·핸들)를 이 화면 좌표까지. 드래그가 없으면 무동작.</summary>
    internal void DragTo(System.Windows.Point screen)
    {
        if (_editDrag.MoveTo((screen.X, screen.Y), ref _view)) InvalidateVisual();   // 팬 — 도형 변경은 도형의 Changed 가 다시 그린다
    }

    /// <summary>놓기 — 드래그 중이었으면 끝내고 true(호출자가 마우스를 놓는다).</summary>
    internal bool ReleaseDrag() => _editDrag.Release();
}
