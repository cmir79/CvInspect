namespace CvInspect.Imaging;

/// <summary>
/// 카메라 디바이스 추상화 — 구현체(가상/비디오/벤더 GigE 어댑터 등)가 공유하는 경계.
/// 프레임은 <see cref="CamFrame"/>(GC 소유 byte[]) 로 발행 — 보관·스레드 전달에 수명 계약이 없고,
/// 벤더 SDK 타입은 구현 내부에만 둔다. Mat 세계로는 <see cref="CamFrameMatExt.AsMat"/> 무복사 래핑.
///
/// 라이프사이클 계약: <see cref="Open"/>·<see cref="Close"/>·<see cref="IDisposable.Dispose"/> 는 <b>다중 호출에 안전</b>해야 한다
/// (이미 그 상태면 이벤트 없이 조용히 반환). <see cref="Close"/> 뒤 <see cref="Open"/> 재호출로 <b>세션을 재개할 수
/// 있어야</b> 하며, <see cref="IDisposable.Dispose"/> 이후에는 어떤 조작도 받지 않는다(<see cref="ObjectDisposedException"/>).
/// 재개를 지원할 수 없는 구현이라면 <see cref="ReconnectingCam"/> 처럼 인스턴스를 새로 만드는 소비자를 쓴다 —
/// 그쪽은 이 재개 계약에 기대지 않는다.
/// </summary>
public interface ICam : IDisposable
{
    string Name { get; }
    string ComType { get; }
    bool IsConnected { get; }
    bool IsGrabbing { get; }

    /// <summary>완전한 프레임만 발행한다 — 전송 손상·부분 수신 프레임은 구현체가 드롭하고
    /// <see cref="CvLog"/> 로 경고한다 (불완전 데이터가 검사 판정에 섞이는 것 방지).</summary>
    event EventHandler<CamFrame>? FrameAcquired;
    event EventHandler<ConnArgs>? ConnectionChanged;

    /// <summary>IsGrabbing 상태 변화 알림 (true: continuous 시작 / false: 정지).</summary>
    event EventHandler<bool>? GrabbingChanged;

    void Open();
    void Close();
    void GrabOne();
    void StartContinuous();
    void StopContinuous();

    /// <summary>노출 시간 설정 (마이크로초). 소스가 지원하지 않으면 무시하고 로그만 남긴다.</summary>
    void SetExposureTimeUs(double timeUs);
}
