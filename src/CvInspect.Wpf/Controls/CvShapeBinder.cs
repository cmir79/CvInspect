using CvInspect.Vision;
using CvInspect.Vision.Opts;
using CvInspect.Vision.Overlay;

namespace CvInspect.Controls;

/// <summary>
/// OpenCV 툴 파라미터(POCO) ↔ 편집 도형 바인더 — 도형 드래그가 파라미터에 즉시 되write.
/// 도형 좌표 공간 = 해당 툴의 입력(단계) 이미지 픽셀 공간 (툴 파라미터와 동일).
/// </summary>
public static class CvShapeBinder
{
    /// <summary>툴 파라미터에서 편집 도형 목록 생성 — 편집 가능 영역이 없으면 null. onEdited 는 드래그 반영 시 호출(dirty 마킹).</summary>
    public static IReadOnlyList<CvEditShape>? For(object toolOpt, Action onEdited)
    {
        switch (toolOpt)
        {
            // 검사 구현 전용 파라미터 — 도형을 스스로 공급 (공용 타입 스위치보다 우선).
            case ICvShapeSource src:
                return src.CreateShapes(onEdited);
            case CvImageProcessOpt ip when ip.UseCrop:
            {
                // 이 자리만 원본 이미지 공간이다 — 잘라내기가 축소보다 먼저라 그때는 원본만 있다.
                var crop = new CvEditRect { Color = ViOverlayColor.Orange, Label = "Crop" };
                crop.Set(ip.CropX, ip.CropY, ip.CropW, ip.CropH);
                crop.Changed += (_, _) =>
                {
                    ip.CropX = crop.X;
                    ip.CropY = crop.Y;
                    ip.CropW = crop.W;
                    ip.CropH = crop.H;
                    onEdited();
                };
                return [crop];
            }
            case CvEdgeFilterOpt ef when ef.UseRegion:
            {
                var rect = new CvEditRect { Color = ViOverlayColor.Yellow, Label = "Region", IsRotatable = true };
                rect.Set(ef.RegionX, ef.RegionY, ef.RegionW, ef.RegionH);
                rect.AngleDeg = ef.RegionAngleDeg;
                rect.Changed += (_, _) =>
                {
                    ef.RegionX = rect.X;
                    ef.RegionY = rect.Y;
                    ef.RegionW = rect.W;
                    ef.RegionH = rect.H;
                    ef.RegionAngleDeg = rect.AngleDeg;
                    onEdited();
                };
                return [rect];
            }
            case CvPatternOpt pat:
            {
                var shapes = new List<CvEditShape>();

                // 탐색 영역은 학습 영역의 회전을 따라간다 — 특징과 그 주변은 같은 국소 좌표계에 있어
                // 각도를 따로 둘 이유가 없고, 따로 두면 되돌리는 변환이 한 겹 더 늘어난다.
                // 그래서 회전 그립은 학습 영역에만 있고 탐색 영역은 그 각을 물려받는다.
                // 원형 학습영역은 각도 개념이 없어 탐색 영역도 축 정렬(0°)이다.
                CvEditRect? search = null;

                if (pat.TrainShape == CvTrainShape.Circle)
                {
                    var circle = new CvEditCircle { Color = ViOverlayColor.Yellow, Label = "Train" };
                    circle.Set(pat.TrainCircleX, pat.TrainCircleY, pat.TrainCircleR);
                    circle.Changed += (_, _) =>
                    {
                        pat.TrainCircleX = circle.CenterX;
                        pat.TrainCircleY = circle.CenterY;
                        pat.TrainCircleR = circle.Radius;
                        onEdited();
                    };
                    shapes.Add(circle);
                }
                else
                {
                    var train = new CvEditRect { Color = ViOverlayColor.Yellow, Label = "Train", IsRotatable = true };
                    train.Set(pat.TrainX, pat.TrainY, pat.TrainW, pat.TrainH);
                    train.AngleDeg = pat.TrainAngleDeg;
                    train.Changed += (_, _) =>
                    {
                        pat.TrainX = train.X;
                        pat.TrainY = train.Y;
                        pat.TrainW = train.W;
                        pat.TrainH = train.H;
                        pat.TrainAngleDeg = train.AngleDeg;
                        if (search is not null) search.AngleDeg = train.AngleDeg;
                        onEdited();
                    };
                    shapes.Add(train);
                }

                if (pat.UseSearchRegion)
                {
                    // 티칭 도형은 Teal(어두운 청록) — 검사 결과 측정 기하(순색 Cyan)와 색으로 구분.
                    search = new CvEditRect { Color = ViOverlayColor.Teal, Label = "Search" };
                    search.Set(pat.SearchX, pat.SearchY, pat.SearchW, pat.SearchH);
                    search.AngleDeg = pat.TrainShape == CvTrainShape.Circle ? 0 : pat.TrainAngleDeg;
                    search.Changed += (_, _) =>
                    {
                        pat.SearchX = search.X;
                        pat.SearchY = search.Y;
                        pat.SearchW = search.W;
                        pat.SearchH = search.H;
                        onEdited();
                    };
                    shapes.Add(search);
                }
                return shapes;
            }
            case CvBlobOpt bl when bl.UseSearchRegion:
            {
                var search = new CvEditRect { Color = ViOverlayColor.Teal, Label = "Search" };
                search.Set(bl.SearchX, bl.SearchY, bl.SearchW, bl.SearchH);
                search.Changed += (_, _) =>
                {
                    bl.SearchX = search.X;
                    bl.SearchY = search.Y;
                    bl.SearchW = search.W;
                    bl.SearchH = search.H;
                    onEdited();
                };
                return [search];
            }
            case CvHoughCircleOpt hc:
            {
                var shapes = new List<CvEditShape>();

                // 기대 원 — 탐색 반경 범위(±허용%)의 기준. 검출은 영역 전체 탐색이라 중심은 시각 기준일 뿐.
                var expect = new CvEditArc { Color = ViOverlayColor.Yellow, Label = "Expect" };
                expect.Set(hc.CenterX, hc.CenterY, hc.Radius, 0, 360);
                expect.Changed += (_, _) =>
                {
                    hc.CenterX = expect.CenterX;
                    hc.CenterY = expect.CenterY;
                    hc.Radius = expect.Radius;
                    onEdited();
                };
                shapes.Add(expect);

                if (hc.UseSearchRegion)
                {
                    var search = new CvEditRect { Color = ViOverlayColor.Teal, Label = "Search" };
                    search.Set(hc.SearchX, hc.SearchY, hc.SearchW, hc.SearchH);
                    search.Changed += (_, _) =>
                    {
                        hc.SearchX = search.X;
                        hc.SearchY = search.Y;
                        hc.SearchW = search.W;
                        hc.SearchH = search.H;
                        onEdited();
                    };
                    shapes.Add(search);
                }
                return shapes;
            }
            case CvColorSegmentOpt cs:
                // 판정/표본 겸용 영역 — 이 툴만 원본(컬러) 이미지 공간 (컬러는 전처리 축소를 안 거친다).
                return RegionShapes(cs.Region, onEdited);
            case CvFindLineOpt fl:
            {
                var seg = new CvEditSeg { Color = ViOverlayColor.Yellow, Label = "Line" };
                seg.Set(fl.StartX, fl.StartY, fl.EndX, fl.EndY);
                seg.Changed += (_, _) =>
                {
                    fl.StartX = seg.X1;
                    fl.StartY = seg.Y1;
                    fl.EndX = seg.X2;
                    fl.EndY = seg.Y2;
                    onEdited();
                };
                return [seg];
            }
            case CvFindCircleOpt fc:
            {
                var arc = new CvEditArc { Color = ViOverlayColor.Yellow, Label = "Circle" };
                arc.Set(fc.CenterX, fc.CenterY, fc.Radius, fc.AngleStartDeg, fc.AngleSpanDeg);
                arc.Changed += (_, _) =>
                {
                    fc.CenterX = arc.CenterX;
                    fc.CenterY = arc.CenterY;
                    fc.Radius = arc.Radius;
                    fc.AngleStartDeg = arc.StartDeg;
                    fc.AngleSpanDeg = arc.SpanDeg;
                    onEdited();
                };
                // 코드 경로 편집(스팬 전환 버튼 등) → 도형 재동기화 — Set 이 Changed 를 되쏴 되write+화면 갱신까지 이어진다.
                fc.EditShapeSync = () => arc.Set(fc.CenterX, fc.CenterY, fc.Radius, fc.AngleStartDeg, fc.AngleSpanDeg);
                return [arc];
            }
            case CvUnwrapOpt uw:
            {
                // 중심은 선행 원 툴 파생(이동 불가) — 반경 밴드(min/max)만 그립 편집.
                var (cx, cy) = uw.CenterHook?.Invoke() ?? (0, 0);
                var ring = new CvEditRing { Color = ViOverlayColor.Teal, Label = "Band" };
                ring.Set(cx, cy, uw.RMin, uw.RMax);
                ring.Changed += (_, _) =>
                {
                    uw.RMin = ring.RMin;
                    uw.RMax = ring.RMax;
                    onEdited();
                };
                return [ring];
            }
            case CvRingFillOpt rf:
            {
                // 중심은 선행 원·블랍 툴 파생(이동 불가) — 충전율 밴드(min/max)만 그립 편집.
                var (cx, cy) = rf.CenterHook?.Invoke() ?? (0, 0);
                var ring = new CvEditRing { Color = ViOverlayColor.Teal, Label = "Ring" };
                ring.Set(cx, cy, rf.RMinPx, rf.RMaxPx);
                ring.Changed += (_, _) =>
                {
                    rf.RMinPx = ring.RMin;
                    rf.RMaxPx = ring.RMax;
                    onEdited();
                };
                return [ring];
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// 공용 영역(<see cref="CvRegionOpt"/>) 편집 도형 — 모양(사각/링) 분기를 한곳에 모은다.
    /// 어느 툴이든 CvRegionOpt 를 품으면 이걸로 도형을 얻는다 (모양별로 검사·바인딩을 따로 만들지 않는다).
    /// </summary>
    public static IReadOnlyList<CvEditShape> RegionShapes(CvRegionOpt region, Action onEdited)
    {
        if (region.Shape == CvRegionShape.Ring)
        {
            // 중심 이동 허용 — 밴드 자리를 티칭에서 직접 잡는다 (파생 중심을 쓰는 언랩/충전율과 다른 점).
            var ring = new CvEditRing { Color = ViOverlayColor.Yellow, Label = "Ring", IsMovable = true };
            ring.Set(region.CenterX, region.CenterY, region.RMinPx, region.RMaxPx);
            ring.Changed += (_, _) =>
            {
                region.CenterX = ring.CenterX;
                region.CenterY = ring.CenterY;
                region.RMinPx = ring.RMin;
                region.RMaxPx = ring.RMax;
                onEdited();
            };
            return [ring];
        }

        var rect = new CvEditRect { Color = ViOverlayColor.Yellow, Label = "Region", IsRotatable = true };
        rect.Set(region.RectX, region.RectY, region.RectW, region.RectH);
        rect.AngleDeg = region.RectAngleDeg;
        rect.Changed += (_, _) =>
        {
            region.RectX = rect.X;
            region.RectY = rect.Y;
            region.RectW = rect.W;
            region.RectH = rect.H;
            region.RectAngleDeg = rect.AngleDeg;
            onEdited();
        };
        return [rect];
    }
}
