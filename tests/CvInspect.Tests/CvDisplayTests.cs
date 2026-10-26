// 표시 표면의 UI 중립 계층(CvInspect.Display) — 뷰 변환·편집 도형 드래그·편집 기하·렌더 기하의 회귀.
// WPF 없이 도는 계산이라 Linux CI 에서도 같은 회귀가 돈다. 화면 좌표는 DIP 다.
// 판정 경계(±8 DIP 칸, 변 길이 21 DIP, 스팬 5°) 바로 위에는 시험점을 두지 않는다 — 플랫폼 libm 의 끝자리 차이로 결과가 뒤집힐 수 있다.
// 비트 단위 일치(WPF Matrix 대조)는 Windows 스위트(CvInspect.Wpf.Tests/CvViewXformMatrixTests)가 맡는다.
using CvInspect.Display;
using CvInspect.Vision.Edit;
using CvInspect.Vision.Overlay;
using Xunit;

namespace CvInspect.Tests;

public class CvDisplayTests
{
    private const double Tol = 1e-9;

    /// <summary>근사 단언 — 실패 메시지가 곧 그 동작이 있는 이유다.</summary>
    private static void Near(double expected, double actual, string why, double tol = Tol)
        => Assert.True(Math.Abs(expected - actual) <= tol, $"{why} (expected {expected:R}, got {actual:R})");

    private static void NearPt((double X, double Y) expected, (double X, double Y) actual, string why, double tol = Tol)
        => Assert.True(Math.Abs(expected.X - actual.X) <= tol && Math.Abs(expected.Y - actual.Y) <= tol,
            $"{why} (expected {expected}, got {actual})");

    private static CvEditRect Rect(double x, double y, double w, double h, double angle = 0, bool rotatable = false, bool resizable = true, string label = "")
    {
        var r = new CvEditRect { IsRotatable = rotatable, IsResizable = resizable, Label = label };
        r.Set(x, y, w, h);
        r.AngleDeg = angle;
        return r;
    }

    private static CvEditArc Arc(double cx, double cy, double r, double start, double span)
    {
        var a = new CvEditArc();
        a.Set(cx, cy, r, start, span);
        return a;
    }

    /// <summary>누름 → 끌기(여러 점) → 놓기. 누름이 잡았는지 돌려준다.</summary>
    private static bool Drag(CvEditShape[] shapes, CvViewXform view, (double X, double Y) press, params (double X, double Y)[] moves)
    {
        var drag = new CvEditDrag();
        if (!drag.Press(shapes, press, view, isPanMode: false)) return false;
        foreach (var m in moves) drag.MoveTo(m, ref view);
        drag.Release();
        return true;
    }

    // ───────────── CvViewXform ─────────────

    [Fact]
    public void ViewXformRoundTripsScreenAndImage()
    {
        var v = new CvViewXform(2.5, 13, -7);
        var s = v.ToScreen(10, 20);
        NearPt((38, 43), s, "image → screen is scale then offset");
        NearPt((10, 20), v.ToImage(s.X, s.Y), "screen → image undoes image → screen");
        NearPt((-4, 3.2), v.ToImage(3, 1), "screen → image divides out the scale after removing the offset");
    }

    [Fact]
    public void ZoomAtKeepsTheCursorPointFixed()
    {
        var v = new CvViewXform(1.3, 10, 20);
        var under = v.ToImage(100, 50);
        Assert.True(v.TryZoomAt(CvViewXform.ZoomStep, 100, 50, out var n), "a zoom step inside the limits changes the view");
        Near(1.3 * 1.25, n.Scale, "one zoom step multiplies the scale by ZoomStep");
        NearPt((100, 50), n.ToScreen(under.X, under.Y), "the image point under the cursor stays under the cursor (wheel zoom anchors there)");
    }

    [Fact]
    public void ZoomClampsAndReportsNoChangeAtTheLimits()
    {
        Assert.True(new CvViewXform(60, 5, 5).TryZoomAt(1.25, 0, 0, out var up), "zooming toward the cap still changes the view");
        Near(CvViewXform.MaxScale, up.Scale, "the step is cut so the scale lands on the cap, not past it", 1e-12);

        var top = new CvViewXform(CvViewXform.MaxScale, 5, 6);
        Assert.False(top.TryZoomAt(1.25, 100, 100, out var same), "at the cap a zoom-in does nothing, so the caller skips the redraw");
        Assert.Equal((top.Scale, top.OffsetX, top.OffsetY), (same.Scale, same.OffsetX, same.OffsetY));

        var bottom = new CvViewXform(CvViewXform.MinScale, 0, 0);
        Assert.False(bottom.TryZoomAt(1 / 1.25, 100, 100, out _), "at the floor a zoom-out does nothing");
        Assert.True(bottom.TryZoomAt(1.25, 100, 100, out _), "at the floor a zoom-in still works");
    }

    [Fact]
    public void FitCentersTheImageWithAMargin()
    {
        Assert.True(CvViewXform.TryFit(800, 600, 400, 200, out var fit), "a normal surface and image fit");
        Near(1.94, fit.Scale, "fit uses the tighter axis times the 0.97 margin, so edge handles and labels do not touch the surface border");
        Near(12, fit.OffsetX, "the fitted image is centred horizontally");
        Near(106, fit.OffsetY, "the fitted image is centred vertically");

        Assert.True(CvViewXform.TryFit(800, 600, 100000, 100000, out var tiny));
        Near(CvViewXform.MinScale, tiny.Scale, "fit clamps to the zoom floor for a huge image");
        Assert.True(CvViewXform.TryFit(800, 600, 1, 1, out var huge));
        Near(CvViewXform.MaxScale, huge.Scale, "fit clamps to the zoom cap for a one-pixel image");

        Assert.False(CvViewXform.TryFit(5, 600, 400, 200, out var f1), "a surface narrower than 8 DIP is not fitted — the scale would be meaningless");
        Assert.False(CvViewXform.TryFit(800, 5, 400, 200, out _), "a surface shorter than 8 DIP is not fitted");
        Assert.False(CvViewXform.TryFit(800, 600, 0, 200, out var f3), "no image width, nothing to fit");
        Assert.Equal((1.0, 0.0, 0.0), (f1.Scale, f1.OffsetX, f1.OffsetY));
        Assert.Equal((1.0, 0.0, 0.0), (f3.Scale, f3.OffsetX, f3.OffsetY));
    }

    [Fact]
    public void TranslateMovesOnlyTheOffset()
    {
        var t = new CvViewXform(2, 1, 1).Translate(3, -4);
        Assert.Equal((2.0, 4.0, -3.0), (t.Scale, t.OffsetX, t.OffsetY));
    }

    // ───────────── CvEditDrag ─────────────

    [Fact]
    public void RotationGripSetsAngleFromCenterWithoutNormalizing()
    {
        var r = Rect(100, 100, 200, 100, rotatable: true);
        var view = CvViewXform.Identity;
        var grip = CvEditGeom.GripPoints(r, view)[4];
        NearPt((200, 74), grip, "the rotation grip sits 26 DIP above the top edge centre");

        var drag = new CvEditDrag();
        Assert.True(drag.Press([r], grip, view, false), "pressing the grip starts a handle drag");
        drag.MoveTo((300, 150), ref view);
        Near(90, r.AngleDeg, "angle = atan2 from the centre + 90 (the grip points along local −y)");
        drag.MoveTo((100, 150), ref view);
        Near(270, r.AngleDeg, "the angle is not normalized to (−180, 180] — the value is written back to the tool as is");
        Assert.Equal((100.0, 100.0, 200.0, 100.0), (r.X, r.Y, r.W, r.H));
    }

    [Fact]
    public void RotatedCornerResizeKeepsTheOppositeCorner()
    {
        var r = Rect(100, 100, 200, 100, angle: 30, rotatable: true);
        var view = new CvViewXform(2, 30, 40);
        var before = CvEditGeom.RectCorners(r);
        var press = view.ToScreen(before[2].X, before[2].Y);
        Assert.True(Drag([r], view, press, (press.X + 40, press.Y + 20)));
        var after = CvEditGeom.RectCorners(r);
        NearPt(before[0], after[0], "dragging the bottom-right corner keeps the top-left corner fixed", 1e-8);
        NearPt((before[2].X + 20, before[2].Y + 10), after[2], "the dragged corner follows the cursor in image space (40,20 DIP at scale 2)", 1e-8);
        Near(30, r.AngleDeg, "a corner resize keeps the angle");
    }

    [Fact]
    public void EdgeHandlesMoveOneSideByTheDragAmount()
    {
        var view = CvViewXform.Identity;
        // 판정 띠 안쪽 4 DIP 를 누르고 10 DIP 민다 — 변은 커서 자리가 아니라 민 만큼만 움직인다.
        var top = Rect(100, 100, 200, 100);
        Assert.True(Drag([top], view, (200, 104), (200, 94)));
        Assert.True((top.X, top.Y, top.W, top.H) == (100, 90, 200, 110),
            $"the top edge moves by the drag (−10), not to the cursor (y=94): others stay ({top.X},{top.Y},{top.W},{top.H})");

        var right = Rect(100, 100, 200, 100);
        Assert.True(Drag([right], view, (296, 150), (306, 150)));
        Assert.True((right.X, right.Y, right.W, right.H) == (100, 100, 210, 100),
            $"the right edge widens to the right only ({right.X},{right.Y},{right.W},{right.H})");

        var bottom = Rect(100, 100, 200, 100);
        Assert.True(Drag([bottom], view, (200, 196), (200, 206)));
        Assert.True((bottom.X, bottom.Y, bottom.W, bottom.H) == (100, 100, 200, 110),
            $"the bottom edge grows downward only ({bottom.X},{bottom.Y},{bottom.W},{bottom.H})");

        var left = Rect(100, 100, 200, 100);
        Assert.True(Drag([left], view, (104, 150), (94, 150)));
        Assert.True((left.X, left.Y, left.W, left.H) == (90, 100, 210, 100),
            $"the left edge widens to the left only ({left.X},{left.Y},{left.W},{left.H})");
    }

    [Fact]
    public void ShapeDragLeavesTheRedrawToTheShapeEvent()
    {
        var r = Rect(100, 100, 200, 100);
        var view = CvViewXform.Identity;
        var drag = new CvEditDrag();
        var changed = 0;
        r.Changed += (_, _) => changed++;
        Assert.True(drag.Press([r], (200, 150), view, false));
        Assert.False(drag.MoveTo((210, 150), ref view),
            "a shape drag does not report a view change — the shape's Changed event already asks the surface to redraw, a second request would only double the work");
        Assert.True(changed > 0, "the move reached the shape and raised Changed");
        Assert.Equal((1.0, 0.0, 0.0), (view.Scale, view.OffsetX, view.OffsetY));
    }

    // 아래 둘은 고치지 않고 그대로 옮긴 현 동작이다(.claude 설계 문서 「지킨 현 동작」). 바꾸려면 따로 정하고 이 단언도 같이 고친다.

    [Fact]
    public void MinimumSizeClampMovesTheOppositeEdge()
    {
        // 위 변을 아래 변 1 DIP 앞까지 민다 — 높이는 2 로 잘리는데 중심은 자르기 전 값으로 잡혀, 아래 변이 200 → 200.5 로 밀린다.
        var r = Rect(100, 100, 200, 100);
        Assert.True(Drag([r], CvViewXform.Identity, (200, 104), (200, 203)));
        Assert.True((r.X, r.Y, r.W, r.H) == (100, 198.5, 200, 2),
            $"current behaviour: at the 2 DIP minimum the opposite edge moves by half the overshoot instead of staying fixed ({r.X},{r.Y},{r.W},{r.H})");
    }

    [Fact]
    public void ZoomDuringADragRecomputesTheDisplacementAtTheNewScale()
    {
        // 누른 자리를 화면 좌표로 들고 매 끌기마다 지금 뷰로 다시 환산한다 — 누른 뒤 배율이 2 배가 되면 같은 10 DIP 가 5 px 로 읽힌다.
        var r = Rect(100, 100, 200, 100);
        var view = CvViewXform.Identity;
        var drag = new CvEditDrag();
        Assert.True(drag.Press([r], (150, 150), view, false));
        view = new CvViewXform(2, 0, 0);   // 드래그 도중 휠 줌
        drag.MoveTo((160, 150), ref view);
        Assert.True((r.X, r.Y) == (105, 100),
            $"current behaviour: the start point is re-read through the new view, so the shape moves by the new-scale displacement ({r.X},{r.Y})");
    }

    [Fact]
    public void SegmentEndpointsAndBodyDrag()
    {
        var view = CvViewXform.Identity;
        var s0 = new CvEditSeg();
        s0.Set(50, 50, 150, 50);
        Assert.True(Drag([s0], view, (50, 50), (55, 57)));
        Assert.True((s0.X1, s0.Y1, s0.X2, s0.Y2) == (55, 57, 150, 50), "grip 0 moves only the start point");

        var s1 = new CvEditSeg();
        s1.Set(50, 50, 150, 50);
        Assert.True(Drag([s1], view, (150, 50), (160, 40)));
        Assert.True((s1.X1, s1.Y1, s1.X2, s1.Y2) == (50, 50, 160, 40), "grip 1 moves only the end point");

        var body = new CvEditSeg();
        body.Set(50, 50, 150, 50);
        Assert.True(Drag([body], view, (100, 53), (110, 63)), "a press within 8 DIP of the line grabs its body");
        Assert.True((body.X1, body.Y1, body.X2, body.Y2) == (60, 60, 160, 60), "a body drag moves both ends together");
    }

    [Fact]
    public void CircleRadiusGripAndBodyDrag()
    {
        var view = CvViewXform.Identity;
        var c = new CvEditCircle();
        c.Set(200, 200, 50);
        Assert.True(Drag([c], view, (250, 200), (260, 280)));
        Near(100, c.Radius, "the radius grip sets the radius to the cursor's distance from the centre");
        Assert.Equal((200.0, 200.0), (c.CenterX, c.CenterY));

        var m = new CvEditCircle();
        m.Set(200, 200, 50);
        Assert.True(Drag([m], view, (200, 200), (210, 205)), "the filled circle is grabbed anywhere inside");
        Assert.Equal((210.0, 205.0, 50.0), (m.CenterX, m.CenterY, m.Radius));
    }

    [Fact]
    public void ArcStartGripKeepsTheEndAngle()
    {
        var view = CvViewXform.Identity;
        var a = Arc(200, 200, 100, 0, 90);
        var g = CvEditGeom.GripPoints(a, view);
        var p = (200 + 100 * Math.Cos(-Math.PI / 6), 200 + 100 * Math.Sin(-Math.PI / 6));
        Assert.True(Drag([a], view, g[0], p));
        Near(-30, a.StartDeg, "the start grip sets the start angle to the cursor direction");
        Near(120, a.SpanDeg, "the span grows so the end angle (90°) stays where it was");

        var snap = Arc(200, 200, 100, 0, 90);
        var q = (200 + 100 * Math.Cos(88 * Math.PI / 180), 200 + 100 * Math.Sin(88 * Math.PI / 180));
        Assert.True(Drag([snap], view, CvEditGeom.GripPoints(snap, view)[0], q));
        Near(88, snap.StartDeg, "the start follows the cursor");
        Near(360, snap.SpanDeg, "a span under 5° snaps to a full circle — collapsing past the end means 'all the way round', not a 5° sliver");
    }

    [Fact]
    public void ArcEndGripKeepsTheStartAngle()
    {
        var view = CvViewXform.Identity;
        var a = Arc(200, 200, 100, 0, 90);
        var drag = new CvEditDrag();
        Assert.True(drag.Press([a], CvEditGeom.GripPoints(a, view)[1], view, false));
        drag.MoveTo((100, 200), ref view);
        Near(180, a.SpanDeg, "the end grip sets span = end − start");
        Near(0, a.StartDeg, "the end grip keeps the start angle");
        drag.MoveTo((200, 100), ref view);
        Near(270, a.SpanDeg, "an end behind the start wraps mod 360 instead of going negative");
        drag.MoveTo((200 + 100 * Math.Cos(2 * Math.PI / 180), 200 + 100 * Math.Sin(2 * Math.PI / 180)), ref view);
        Near(360, a.SpanDeg, "a span under 5° snaps to a full circle");
        drag.Release();
    }

    [Fact]
    public void ArcRadiusGripAndBodyDrag()
    {
        var view = CvViewXform.Identity;
        var a = Arc(200, 200, 100, 0, 90);
        Assert.True(Drag([a], view, CvEditGeom.GripPoints(a, view)[2], (200, 350)));
        Near(150, a.Radius, "the mid-span grip sets the radius");
        Assert.Equal((0.0, 90.0), (a.StartDeg, a.SpanDeg));

        var m = Arc(200, 200, 100, 0, 90);
        Assert.True(Drag([m], view, (100, 200), (110, 200)), "a press on the circle line grabs the arc body");
        Assert.Equal((210.0, 200.0, 100.0, 0.0, 90.0), (m.CenterX, m.CenterY, m.Radius, m.StartDeg, m.SpanDeg));
    }

    [Fact]
    public void RingRadiusGripsAreClampedByTheSetters()
    {
        var view = CvViewXform.Identity;
        var inner = new CvEditRing();
        inner.Set(200, 200, 40, 120);
        var drag = new CvEditDrag();
        Assert.True(drag.Press([inner], (160, 200), view, false), "the inner grip sits on the left (180°)");
        drag.MoveTo((200, 130), ref view);
        Near(70, inner.RMin, "the inner grip sets the inner radius to the cursor distance");
        drag.MoveTo((200, 0), ref view);
        Near(116, inner.RMin, "the inner radius cannot cross the outer one (outer − 4)");
        drag.Release();

        var outer = new CvEditRing();
        outer.Set(200, 200, 40, 120);
        Assert.True(Drag([outer], view, (320, 200), (230, 200)), "the outer grip sits on the right (0°)");
        Near(44, outer.RMax, "the outer radius cannot cross the inner one (inner + 4)");
        Near(40, outer.RMin, "the outer grip leaves the inner radius");
    }

    [Fact]
    public void FixedRingBodyDoesNotMove()
    {
        var view = CvViewXform.Identity;
        var ring = new CvEditRing();
        ring.Set(200, 200, 40, 120);
        Assert.False(new CvEditDrag().Press([ring], (200, 320), view, false),
            "a ring whose centre is derived from another tool (IsMovable=false) has no body to drag");

        var movable = new CvEditRing { IsMovable = true };
        movable.Set(200, 200, 40, 120);
        Assert.True(Drag([movable], view, (200, 320), (210, 330)), "the same press grabs a movable ring");
        Assert.Equal((210.0, 210.0), (movable.CenterX, movable.CenterY));
    }

    [Fact]
    public void PanModeIgnoresShapesAndMovesTheViewIncrementally()
    {
        var r = Rect(100, 100, 200, 100);
        var view = CvViewXform.Identity;
        var drag = new CvEditDrag();
        Assert.True(drag.Press([r], (200, 150), view, isPanMode: true), "pan mode always starts a drag");
        Assert.True(drag.MoveTo((210, 155), ref view), "a pan move reports that the view changed");
        Assert.Equal((10.0, 5.0), (view.OffsetX, view.OffsetY));
        Assert.True(drag.MoveTo((215, 155), ref view));
        Assert.True((view.OffsetX, view.OffsetY) == (15, 5),
            $"each pan move adds only the step since the last move, not since the press ({view.OffsetX},{view.OffsetY})");
        Assert.Equal((100.0, 100.0, 200.0, 100.0), (r.X, r.Y, r.W, r.H));
        Assert.True(drag.Release());

        Assert.False(new CvEditDrag().Press([r], (500, 500), view, isPanMode: false), "an empty spot in edit mode does not pan");
        Assert.False(new CvEditDrag().Press(null, (500, 500), view, isPanMode: false), "no shapes in edit mode does nothing");
        Assert.True(new CvEditDrag().Press(null, (500, 500), view, isPanMode: true), "pan mode pans even without shapes");
    }

    [Fact]
    public void GripTieGoesToTheLaterShape()
    {
        var a = Rect(100, 100, 200, 100, label: "A");
        var b = Rect(100, 100, 200, 100, label: "B");
        Assert.True(Drag([a, b], CvViewXform.Identity, (100, 100), (90, 90)));
        Assert.True(b.X == 90 && a.X == 100,
            $"the same-place corner of two stacked shapes grabs the later one — it is drawn on top (a.X={a.X}, b.X={b.X})");
    }

    [Fact]
    public void BodyTieGoesToTheEarlierShape()
    {
        var a = Rect(100, 100, 200, 100, label: "A");
        var b = Rect(150, 120, 200, 100, label: "B");
        Assert.True(Drag([a, b], CvViewXform.Identity, (220, 160), (230, 160)));
        Assert.True(a.X == 110 && b.X == 150,
            $"two equal-area bodies under the cursor grab the earlier one (strict '<') (a.X={a.X}, b.X={b.X})");
    }

    [Fact]
    public void CornerHitTakesTheFirstCornerNotTheNearest()
    {
        var r = Rect(100, 100, 10, 10);
        // (106,106) 은 네 모서리 칸 모두 안이다 — 가장 가까운 것은 우하(110,110) 지만 첫 칸(좌상)을 잡는다.
        Assert.True(Drag([r], CvViewXform.Identity, (106, 106), (101, 101)));
        Assert.True((r.X, r.Y, r.W, r.H) == (101, 101, 9, 9),
            $"the first corner in the box (top-left) is grabbed and the bottom-right stays at 110 ({r.X},{r.Y},{r.W},{r.H})");
    }

    [Fact]
    public void EdgeHandleLosesToASmallerBody()
    {
        var view = CvViewXform.Identity;
        var train = Rect(150, 2, 100, 60, resizable: false, label: "Train");
        var search = Rect(0, 0, 400, 300, label: "Search");
        Assert.True(Drag([train, search], view, (200, 5), (210, 15)));
        Assert.True((train.X, train.Y) == (160, 12) && (search.X, search.Y, search.W, search.H) == (0, 0, 400, 300),
            $"a press on the Search top-edge band over the smaller Train body moves Train, not resizes Search (train {train.X},{train.Y}; search {search.Y},{search.H})");

        Assert.True(Drag([train, search], view, (200, -5), (200, -15)));
        Assert.True((search.Y, search.H) == (-10, 310),
            $"the same band outside any body resizes Search (y={search.Y}, h={search.H})");
    }

    [Fact]
    public void HitBoxIsEightDipOnScreen()
    {
        // 배율 2 — 칸은 화면 DIP 로 잰다. 그립 (250,200) 은 화면 (500,400).
        var view = new CvViewXform(2, 0, 0);
        var miss = new CvEditCircle();
        miss.Set(200, 200, 50);
        Assert.False(new CvEditDrag().Press([miss], (508.5, 400), view, false),
            "8.5 DIP right of the radius grip is outside the ±8 DIP box and outside the body");

        var hit = new CvEditCircle();
        hit.Set(200, 200, 50);
        Assert.True(Drag([hit], view, (507.5, 400), (600, 400)), "7.5 DIP from the grip is inside the box");
        Near(100, hit.Radius, "the grab was the radius grip (cursor at image x=300)");
    }

    [Fact]
    public void CancelStopsTheDragAndReleaseReportsIt()
    {
        var view = CvViewXform.Identity;
        var r = Rect(100, 100, 200, 100);
        var drag = new CvEditDrag();
        Assert.False(drag.Release(), "nothing to release before a press");
        Assert.True(drag.Press([r], (200, 150), view, false));
        Assert.True(drag.IsActive);
        drag.Cancel();
        Assert.False(drag.IsActive, "cancel ends the drag when the shape list changes");
        Assert.False(drag.MoveTo((250, 150), ref view), "a move after cancel does nothing");
        Assert.Equal(100, r.X);
        Assert.False(drag.Release(), "release after cancel reports nothing held, so the caller does not release capture twice");

        Assert.True(drag.Press([r], (200, 150), view, false));
        Assert.True(drag.Release(), "release after a press reports the drag it ended");
        Assert.False(drag.Release(), "a second release reports nothing");
    }

    // ───────────── CvEditGeom ─────────────

    [Fact]
    public void EdgeHandlesSkipShortEdges()
    {
        var hs = CvEditGeom.RectEdgeHandles(Rect(0, 0, 23, 20), CvViewXform.Identity).Select(h => h.Handle).ToArray();
        Assert.True(hs.SequenceEqual([5, 7]),
            $"edges shorter than 21 DIP (3 handle sizes) get no edge handle — it would overlap the corner handles ([{string.Join(",", hs)}])");
    }

    [Fact]
    public void EdgeHandlesHideWhenTheMidpointIsInACornerBox()
    {
        var view = CvViewXform.Identity;
        Assert.Empty(CvEditGeom.RectEdgeHandles(Rect(0, 0, 22, 22, angle: 45), view));   // 변 가운데 (7.8, 7.8) — 모서리 칸 안
        Assert.Equal(4, CvEditGeom.RectEdgeHandles(Rect(0, 0, 22, 22), view).Count);
        Assert.Equal(4, CvEditGeom.RectEdgeHandles(Rect(0, 0, 24, 24, angle: 45), view).Count);   // (8.5, 8.5) — 칸 밖
    }

    [Fact]
    public void EdgeHandleLengthIsAThirdCappedAt24()
    {
        var hs = CvEditGeom.RectEdgeHandles(Rect(0, 0, 300, 60), CvViewXform.Identity);
        static double Len((int Handle, (double X, double Y) P1, (double X, double Y) P2) h)
            => Math.Sqrt((h.P2.X - h.P1.X) * (h.P2.X - h.P1.X) + (h.P2.Y - h.P1.Y) * (h.P2.Y - h.P1.Y));
        Near(24, Len(hs.Single(h => h.Handle == 5)), "a long edge gets a fixed 24 DIP handle, not a third of the edge");
        Near(20, Len(hs.Single(h => h.Handle == 6)), "a shorter edge gets a third of its length");
        NearPt((150, 0), ((hs[0].P1.X + hs[0].P2.X) / 2, (hs[0].P1.Y + hs[0].P2.Y) / 2), "the handle is centred on its edge");
    }

    [Fact]
    public void GripPointCountsPerShape()
    {
        var view = CvViewXform.Identity;
        Assert.Equal(5, CvEditGeom.GripPoints(Rect(0, 0, 50, 50, rotatable: true), view).Length);
        Assert.Equal(4, CvEditGeom.GripPoints(Rect(0, 0, 50, 50), view).Length);
        Assert.Empty(CvEditGeom.GripPoints(Rect(0, 0, 50, 50, rotatable: true, resizable: false), view));
        Assert.Equal(2, CvEditGeom.GripPoints(new CvEditSeg(), view).Length);
        Assert.Single(CvEditGeom.GripPoints(new CvEditCircle(), view));
        Assert.Equal(3, CvEditGeom.GripPoints(new CvEditArc(), view).Length);
        Assert.Equal(2, CvEditGeom.GripPoints(new CvEditRing(), view).Length);
    }

    [Fact]
    public void DistToSegmentHandlesDegenerateAndClampedCases()
    {
        Near(5, CvEditGeom.DistToSegment((4, 5), (1, 1), (1, 1)), "a zero-length segment measures to its point instead of dividing by zero");
        Near(3, CvEditGeom.DistToSegment((5, 3), (0, 0), (10, 0)), "inside the span the distance is perpendicular");
        Near(5, CvEditGeom.DistToSegment((13, 4), (0, 0), (10, 0)), "past the end the distance is to the end point");
    }

    // ───────────── ViDrawGeom ─────────────

    [Fact]
    public void ResolveStrokeMapsOddValuesToOneAndClampsTheRest()
    {
        Assert.Equal(1.0, ViDrawGeom.ResolveStroke(0));
        Assert.Equal(1.0, ViDrawGeom.ResolveStroke(-1));
        Assert.Equal(1.0, ViDrawGeom.ResolveStroke(double.NaN));
        Assert.Equal(1.0, ViDrawGeom.ResolveStroke(double.PositiveInfinity));
        Assert.Equal(1.0, ViDrawGeom.ResolveStroke(double.NegativeInfinity));
        Assert.True(ViDrawGeom.ResolveStroke(1e-6) == 0.25, "a tiny positive scale is raised to 0.25 — the line must not vanish just because 0 maps to 1");
        Assert.Equal(0.25, ViDrawGeom.ResolveStroke(0.25));
        Assert.Equal(50.0, ViDrawGeom.ResolveStroke(50));
        Assert.True(ViDrawGeom.ResolveStroke(100) == 50, "a huge finite scale is capped at 50 so the thickness and arrow coordinates stay finite");
    }

    [Fact]
    public void LabelDipClampsBeforeConverting()
    {
        Assert.Equal(0.5 * 96.0 / 72.0, ViDrawGeom.LabelDip(float.NaN));
        Assert.True(ViDrawGeom.LabelDip(0f) == 0.5 * 96.0 / 72.0, "a zero size would throw in the text renderer, so it is raised to 0.5 pt");
        Assert.Equal(0.5 * 96.0 / 72.0, ViDrawGeom.LabelDip(-5f));
        Assert.Equal(16.0, ViDrawGeom.LabelDip(12f));
        Assert.True(ViDrawGeom.LabelDip(1e30f) == 26000.0 * 96.0 / 72.0, "an extreme size is cut to 26000 pt, below the renderer's limit");
        Assert.Equal(26000.0 * 96.0 / 72.0, ViDrawGeom.LabelDip(float.PositiveInfinity));
    }

    [Fact]
    public void LabelSizeIgnoresTheScaleOnlyForCornerHuds()
    {
        Assert.True(ViDrawGeom.LabelSizePt(new ViOverlayLabel { IsHud = true, Align = ViOverlayAlign.TopLeft, FontSize = 20 }, 3) == 20f,
            "a corner HUD keeps its own size — it is a fixed summary block, not geometry");
        Assert.True(ViDrawGeom.LabelSizePt(new ViOverlayLabel { IsHud = true, Align = ViOverlayAlign.TopCenter, FontSize = 20 }, 3) == 60f,
            "a HUD flag without a corner alignment is not a corner HUD, so the label scale applies");
        Assert.True(ViDrawGeom.LabelSizePt(new ViOverlayLabel { Align = ViOverlayAlign.BottomRight, FontSize = 20 }, 3) == 60f,
            "a corner alignment alone (label on a box) is scaled");
    }

    [Fact]
    public void LabelOriginResolvesEveryAlignment()
    {
        var anchor = (100.0, 50.0);
        Assert.Equal((80.0, 50.0), ViDrawGeom.LabelOrigin(ViOverlayAlign.TopCenter, anchor, 40, 20));
        Assert.Equal((80.0, 30.0), ViDrawGeom.LabelOrigin(ViOverlayAlign.BottomCenter, anchor, 40, 20));
        Assert.Equal((100.0, 50.0), ViDrawGeom.LabelOrigin(ViOverlayAlign.TopLeft, anchor, 40, 20));
        Assert.Equal((100.0, 30.0), ViDrawGeom.LabelOrigin(ViOverlayAlign.BottomLeft, anchor, 40, 20));
        Assert.Equal((60.0, 50.0), ViDrawGeom.LabelOrigin(ViOverlayAlign.TopRight, anchor, 40, 20));
        Assert.Equal((60.0, 30.0), ViDrawGeom.LabelOrigin(ViOverlayAlign.BottomRight, anchor, 40, 20));
        Assert.Equal(6, Enum.GetValues<ViOverlayAlign>().Length);   // 정렬이 늘면 위 표에 더한다
        Assert.True(ViDrawGeom.LabelOrigin((ViOverlayAlign)99, anchor, 40, 20) == (80, 50),
            "an unknown alignment falls back to top-centre instead of throwing in the render pass");
    }

    [Fact]
    public void ArrowHeadScalesAndShrinksOnShortSegments()
    {
        Assert.Null(ViDrawGeom.ArrowHead((0, 0), (1.5, 0)));
        Assert.Null(ViDrawGeom.ArrowHead((3, 3), (3, 3)));

        var h = ViDrawGeom.ArrowHead((0, 0), (100, 0))!.Value;
        NearPt((100, 0), h.Tip, "the tip is the end point");
        NearPt((88, 4.5), h.Left, "the default head is 12 long and 4.5 to each side");
        NearPt((88, -4.5), h.Right, "the wings are mirrored");

        var big = ViDrawGeom.ArrowHead((0, 0), (100, 0), 2)!.Value;
        NearPt((76, 9), big.Left, "the head grows with the stroke scale so a thick line does not bury it");

        var shortHead = ViDrawGeom.ArrowHead((0, 0), (6, 0), 2)!.Value;
        NearPt((0, 2.25), shortHead.Left, "a head longer than the segment is shrunk to the segment length, keeping its proportions");
    }

    [Fact]
    public void CirclePointsCloseTheLoop()
    {
        var view = new CvViewXform(2, 1, 1);
        var pts = ViDrawGeom.CirclePoints(10, 20, 5, view);
        Assert.Equal(ViDrawGeom.CircleSegs + 1, pts.Length);
        NearPt((31, 41), pts[0], "the circle starts at 0° (right)");
        NearPt(pts[0], pts[^1], "the last point returns to the first so the polyline closes");
        NearPt((21, 51), pts[ViDrawGeom.CircleSegs / 4], "a quarter of the way round is 90° (down in image space)");
    }

    [Fact]
    public void ArcPointsSegmentBySpanWithAMinimum()
    {
        var view = CvViewXform.Identity;
        Assert.Equal(13, ViDrawGeom.ArcPoints(Arc(0, 0, 10, 0, 30), view).Length);    // 30/5 = 6 → 최소 12 분할
        Assert.Equal(19, ViDrawGeom.ArcPoints(Arc(0, 0, 10, 0, 90), view).Length);    // 5° 마다 한 분할
        Assert.Equal(73, ViDrawGeom.ArcPoints(Arc(0, 0, 10, 0, 360), view).Length);

        var a = Arc(200, 200, 100, 30, 120);
        var pts = ViDrawGeom.ArcPoints(a, view);
        NearPt(CvEditGeom.ArcPoint(a, 0), pts[0], "the polyline starts at the start angle");
        NearPt(CvEditGeom.ArcPoint(a, 120), pts[^1], "the polyline ends at the span end, where the arrow head goes");
    }
}
