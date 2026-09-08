// CvDispSurface 의 마우스 조작 — 휠 줌, 팬 드래그, 편집 도형 히트 판정과
// 이동·리사이즈·회전 드래그 처리.

using System.Windows.Input;
using CvInspect.Vision.Edit;

namespace CvInspect.Controls;

internal sealed partial class CvDispSurface
{
    private const double HitScreenPx = 8;      // 선/핸들 히트 판정 화면 여유

    private enum DragMode { None, Pan, MoveShape, Handle }

    private DragMode _drag;
    private CvEditShape? _dragShape;
    private int _dragHandle;
    private System.Windows.Point _dragStartScreen;
    private (double A, double B, double C, double D) _dragStartVals;   // 도형 시작 값 스냅샷
    private double _dragStartAngle;                                    // 사각 시작 회전각
    private (double X, double Y) _dragStartCenter;                     // 사각 시작 중심(이미지)

    // === 인터랙션 ===

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_bitmap is null) return;
        var pos = e.GetPosition(this);
        var factor = e.Delta > 0 ? 1.25 : 1 / 1.25;
        var next = Math.Clamp(Scale * factor, MinScale, MaxScale);
        factor = next / Scale;
        if (Math.Abs(factor - 1) < 1e-9) return;
        var m = _view;
        m.ScaleAt(factor, factor, pos.X, pos.Y);
        _view = m;
        InvalidateVisual();
        StatusChanged?.Invoke();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_bitmap is null) return;
        var screen = e.GetPosition(this);
        _dragStartScreen = screen;

        // 팬 모드면 도형 히트 무시하고 화면 이동. 편집 모드는 1) 핸들 → 2) 도형 몸체만 —
        // 빈 곳 드래그는 무동작 (의도치 않은 화면 이동 방지 — 팬은 ✋ 토글로만).
        if (!IsPanMode && _shapes is not null)
        {
            for (var i = _shapes.Count - 1; i >= 0; i--)
            {
                var shape = _shapes[i];
                var handle = HitHandle(shape, screen);
                if (handle >= 0)
                {
                    StartDrag(DragMode.Handle, shape, handle);
                    CaptureMouse();
                    e.Handled = true;
                    return;
                }
            }
            // 몸체 히트 — 겹치면 면적이 가장 작은 도형 우선. Search 가 Train 을 감싸는 중첩에서
            // 안쪽 작은 도형을 내부 드래그로 집을 수 있게 (큰 도형은 겹치지 않는 바깥 영역으로).
            CvEditShape? bodyHit = null;
            var bodyArea = double.MaxValue;
            foreach (var shape in _shapes)
            {
                if (!HitBody(shape, screen)) continue;
                var area = BodyArea(shape);
                if (area < bodyArea)
                {
                    bodyArea = area;
                    bodyHit = shape;
                }
            }
            if (bodyHit is not null)
            {
                StartDrag(DragMode.MoveShape, bodyHit, -1);
                CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        if (!IsPanMode) return;

        _drag = DragMode.Pan;
        CaptureMouse();
        e.Handled = true;
    }

    /// <summary>도형 몸체 면적 — 중첩 히트 시 작은 도형 우선 선택 기준. 선분은 몸체가 얇아 항상 최우선.</summary>
    private static double BodyArea(CvEditShape shape) => shape switch
    {
        CvEditRect r => r.W * r.H,
        CvEditCircle c => Math.PI * c.Radius * c.Radius,
        CvEditArc a => Math.PI * a.Radius * a.Radius,
        CvEditRing g => Math.PI * (g.RMax * g.RMax - g.RMin * g.RMin),
        CvEditSeg => 0,
        _ => double.MaxValue,
    };

    private void StartDrag(DragMode mode, CvEditShape shape, int handle)
    {
        _drag = mode;
        _dragShape = shape;
        _dragHandle = handle;
        _dragStartVals = shape switch
        {
            CvEditRect r => (r.X, r.Y, r.W, r.H),
            CvEditSeg s => (s.X1, s.Y1, s.X2, s.Y2),
            CvEditCircle c => (c.CenterX, c.CenterY, c.Radius, 0),
            CvEditArc a => (a.CenterX, a.CenterY, a.Radius, 0),
            CvEditRing g => (g.CenterX, g.CenterY, g.RMin, g.RMax),
            _ => default,
        };
        if (shape is CvEditRect rect)
        {
            _dragStartAngle = rect.AngleDeg;
            _dragStartCenter = RectCenter(rect);
        }
        if (shape is CvEditArc arc)
        {
            _dragStartArc = (arc.StartDeg, arc.SpanDeg);
            _dragStartCenter = (arc.CenterX, arc.CenterY);
        }
        if (shape is CvEditCircle circ)
        {
            _dragStartCenter = (circ.CenterX, circ.CenterY);
        }
        if (shape is CvEditRing ring)
        {
            _dragStartCenter = (ring.CenterX, ring.CenterY);
        }
    }

    /// <summary>원호 드래그 시작 스냅샷 — (시작각, 스팬). 시작/끝 그립이 반대편 각을 고정한 채 갱신하는 기준.</summary>
    private (double Start, double Span) _dragStartArc;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag == DragMode.None) return;
        var screen = e.GetPosition(this);

        if (_drag == DragMode.Pan)
        {
            var m = _view;
            m.Translate(screen.X - _dragStartScreen.X, screen.Y - _dragStartScreen.Y);
            _view = m;
            _dragStartScreen = screen;
            InvalidateVisual();
            return;
        }

        if (_dragShape is null) return;
        var dImg = ToImage(screen);
        var dStart = ToImage(_dragStartScreen);
        var dx = dImg.X - dStart.X;
        var dy = dImg.Y - dStart.Y;
        var (a, b, c, d) = _dragStartVals;

        switch (_dragShape)
        {
            case CvEditRect r when _drag == DragMode.MoveShape:
                r.Set(a + dx, b + dy, c, d);   // 각도 유지 — Set 은 위치/크기만
                break;
            case CvEditRect r when _dragHandle == 4:
            {
                // 회전 그립 — 중심 기준 각도 (그립 방향 = 로컬 −y = 각도 −90°)
                r.AngleDeg = Math.Atan2(dImg.Y - _dragStartCenter.Y, dImg.X - _dragStartCenter.X) * 180.0 / Math.PI + 90.0;
                break;
            }
            case CvEditRect r:
            {
                // 핸들 0..3 = 좌상/우상/우하/좌하 — 드래그 시작 시점의 로컬 프레임(중심/각 고정)에서
                // 반대편 모서리를 고정한 리사이즈 후 이미지 공간으로 환원 (각도 유지).
                var rad = -_dragStartAngle * Math.PI / 180.0;
                var cos = Math.Cos(rad);
                var sin = Math.Sin(rad);
                var ddx = dImg.X - _dragStartCenter.X;
                var ddy = dImg.Y - _dragStartCenter.Y;
                var px = cos * ddx - sin * ddy;
                var py = sin * ddx + cos * ddy;

                var (sxSign, sySign) = _dragHandle switch { 0 => (-1, -1), 1 => (1, -1), 2 => (1, 1), _ => (-1, 1) };
                var ox = -sxSign * c / 2.0;
                var oy = -sySign * d / 2.0;
                var nw = Math.Max(2, Math.Abs(px - ox));
                var nh = Math.Max(2, Math.Abs(py - oy));
                var ncx = (px + ox) / 2.0;
                var ncy = (py + oy) / 2.0;

                var fwd = _dragStartAngle * Math.PI / 180.0;
                var fc = Math.Cos(fwd);
                var fs = Math.Sin(fwd);
                var cxImg = _dragStartCenter.X + fc * ncx - fs * ncy;
                var cyImg = _dragStartCenter.Y + fs * ncx + fc * ncy;
                r.Set(cxImg - nw / 2, cyImg - nh / 2, nw, nh);
                break;
            }
            case CvEditSeg s when _drag == DragMode.MoveShape:
                s.Set(a + dx, b + dy, c + dx, d + dy);
                break;
            case CvEditSeg s when _dragHandle == 0:
                s.Set(a + dx, b + dy, c, d);
                break;
            case CvEditSeg s:
                s.Set(a, b, c + dx, d + dy);
                break;
            case CvEditCircle circ when _drag == DragMode.MoveShape:
                circ.Set(a + dx, b + dy, c);
                break;
            case CvEditCircle circ:
            {
                // 반경 그립 — 중심 거리
                var dist = Math.Sqrt((dImg.X - _dragStartCenter.X) * (dImg.X - _dragStartCenter.X)
                                     + (dImg.Y - _dragStartCenter.Y) * (dImg.Y - _dragStartCenter.Y));
                circ.Radius = dist;
                break;
            }
            case CvEditArc arc when _drag == DragMode.MoveShape:
                arc.Set(a + dx, b + dy, c, _dragStartArc.Start, _dragStartArc.Span);
                break;
            case CvEditArc arc when _dragHandle == 0:
            {
                // 시작각 그립 — 끝각 고정: 새 시작각으로 회전하고 스팬 = 끝 − 새 시작 (mod 360, 0 → 360)
                var newStart = Math.Atan2(dImg.Y - _dragStartCenter.Y, dImg.X - _dragStartCenter.X) * 180.0 / Math.PI;
                var endDeg = _dragStartArc.Start + _dragStartArc.Span;
                var span = ((endDeg - newStart) % 360 + 360) % 360;
                arc.Set(a, b, c, newStart, span < 5 ? 360 : span);
                break;
            }
            case CvEditArc arc when _dragHandle == 1:
            {
                // 스팬 끝 그립 — 시작각 고정: 스팬 = 새 끝 − 시작 (mod 360, 0 → 360)
                var newEnd = Math.Atan2(dImg.Y - _dragStartCenter.Y, dImg.X - _dragStartCenter.X) * 180.0 / Math.PI;
                var span = ((newEnd - _dragStartArc.Start) % 360 + 360) % 360;
                arc.Set(a, b, c, _dragStartArc.Start, span < 5 ? 360 : span);
                break;
            }
            case CvEditArc arc:
            {
                // 반경 그립 — 중심 거리
                var r = Math.Sqrt((dImg.X - _dragStartCenter.X) * (dImg.X - _dragStartCenter.X)
                                  + (dImg.Y - _dragStartCenter.Y) * (dImg.Y - _dragStartCenter.Y));
                arc.Set(a, b, r, _dragStartArc.Start, _dragStartArc.Span);
                break;
            }
            case CvEditRing ring when _drag == DragMode.MoveShape:
                ring.Set(a + dx, b + dy, c, d);
                break;
            case CvEditRing ring:
            {
                // 그립 0=안 반경, 1=바깥 반경 — 중심 거리. 클램프(교차 방지)는 프로퍼티 세터가 담당.
                var dist = Math.Sqrt((dImg.X - _dragStartCenter.X) * (dImg.X - _dragStartCenter.X)
                                     + (dImg.Y - _dragStartCenter.Y) * (dImg.Y - _dragStartCenter.Y));
                if (_dragHandle == 0) ring.RMin = dist;
                else ring.RMax = dist;
                break;
            }
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag == DragMode.None) return;
        _drag = DragMode.None;
        _dragShape = null;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private int HitHandle(CvEditShape shape, System.Windows.Point screen)
    {
        var pts = shape switch
        {
            // 사각 핸들 0..3 = 모서리, 4 = 회전 그립 (IsRotatable 일 때만). 크기 고정 사각은 그립 없음(이동만).
            CvEditRect { IsResizable: false } => Array.Empty<(double, double)>(),
            CvEditRect r => r.IsRotatable
                ? [.. RectCorners(r), RectGrip(r, GripScreenPx / Scale)]
                : RectCorners(r),
            CvEditSeg s => [(s.X1, s.Y1), (s.X2, s.Y2)],
            // 원 핸들 0=반경(우측 0°)
            CvEditCircle cc => [(cc.CenterX + cc.Radius, cc.CenterY)],
            // 원호 핸들 0=시작각, 1=스팬 끝, 2=반경(중간각)
            CvEditArc a => [ArcPoint(a, 0), ArcPoint(a, a.SpanDeg), ArcPoint(a, a.SpanDeg / 2)],
            // 링 핸들 0=안 반경(좌측 180°), 1=바깥 반경(우측 0°)
            CvEditRing g => [(g.CenterX - g.RMin, g.CenterY), (g.CenterX + g.RMax, g.CenterY)],
            _ => Array.Empty<(double, double)>(),
        };
        for (var i = 0; i < pts.Length; i++)
        {
            var p = ToScreen(pts[i].Item1, pts[i].Item2);
            if (Math.Abs(p.X - screen.X) <= HitScreenPx && Math.Abs(p.Y - screen.Y) <= HitScreenPx)
                return i;
        }
        return -1;
    }

    private bool HitBody(CvEditShape shape, System.Windows.Point screen)
    {
        var img = ToImage(screen);
        switch (shape)
        {
            case CvEditRect r:
            {
                var (lx, ly) = ToRectLocal(r, img.X, img.Y);
                return Math.Abs(lx) <= r.W / 2 && Math.Abs(ly) <= r.H / 2;
            }
            case CvEditSeg s:
            {
                var p1 = ToScreen(s.X1, s.Y1);
                var p2 = ToScreen(s.X2, s.Y2);
                return DistToSegment(screen, p1, p2) <= HitScreenPx;
            }
            case CvEditCircle circ:
            {
                // 꽉 찬 영역 — 원 안 어디든 이동 드래그 (사각 몸체 히트와 동일 감각). 그립은 HitHandle 이 선점.
                var dx = img.X - circ.CenterX;
                var dy = img.Y - circ.CenterY;
                return Math.Sqrt(dx * dx + dy * dy) <= circ.Radius + HitScreenPx / Scale;
            }
            case CvEditArc arc:
            {
                // 링(원주 근처) 또는 중심 십자 근처 = 이동. 그립은 HitHandle 이 선점.
                var dx = img.X - arc.CenterX;
                var dy = img.Y - arc.CenterY;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                var tolImg = HitScreenPx / Scale;
                return Math.Abs(dist - arc.Radius) <= tolImg || dist <= tolImg;
            }
            case CvEditRing ring:
            {
                if (!ring.IsMovable) return false;   // 중심 파생(이동 불가) — 반경 그립만 편집
                var dx = img.X - ring.CenterX;
                var dy = img.Y - ring.CenterY;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                var tolImg = HitScreenPx / Scale;
                return Math.Abs(dist - ring.RMin) <= tolImg || Math.Abs(dist - ring.RMax) <= tolImg || dist <= tolImg;
            }
            default:
                return false;
        }
    }

    private static double DistToSegment(System.Windows.Point p, System.Windows.Point a, System.Windows.Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        if (len2 < 1e-9) return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        var qx = a.X + t * dx;
        var qy = a.Y + t * dy;
        return Math.Sqrt((p.X - qx) * (p.X - qx) + (p.Y - qy) * (p.Y - qy));
    }
}
