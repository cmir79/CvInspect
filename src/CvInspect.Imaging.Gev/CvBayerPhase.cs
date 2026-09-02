namespace CvInspect.Imaging.Gev;

/// <summary>Bayer 컬러 필터 배열의 2×2 패턴 — 이미지 좌상단(0,0) 화소부터 읽은 배열이다.</summary>
public enum CvBayerPattern
{
    /// <summary>R G / G B</summary>
    RG,
    /// <summary>G R / B G</summary>
    GR,
    /// <summary>G B / R G</summary>
    GB,
    /// <summary>B G / G R</summary>
    BG,
}

/// <summary>
/// Bayer 패턴의 <b>위상(phase)</b> 계산 — 센서가 선언한 패턴과 실제로 전송되는 영상의 패턴이 달라지는 경우를 다룬다.
///
/// 카메라의 영상 처리 순서는 비닝/데시메이션 → 좌우·상하 미러 → ROI 잘라내기다. 미러와 잘라내기는 2×2 배열의
/// 시작점을 옮기므로, 같은 센서라도 <b>ROI 오프셋이 홀수</b>이거나 <b>미러가 켜져</b> 있으면 전송 영상의 실효 패턴이
/// 바뀐다. 예외도 경고도 없이 색만 뒤바뀌므로 현장에서 가장 잡기 어려운 부류의 결함이 된다.
///
/// 결론은 단순하다 — <b>패리티 두 비트</b>가 전부다. 가로/세로 각각 시작 화소가 짝수 칸인지 홀수 칸인지만 결정하면
/// 실효 패턴이 정해진다(전수 대조로 확인).
///
/// ⚠ 계산값을 무조건 적용하면 안 된다. 이미 보정해서 보고하는 펌웨어에서는 <b>이중 보정</b>이 되어 오히려 틀린다.
/// 장치가 선언한 패턴을 1차로 쓰고, 이 계산과 어긋날 때 <b>양쪽을 모두 알리는</b> 용도로 쓴다.
/// </summary>
public static class CvBayerPhase
{
    // [패턴][가로 패리티][세로 패리티] — 2×2 타일을 (dx,dy) 만큼 민 결과.
    private static readonly CvBayerPattern[,,] _shift =
    {
        // RG = R G / G B
        { { CvBayerPattern.RG, CvBayerPattern.GB }, { CvBayerPattern.GR, CvBayerPattern.BG } },
        // GR = G R / B G
        { { CvBayerPattern.GR, CvBayerPattern.BG }, { CvBayerPattern.RG, CvBayerPattern.GB } },
        // GB = G B / R G
        { { CvBayerPattern.GB, CvBayerPattern.RG }, { CvBayerPattern.BG, CvBayerPattern.GR } },
        // BG = B G / G R
        { { CvBayerPattern.BG, CvBayerPattern.GR }, { CvBayerPattern.GB, CvBayerPattern.RG } },
    };

    /// <summary>패턴을 (dx, dy) 화소만큼 민 결과. 패리티만 의미가 있다.</summary>
    public static CvBayerPattern Shift(CvBayerPattern pattern, int dx, int dy)
        => _shift[(int)pattern, Parity(dx), Parity(dy)];

    /// <summary>
    /// 카메라 설정으로부터 <b>전송 영상의 실효 패턴</b>을 계산한다.
    /// </summary>
    /// <param name="sensorPattern">센서 고유 패턴(전체 화면·미러 꺼짐·오프셋 0 일 때의 배열).</param>
    /// <param name="sensorWidth">미러가 적용되는 기준 폭 — 센서 전체 폭(WidthMax)이지 ROI 폭이 아니다.</param>
    /// <param name="sensorHeight">같은 의미의 기준 높이(HeightMax).</param>
    /// <param name="reverseX">카메라 내부 좌우 미러(호스트 측 플립이 아니다).</param>
    /// <param name="reverseY">카메라 내부 상하 미러.</param>
    /// <param name="offsetX">ROI 가로 오프셋.</param>
    /// <param name="offsetY">ROI 세로 오프셋.</param>
    /// <remarks>
    /// 반직관적이지만 <b>미러는 기준 치수가 짝수일 때만 패턴을 바꾼다</b> — 폭 1920 에서 좌우 미러를 켜면
    /// RG 가 GR 이 되지만, 폭 1921 에서는 RG 그대로다. 미러와 홀수 오프셋은 서로 상쇄되기도 한다.
    /// </remarks>
    public static CvBayerPattern Effective(
        CvBayerPattern sensorPattern,
        int sensorWidth, int sensorHeight,
        bool reverseX, bool reverseY,
        int offsetX, int offsetY)
    {
        var px = reverseX ? sensorWidth - 1 - offsetX : offsetX;
        var py = reverseY ? sensorHeight - 1 - offsetY : offsetY;
        return Shift(sensorPattern, px, py);
    }

    /// <summary>음수 오프셋도 안전하게 접는다.</summary>
    private static int Parity(int v) => ((v % 2) + 2) % 2;
}
