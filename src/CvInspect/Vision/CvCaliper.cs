using System.Text.Json.Serialization;
using OpenCvSharp;
using CvInspect.Vision.Opts;

namespace CvInspect.Vision;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CvCaliperEdgeMode
{
    /// <summary>에지 1개 검출.</summary>
    SingleEdge,

    /// <summary>에지 쌍 검출 — 폭 측정 (Edge0 → Edge1, 진행 방향 순서).</summary>
    EdgePair,
}

/// <summary>
/// 단독 캘리퍼 파라미터 — 지정 중심에서 SearchDirDeg 방향으로 1D 프로파일을 떠 에지(또는 에지 쌍)를 검출.
/// 프로파일 축 = SearchDirDeg(이미지 atan2 규약), 투영(평균) 축 = 그 수직.
/// 좌표는 대상 이미지 픽셀 공간.
/// </summary>
public sealed class CvCaliperOpt
{
    [CvCategory("cv:CatPlacement", 1)]
    [CvName("cv:CenterX")]
    [CvDesc("cv:CenterXDesc")]
    public double CenterX { get; set; }

    [CvCategory("cv:CatPlacement", 1)]
    [CvName("cv:CenterY")]
    [CvDesc("cv:CenterYDesc")]
    public double CenterY { get; set; }

    [CvCategory("cv:CatPlacement", 1)]
    [CvName("cv:SearchDirDeg")]
    [CvDesc("cv:SearchDirDegDesc")]
    public double SearchDirDeg { get; set; }

    [CvCategory("cv:CatCaliper", 2)]
    [CvName("cv:SearchLength")]
    [CvDesc("cv:CaliperSearchLengthDesc")]
    public int SearchLength { get; set; } = 40;

    [CvCategory("cv:CatCaliper", 2)]
    [CvName("cv:ProjectionLength")]
    [CvDesc("cv:CaliperProjectionLengthDesc")]
    public int ProjectionLength { get; set; } = 5;

    [CvCategory("cv:CatEdge", 3)]
    [CvName("cv:Polarity")]
    [CvDesc("cv:CaliperPolarityDesc")]
    public CvEdgePolarity Polarity { get; set; } = CvEdgePolarity.Either;

    [CvCategory("cv:CatEdge", 3)]
    [CvName("cv:ContrastThreshold")]
    [CvDesc("cv:ContrastThresholdDesc")]
    public double ContrastThreshold { get; set; } = 10;

    [CvCategory("cv:CatEdge", 3)]
    [CvName("cv:EdgeSelect")]
    [CvDesc("cv:CaliperEdgeSelectDesc")]
    public CvEdgeSelect EdgeSelect { get; set; } = CvEdgeSelect.Best;

    [CvCategory("cv:CatPair", 4)]
    [CvName("cv:EdgeMode")]
    [CvDesc("cv:EdgeModeDesc")]
    public CvCaliperEdgeMode EdgeMode { get; set; } = CvCaliperEdgeMode.SingleEdge;

    [CvCategory("cv:CatPair", 4)]
    [CvName("cv:Polarity1")]
    [CvDesc("cv:Polarity1Desc")]
    public CvEdgePolarity Polarity1 { get; set; } = CvEdgePolarity.Either;
}

/// <summary>검출 에지 1개 — 이미지 좌표(서브픽셀) + 중심 기준 부호 거리 s + 대비.</summary>
public readonly record struct CvCaliperEdge(double X, double Y, double S, double Contrast);

/// <summary>캘리퍼 결과 — SingleEdge 는 Edge0 만, EdgePair 는 Edge0/Edge1 + Width(px).</summary>
public sealed record CvCaliperResult(CvCaliperEdge Edge0, CvCaliperEdge? Edge1, double? Width);

/// <summary>
/// 단독 캘리퍼 — 1D 프로파일(투영 평균 + 바이리니어) → 미분 극성/대비/국소피크 필터 → 파라볼라 서브픽셀.
/// 라인/서클 파인더가 같은 검출 코어(DetectEdge)를 배열로 사용한다.
/// </summary>
public static class CvCaliper
{
    /// <summary>단독 실행 — 검출 실패(프로파일 불능/에지 없음) 시 null.</summary>
    public static CvCaliperResult? Run(Mat img, CvCaliperOpt opt)
    {
        var rad = opt.SearchDirDeg * Math.PI / 180.0;
        var nX = Math.Cos(rad);            // 프로파일(+s) 축
        var nY = Math.Sin(rad);
        var dirX = -nY;                    // 투영(평균) 축 — 프로파일 축의 수직
        var dirY = nX;

        if (opt.EdgeMode == CvCaliperEdgeMode.SingleEdge)
        {
            var hit = DetectEdge(img, opt.CenterX, opt.CenterY, nX, nY, dirX, dirY,
                opt.SearchLength, opt.ProjectionLength, opt.Polarity, opt.ContrastThreshold, opt.EdgeSelect);
            if (hit is null) return null;
            return new CvCaliperResult(ToEdge(opt, nX, nY, hit.Value), null, null);
        }

        // EdgePair — 후보 피크 전체를 뽑아 (극성0 에지, 그보다 +s 쪽의 극성1 에지) 중 대비 합 최대 쌍
        var half = Math.Max(2, opt.SearchLength / 2);
        var proj = Math.Max(1, opt.ProjectionLength);
        var prof = SampleProfile(img, opt.CenterX, opt.CenterY, nX, nY, dirX, dirY, half, proj);
        if (prof is null) return null;

        var peaks0 = CollectPeaks(prof, opt.Polarity, opt.ContrastThreshold, half);
        var peaks1 = CollectPeaks(prof, opt.Polarity1, opt.ContrastThreshold, half);
        if (peaks0.Count == 0 || peaks1.Count == 0) return null;

        (double S, double Mag) e0 = default, e1 = default;
        var bestSum = double.NegativeInfinity;
        foreach (var p0 in peaks0)
            foreach (var p1 in peaks1)
            {
                if (p1.S <= p0.S) continue;
                var sum = p0.Mag + p1.Mag;
                if (sum > bestSum) { bestSum = sum; e0 = p0; e1 = p1; }
            }
        if (double.IsNegativeInfinity(bestSum)) return null;

        var edge0 = ToEdge(opt, nX, nY, e0);
        var edge1 = ToEdge(opt, nX, nY, e1);
        return new CvCaliperResult(edge0, edge1, e1.S - e0.S);
    }

    private static CvCaliperEdge ToEdge(CvCaliperOpt opt, double nX, double nY, (double S, double Mag) hit)
        => new(opt.CenterX + nX * hit.S, opt.CenterY + nY * hit.S, hit.S, hit.Mag);

    /// <summary>
    /// 검출 코어 — 중심(cx,cy)에서 법선(n) 방향 프로파일의 에지 1개.
    /// 반환 S 는 중심 기준 부호 거리(px), Mag 는 대비. 파인더들(라인/서클)이 배열 호출한다.
    /// </summary>
    internal static (double S, double Mag)? DetectEdge(
        Mat img, double cx, double cy, double nX, double nY, double dirX, double dirY,
        int searchLength, int projectionLength,
        CvEdgePolarity polarity, double contrastThreshold, CvEdgeSelect edgeSelect)
    {
        var half = Math.Max(2, searchLength / 2);
        var proj = Math.Max(1, projectionLength);

        var prof = SampleProfile(img, cx, cy, nX, nY, dirX, dirY, half, proj);
        if (prof is null) return null;

        var profLen = prof.Length;

        // 중앙차분 미분 — 극성/대비 만족 최강 에지 + 파라볼라 서브픽셀
        var mags = new double[profLen];
        var signs = new double[profLen];
        for (var k = 1; k < profLen - 1; k++)
        {
            var d = (prof[k + 1] - prof[k - 1]) / 2.0;
            signs[k] = d;
            mags[k] = Math.Abs(d);
        }

        // 후보 = 극성/대비 만족 지점 중 국소 최대(이웃보다 크거나 같음) — First/Last 가 에지 램프의
        // 문턱 통과점(대비 의존 위치)이 아닌 에지 중심(미분 피크)을 잡도록.
        var bestIdx = -1;
        var bestMag = 0.0;
        for (var k = 1; k < profLen - 1; k++)
        {
            var ok = polarity switch
            {
                CvEdgePolarity.DarkToLight => signs[k] >= contrastThreshold,
                CvEdgePolarity.LightToDark => -signs[k] >= contrastThreshold,
                _ => mags[k] >= contrastThreshold,
            };
            if (!ok) continue;

            var isPeak = mags[k] >= (k > 1 ? mags[k - 1] : 0) && mags[k] >= (k < profLen - 2 ? mags[k + 1] : 0);

            switch (edgeSelect)
            {
                case CvEdgeSelect.First:
                    if (isPeak) { bestIdx = k; k = profLen; }   // 첫 피크에서 종료
                    break;
                case CvEdgeSelect.Last:
                    if (isPeak) bestIdx = k;                    // 계속 갱신 — 마지막 피크
                    break;
                default:
                    if (mags[k] > bestMag) { bestMag = mags[k]; bestIdx = k; }
                    break;
            }
        }
        if (bestIdx <= 0) return null;

        var sSub = (double)bestIdx;
        if (bestIdx > 1 && bestIdx < profLen - 2)
        {
            var mPrev = mags[bestIdx - 1];
            var m0 = mags[bestIdx];
            var mNext = mags[bestIdx + 1];
            var denom = mPrev - 2 * m0 + mNext;
            if (Math.Abs(denom) > 1e-12)
                sSub += Math.Clamp(0.5 * (mPrev - mNext) / denom, -0.5, 0.5);
        }

        return (sSub - half, mags[bestIdx]);
    }

    /// <summary>법선 프로파일 추출 — 진행(투영) 방향으로 proj 폭 평균, 바이리니어.
    /// 이미지 밖으로 나간 열(전 샘플 무효)은 가장 가까운 유효 값으로 경계 복제 — 프로파일이 부분적으로
    /// 화면 밖이어도 유효 부분에서 에지를 검출한다(무효 구간은 평탄 → 미분 0 → 가짜 에지 없음).
    /// 큰 회전으로 세그먼트가 가장자리로 쓸려 캘리퍼 한쪽이 밖이어도 전멸하지 않게. 전 열이 밖이면 null.</summary>
    internal static double[]? SampleProfile(
        Mat img, double cx, double cy, double nX, double nY, double dirX, double dirY, int half, int proj)
    {
        var profLen = 2 * half + 1;
        var prof = new double[profLen];
        var valid = new bool[profLen];
        var anyValid = false;
        for (var k = -half; k <= half; k++)
        {
            double sum = 0;
            var cnt = 0;
            for (var p = 0; p < proj; p++)
            {
                var off = p - (proj - 1) / 2.0;
                var v = SampleBilinear(img, cx + nX * k + dirX * off, cy + nY * k + dirY * off);
                if (v is null) continue;
                sum += v.Value;
                cnt++;
            }
            if (cnt > 0)
            {
                prof[k + half] = sum / cnt;
                valid[k + half] = true;
                anyValid = true;
            }
        }
        if (!anyValid) return null;   // 프로파일 전체가 이미지 밖 — 검출 불능

        // 무효(이미지 밖) 열 경계 복제 — 볼록 이미지에서 유효 열은 연속이라 전방 채움으로 앞/뒤(및 드문 중간 결손) 모두 덮음.
        var firstValid = 0;
        while (!valid[firstValid]) firstValid++;
        for (var k = 0; k < firstValid; k++) prof[k] = prof[firstValid];
        for (var k = firstValid + 1; k < profLen; k++)
            if (!valid[k]) prof[k] = prof[k - 1];

        return prof;
    }

    /// <summary>프로파일의 극성/대비 만족 국소 피크 전부 — EdgePair 용. 각 피크는 파라볼라 서브픽셀 적용.</summary>
    private static List<(double S, double Mag)> CollectPeaks(double[] prof, CvEdgePolarity polarity, double contrastThreshold, int half)
    {
        var profLen = prof.Length;
        var mags = new double[profLen];
        var signs = new double[profLen];
        for (var k = 1; k < profLen - 1; k++)
        {
            var d = (prof[k + 1] - prof[k - 1]) / 2.0;
            signs[k] = d;
            mags[k] = Math.Abs(d);
        }

        var peaks = new List<(double S, double Mag)>();
        for (var k = 1; k < profLen - 1; k++)
        {
            var ok = polarity switch
            {
                CvEdgePolarity.DarkToLight => signs[k] >= contrastThreshold,
                CvEdgePolarity.LightToDark => -signs[k] >= contrastThreshold,
                _ => mags[k] >= contrastThreshold,
            };
            if (!ok) continue;
            var isPeak = mags[k] >= (k > 1 ? mags[k - 1] : 0) && mags[k] >= (k < profLen - 2 ? mags[k + 1] : 0);
            if (!isPeak) continue;

            var sSub = (double)k;
            if (k > 1 && k < profLen - 2)
            {
                var denom = mags[k - 1] - 2 * mags[k] + mags[k + 1];
                if (Math.Abs(denom) > 1e-12)
                    sSub += Math.Clamp(0.5 * (mags[k - 1] - mags[k + 1]) / denom, -0.5, 0.5);
            }
            peaks.Add((sSub - half, mags[k]));
        }
        return peaks;
    }

    /// <summary>바이리니어 샘플 — 이미지 밖은 null.
    /// 판정을 <b>부정형으로</b> 쓴 것은 NaN 방어다: NaN 은 어떤 비교에도 false 라 "밖이면 null" 형태의
    /// 조건을 그냥 통과하고, 그 뒤 (int)NaN = int.MinValue 로 네이티브 픽셀을 찔러 접근 위반으로
    /// 프로세스가 죽는다(캐치 불가). 좌표가 유한하고 범위 안일 때만 샘플한다.</summary>
    internal static double? SampleBilinear(Mat img, double x, double y)
    {
        if (!(x >= 0 && y >= 0 && x <= img.Cols - 1.001 && y <= img.Rows - 1.001)) return null;
        var x0 = (int)x;
        var y0 = (int)y;
        var fx = x - x0;
        var fy = y - y0;
        double v00 = img.At<byte>(y0, x0);
        double v01 = img.At<byte>(y0, x0 + 1);
        double v10 = img.At<byte>(y0 + 1, x0);
        double v11 = img.At<byte>(y0 + 1, x0 + 1);
        return v00 * (1 - fx) * (1 - fy) + v01 * fx * (1 - fy) + v10 * (1 - fx) * fy + v11 * fx * fy;
    }
}
