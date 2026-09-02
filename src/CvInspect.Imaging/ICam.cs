using OpenCvSharp;

namespace CvInspect.Imaging;

/// <summary>
/// 카메라 디바이스 추상화 — 구현체(가상/비디오/벤더 GigE 어댑터 등)가 공유하는 경계.
/// 프레임은 <see cref="CamFrameEvt"/> 로 발행하며, 벤더 SDK 타입은 구현 내부에만 둔다.
/// </summary>
public interface ICam : IDisposable
{
    string Name { get; }
    string ComType { get; }
    bool IsConnected { get; }
    bool IsGrabbing { get; }

    /// <summary>프레임 발행. <see cref="CamFrameEvt.Frame"/> 은 <b>이벤트 호출 동안만 유효</b> —
    /// 발행자가 호출 후 버퍼를 재사용/해제할 수 있으므로, 보관·비동기 전달하려면 Clone() 한다.</summary>
    event EventHandler<CamFrameEvt>? FrameAcquired;

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
