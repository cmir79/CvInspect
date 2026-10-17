// 편집 도형 — 각 Opt 가 ICvShapeSource 로 자기 도형을 내고, 도형을 움직이면 Opt 에 즉시 되쓰이며 onEdited 가 불리는지.
// 토글(UseCrop·UseSearchRegion·UseRegion)이 꺼져 있으면 도형이 없다. 코어에 있으니 어느 OS 에서든 같은 회귀가 돈다.
// 전처리(CvImageProcessOpt)는 0.27 부터 기하가 없다 — 자르기 사각은 CvCropOpt 가 낸다.
using System.ComponentModel;
using System.Reflection;
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
            typeof(CvCropOpt), typeof(CvEdgeFilterOpt), typeof(CvPatternOpt), typeof(CvBlobOpt), typeof(CvHoughCircleOpt),
            typeof(CvColorSegmentOpt), typeof(CvFindLineOpt), typeof(CvFindCircleOpt), typeof(CvUnwrapOpt), typeof(CvRingFillOpt), typeof(CvRegionOpt),
        ];
        foreach (var t in shaped) Assert.True(typeof(ICvShapeSource).IsAssignableFrom(t), $"{t.Name} supplies its own edit shapes");
        Assert.Null(CvShapeBinder.For(new CvCaliperOpt(), () => { }));   // 기하가 없는 파라미터 — 도형 없음, 예외 없음
        Assert.Null(CvShapeBinder.For(new CvImageProcessOpt(), () => { }));
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
        var cr = new CvCropOpt { UseCrop = false, CropX = 5, CropY = 6, CropW = 100, CropH = 80 };
        Assert.Null(CvShapeBinder.For(cr, () => { }));
        cr.UseCrop = true;
        var crop = Assert.IsType<CvEditRect>(Assert.Single(CvShapeBinder.For(cr, () => { })!));
        Assert.Equal("Crop", crop.Label);
        crop.X = 7;
        Assert.Equal(7, cr.CropX);

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

    /// <summary>그립 편집이 opt 에 쓴 값은 전부 PropertyChanged 로 나간다 — 속성 편집기의 숫자 칸은 그 통지로만 다시 그린다.
    /// 통지가 없던 판(0.31.0)에서는 밴드 그립을 끌어도 RMinPx 칸이 그대로였다(소비자 현장 보고). 어느 프로퍼티를 쓰는지 손으로
    /// 나열하지 않고 그립 전후 값을 비교한다 — 새 그립 좌표가 통지 없이 들어오면 여기서 빨개진다.</summary>
    [Fact]
    public void GripEditsNotifyEveryPropertyTheyWrite()
    {
        object[] opts =
        [
            new CvCropOpt { UseCrop = true }, new CvEdgeFilterOpt { UseRegion = true },
            new CvPatternOpt { UseSearchRegion = true }, new CvPatternOpt { UseSearchRegion = true, TrainShape = CvTrainShape.Circle },
            new CvBlobOpt { UseSearchRegion = true }, new CvHoughCircleOpt { UseSearchRegion = true },
            new CvColorSegmentOpt(), new CvColorSegmentOpt { Shape = CvRegionShape.Ring },
            new CvFindLineOpt(), new CvFindCircleOpt { AngleSpanDeg = 180 }, new CvUnwrapOpt(), new CvRingFillOpt(),
            new CvRegionOpt(), new CvRegionOpt { Shape = CvRegionShape.Ring },
        ];
        foreach (var opt in opts)
        {
            var name = opt.GetType().Name;
            // 그립이 쓰는 자리 — 영역을 품은 툴(컬러 세그먼트)은 품은 영역 객체에 쓴다. 편집기는 그 객체를 따로 구독한다.
            var targets = new List<object> { opt };
            targets.AddRange(opt.GetType().GetProperties()
                .Where(p => p.GetIndexParameters().Length == 0 && typeof(INotifyPropertyChanged).IsAssignableFrom(p.PropertyType))
                .Select(p => p.GetValue(opt)).OfType<object>());

            var shapes = CvShapeBinder.For(opt, () => { });
            Assert.True(shapes is { Count: > 0 }, $"{name} offers grips in this setup — else nothing below is tested");
            foreach (var shape in shapes!)
            {
                var notified = targets.Select(_ => new HashSet<string>()).ToList();
                var handlers = targets.Select((t, i) => (PropertyChangedEventHandler)((_, e) => notified[i].Add(e.PropertyName ?? ""))).ToList();
                for (int i = 0; i < targets.Count; i++)
                {
                    var inpc = Assert.IsAssignableFrom<INotifyPropertyChanged>(targets[i]);
                    inpc.PropertyChanged += handlers[i];
                }
                var before = targets.Select(Snap).ToList();

                Nudge(shape);

                var changedTotal = 0;
                for (int i = 0; i < targets.Count; i++)
                {
                    ((INotifyPropertyChanged)targets[i]).PropertyChanged -= handlers[i];
                    var after = Snap(targets[i]);
                    var changed = after.Where(kv => !Equals(kv.Value, before[i][kv.Key])).Select(kv => kv.Key).ToList();
                    changedTotal += changed.Count;
                    var silent = changed.Where(p => !notified[i].Contains(p)).ToList();
                    Assert.True(silent.Count == 0,
                        $"{name} {shape.Label} grip wrote {targets[i].GetType().Name}.{string.Join(", ", silent)} without PropertyChanged — the property editor row stays stale while dragging");
                }
                Assert.True(changedTotal > 0, $"{name} {shape.Label}: the nudge moved something — else the check above proves nothing");
            }
        }

        // 코드 경로도 같다 — 스팬 전환 버튼이 바꾼 값이 편집기에 보인다.
        var fc = new CvFindCircleOpt();
        var names = new List<string?>();
        fc.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        fc.ToggleSpan();
        Assert.Contains(nameof(CvFindCircleOpt.AngleSpanDeg), names);

        // 같은 값 대입은 통지하지 않는다 — 드래그 중 안 움직인 축까지 편집기가 다시 그리지 않게.
        var uw = new CvUnwrapOpt();
        var count = 0;
        uw.PropertyChanged += (_, _) => count++;
        uw.RMin = uw.RMin;
        Assert.Equal(0, count);
    }

    /// <summary>값 형식·문자열 프로퍼티의 현재 값 — 델리게이트·배열·참조 객체는 비교 대상이 아니다.</summary>
    private static Dictionary<string, object?> Snap(object o)
        => o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && (p.PropertyType.IsValueType || p.PropertyType == typeof(string)))
            .ToDictionary(p => p.Name, p => p.GetValue(o));

    /// <summary>그립을 사람이 끄는 것처럼 움직인다 — 도형의 쓰기 가능한 수치 축을 전부 조금씩 민다(축마다 Changed 가 난다).</summary>
    private static void Nudge(CvEditShape shape)
    {
        foreach (var p in shape.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.PropertyType == typeof(double) && p.CanWrite && p.SetMethod!.IsPublic))
            p.SetValue(shape, (double)p.GetValue(shape)! - 7);
    }
}
