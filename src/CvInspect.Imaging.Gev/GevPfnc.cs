namespace CvInspect.Imaging.Gev;

/// <summary>변환기가 다루는 화소 배치 — PFNC 코드가 아니라 "채널이 어떻게 놓였나"만 나타낸다.</summary>
public enum GevPixelLayout
{
    /// <summary>단채널 흑백.</summary>
    Mono,
    /// <summary>단채널 Bayer 모자이크 — 디모자이크가 필요하다.</summary>
    Bayer,
    /// <summary>3채널 R,G,B 순.</summary>
    Rgb,
    /// <summary>3채널 B,G,R 순.</summary>
    Bgr,
    /// <summary>4채널 R,G,B,A 순.</summary>
    Rgba,
    /// <summary>4채널 B,G,R,A 순.</summary>
    Bgra,
}

/// <summary>
/// PFNC(픽셀 포맷 명명 규약) 코드 해석 — 자주 쓰는 코드만 담은 얇은 표다.
/// 코드 상위 바이트에 화소당 비트 수가 들어 있다는 규약(<c>(code &gt;&gt; 16) &amp; 0xFF</c>)은 전체에 적용된다.
///
/// 취득 라이브러리가 자체 포맷 표를 제공하면 그쪽이 정본이다 — 이 표는 그것 없이도 변환이 성립하게 하는 편의일 뿐이라,
/// 여기 없는 코드는 <see cref="TryDescribe"/> 가 false 를 돌려주고 호출자가 배치를 직접 지정하면 된다.
/// </summary>
public static class GevPfnc
{
    public const uint Mono8 = 0x01080001;
    public const uint Mono10 = 0x01100003;
    public const uint Mono12 = 0x01100005;
    public const uint Mono16 = 0x01100007;

    public const uint BayerGR8 = 0x01080008;
    public const uint BayerRG8 = 0x01080009;
    public const uint BayerGB8 = 0x0108000A;
    public const uint BayerBG8 = 0x0108000B;

    public const uint Rgb8 = 0x02180014;
    public const uint Bgr8 = 0x02180015;
    public const uint Rgba8 = 0x02200016;
    public const uint Bgra8 = 0x02200017;

    /// <summary>화소당 비트 수 — PFNC 코드 자체에 실려 있다.</summary>
    public static int BitsPerPixel(uint code) => (int)((code >> 16) & 0xFF);

    /// <summary>
    /// 코드를 변환기 입력으로 풀어낸다. 아는 코드가 아니면 false — 그때는 호출자가
    /// (배치·비트수·패턴)을 직접 넘긴다.
    /// </summary>
    /// <remarks>
    /// 압축(packed) 포맷은 여기서 다루지 않는다 — 비트 스트림을 먼저 풀어 8/16비트 화소로 만든 뒤
    /// 그 결과를 <see cref="GevFrameConv"/> 에 넘겨야 한다. 같은 비트 수라도 <c>Mono10Packed</c>(3바이트에 2화소)와
    /// <c>Mono10p</c>(빈틈없는 lsb 스트림, 5바이트에 4화소)는 배치도 밀도도 달라, 한쪽 해석기로 다른 쪽을 읽으면
    /// 조용히 망가진다.
    /// </remarks>
    public static bool TryDescribe(uint code, out GevPixelLayout layout, out int bitsPerPixel, out CvBayerPattern? bayer)
    {
        bitsPerPixel = BitsPerPixel(code);
        bayer = null;
        switch (code)
        {
            case Mono8:
            case Mono10:
            case Mono12:
            case Mono16:
                layout = GevPixelLayout.Mono;
                return true;

            case BayerRG8: layout = GevPixelLayout.Bayer; bayer = CvBayerPattern.RG; return true;
            case BayerGR8: layout = GevPixelLayout.Bayer; bayer = CvBayerPattern.GR; return true;
            case BayerGB8: layout = GevPixelLayout.Bayer; bayer = CvBayerPattern.GB; return true;
            case BayerBG8: layout = GevPixelLayout.Bayer; bayer = CvBayerPattern.BG; return true;

            case Rgb8: layout = GevPixelLayout.Rgb; return true;
            case Bgr8: layout = GevPixelLayout.Bgr; return true;
            case Rgba8: layout = GevPixelLayout.Rgba; return true;
            case Bgra8: layout = GevPixelLayout.Bgra; return true;

            default:
                layout = GevPixelLayout.Mono;
                return false;
        }
    }
}
