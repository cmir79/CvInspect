using OpenCvSharp;
using CvInspect.Vision.Opts;

namespace CvInspect.Vision;

/// <summary>원환 충전율 결과 — 채워진 화소 수와 밴드 전체 화소 수, 그리고 그 비율(%).
/// <paramref name="TotalPx"/> 는 <b>밴드 전체</b>다 — 이미지 밖으로 걸친 부분까지 센다(<see cref="OutsidePx"/>).</summary>
public readonly record struct CvRingFillHit(double RatePct, double FillPx, double TotalPx, double ThresholdUsed)
{
    /// <summary>밴드 화소 중 이미지 밖이라 <b>보지 못한</b> 수. 분모(<see cref="TotalPx"/>)에는 들어가고
    /// 분자에는 안 들어가므로, 이 값이 0 이 아니면 그만큼이 "안 찬 것" 으로 계산된 것이다.
    /// 0 이 아니면 대개 중심·반경 설정이나 시야가 잘못된 것이니, 낮은 충전율을 결함으로 읽기 전에 여기를 먼저 본다.
    /// 밴드가 온전히 화면 안이면 0 이고, 그때 <see cref="RatePct"/> 는 이 값을 넣기 전의 셈과 같다(밴드 밖을 셀 일이 없다).
/// 0.29.0 부터 Bright 는 문턱과 같은 화소를 채움으로 세지 않는다 — 그 규칙은 <see cref="CvRingFill.Measure"/> 에 있다.</summary>
    public double OutsidePx { get; init; }
}

/// <summary>
/// 원환(도넛) 밴드의 충전율 — 밴드 안에서 "채워진" 화소가 몇 %인지 잰다.
/// 패킹류 검사가 "링 자리에 재료가 얼마나 찼는가"를 재는 방식이 이것 하나다.
///
/// 분모는 밴드의 화소 수를 직접 센다. 임계 0 짜리 블랍을 하나 더 돌려 그 면적을 분모로 삼는
/// 방법도 있지만, 그러면 밴드가 이미지 밖으로 조금이라도 걸치거나 블랍이 둘로 갈리는 순간
/// 분모가 조용히 달라진다 — 셈의 기준이 흔들리면 충전율은 아무 의미가 없다.
/// 같은 이유로 <b>이미지 밖으로 걸친 부분도 분모에 넣는다</b>(<see cref="CvRingFillHit.OutsidePx"/>).
/// 보이는 부분만 세면 밴드가 화면을 벗어날수록 충전율이 올라가 100% 가 되는데, 그것은 잘 찬 것이 아니라
/// 못 본 것이다 — 검사에서 가장 나쁜 방향(거짓 OK)으로 틀린다. 못 본 만큼은 "안 찬 것" 으로 계산되고,
/// 그 양을 <c>OutsidePx</c> 로 함께 내주어 호출자가 "설정이 틀렸다" 와 "정말 덜 찼다" 를 가를 수 있게 한다.
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
        // 셈은 자르지 않은 사각으로 돈다(아래 ux0..uy1): 분모가 "본 만큼" 이 되면 밴드가 화면 밖으로 걸칠수록
        // 충전율이 올라가 100% 가 되는데, 그건 잘 찬 것이 아니라 못 본 것이다.
        var ux0 = (int)Math.Floor(cx - rMax);
        var ux1 = (int)Math.Ceiling(cx + rMax);
        var uy0 = (int)Math.Floor(cy - rMax);
        var uy1 = (int)Math.Ceiling(cy + rMax);

        // 화소를 실제로 읽을 수 있는 부분은 이미지와의 교집합뿐이다.
        var x0 = Math.Max(0, ux0);
        var x1 = Math.Min(img.Cols - 1, ux1);
        var y0 = Math.Max(0, uy0);
        var y1 = Math.Min(img.Rows - 1, uy1);
        if (x1 <= x0 || y1 <= y0) return null;

        using var roi = new Mat(img, new Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1));

        // 문턱 — 자동(Otsu)이면 밴드를 감싼 사각 전체에서 낸다. 이것은 편의가 아니라 이 셈이 기대는 전제다:
        // Otsu 는 분포를 **언제나** 둘로 가르므로, 재료와 무재료 두 무리가 사각 안에 다 있어야 뜻이 있다.
        // 사각의 안쪽 구멍·모서리가 배경 무리를 대 주는 덕에, 배경과 밝기가 다른 빈 밴드·꽉 찬 밴드도 맞게 읽힌다(반만 찬
        // 밴드는 밴드 안에 두 무리가 이미 있다). 그런데 **빈 밴드가
        // 배경과 같은 밝기이거나 꽉 찬 밴드가 배경과 같은 밝기이면** 사각에 한 무리뿐이라 Otsu 가 잡음을 가른다
        // — 실측(r 40..80, 30/220, 잡음 ±5): 빈 밴드도 꽉 찬 밴드도 54.5%(Bright)·45.5%(Dark)로 읽었다. 잡음이 없으면
        // 문턱이 0 이 되어 100% 나 0% 로 간다.
        // 빈 부품이 합격하는 쪽(거짓 OK)과 멀쩡한 부품이 떨어지는 쪽 둘 다 난다. 그러니 문턱을 밴드 화소만으로 내도록
        // "고치면" 안 된다 — 배경 무리가 아예 없어져 빈 밴드·꽉 찬 밴드가 언제나 이렇게 된다.
        // 그런 부품이면 자동을 끄고 고정 문턱을 쓴다(옵션 설명에도 적었다). 고정 문턱이면 이 셈 자체가 없다.
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
        double outside = 0;

        // 크기는 루프 밖에서 한 번만 읽는다 — Mat 의 Rows/Cols 는 호출마다 네이티브를 넘는다.
        // 화소도 관리 배열로 한 번에 받아 훑는다: 밴드 하나가 수십만 화소라 화소마다 네이티브를
        // 넘나들면 검사 시간이 통째로 여기에 먹힌다.
        var rows = roi.Rows;
        var cols = roi.Cols;
        var buf = new byte[rows * cols];
        using (var packed = roi.IsContinuous() ? roi : roi.Clone())
            System.Runtime.InteropServices.Marshal.Copy(packed.Data, buf, 0, buf.Length);

        // 자르지 않은 사각을 돈다. 이미지 안이면 화소를 보고, 밖이면 분모에만 넣는다 —
        // 밴드가 온전히 화면 안이면 두 사각이 같으므로 밖을 세는 규칙은 결과를 한 화소도 바꾸지 않는다.
        for (var y = uy0; y <= uy1; y++)
        {
            var dy = y - cy;
            var dySq = dy * dy;
            var inRow = y >= y0 && y <= y1;
            var row = inRow ? (y - y0) * cols : 0;
            for (var x = ux0; x <= ux1; x++)
            {
                var dx = x - cx;
                var dSq = dx * dx + dySq;
                if (dSq < rMinSq || dSq > rMaxSq) continue;

                total++;
                if (!inRow || x < x0 || x > x1) { outside++; continue; }

                // 문턱과 같은 값은 밝은 쪽이 아니다 — OpenCV 이진화 규칙("문턱보다 큰 것" 이 전경)이고 블랍 툴도
                // 같은 규칙으로 가른다. 특히 Otsu 가 돌려주는 값은 **아래 무리의 꼭대기**라, 밝은 쪽을 ">=" 로 세면
                // 아래 무리의 한 칸이 통째로 채움이 된다. 실측: 잡음 없는 두 단계(30/220) 영상에서 문턱이 30 이 되어
                // 반만 찬 밴드가 100% 로 읽혔다(거짓 OK). Dark 는 처음부터 "<=" 라 맞았다.
                var v = buf[row + (x - x0)];
                if (bright ? v > thr : v <= thr) fill++;
            }
        }

        if (total <= 0) return null;
        return new CvRingFillHit(fill / total * 100.0, fill, total, thr) { OutsidePx = outside };
    }
}
