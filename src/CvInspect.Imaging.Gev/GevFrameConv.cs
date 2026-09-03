using OpenCvSharp;

namespace CvInspect.Imaging.Gev;

/// <summary>
/// 취득 프레임의 화소를 <see cref="CamFrame"/>(Mono8 / Bgr24 / Bgra32) 으로 옮긴다.
///
/// 입력은 <b>풀린(unpacked)</b> 화소여야 한다 — 압축 포맷은 취득 라이브러리가 먼저 8/16비트 화소로 풀어 준다.
/// 프레임 객체가 아니라 원시 배열을 받으므로 장비 없이 단위 테스트할 수 있다.
/// </summary>
public static class GevFrameConv
{
    /// <summary>
    /// 화소 → <see cref="CamFrame"/>.
    /// </summary>
    /// <param name="pixels">풀린 화소. 변환이 필요 없는 배치(Mono8·Bgr24·Bgra32)에서는 <b>이 배열을 그대로 소유</b>하므로,
    /// 재사용 버퍼가 아니라 갓 만든 배열을 넘겨야 한다.</param>
    /// <param name="width">화소 단위 가로 크기.</param>
    /// <param name="height">화소 단위 세로 크기.</param>
    /// <param name="stride">행 바이트 수 — 장치가 행 끝을 채우면 가로×바이트/화소보다 클 수 있다.
    /// <b>0 이하면 "줄 간격이 없다"</b>로 보고 빈틈없는 값(가로×바이트/화소)으로 친다. 취득 라이브러리가
    /// 압축 포맷에서 줄이 바이트 경계에 안 떨어질 때 그렇게 알리는데, 여기 오는 화소는 이미 풀린 뒤라
    /// 그 경우 실제 배치가 빈틈없는 행이 된다.</param>
    /// <param name="layout">채널 배치.</param>
    /// <param name="significantBits">화소가 실제로 담고 있는 <b>유효 비트 수</b>(깊이)이지, PFNC 코드가 차지하는
    /// 비트 수가 아니다 — <b>Mono10 은 10, Mono12 는 12</b> 다(둘 다 16비트 그릇에 담기지만 코드의 점유 비트는 16).
    /// 이걸 점유 비트로 넘기면 예외 없이 화면이 4배·16배 어두워진다. 9~16 이면 16비트 우측 정렬로 보고
    /// 상위 8비트만 남기고, 컬러 배치에서는 채널당 비트 수(8)를 넘긴다.</param>
    /// <param name="bayer">Bayer 배치일 때의 패턴 — <b>전송 영상의 실효 패턴</b>이다(<see cref="CvBayerPhase"/> 참조).</param>
    /// <param name="toMono">Bayer 를 컬러로 펴지 않고 흑백으로 바로 접는다(검사만 하고 화면에 색이 필요 없을 때).</param>
    public static CamFrame ToCamFrame(
        byte[] pixels, int width, int height, int stride,
        GevPixelLayout layout, int significantBits,
        CvBayerPattern? bayer = null, bool toMono = false)
    {
        if (pixels is null) throw new ArgumentNullException(nameof(pixels));
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), $"Invalid frame size: {width}x{height}");

        // "줄 간격 없음"(0) 을 빈틈없는 행으로 정규화한다. CamFrame.Stride 는 항상 양수여야 하고,
        // 0 을 그대로 넘기면 그 프레임을 받는 모든 소비자가 행을 못 건다.
        var lineBytes = width * TightBytesPerPixel(layout, significantBits);
        if (stride <= 0) stride = lineBytes;

        // 버퍼가 그 기하를 실제로 담는지 확인한다. 짧은 버퍼를 그대로 Mat 에 물리면 <b>관리 힙 밖을 읽는다</b> —
        // 예외도 없이 쓰레기 화소가 나오거나 프로세스가 죽는다. 마지막 행은 패딩까지 필요하지 않으므로
        // 정확한 하한으로 잰다(멀쩡한 버퍼를 거절하지 않게).
        // 부수 효과로, 아직 풀지 않은 압축 화소를 푼 것인 양 넘기는 실수도 대개 여기서 걸린다.
        var need = (long)stride * (height - 1) + lineBytes;
        if (pixels.Length < need)
            throw new ArgumentException(
                $"Pixel buffer is too small: {pixels.Length} bytes for {width}x{height} {layout}/{significantBits}bit at stride {stride} (needs {need}). Are the pixels still packed?",
                nameof(pixels));

        switch (layout)
        {
            case GevPixelLayout.Mono:
            {
                if (significantBits <= 8)
                    return new CamFrame(pixels, width, height, stride, CamPixelFormat.Mono8);
                using var mono8 = DownShift(pixels, width, height, stride, significantBits);
                return CamFrame.FromMat(mono8);
            }

            case GevPixelLayout.Bayer:
            {
                var pattern = bayer ?? throw new ArgumentNullException(
                    nameof(bayer), "A Bayer layout needs the pattern of the transmitted image.");
                using var src = significantBits <= 8
                    ? Mat.FromPixelData(height, width, MatType.CV_8UC1, pixels, stride)
                    : DownShift(pixels, width, height, stride, significantBits);
                using var dst = new Mat();
                Cv2.CvtColor(src, dst, DemosaicCode(pattern, toMono));
                return CamFrame.FromMat(dst);
            }

            case GevPixelLayout.Bgr:
                return new CamFrame(pixels, width, height, stride, CamPixelFormat.Bgr24);

            case GevPixelLayout.Bgra:
                return new CamFrame(pixels, width, height, stride, CamPixelFormat.Bgra32);

            case GevPixelLayout.Rgb:
            {
                using var src = Mat.FromPixelData(height, width, MatType.CV_8UC3, pixels, stride);
                using var dst = new Mat();
                Cv2.CvtColor(src, dst, ColorConversionCodes.RGB2BGR);
                return CamFrame.FromMat(dst);
            }

            case GevPixelLayout.Rgba:
            {
                using var src = Mat.FromPixelData(height, width, MatType.CV_8UC4, pixels, stride);
                using var dst = new Mat();
                Cv2.CvtColor(src, dst, ColorConversionCodes.RGBA2BGRA);
                return CamFrame.FromMat(dst);
            }

            default:
                throw new NotSupportedException($"Unsupported pixel layout: {layout}");
        }
    }

    /// <summary>
    /// Bayer 패턴 → OpenCV 디모자이크 코드. 규칙은 <b>글자에서 R 과 B 를 맞바꾸는 것</b>이다
    /// (RG→BG, GR→GB, GB→GR, BG→RG). OpenCV 는 (1,1) 화소를 기준으로, 표준 패턴 명명은 (0,0) 을 기준으로 하기 때문에
    /// 생기는 대각 한 칸 차이이며, 16개 조합을 합성 모자이크로 전부 돌려 확정했다.
    ///
    /// ⚠ <b>이름이 어긋나 보이는 것이 정상이다 — 고치지 말 것.</b> 게다가 OpenCV 는 이 둘을 같은 정수로 정의해 두었다:
    /// <c>BayerRG2BGR</c> 과 <c>BayerBG2RGB</c> 가 같은 값이다. 패턴 글자와 출력 채널 순서를 <b>둘 다</b> 틀리면
    /// 서로 상쇄되어 컴파일도 되고 결과도 맞다. 그래서 "여기는 RG 인데 왜 BG 를 쓰나" 하고 한쪽만 고치는 순간
    /// 빨강과 파랑이 뒤집힌다.
    /// </summary>
    public static ColorConversionCodes DemosaicCode(CvBayerPattern pattern, bool toMono = false) => pattern switch
    {
        CvBayerPattern.RG => toMono ? ColorConversionCodes.BayerBG2GRAY : ColorConversionCodes.BayerBG2BGR,
        CvBayerPattern.GR => toMono ? ColorConversionCodes.BayerGB2GRAY : ColorConversionCodes.BayerGB2BGR,
        CvBayerPattern.GB => toMono ? ColorConversionCodes.BayerGR2GRAY : ColorConversionCodes.BayerGR2BGR,
        _ => toMono ? ColorConversionCodes.BayerRG2GRAY : ColorConversionCodes.BayerRG2BGR,
    };

    /// <summary>빈틈없이 채웠을 때의 화소당 바이트 수 — 줄 간격이 주어지지 않았을 때 쓴다.</summary>
    private static int TightBytesPerPixel(GevPixelLayout layout, int significantBits) => layout switch
    {
        GevPixelLayout.Mono or GevPixelLayout.Bayer => significantBits <= 8 ? 1 : 2,
        GevPixelLayout.Rgb or GevPixelLayout.Bgr => 3,
        _ => 4,
    };

    /// <summary>9~16비트 → 8비트. 값이 우측 정렬(0 ~ 2^n−1)이라는 전제로 상위 8비트만 남긴다.</summary>
    private static Mat DownShift(byte[] pixels, int width, int height, int stride, int significantBits)
    {
        if (significantBits is < 9 or > 16)
            throw new NotSupportedException($"Unsupported bit depth: {significantBits}");
        using var src = Mat.FromPixelData(height, width, MatType.CV_16UC1, pixels, stride);
        var dst = new Mat();
        src.ConvertTo(dst, MatType.CV_8UC1, 1.0 / (1 << (significantBits - 8)));
        return dst;
    }
}
