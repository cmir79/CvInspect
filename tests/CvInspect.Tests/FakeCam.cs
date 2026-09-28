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

    /// <summary>열기가 성공한 <b>직후</b>(돌려주기 전) 연결을 잃는다 — 열자마자 링크가 죽는 경우를 결정적으로 만든다.</summary>
    public bool LoseRightAfterOpen { get; init; }

    /// <summary>0 보다 크면 열기가 그만큼 붙잡혔다가 성공한다 — 끊긴 장치로의 연결 시도처럼 취소할 수 없는 느린 열기.</summary>
    public int HoldOnOpenMs { get; init; }

    /// <summary>열기 안에 들어와 붙잡혀 있는가 — 시험이 "지금 열리는 중" 을 기다렸다가 다음 조작을 걸 때 본다.</summary>
    public volatile bool IsOpening;

    public void Open()
    {
        if (FailOnOpen) throw new InvalidOperationException("fake open failure");
        if (IsConnected) return;
        if (HoldOnOpenMs > 0)
        {
            IsOpening = true;
            Thread.Sleep(HoldOnOpenMs);
            IsOpening = false;
        }
        IsConnected = true;
        ConnectionChanged?.Invoke(this, new CvInspect.Imaging.ConnArgs(true));
        if (LoseRightAfterOpen) LoseConnection();
    }

    public void Close()
    {
        if (IsGrabbing) { IsGrabbing = false; GrabbingChanged?.Invoke(this, false); }
        if (!IsConnected) return;
        IsConnected = false;
        ConnectionChanged?.Invoke(this, new CvInspect.Imaging.ConnArgs(false));
    }

    /// <summary>GrabOne 이 프레임을 발행하기까지의 지연 — 0 이면 부른 그 자리에서 발행한다(실제 구현의 모양).
    /// 0 보다 크면 그만큼 붙잡았다가 발행한다. 시한 만료를 만들려면 시한보다 크게 준다.</summary>
    public int GrabDelayMs { get; set; }

    /// <summary>true 면 GrabOne 이 프레임을 내지 않고 조용히 돌아간다 — 답할 수 없는 구현(DeadCam 류)의 모양.</summary>
    public bool GrabPublishesNothing { get; set; }

    /// <summary>0 보다 크면 GrabOne 이 **먼저 돌아가고** 그만큼 뒤에 다른 스레드에서 발행한다 —
    /// 프레임이 벤더 콜백으로 들어오는 취득 계층의 모양. 반환만 보고 단락하는 구현은 여기서 거짓 null 을 낸다.</summary>
    public int PublishAfterReturnMs { get; set; }

    /// <summary>GrabOne 이 던질 예외 — 답해야 하는데 못 하는 상태를 흉내낸다.</summary>
    public Exception? GrabThrows { get; set; }

    public int GrabCalls;

    /// <summary>지금 FrameAcquired 에 걸려 있는 핸들러 수 — 구독을 실제로 놓았는지 **직접** 본다.
    /// 프레임 수를 세는 것으로는 못 본다: 남은 핸들러가 무해하면 발행 수가 그대로라 시험이 통과해 버린다.</summary>
    public int FrameSubscribers => FrameAcquired?.GetInvocationList().Length ?? 0;

    public void GrabOne()
    {
        Interlocked.Increment(ref GrabCalls);
        if (GrabThrows is { } ex) throw ex;
        if (GrabDelayMs > 0) Thread.Sleep(GrabDelayMs);
        if (GrabPublishesNothing) return;
        var frame = new CvInspect.Imaging.CamFrame(new byte[4], 2, 2, 2, CvInspect.Imaging.CamPixelFormat.Mono8);
        if (PublishAfterReturnMs > 0)
        {
            var after = PublishAfterReturnMs;
            System.Threading.Tasks.Task.Run(() =>
            {
                Thread.Sleep(after);
                FrameAcquired?.Invoke(this, frame);
            });
            return;
        }
        FrameAcquired?.Invoke(this, frame);
    }
    /// <summary>0 보다 크면 StartContinuous 가 시작 전에 그만큼 기다리고, 그 사이 StopContinuous 가 오면 <b>시작하지 않고 예외 없이</b>
    /// 돌아온다 — 비정상 정지 뒤 대기를 하는 GevCam 의 모양(ICam.StartContinuous: 대기 사이 온 정지가 이긴다).</summary>
    public int HoldBeforeStartMs { get; set; }
    private int _stops;

    /// <summary>시작 대기 안에 들어와 있는가 — 시험이 "지금 시작하는 중" 을 기다렸다가 다음 조작을 걸 때 본다.</summary>
    public volatile bool IsStarting;

    /// <summary>주어지면 StartContinuous 가 시작 전에 <b>이 신호가 설 때까지</b> 기다린다(<see cref="HoldBeforeStartMs"/> 의 시각 대신 시험이
    /// 놓는 순간). "정지가 시작 대기 도중에 온다" 를 잠깐 자기로 세우면 부하에서 순서가 뒤집혀 거짓 실패·거짓 통과가 났다(형제 저장소 실측).
    /// ⚠ 단언이 던져도 놓이게 finally 에서 세운다.</summary>
    public ManualResetEventSlim? StartGate { get; set; }

    /// <summary>주어지면 늦은 켜짐(<see cref="LateStartMs"/>)이 시각 대신 이 신호를 기다렸다가 켠다.</summary>
    public ManualResetEventSlim? LateStartGate { get; set; }

    /// <summary>늦은 켜짐이 실제로 돈 횟수(켰든 이미 켜져 있었든) — 부정 단언 전에 "그 늦은 켜짐이 이미 지나갔다" 를 확인한다.</summary>
    public int LateStartsFired;

    /// <summary>다음 <see cref="StartContinuous"/> 한 번이 <b>시작하지 않고 예외 없이</b> 돌아온다 — 다른 경로의 정지에 진 시작(GevCam)의 모양.</summary>
    public bool StandDownNextStart { get; set; }

    /// <summary>0 보다 크면 StartContinuous 가 <b>켜지 않은 채 돌아오고</b>, 그만큼 뒤에 다른 스레드에서 켜고 알린다 — 시작을 SDK 콜백으로
    /// 늦게 알리는 어댑터의 모양(ICam 요건을 안 지키는 구현).</summary>
    public int LateStartMs { get; set; }

    /// <summary>노출을 쓸 때 부를 것 — 시험이 "안쪽에 노출이 들어가는 그 순간" 에 다른 조작을 끼워 넣는다.</summary>
    public Action<double>? OnSetExposure { get; set; }

    public void StartContinuous()
    {
        if (FailNextStart) { FailNextStart = false; throw new InvalidOperationException("fake start failure"); }
        var stopsAtCall = Volatile.Read(ref _stops);
        if (StartGate is { } gate)
        {
            IsStarting = true;
            gate.Wait();
            IsStarting = false;
        }
        else if (HoldBeforeStartMs > 0)
        {
            IsStarting = true;
            Thread.Sleep(HoldBeforeStartMs);
            IsStarting = false;
        }
        // 기다리는 사이 폐기됐으면 던진다 — GevCam 도 락을 잡자마자 해제를 확인해 던진다.
        if (Disposed) throw new ObjectDisposedException(nameof(FakeCam));
        if (StandDownNextStart) { StandDownNextStart = false; return; }
        if (Volatile.Read(ref _stops) != stopsAtCall) return;
        if (LateStartMs > 0 || LateStartGate is not null)
        {
            var late = LateStartMs;
            var lateGate = LateStartGate;
            System.Threading.Tasks.Task.Run(() =>
            {
                if (lateGate is not null) lateGate.Wait();
                else Thread.Sleep(late);
                if (!IsGrabbing) { IsGrabbing = true; GrabbingChanged?.Invoke(this, true); }
                Interlocked.Increment(ref LateStartsFired);
            });
            return;
        }
        if (!IsGrabbing) { IsGrabbing = true; GrabbingChanged?.Invoke(this, true); }
    }
    public void StopContinuous()
    {
        Interlocked.Increment(ref _stops);
        if (IsGrabbing) { IsGrabbing = false; GrabbingChanged?.Invoke(this, false); }
    }
    public void SetExposureTimeUs(double timeUs)
    {
        LastExposure = timeUs;
        OnSetExposure?.Invoke(timeUs);
    }
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
