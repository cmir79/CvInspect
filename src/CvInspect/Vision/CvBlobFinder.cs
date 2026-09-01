using OpenCvSharp;
using CvInspect.Vision.Opts;

namespace CvInspect.Vision;

/// <summary>블랍 검출 결과 — 무게중심(입력 이미지 공간)과 면적(px).</summary>
public readonly record struct CvBlobHit(double X, double Y, double Area);

/// <summary>블랍 상세 결과 (튜닝 진단용) — 검출 + 실사용 문턱(Otsu 자동값 확인) + 외곽 컨투어(입력 이미지 공간).</summary>
public readonly record struct CvBlobDetail(CvBlobHit Hit, double ThresholdUsed, IReadOnlyList<(double X, double Y)> Contour);

/// <summary>MinArea 이상 블랍 전부 — 면적 점유(합계)로 판정하는 검사용.
/// 최대 하나만 보는 <see cref="CvBlobFinder.Find"/> 와 달리 조각난 대상(끊긴 호·가려진 부위)도 온전히 센다.
/// Contours 는 Hits 와 같은 순서(면적 내림차순), withContours=false 면 빈 목록.</summary>
public readonly record struct CvBlobSet(
    double TotalArea,
    double ThresholdUsed,
    IReadOnlyList<CvBlobHit> Hits,
    IReadOnlyList<IReadOnlyList<(double X, double Y)>> Contours);

/// <summary>
/// 블랍 파인더 — 극성 이진화(Otsu/고정 문턱) → 연결요소 중 최대 면적 블랍의 무게중심.
/// 탐색 영역 제한 시 영역 내에서만 이진화/라벨링 — 밝은 배경 등 영역 밖 오인을 원천 차단.
/// </summary>
public static class CvBlobFinder
{
    public static CvBlobHit? Find(Mat img, CvBlobOpt opt)
        => FindCore(img, opt, withContour: false)?.Hit;

    /// <summary>상세 검출 (툴 단독 실행 등 튜닝용) — 잡힌 블랍의 외곽 컨투어와 실사용 문턱 동반.</summary>
    public static CvBlobDetail? FindDetail(Mat img, CvBlobOpt opt)
        => FindCore(img, opt, withContour: true);

    /// <summary>
    /// MinArea 이상 블랍을 <b>전부</b> 검출 — 면적 점유(합계)로 판정하는 검사용.
    /// 대상이 조각나 있어도(끊긴 호·가려진 부위) 합계가 실제 점유를 반영한다.
    /// 잡티는 MinArea 로 거른다 — 0 으로 두면 1px 노이즈까지 합산되니 반드시 의미 있는 하한을 줄 것.
    /// </summary>
    public static CvBlobSet FindAll(Mat img, CvBlobOpt opt, bool withContours = false)
    {
        var empty = new CvBlobSet(0, 0, Array.Empty<CvBlobHit>(), Array.Empty<IReadOnlyList<(double X, double Y)>>());
        if (!Binarize(img, opt, out var roi, out var bin, out var thrUsed)) return empty;

        using (bin)
        using (var labels = new Mat())
        using (var stats = new Mat())
        using (var centroids = new Mat())
        {
            var n = Cv2.ConnectedComponentsWithStats(bin, labels, stats, centroids);
            var minArea = Math.Max(1, opt.MinArea);

            var picked = new List<(int Idx, CvBlobHit Hit)>();
            for (var i = 1; i < n; i++)   // 0 = 배경 라벨
            {
                var area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
                if (area < minArea) continue;
                picked.Add((i, new CvBlobHit(roi.X + centroids.At<double>(i, 0), roi.Y + centroids.At<double>(i, 1), area)));
            }
            if (picked.Count == 0) return empty with { ThresholdUsed = thrUsed };

            picked.Sort((a, b) => b.Hit.Area.CompareTo(a.Hit.Area));   // 면적 내림차순 — 표시·진단 우선순위
            var hits = picked.Select(p => p.Hit).ToArray();
            var contours = withContours
                ? picked.Select(p => (IReadOnlyList<(double X, double Y)>)ExtractContour(labels, p.Idx, roi)).ToArray()
                : Array.Empty<IReadOnlyList<(double X, double Y)>>();

            return new CvBlobSet(hits.Sum(h => h.Area), thrUsed, hits, contours);
        }
    }

    /// <summary>탐색 영역 클립 + 극성 이진화 — 검출 경로 공용. 영역이 무효면 false.</summary>
    private static bool Binarize(Mat img, CvBlobOpt opt, out Rect roi, out Mat bin, out double thrUsed)
    {
        bin = null!;
        thrUsed = 0;
        roi = new Rect(0, 0, img.Cols, img.Rows);
        if (opt.UseSearchRegion)
        {
            var clip = CvImageOps.ClipRect(opt.SearchX, opt.SearchY, opt.SearchW, opt.SearchH, img.Cols, img.Rows);
            if (clip is null || clip.Value.Width < 4 || clip.Value.Height < 4) return false;
            roi = clip.Value;
        }

        using var view = img[roi];
        bin = new Mat();
        var type = opt.Polarity == CvBlobPolarity.Bright ? ThresholdTypes.Binary : ThresholdTypes.BinaryInv;
        thrUsed = opt.UseOtsu
            ? Cv2.Threshold(view, bin, 0, 255, type | ThresholdTypes.Otsu)
            : Cv2.Threshold(view, bin, opt.Threshold, 255, type);

        if (opt.FillHoles) FillEnclosed(bin);
        return true;
    }

    /// <summary>
    /// 사방이 막힌 구멍을 메운다. 바깥 컨투어만 찾아 속을 채우므로, 한 곳이라도 바깥으로 트인
    /// 자리는 그 통로를 타고 배경으로 남는다 — "완전히 갇힌 것만 부모 면적에 든다"는 규칙 그대로다.
    /// </summary>
    private static void FillEnclosed(Mat bin)
    {
        // FindContours 는 입력을 건드릴 수 있어 사본에서 찾고 원본에 그린다.
        using var src = bin.Clone();
        Cv2.FindContours(src, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        if (contours.Length == 0) return;
        Cv2.DrawContours(bin, contours, -1, Scalar.All(255), thickness: -1);
    }

    private static CvBlobDetail? FindCore(Mat img, CvBlobOpt opt, bool withContour)
    {
        if (!Binarize(img, opt, out var roi, out var bin, out var thrUsed)) return null;

        using var _bin = bin;
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var n = Cv2.ConnectedComponentsWithStats(bin, labels, stats, centroids);

        var idx = -1;
        var maxArea = 0;
        for (var i = 1; i < n; i++)   // 0 = 배경 라벨
        {
            var area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
            if (area > maxArea)
            {
                maxArea = area;
                idx = i;
            }
        }
        if (idx < 0 || maxArea < Math.Max(1, opt.MinArea)) return null;

        var hit = new CvBlobHit(roi.X + centroids.At<double>(idx, 0), roi.Y + centroids.At<double>(idx, 1), maxArea);
        return new CvBlobDetail(hit, thrUsed, withContour ? ExtractContour(labels, idx, roi) : []);
    }

    /// <summary>선택 라벨 외곽 컨투어 — 폴리라인 경량화(ApproxPolyDP)로 오버레이 점수 절감, 좌표는 원 이미지 공간.</summary>
    private static List<(double X, double Y)> ExtractContour(Mat labels, int idx, Rect roi)
    {
        using var mask = new Mat();
        Cv2.Compare(labels, idx, mask, CmpTypes.EQ);   // 32S 라벨 == idx → 8U 255 마스크

        Cv2.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        var best = Array.Empty<Point>();
        var bestLen = 0;
        foreach (var c in contours)
        {
            if (c.Length > bestLen)
            {
                bestLen = c.Length;
                best = c;
            }
        }
        if (best.Length < 3) return [];

        var approx = Cv2.ApproxPolyDP(best, 1.5, closed: true);
        return approx.Select(p => (roi.X + (double)p.X, roi.Y + (double)p.Y)).ToList();
    }
}
