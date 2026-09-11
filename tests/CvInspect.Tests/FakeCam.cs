namespace CvInspect.Tests;

/// <summary>테스트용 ICam — 연결 상실·Open 실패·프레임 발화를 명령으로 일으킨다.</summary>
sealed class FakeCam : CvInspect.Imaging.ICam
{
    public string Name => "FakeCam";
    public string ComType => "Fake";
    public bool IsConnected { get; private set; }
    public bool IsGrabbing { get; private set; }
    public bool FailOnOpen { get; init; }
    /// <summary>다음 <see cref="StartContinuous"/> 한 번을 실패시킨다 — 안쪽이 시작을 거부하는 경우를 흉내낸다.</summary>
    public bool FailNextStart { get; set; }
    public double? LastExposure { get; private set; }
    public bool Disposed { get; private set; }

    public event EventHandler<CvInspect.Imaging.CamFrame>? FrameAcquired;
    public event EventHandler<CvInspect.Imaging.ConnArgs>? ConnectionChanged;
    public event EventHandler<bool>? GrabbingChanged;

    public void Open()
    {
        if (FailOnOpen) throw new InvalidOperationException("fake open failure");
        if (IsConnected) return;
        IsConnected = true;
        ConnectionChanged?.Invoke(this, new CvInspect.Imaging.ConnArgs(true));
    }

    public void Close()
    {
        if (IsGrabbing) { IsGrabbing = false; GrabbingChanged?.Invoke(this, false); }
        if (!IsConnected) return;
        IsConnected = false;
        ConnectionChanged?.Invoke(this, new CvInspect.Imaging.ConnArgs(false));
    }

    public void GrabOne() { }
    public void StartContinuous()
    {
        if (FailNextStart) { FailNextStart = false; throw new InvalidOperationException("fake start failure"); }
        if (!IsGrabbing) { IsGrabbing = true; GrabbingChanged?.Invoke(this, true); }
    }
    public void StopContinuous() { if (IsGrabbing) { IsGrabbing = false; GrabbingChanged?.Invoke(this, false); } }
    public void SetExposureTimeUs(double timeUs) => LastExposure = timeUs;
    public void Dispose() { Disposed = true; IsConnected = false; IsGrabbing = false; }

    /// <summary>장치 쪽 연결 상실을 흉내낸다.</summary>
    public void LoseConnection()
    {
        IsGrabbing = false;
        IsConnected = false;
        ConnectionChanged?.Invoke(this, new CvInspect.Imaging.ConnArgs(false));
    }

    public void EmitFrame(CvInspect.Imaging.CamFrame frame) => FrameAcquired?.Invoke(this, frame);
}
