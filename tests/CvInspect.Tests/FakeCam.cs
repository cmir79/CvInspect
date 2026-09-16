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

    /// <summary>연결은 살아 있는데 취득만 죽은 경우를 흉내낸다 — 수신 스트림이 접히거나 수신이 실패해
    /// 구현이 스스로 취득을 접고 통지만 내는 길. 연결 상실과 달리 ConnectionChanged 는 나지 않는다.</summary>
    public void StopGrabbingOnItsOwn()
    {
        if (!IsGrabbing) return;
        IsGrabbing = false;
        GrabbingChanged?.Invoke(this, false);
    }

    /// <summary>같은 연결 상실을 <b>상태를 나중에 내리는</b> 구현으로 흉내낸다 — 취득 통지를 낼 때는 아직
    /// IsConnected 가 true 다. ICam 이 상태 갱신 시점을 못 박고 있지 않으므로 허용되는 구현이고,
    /// 데코레이터는 "어떤 ICam 구현에도 붙는다" 고 적어 두었다. 이 순서에서도 세션 교체는 한 번이어야 한다.</summary>
    public void LoseConnectionAnnouncingGrabFirst(int gapMs = 0)
    {
        var wasGrabbing = IsGrabbing;
        IsGrabbing = false;
        if (wasGrabbing) GrabbingChanged?.Invoke(this, false);   // 아직 IsConnected == true
        if (gapMs > 0) Thread.Sleep(gapMs);
        IsConnected = false;
        ConnectionChanged?.Invoke(this, new CvInspect.Imaging.ConnArgs(false));
    }

    /// <summary>장치 쪽 연결 상실을 흉내낸다. <b>통지 순서와 상태 갱신 시점이 실제 구현과 같아야 한다</b> —
    /// ICam 은 "제어권을 잃어 취득이 끊긴 경우도 GrabbingChanged 가 false 로 난다" 를 계약으로 적어 두었고,
    /// 구현은 두 상태를 먼저 내린 뒤 GrabbingChanged → ConnectionChanged 순으로 낸다. 이 순서를 안 지키면
    /// 두 통지를 갈라 보는 쪽(ReconnectingCam)의 결함이 하네스에서 통째로 안 보인다.</summary>
    /// <param name="gapMs">두 통지 사이 간격 — 상위가 첫 통지를 처리하는 동안 둘째가 늦게 도착하는
    /// 실제 상황(핸들러가 UI 로 마샬링되는 등)을 결정적으로 만든다.</param>
    public void LoseConnection(int gapMs = 0)
    {
        var wasGrabbing = IsGrabbing;
        IsGrabbing = false;
        IsConnected = false;
        if (wasGrabbing) GrabbingChanged?.Invoke(this, false);
        if (gapMs > 0) Thread.Sleep(gapMs);
        ConnectionChanged?.Invoke(this, new CvInspect.Imaging.ConnArgs(false));
    }

    public void EmitFrame(CvInspect.Imaging.CamFrame frame) => FrameAcquired?.Invoke(this, frame);
}
