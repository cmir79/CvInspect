using OpenCvSharp;
using CvInspect.Vision.Opts;

namespace CvInspect.Vision;

/// <summary>원환 충전율 결과 — 채워진 화소 수와 밴드 전체 화소 수, 그리고 그 비율(%).</summary>
public readonly record struct CvRingFillHit(double RatePct, double FillPx, double TotalPx, double ThresholdUsed);

/// <summary>
/// 원환(도넛) 밴드의 충전율 — 밴드 안에서 "채워진" 화소가 몇 %인지 잰다.
/// 패킹류 검사가 "링 자리에 재료가 얼마나 찼는가"를 재는 방식이 이것 하나다.
///
/// 분모는 밴드의 화소 수를 직접 센다. 임계 0 짜리 블랍을 하나 더 돌려 그 면적을 분모로 삼는
/// 방법도 있지만, 그러면 밴드가 이미지 밖으로 조금이라도 걸치거나 블랍이 둘로 갈리는 순간
/// 분모가 조용히 달라진다 — 셈의 기준이 흔들리면 충전율은 아무 의미가 없다.
///
/// 중심은 호출자가 준다. 링 위치는 부품마다 원 피팅이나 블랍으로 먼저 잡고, 이 부품은
/// 그 중심을 받아 밴드만 세는 일에 집중한다.
/// </summary>
public static class CvRingFill
{
    public static CvRingFillHit? Measure(Mat img, double cx, double cy, CvRingFillOpt opt)
    {
        if (img is null || img.Empty() || opt is null) return null;

        var rMin = Math.Min(opt.RMinPx, opt.RMaxPx);
        var rMax = Math.Max(opt.RMinPx, opt.RMaxPx);
        if (rMax <= 0 || rMax - rMin < 1) return null;

        // 밴드를 감싸는 사각만 훑는다 — 전체 이미지를 도는 것과 결과는 같고 값싸다.
        var x0 = Math.Max(0, (int)Math.Floor(cx - rMax));
        var x1 = Math.Min(img.Cols - 1, (int)Math.Ceiling(cx + rMax));
        var y0 = Math.Max(0, (int)Math.Floor(cy - rMax));
        var y1 = Math.Min(img.Rows - 1, (int)Math.Ceiling(cy + rMax));
        if (x1 <= x0 || y1 <= y0) return null;

        using var roi = new Mat(img, new Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1));

        // 문턱 — 밴드 화소만 놓고 정하는 것이 옳지만, Otsu 는 Mat 단위라 사각 전체로 낸다.
        // 밴드가 사각의 대부분을 차지하므로 실용상 같고, 고정 문턱이면 이 셈 자체가 없다.
        double thr;
        if (opt.UseOtsu)
        {
            using var tmp = new Mat();
            thr = Cv2.Threshold(roi, tmp, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        }
        else
        {
            thr = opt.Threshold;
        }

        var bright = opt.Polarity == CvBlobPolarity.Bright;
        var rMinSq = rMin * rMin;
        var rMaxSq = rMax * rMax;

        double total = 0;
        double fill = 0;

        // 크기는 루프 밖에서 한 번만 읽는다 — Mat 의 Rows/Cols 는 호출마다 네이티브를 넘는다.
        // 화소도 관리 배열로 한 번에 받아 훑는다: 밴드 하나가 수십만 화소라 화소마다 네이티브를
        // 넘나들면 검사 시간이 통째로 여기에 먹힌다.
        var rows = roi.Rows;
        var cols = roi.Cols;
        var buf = new byte[rows * cols];
        using (var packed = roi.IsContinuous() ? roi : roi.Clone())
            System.Runtime.InteropServices.Marshal.Copy(packed.Data, buf, 0, buf.Length);

        for (var y = 0; y < rows; y++)
        {
            var dy = y0 + y - cy;
            var dySq = dy * dy;
            var row = y * cols;
            for (var x = 0; x < cols; x++)
            {
                var dx = x0 + x - cx;
                var dSq = dx * dx + dySq;
                if (dSq < rMinSq || dSq > rMaxSq) continue;

                total++;
                var v = buf[row + x];
                if (bright ? v >= thr : v <= thr) fill++;
            }
        }

        if (total <= 0) return null;
        return new CvRingFillHit(fill / total * 100.0, fill, total, thr);
    }
}
