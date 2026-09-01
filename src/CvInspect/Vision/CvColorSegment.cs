using OpenCvSharp;
using CvInspect.Vision.Opts;

namespace CvInspect.Vision;

/// <summary>
/// 컬러 세그먼트 — HSV 대역(<see cref="CvColorSegmentOpt"/>)으로 픽셀을 가른다.
/// 색 학습(<see cref="Sample"/>)은 티칭 영역 픽셀 분포에서 대역을 산출하고,
/// 마스크(<see cref="BuildMask"/>)는 H 랩어라운드(빨강 계열이 0/179 경계에 걸침)를 두 구간 OR 로 처리한다.
/// 영역 기하(사각/링)는 공용 <see cref="CvRegionMask"/> 가 담당 — 여기는 색만 안다.
/// 판정은 리샘플 없는 래스터 마스크 계수 — 언랩(극좌표 전개)은 위상이 필요한 각도 측정용이고,
/// 면적 비율만 잴 때는 반경별 가중이 틀어지는 리샘플을 끼울 이유가 없다.
/// </summary>
public static class CvColorSegment
{
    /// <summary>색 학습이 무채색으로 판단하는 채도 상한 — 이하이면 H 는 노이즈라 전 범위로 연다.</summary>
    private const int AchromaticSatMax = 40;

    /// <summary>
    /// 색 학습 — 티칭 영역(opt.Region, bgr 이미지 공간) 픽셀의 HSV 분포에서 대역 산출 후 opt 에 기록.
    /// H 는 원형 평균 중심 + 원형 편차 기반 허용폭(5~40 클램프), S/V 는 5~95 퍼센타일 ± 여유 20.
    /// 무채색 표본(채도 95퍼센타일 &lt; 40)은 H 전 범위(±90)로 열어 S/V 만으로 가른다.
    /// 영역이 이미지 밖이거나 표본이 부족하면(&lt;16px) false.
    /// </summary>
    public static bool Sample(Mat bgr, CvColorSegmentOpt opt)
    {
        var (bbox, regionMask) = CvRegionMask.MaskOf(bgr.Cols, bgr.Rows, opt.Region);
        if (regionMask is null) return false;

        using var _ = regionMask;
        regionMask.GetArray(out byte[] mask);
        if (mask.Count(v => v != 0) < 16) return false;

        using var patch = new Mat(bgr, bbox);
        using var hsv = new Mat();
        Cv2.CvtColor(patch, hsv, ColorConversionCodes.BGR2HSV);

        var channels = Cv2.Split(hsv);
        try
        {
            var s = Percentiles(channels[1], mask);
            var v = Percentiles(channels[2], mask);

            opt.SatMin = Math.Clamp(s.P5 - 20, 0, 255);
            opt.SatMax = Math.Clamp(s.P95 + 20, 0, 255);
            opt.ValMin = Math.Clamp(v.P5 - 20, 0, 255);
            opt.ValMax = Math.Clamp(v.P95 + 20, 0, 255);

            if (s.P95 < AchromaticSatMax)
            {
                // 무채색(금속·회색) — H 는 노이즈다. 전 범위로 열고 S/V 대역만으로 가른다.
                opt.HueCenter = 90;
                opt.HueTol = 90;
            }
            else
            {
                var (center, tol) = HueBand(channels[0], mask);
                opt.HueCenter = center;
                opt.HueTol = tol;
            }
        }
        finally
        {
            foreach (var c in channels) c.Dispose();
        }

        opt.Trained = true;
        return true;
    }

    /// <summary>
    /// 링 밴드 안의 등록 색 픽셀 비율 — 중심·반경은 포즈 변환된 값을 받는다
    /// (패턴 스케일 탐색 시 반경도 발견 스케일만큼 늘어야 밴드가 대상에 붙는다 — 호출자가 곱해 넘긴다).
    /// 분모는 <b>클리핑 전 링 기하 면적</b> π(R²−r²) — 밴드가 이미지 밖으로 걸치면 보이는 픽셀만
    /// 세어져 비율이 낮아지고 NG 쪽(안전 방향)으로 기운다. 반환 bbox 는 원본 공간(썸네일 ROI 용).
    /// 무효면 (0, null).
    /// </summary>
    public static (double Ratio, Rect? Bbox) MaskRatioInRing(Mat bgr, double cx, double cy, double rMinPx, double rMaxPx, CvColorSegmentOpt opt)
    {
        var (bbox, ringMask) = CvRegionMask.RingMask(bgr.Cols, bgr.Rows, cx, cy, rMinPx, rMaxPx);
        if (ringMask is null) return (0, null);

        using var _ = ringMask;
        var denom = CvRegionMask.RingArea(rMinPx, rMaxPx);
        if (denom < 1) return (0, null);

        return CountRatio(bgr, bbox, ringMask, denom, opt);
    }

    /// <summary>
    /// 폴리곤(사각 영역의 포즈 변환 결과) 안의 등록 색 픽셀 비율 — 분모는 클리핑 전 폴리곤 기하 면적.
    /// 화면 밖 걸침의 편향 방향은 링 판과 동일(NG 안전). 무효면 (0, null).
    /// </summary>
    public static (double Ratio, Rect? Bbox) MaskRatioInPoly(Mat bgr, (double X, double Y)[] poly, CvColorSegmentOpt opt)
    {
        var (bbox, polyMask) = CvRegionMask.PolyMask(bgr.Cols, bgr.Rows, poly);
        if (polyMask is null) return (0, null);

        using var _ = polyMask;
        var denom = CvRegionMask.PolyArea(poly);
        if (denom < 1) return (0, null);

        return CountRatio(bgr, bbox, polyMask, denom, opt);
    }

    private static (double Ratio, Rect? Bbox) CountRatio(Mat bgr, Rect bbox, Mat regionMask, double denom, CvColorSegmentOpt opt)
    {
        using var patch = new Mat(bgr, bbox);
        using var seg = BuildMask(patch, opt);
        using var hit = new Mat();
        Cv2.BitwiseAnd(seg, regionMask, hit);
        return (Cv2.CountNonZero(hit) / denom, bbox);
    }

    /// <summary>
    /// 등록 대역 마스크 (CV_8UC1, 255=대역 안) — 입력은 BGR. H 랩어라운드는 두 inRange OR.
    /// MorphOpenKernel ≥ 3 이면 열림 연산으로 점 노이즈 제거 (짝수는 홀수로 올림).
    /// </summary>
    public static Mat BuildMask(Mat bgr, CvColorSegmentOpt opt)
    {
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);

        var hLo = opt.HueCenter - opt.HueTol;
        var hHi = opt.HueCenter + opt.HueTol;

        var mask = new Mat();
        if (opt.HueTol >= 90)
        {
            // H 전 범위 — S/V 만으로 가른다.
            Cv2.InRange(hsv,
                new Scalar(0, opt.SatMin, opt.ValMin),
                new Scalar(179, opt.SatMax, opt.ValMax), mask);
        }
        else if (hLo >= 0 && hHi <= 179)
        {
            Cv2.InRange(hsv,
                new Scalar(hLo, opt.SatMin, opt.ValMin),
                new Scalar(hHi, opt.SatMax, opt.ValMax), mask);
        }
        else
        {
            // 랩어라운드 — 빨강 계열이 0/179 경계에 걸린다. 두 구간을 따로 뽑아 합친다.
            using var a = new Mat();
            using var b = new Mat();
            Cv2.InRange(hsv,
                new Scalar(Mod180(hLo), opt.SatMin, opt.ValMin),
                new Scalar(179, opt.SatMax, opt.ValMax), a);
            Cv2.InRange(hsv,
                new Scalar(0, opt.SatMin, opt.ValMin),
                new Scalar(Mod180(hHi), opt.SatMax, opt.ValMax), b);
            Cv2.BitwiseOr(a, b, mask);
        }

        if (opt.MorphOpenKernel >= 3)
        {
            var k = opt.MorphOpenKernel | 1;   // 홀수 강제
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(k, k));
            Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kernel);
        }
        return mask;
    }

    private static double Mod180(double h) => ((h % 180) + 180) % 180;

    /// <summary>H 채널의 원형 평균 중심 + 허용폭 — 원형 편차 2.5배, 5~40 클램프 (H 는 0~179 원환).
    /// mask 가 0 이 아닌 자리만 표본 (영역 한정 샘플링).</summary>
    private static (double Center, double Tol) HueBand(Mat hue, byte[] mask)
    {
        // 픽셀 단위 At() 은 호출마다 P/Invoke 라 배열로 한 번에 뽑는다 (Split 출력은 연속 메모리).
        hue.GetArray(out byte[] px);

        // H 1칸 = 2° — 원형 통계는 실각(0~360°)으로 환산해 계산한다. 180개 값뿐이라 sin/cos 는 표로.
        var sin = new double[180];
        var cos = new double[180];
        for (var i = 0; i < 180; i++)
        {
            var rad = i * 2.0 * Math.PI / 180.0;
            sin[i] = Math.Sin(rad);
            cos[i] = Math.Cos(rad);
        }

        double sumSin = 0, sumCos = 0;
        long n = 0;
        for (var i = 0; i < px.Length; i++)
        {
            if (mask[i] == 0) continue;
            sumSin += sin[px[i]];
            sumCos += cos[px[i]];
            n++;
        }
        if (n == 0) return (90, 90);

        var meanRad = Math.Atan2(sumSin / n, sumCos / n);
        var center = ((meanRad * 180.0 / Math.PI / 2.0) % 180 + 180) % 180;

        // 원형 분산 → 표준편차(°) → H 단위. R(합벡터 길이)이 1 에 가까울수록 색이 몰려 있다.
        var r = Math.Sqrt(sumSin * sumSin + sumCos * sumCos) / n;
        var stdDeg = Math.Sqrt(Math.Max(0, -2.0 * Math.Log(Math.Max(r, 1e-9)))) * 180.0 / Math.PI;
        var tol = Math.Clamp(stdDeg / 2.0 * 2.5, 5, 40);   // ° → H 단위(÷2), 여유 2.5σ
        return (center, tol);
    }

    /// <summary>단채널 5/95 퍼센타일 — 256 히스토그램 기반 (표본 노이즈 양끝 컷). mask 가 0 이 아닌 자리만 표본.</summary>
    private static (int P5, int P95) Percentiles(Mat ch, byte[] mask)
    {
        ch.GetArray(out byte[] px);
        var hist = new long[256];
        long total = 0;
        for (var i = 0; i < px.Length; i++)
        {
            if (mask[i] == 0) continue;
            hist[px[i]]++;
            total++;
        }
        if (total == 0) return (0, 255);

        // 하한은 최소 1 — 20픽셀 미만 표본에서 5% 절사값이 0 이 되면 p5 루프가 첫 인덱스에서
        // 무조건 멈춰 하한이 데이터와 무관하게 0 으로 붕괴한다(대역이 조용히 전개방 = 오검 편향).
        long lo = Math.Max(1, (long)Math.Ceiling(total * 0.05));
        long hi = (long)(total * 0.95);

        int p5 = 0, p95 = 255;
        long cum = 0;
        for (var i = 0; i < 256; i++)
        {
            cum += hist[i];
            if (cum >= lo) { p5 = i; break; }
        }
        cum = 0;
        for (var i = 0; i < 256; i++)
        {
            cum += hist[i];
            if (cum >= hi) { p95 = i; break; }
        }
        return (p5, p95);
    }
}
