// 예제 레시피 회귀 — 픽스처가 옮기고 돌린 부품을 따라가는지, 저장/로드가 툴 순서·값·템플릿을 되살리는지,
// 전처리 단계의 공간 환산이 양방향으로 맞는지. README 가 약속한 것을 수치로 못 박는다.
using System.IO;
using CvInspect.Demo;
using CvInspect.Vision;
using CvInspect.Vision.Opts;
using CvInspect.Vision.Overlay;
using Xunit;

namespace CvInspect.Wpf.Tests;

public class DemoRecipeTests
{
    private static void Check(bool cond, string name) => Assert.True(cond, name);
    private static bool Near((double X, double Y) a, (double X, double Y) b, double tol)
        => Math.Abs(a.X - b.X) < tol && Math.Abs(a.Y - b.Y) < tol;

    private static DemoToolEntry Fixture(DemoRecipe r) => r.Tools.First(t => t.Kind == DemoToolKind.Pattern);
    private static T Opt<T>(DemoRecipe r, string key) => (T)r.Tools.First(t => t.Key == key).Opt;
    private static DemoToolResult Res(DemoRunResult run, string key) => run.Tools.First(t => t.Tool.Key == key);

    [Fact]
    public void DefaultRecipeFollowsTheMovedPart()
    {
        var recipe = DemoRecipe.Default();
        using (var reference = DemoImage.Create())
            Check(DemoRecipeRunner.Train(recipe, Fixture(recipe), reference), "pattern trains on the reference part");

        const double dx = 15, dy = -10, deg = 6;
        using var moved = DemoImage.Create(dx, dy, deg);
        using var run = DemoRecipeRunner.Run(recipe, moved);

        Check(run.IsOk, "every tool is OK on the moved part: " + string.Join(" | ", run.Tools.Select(t => $"{t.Tool.Key}={t.Ok}")));
        var pat = Opt<CvPatternOpt>(recipe, "fixture");
        var origin = DemoImage.Map(pat.TrainedOriginX, pat.TrainedOriginY, dx, dy, deg);
        Check(Res(run, "fixture").Summary.Contains("angle"), "fixture summary carries the angle");
        // 그래픽으로 포즈를 검증 — 발견 사각의 중심이 학습 원점이 옮겨 간 자리, 각도가 부품 회전(허용 1°: 파인 스텝 1° 의 파라볼라 보간).
        var rect = Res(run, "fixture").Graphic.Items.OfType<ViOverlayRect>().Single();
        Check(Near((rect.CenterX, rect.CenterY), origin, 1.5) && Math.Abs(rect.AngleDeg - deg) < 1.0,
            $"pose: centre ({rect.CenterX:F1}, {rect.CenterY:F1}) vs ({origin.X:F1}, {origin.Y:F1}), angle {rect.AngleDeg:F2}° vs {deg}°");
        var hole = DemoImage.Map(DemoImage.HoleCenterX, DemoImage.HoleCenterY, dx, dy, deg);
        var circle = Res(run, "hole").Graphic.Items.OfType<ViOverlayPoly>().Single();
        var centroid = (circle.Points.Average(p => p.X), circle.Points.Average(p => p.Y));
        Check(Near(centroid, hole, 1.5), $"circle followed the hole: ({centroid.Item1:F1}, {centroid.Item2:F1}) vs ({hole.X:F1}, {hole.Y:F1})");
        Check(Res(run, "hole-area").Summary.StartsWith("n 1"), $"blob search region followed the part: {Res(run, "hole-area").Summary}");
    }

    [Fact]
    public void SaveThenLoadRestoresToolsValuesAndTemplate()
    {
        var recipe = DemoRecipe.Default();
        using (var reference = DemoImage.Create())
            Check(DemoRecipeRunner.Train(recipe, Fixture(recipe), reference), "trained before saving");
        Opt<CvFindLineOpt>(recipe, "top-edge").NumCalipers = 13;
        Opt<CvFindCircleOpt>(recipe, "hole").AngleSpanDeg = 270;
        recipe.Add(DemoToolKind.Preprocess, "smooth");   // 맨 뒤 — 결과엔 영향 없고 저장/로드 대상만 늘린다
        Opt<CvImageProcessOpt>(recipe, "smooth").MedianKernel = 5;

        var dir = Path.Combine(Path.GetTempPath(), "cvinspect-recipe-" + Guid.NewGuid().ToString("N"));
        try
        {
            recipe.Save(dir);
            Check(File.Exists(Path.Combine(dir, "Recipe.json")) && File.Exists(Path.Combine(dir, "fixture.json")) && File.Exists(Path.Combine(dir, "fixture.Template.png")),
                $"folder layout: {string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName))}");

            var loaded = DemoRecipe.Load(dir);
            Check(loaded.Tools.Select(t => (t.Key, t.Kind)).SequenceEqual(recipe.Tools.Select(t => (t.Key, t.Kind))), "tool order, keys and kinds round-trip");
            Check(Opt<CvFindLineOpt>(loaded, "top-edge").NumCalipers == 13 && Opt<CvFindCircleOpt>(loaded, "hole").AngleSpanDeg == 270 && Opt<CvImageProcessOpt>(loaded, "smooth").MedianKernel == 5,
                "edited values round-trip");
            var lp = Opt<CvPatternOpt>(loaded, "fixture");
            var op = Opt<CvPatternOpt>(recipe, "fixture");
            Check(lp.Trained && lp.TemplatePng is not null && lp.TemplatePng.AsSpan().SequenceEqual(op.TemplatePng) && lp.TrainedOriginX == op.TrainedOriginX,
                "the trained template and origin round-trip through Template.png");

            using var moved = DemoImage.Create(15, -10, 6);
            using var a = DemoRecipeRunner.Run(recipe, moved);
            using var b = DemoRecipeRunner.Run(loaded, moved);
            Check(a.Tools.Select(t => t.Summary).SequenceEqual(b.Tools.Select(t => t.Summary)), "the loaded recipe produces the identical run");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void PreprocessStageMapsBothWays()
    {
        // 전처리가 2:1 축소하면 이후 툴은 절반 크기 공간에서 돌고, 그 결과는 원본 공간으로 환산돼 표시돼야 한다.
        var recipe = new DemoRecipe();
        recipe.Add(DemoToolKind.Preprocess, "half");
        var pre = Opt<CvImageProcessOpt>(recipe, "half");
        pre.SampleX = 2; pre.SampleY = 2;
        recipe.Add(DemoToolKind.Pattern, "fixture");
        recipe.Add(DemoToolKind.Circle, "hole");
        foreach (var t in recipe.Tools)
            switch (t.Opt)
            {
                case CvPatternOpt p: p.TrainX /= 2; p.TrainY /= 2; p.TrainW /= 2; p.TrainH /= 2; p.CoarseScale = 1.0; break;   // 절반 공간의 기하; 이미 작아서 코스 축소는 끈다
                case CvFindCircleOpt c: c.CenterX /= 2; c.CenterY /= 2; c.Radius /= 2; break;
            }
        using (var reference = DemoImage.Create())
            Check(DemoRecipeRunner.Train(recipe, Fixture(recipe), reference), "pattern trains in the half-size stage");

        const double dx = 12, dy = -8, deg = 5;
        using var moved = DemoImage.Create(dx, dy, deg);
        using var run = DemoRecipeRunner.Run(recipe, moved);
        Check(run.IsOk, "OK in the half-size stage: " + string.Join(" | ", run.Tools.Select(t => $"{t.Tool.Key}={t.Ok} {t.Summary}")));
        Check(run.StageOf(1).Width == DemoImage.Width / 2 && run.IsOriginalStage(0) && !run.IsOriginalStage(1), "stage 1 is the half-size image, stage 0 the original");

        var hole = DemoImage.Map(DemoImage.HoleCenterX, DemoImage.HoleCenterY, dx, dy, deg);
        (double X, double Y) Centroid(ViOverlay o) { var poly = o.Items.OfType<ViOverlayPoly>().Single(); return (poly.Points.Average(p => p.X), poly.Points.Average(p => p.Y)); }
        // 원본 공간(툴 0 = 전처리의 입력)으로 모으면 구멍 자리에, 절반 공간(툴 2)으로 모으면 그 절반 자리에.
        var inOriginal = Centroid(run.OverlayFor(0));
        var inHalf = Centroid(run.OverlayFor(2));
        Check(Near(inOriginal, hole, 2.5), $"circle mapped to original space: ({inOriginal.X:F1}, {inOriginal.Y:F1}) vs ({hole.X:F1}, {hole.Y:F1})");
        Check(Near(inHalf, (hole.X / 2, hole.Y / 2), 1.5), $"circle in the half-size space: ({inHalf.X:F1}, {inHalf.Y:F1}) vs ({hole.X / 2:F1}, {hole.Y / 2:F1})");
    }

    [Fact]
    public void TrainingRefusesEmptyOrRotatedRegions()
    {
        var recipe = DemoRecipe.Default();
        var pat = Opt<CvPatternOpt>(recipe, "fixture");
        using var img = DemoImage.Create();
        pat.TrainX = 10; pat.TrainY = 10; pat.TrainW = 60; pat.TrainH = 60;   // 배경만 있는 자리 — 특징 없음
        Check(!DemoRecipeRunner.Train(recipe, Fixture(recipe), img) && !pat.Trained, "a featureless region is refused");
        pat.TrainX = DemoImage.PlateX - 24; pat.TrainY = DemoImage.PlateY - 24; pat.TrainW = 120; pat.TrainH = 120;
        pat.TrainAngleDeg = 5;
        Check(!DemoRecipeRunner.Train(recipe, Fixture(recipe), img), "a rotated train box is refused (rotate the part, not the box)");
        pat.TrainAngleDeg = 0;
        Check(DemoRecipeRunner.Train(recipe, Fixture(recipe), img), "back at the corner with no rotation it trains");
    }
}
