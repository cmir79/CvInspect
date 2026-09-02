namespace CvInspect.Imaging;

/// <summary>
/// 카메라 설정 POCO — <see cref="CamFactory.Create"/> 가 <see cref="ComType"/> 으로 구현체를 고른다.
///
/// 경계 기준: 이 타입은 팩토리로 주입되는 어댑터·앱이 읽는 <b>공용 옵션 가방</b>이다 — 개별 필드는
/// 특정 구현체가 안 읽어도 실릴 수 있다(디바이스 일반 어휘 한정, 앱 도메인 개념 금지).
/// 반면 <b>여러 카메라의 목록 스키마와 영속(파일 위치·직렬화 방식)은 앱 소관</b>이라 여기 싣지 않는다.
/// </summary>
public sealed class CamOpt
{
    /// <summary>표시/로그용 카메라 이름. 빈 값이면 구현체가 기본 이름을 쓴다.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>디바이스 번호 — 다중 카메라 앱의 라우팅 키 (1부터).</summary>
    public int No { get; set; } = 1;

    /// <summary>활성 스위치 — 끄면 앱이 이 카메라를 기동/목록에서 걸러낸다.</summary>
    public bool Enabled { get; set; } = true;

    public string ComType { get; set; } = "Virtual";

    /// <summary>
    /// Virtual 카메라 전용 — 그랩/라이브마다 이름순 순환 공급할 이미지 폴더.
    /// 빈 값 = 테스트 패턴(시프팅 그라데이션). 다른 프로바이더는 무시.
    /// </summary>
    public string VirtualImageDir { get; set; } = string.Empty;

    /// <summary>
    /// VideoCapture 카메라 전용 — 장치 인덱스("0"), 동영상 파일 경로, 또는 스트림 URL(RTSP 등).
    /// 다른 프로바이더는 무시.
    /// </summary>
    public string VideoSource { get; set; } = "0";

    public string SerialNumber { get; set; } = string.Empty;
    public string UserSettings { get; set; } = string.Empty;
    public bool IsColor { get; set; }
    public double FrameRate { get; set; } = 30;

    /// <summary>노출 시간 (마이크로초).</summary>
    public double ExposureTimeUs { get; set; } = 10000;

    public FlipMode Flip { get; set; } = FlipMode.None;
    public RotateMode Rotation { get; set; } = RotateMode.None;
    public bool IsHardTrigger { get; set; }
    public double CameraAngle { get; set; }

    /// <summary>
    /// 화소 하나가 몇 mm 인지. 렌즈와 작동 거리가 정하는 이 카메라의 성질이다.
    /// 길이를 mm 로 내는 검사만 쓰고, 각도·비율만 내는 검사는 보지 않는다.
    /// 1 이면 화소를 그대로 쓴다는 뜻이다.
    /// <para>
    /// 가로·세로를 나눠 둔 것은 센서 화소가 정사각이 아닌 카메라 대비다. 정사각이면 두 값을
    /// 같게 적는다 — 값 하나만 받는 소비자는 두 값의 평균으로 접는 것이 규약이다.
    /// </para>
    /// </summary>
    public double ResolutionX { get; set; } = 1.0;
    public double ResolutionY { get; set; } = 1.0;
}
