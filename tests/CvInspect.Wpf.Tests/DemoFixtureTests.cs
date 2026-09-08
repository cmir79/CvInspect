// 예제 검사기의 픽스처 회귀 — 학습한 패턴이 옮기고 돌린 부품을 찾고, 그 포즈로 옮긴 라인·원·블랍이 제자리를 잡는지.
// README 가 약속한 것("부품이 움직여도 툴이 따라간다")을 수치로 못 박는다.
using CvInspect.Demo;
using Xunit;

namespace CvInspect.Wpf.Tests;

public class DemoFixtureTests
{
    private static void Check(bool cond, string name) => Assert.True(cond, name);
    private static bool Near((double X, double Y) a, (double X, double Y) b, double tol)
        => Math.Abs(a.X - b.X) < tol && Math.Abs(a.Y - b.Y) < tol;

    [Fact]
    public void ToolsFollowTheMovedPart()
    {
        var insp = new DemoInspector();
        using (var reference = DemoImage.Create())
            Check(insp.Train(reference), "pattern trains on the reference part");
        Check(insp.Pattern.Trained && insp.Pattern.TemplatePng is { Length: > 0 }, "training leaves a template and the Trained flag");

        const double dx = 15, dy = -10, deg = 6;
        using var moved = DemoImage.Create(dx, dy, deg);
        var r = insp.Run(moved);

        Check(r.Pose is not null, $"pattern found on the moved part (best score {r.PatternScore:F2})");
        var pose = r.Pose!.Value;
        // 허용 1.0° — 패턴 파인더의 각도는 파인 스텝 1° 를 파라볼라로 보간한 값이고, 라이브러리 자체의 성공 기준은 2° 다.
        Check(Math.Abs(pose.ThetaDeg - deg) < 1.0, $"pose angle tracks the part rotation: {pose.ThetaDeg:F2}° vs {deg}°");
        var expectedOrigin = DemoImage.Map(insp.Pattern.TrainedOriginX, insp.Pattern.TrainedOriginY, dx, dy, deg);
        Check(Near((pose.FoundX, pose.FoundY), expectedOrigin, 1.5), $"pose position is where the taught origin went: ({pose.FoundX:F1}, {pose.FoundY:F1}) vs ({expectedOrigin.X:F1}, {expectedOrigin.Y:F1})");

        Check(r.Line is { } l && Math.Abs(l.AngleDeg - deg) < 0.6 && l.RmsPx < 1.0,
            $"line follows the plate edge: angle={r.Line?.AngleDeg:F2} rms={r.Line?.RmsPx:F2}");
        var hole = DemoImage.Map(DemoImage.HoleCenterX, DemoImage.HoleCenterY, dx, dy, deg);
        Check(r.Circle is { } c && Near((c.CenterX, c.CenterY), hole, 1.5) && Math.Abs(c.Radius - DemoImage.HoleRadius) < 1.5,
            $"circle follows the hole: ({r.Circle?.CenterX:F1}, {r.Circle?.CenterY:F1}) r={r.Circle?.Radius:F1} vs ({hole.X:F1}, {hole.Y:F1})");
        Check(r.Blobs.Hits.Count == 1 && Near((r.Blobs.Hits[0].X, r.Blobs.Hits[0].Y), hole, 1.5),
            $"blob search region followed the part: n={r.Blobs.Hits.Count} centroid=({r.Blobs.Hits.FirstOrDefault().X:F1}, {r.Blobs.Hits.FirstOrDefault().Y:F1})");
        Check(r.IsOk, "the whole inspection is OK on the moved part");
    }

    [Fact]
    public void WithoutTrainingToolsStayAtTaughtGeometry()
    {
        // 학습 전에는 픽스처가 없다 — 티칭 자리 그대로 돌고, 움직인 부품에서는 정직하게 실패해야 한다.
        var insp = new DemoInspector();
        using var still = DemoImage.Create();
        var r0 = insp.Run(still);
        Check(r0.Pose is null && r0.IsOk && r0.Line is not null && r0.Circle is not null, "untrained: taught geometry works on the still part");

        using var moved = DemoImage.Create(60, 40, 15);
        var r1 = insp.Run(moved);
        Check(r1.Pose is null && (r1.Circle is null || Math.Abs(r1.Circle.Value.Radius - DemoImage.HoleRadius) > 1.5 || !r1.IsOk),
            "untrained: the circle at the taught centre does not find the hole that moved 60 px away");
    }

    [Fact]
    public void TrainingRefusesEmptyOrRotatedRegions()
    {
        var insp = new DemoInspector();
        using var img = DemoImage.Create();
        insp.Pattern.TrainX = 10; insp.Pattern.TrainY = 10; insp.Pattern.TrainW = 60; insp.Pattern.TrainH = 60;   // 배경만 있는 자리 — 특징 없음
        Check(!insp.Train(img) && !insp.Pattern.Trained, "a featureless region is refused");
        insp.Pattern.TrainX = DemoImage.PlateX - 24; insp.Pattern.TrainY = DemoImage.PlateY - 24; insp.Pattern.TrainW = 120; insp.Pattern.TrainH = 120;
        insp.Pattern.TrainAngleDeg = 5;
        Check(!insp.Train(img), "a rotated train box is refused (rotate the part, not the box)");
        insp.Pattern.TrainAngleDeg = 0;
        Check(insp.Train(img), "back at the key with no rotation it trains");
    }
}
