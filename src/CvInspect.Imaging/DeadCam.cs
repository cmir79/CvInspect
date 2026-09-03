namespace CvInspect.Imaging;

/// <summary>
/// 아무것도 하지 않는 카메라 — 쓸 수 없는 자리를 <b>메워 두는</b> 용도다.
///
/// 여러 대를 쓰는 설비에서 한 대가 없거나 설정이 틀렸다고 앱 전체가 못 뜨면 나머지 검사까지 멈춘다.
/// 그렇다고 그 자리를 <see cref="VirtualCam"/> 으로 채우면 <b>합성 프레임이 진짜인 척 검사로 흘러든다</b> —
/// 그쪽이 훨씬 나쁘다. 이 구현은 연결되지 않고, 프레임을 만들지 않으며, 조작을 조용히 삼키지도 않는다:
/// 즉시 결과가 필요한 <see cref="GrabOne"/> 은 <b>명확히 실패</b>하고 상태 계열은 무시된다.
///
/// <see cref="Reason"/> 에 왜 죽은 자리인지 담아 두면 화면·로그에서 그대로 쓸 수 있다.
/// 카메라가 나중에 돌아올 수 있는 상황이라면 이것 대신 <see cref="ReconnectingCam"/> 을 쓴다 —
/// 그쪽은 처음에 못 열려도 계속 다시 시도할 수 있다.
/// </summary>
public sealed class DeadCam : ICam
{
    private const string LogSource = nameof(DeadCam);
    private bool _disposed;

    public DeadCam(CamOpt? opt = null, string? reason = null)
    {
        Name = string.IsNullOrWhiteSpace(opt?.Name) ? nameof(DeadCam) : opt!.Name;
        ComType = string.IsNullOrWhiteSpace(opt?.ComType) ? nameof(DeadCam) : opt!.ComType;
        Reason = string.IsNullOrWhiteSpace(reason) ? "camera unavailable" : reason!;
    }

    /// <summary>이 자리가 죽은 이유 — 화면·로그에 그대로 보여 줄 수 있는 문구.</summary>
    public string Reason { get; }

    public string Name { get; }
    public string ComType { get; }
    public bool IsConnected => false;
    public bool IsGrabbing => false;

    // 계약을 채우되 절대 발화하지 않는다 — 죽은 자리에서는 아무 일도 일어나지 않는 것이 정의다.
    // 그래서 "사용되지 않음" 경고는 여기서만 의도된 상태다.
#pragma warning disable CS0067
    /// <summary>절대 발화하지 않는다.</summary>
    public event EventHandler<CamFrame>? FrameAcquired;

    /// <summary>절대 발화하지 않는다 — 이 자리는 연결되지 않는다.</summary>
    public event EventHandler<ConnArgs>? ConnectionChanged;

    /// <summary>절대 발화하지 않는다.</summary>
    public event EventHandler<bool>? GrabbingChanged;
#pragma warning restore CS0067

    public void Open()
    {
        ThrowIfDisposed();
        // 던지지 않는다 — 이 타입의 존재 이유가 "한 자리가 죽어도 나머지는 뜬다" 이다.
        CvLog.Publish(CvLogLevel.Warning, LogSource, $"[{Name}] stays disconnected: {Reason}");
    }

    public void Close() { }

    public void GrabOne()
    {
        ThrowIfDisposed();
        // 조용히 무시하면 상위가 오지 않을 프레임을 영원히 기다린다.
        throw new InvalidOperationException($"Camera '{Name}' is unavailable: {Reason}");
    }

    public void StartContinuous() => ThrowIfDisposed();

    public void StopContinuous() { }

    public void SetExposureTimeUs(double timeUs) => ThrowIfDisposed();

    public void Dispose() => _disposed = true;

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DeadCam));
    }
}
