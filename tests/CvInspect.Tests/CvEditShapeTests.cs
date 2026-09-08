// 편집 도형 — 각 Opt 가 ICvShapeSource 로 자기 도형을 내고, 도형을 움직이면 Opt 에 즉시 되쓰이며 onEdited 가 불리는지.
// 토글(UseCrop·UseSearchRegion·UseRegion)이 꺼져 있으면 도형이 없다. 코어에 있으니 어느 OS 에서든 같은 회귀가 돈다.
using CvInspect.Vision;
using CvInspect.Vision.Edit;
using CvInspect.Vision.Opts;
using Xunit;

namespace CvInspect.Tests;

public class CvEditShapeTests
{
    [Fact]
    public void EveryOptionWithGeometryIsItsOwnShapeSource()
    {
        Type[] shaped =
        [
            typeof(CvImageProcessOpt), typeof(CvEdgeFilterOpt), typeof(CvPatternOpt), typeof(CvBlobOpt), typeof(CvHoughCircleOpt),
            typeof(CvColorSegmentOpt), typeof(CvFindLineOpt), typeof(CvFindCircleOpt), typeof(CvUnwrapOpt), typeof(CvRingFillOpt), typeof(CvRegionOpt),
        ];
        foreach (var t in shaped) Assert.True(typeof(ICvShapeSource).IsAssignableFrom(t), $"{t.Name} supplies its own edit shapes");
        Assert.Null(CvShapeBinder.For(new CvCaliperOpt(), () => { }));   // 기하가 없는 파라미터 — 도형 없음, 예외 없음
        Assert.Null(CvShapeBinder.For(new object(), () => { }));
    }

    [Fact]
    public void LineSegmentWritesBackAndReportsEdits()
    {
        var opt = new CvFindLineOpt { StartX = 10, StartY = 20, EndX = 110, EndY = 20 };
        var edited = 0;
        var seg = Assert.IsType<CvEditSeg>(Assert.Single(CvShapeBinder.For(opt, () => edited++)!));
        Assert.Equal((10, 20, 110, 20), (seg.X1, seg.Y1, seg.X2, seg.Y2));
        seg.X1 = 15;
        seg.Y2 = 25;
        Assert.Equal((15.0, 25.0, 2), (opt.StartX, opt.EndY, edited));
    }

    [Fact]
    public void TogglesGateTheShapes()
    {
        var ip = new CvImageProcessOpt { UseCrop = false, CropX = 5, CropY = 6, CropW = 100, CropH = 80 };
        Assert.Null(CvShapeBinder.For(ip, () => { }));
        ip.UseCrop = true;
        var crop = Assert.IsType<CvEditRect>(Assert.Single(CvShapeBinder.For(ip, () => { })!));
        Assert.Equal("Crop", crop.Label);
        crop.X = 7;
        Assert.Equal(7, ip.CropX);

        var bl = new CvBlobOpt { UseSearchRegion = false };
        Assert.Null(CvShapeBinder.For(bl, () => { }));
        bl.UseSearchRegion = true;
        var search = Assert.IsType<CvEditRect>(Assert.Single(CvShapeBinder.For(bl, () => { })!));
        search.W = 123;
        Assert.Equal(123, bl.SearchW);

        var ef = new CvEdgeFilterOpt { UseRegion = false };
        Assert.Null(CvShapeBinder.For(ef, () => { }));
        ef.UseRegion = true;
        var region = Assert.IsType<CvEditRect>(Assert.Single(CvShapeBinder.For(ef, () => { })!));
        Assert.True(region.IsRotatable);
        region.AngleDeg = 12;
        Assert.Equal(12, ef.RegionAngleDeg);
    }

    [Fact]
    public void PatternSearchRegionFollowsTheTrainAngle()
    {
        var pat = new CvPatternOpt { UseSearchRegion = true, TrainShape = CvTrainShape.Rect };
        var shapes = CvShapeBinder.For(pat, () => { })!;
        var train = Assert.IsType<CvEditRect>(shapes[0]);
        var search = Assert.IsType<CvEditRect>(shapes[1]);
        Assert.True(train.IsRotatable && !search.IsRotatable, "only the train box has a rotation grip; the search box inherits the angle");
        train.AngleDeg = 15;
        Assert.Equal(15, pat.TrainAngleDeg);
        Assert.Equal(15, search.AngleDeg);

        pat.TrainShape = CvTrainShape.Circle;
        shapes = CvShapeBinder.For(pat, () => { })!;
        var circle = Assert.IsType<CvEditCircle>(shapes[0]);
        Assert.Equal(0, Assert.IsType<CvEditRect>(shapes[1]).AngleDeg);   // 원형 학습영역은 각도 개념이 없다
        circle.Radius = 42;
        Assert.Equal(42, pat.TrainCircleR);
    }

    [Fact]
    public void CircleArcWritesBackAndResyncsFromCode()
    {
        var fc = new CvFindCircleOpt { CenterX = 100, CenterY = 90, Radius = 40 };
        var arc = Assert.IsType<CvEditArc>(Assert.Single(CvShapeBinder.For(fc, () => { })!));
        arc.Radius = 33;
        Assert.Equal(33, fc.Radius);
        fc.AngleSpanDeg = 180;          // 코드 경로 편집 — 도형은 아직 옛 값
        Assert.NotEqual(180, arc.SpanDeg);
        fc.EditShapeSync!();            // 파라미터가 제공하는 동기화 훅으로 도형을 맞춘다
        Assert.Equal(180, arc.SpanDeg);
    }

    [Fact]
    public void RegionOptPicksRectOrRingAndColorSegmentDelegatesToIt()
    {
        var region = new CvRegionOpt { Shape = CvRegionShape.Ring, CenterX = 50, CenterY = 60, RMinPx = 10, RMaxPx = 30 };
        var ring = Assert.IsType<CvEditRing>(Assert.Single(CvShapeBinder.For(region, () => { })!));
        Assert.True(ring.IsMovable, "a taught ring band moves with its centre, unlike derived-centre bands");
        ring.RMax = 44;
        Assert.Equal(44, region.RMaxPx);

        region.Shape = CvRegionShape.Rect;
        var rect = Assert.IsType<CvEditRect>(Assert.Single(CvShapeBinder.For(region, () => { })!));
        rect.X = 3;
        Assert.Equal(3, region.RectX);

        var cs = new CvColorSegmentOpt();
        cs.Region.Shape = CvRegionShape.Rect;
        var viaSegment = Assert.IsType<CvEditRect>(Assert.Single(CvShapeBinder.For(cs, () => { })!));
        viaSegment.Y = 9;
        Assert.Equal(9, cs.Region.RectY);
    }

    [Fact]
    public void DerivedCentreBandsOnlyEditTheirRadii()
    {
        var uw = new CvUnwrapOpt { RMin = 20, RMax = 60, CenterHook = () => (200, 150) };
        var band = Assert.IsType<CvEditRing>(Assert.Single(CvShapeBinder.For(uw, () => { })!));
        Assert.Equal((200.0, 150.0), (band.CenterX, band.CenterY));
        Assert.False(band.IsMovable);
        band.RMin = 25;
        Assert.Equal(25, uw.RMin);

        var rf = new CvRingFillOpt { RMinPx = 20, RMaxPx = 60 };
        var ring = Assert.IsType<CvEditRing>(Assert.Single(CvShapeBinder.For(rf, () => { })!));
        ring.RMax = 70;
        Assert.Equal(70, rf.RMaxPx);
    }
}
