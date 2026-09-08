// CvPose 회귀 — 픽스처 변환의 대수: 역포즈가 되돌리고, 합성이 차례 적용과 같고, 항등 포즈가 아무것도 안 옮기는지.
// 수치는 회전·비등방 아닌 등방 스케일·이동을 전부 섞어 둔다 — 하나라도 빠지면 부호 실수가 숨는다.
using CvInspect.Vision;
using Xunit;

namespace CvInspect.Tests;

public class CvPoseTests
{
    private static void Check(bool cond, string name) => Assert.True(cond, name);
    private static bool Near((double X, double Y) a, (double X, double Y) b, double tol = 1e-9)
        => Math.Abs(a.X - b.X) < tol && Math.Abs(a.Y - b.Y) < tol;

    private static readonly (double X, double Y)[] Points = [(0, 0), (123.4, -45.6), (100, 50), (310, 220), (-7.5, 999)];

    [Fact]
    public void InverseUndoesApply()
    {
        var pose = new CvPose(ThetaDeg: 37.5, OriginX: 100, OriginY: 50, FoundX: 310, FoundY: 220, Score: 0.91, Scale: 1.25);
        var inv = pose.Inverse();
        foreach (var p in Points)
        {
            var run = pose.Apply(p.X, p.Y);
            Check(Near(inv.Apply(run.X, run.Y), p), $"Inverse().Apply undoes Apply for {p}: got {inv.Apply(run.X, run.Y)}");
            Check(Near(pose.Apply(inv.Apply(p.X, p.Y).X, inv.Apply(p.X, p.Y).Y), p), $"Apply undoes Inverse().Apply for {p}");
        }
        Check(Math.Abs(inv.Scale - 0.8) < 1e-12 && Math.Abs(inv.ThetaDeg + 37.5) < 1e-12 && inv.Score == pose.Score,
            $"inverse has 1/Scale, -Theta and the same Score: scale={inv.Scale} theta={inv.ThetaDeg}");
        var back = inv.Inverse();
        Check(Math.Abs(back.Scale - pose.Scale) < 1e-12 && Math.Abs(back.ThetaDeg - pose.ThetaDeg) < 1e-12
              && back.OriginX == pose.OriginX && back.FoundX == pose.FoundX, "Inverse().Inverse() is the original pose");
    }

    [Fact]
    public void ComposeEqualsApplyingInOrder()
    {
        var a = new CvPose(ThetaDeg: 12, OriginX: 40, OriginY: 30, FoundX: 200, FoundY: 90, Score: 0.95, Scale: 1.1);
        var b = new CvPose(ThetaDeg: -50, OriginX: 250, OriginY: 60, FoundX: 20, FoundY: 400, Score: 0.80, Scale: 0.7);
        var ab = a.Compose(b);
        foreach (var p in Points)
        {
            var stepwise = b.Apply(a.Apply(p.X, p.Y).X, a.Apply(p.X, p.Y).Y);
            Check(Near(ab.Apply(p.X, p.Y), stepwise, 1e-9), $"a.Compose(b).Apply == b.Apply(a.Apply) for {p}: {ab.Apply(p.X, p.Y)} vs {stepwise}");
        }
        Check(Math.Abs(ab.Scale - 0.77) < 1e-12 && Math.Abs(ab.ThetaDeg - (-38)) < 1e-12 && ab.Score == 0.80,
            $"composed scale is the product, angle the sum, score the weaker: scale={ab.Scale} theta={ab.ThetaDeg} score={ab.Score}");

        // 합성 뒤 역포즈 = 역포즈들의 역순 합성 — (a·b)^-1 == b^-1 · a^-1
        var lhs = ab.Inverse();
        var rhs = b.Inverse().Compose(a.Inverse());
        foreach (var p in Points)
            Check(Near(lhs.Apply(p.X, p.Y), rhs.Apply(p.X, p.Y), 1e-9), $"(a∘b)⁻¹ == b⁻¹∘a⁻¹ for {p}");
    }

    [Fact]
    public void IdentityPoseMovesNothing()
    {
        var id = new CvPose(0, 100, 100, 100, 100, 1.0);
        foreach (var p in Points)
            Check(Near(id.Apply(p.X, p.Y), p), $"a pose found exactly at its taught origin with no rotation is the identity for {p}");
        Check(Near(id.Compose(id).Apply(5, 6), (5, 6)) && Near(id.Inverse().Apply(5, 6), (5, 6)), "identity composes and inverts to identity");
    }
}
