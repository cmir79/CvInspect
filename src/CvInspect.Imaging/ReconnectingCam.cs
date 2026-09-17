namespace CvInspect.Imaging;

/// <summary>
/// 연결이 끊기면 카메라를 자동으로 되살리는 <see cref="ICam"/> 데코레이터.
///
/// 내부 인스턴스를 <b>팩토리로 새로 만들어 통째로 교체</b>한다 — 구현체가 "Close 뒤 Open 재개"를 지원하는지에
/// 기대지 않으므로 어떤 ICam 구현에도 붙는다. 구독자는 교체를 알 필요가 없다: 이벤트는 이 데코레이터가
/// 계속 발행하므로 한 번만 구독하면 된다.
///
/// 되살리는 것 — 연결, 끊기기 전의 <b>연속취득 의도</b>, 런타임에 지정한 <b>노출 시간</b>.
/// 되살리는 계기도 둘이다: 연결 상실과, <b>연결은 멀쩡한데 취득만 죽은 경우</b>(수신 스트림이 접히는 길).
/// 뒤엣것을 안 들으면 의도만 true 로 남아 아무도 다시 켜지 않는다 — 연결은 정상이라고 답하는데 프레임이
/// 영영 오지 않는다.
/// 되살리지 않는 것 — 사용자가 <see cref="Close"/> 나 <see cref="StopContinuous"/> 로 청산한 의도.
/// <see cref="Close"/> 이후에는 늦게 도착한 상실 통지도, 이미 예약된 백오프 만료도 카메라를 다시 열지 못한다
/// (정비하려고 끈 장치가 저절로 살아나면 안 된다). 게이트는 <see cref="Open"/> 만 푼다.
///
/// 미연결 구간(교체 중)의 호출 규약: 상태 계열(<see cref="StartContinuous"/>/<see cref="StopContinuous"/>/
/// <see cref="SetExposureTimeUs"/>)은 <b>의도로 기록</b>되어 복원 시 반영되고, 즉시 결과가 필요한
/// <see cref="GrabOne"/> 은 <see cref="InvalidOperationException"/> 으로 <b>명확히 실패</b>한다(조용히 무시하면
/// 상위가 프레임을 영원히 기다린다).
/// </summary>
public sealed class ReconnectingCam : ICam
{
    private const string LogSource = nameof(ReconnectingCam);

    private readonly Func<ICam> _factory;
    private readonly CamReconnectOpt _opt;
    private readonly object _sync = new();

    /// <summary>전이 직렬화 — 팩토리·Open·Close 는 락 밖에서 부르므로(사용자 코드가 무엇을 되부를지 모른다)
    /// 겹치지 않게 이 게이트로 줄 세운다. 재연결 루프는 자기 자신을 기다리지 않는다 — 루프는 우리 Close 를
    /// 부르지 않고 그냥 물러나므로, 자기 대기 교착이 구조적으로 생기지 않는다.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    // 내부 인스턴스 이벤트 중계 — 구독과 해제가 같은 델리게이트 참조여야 하므로 생성자에서 한 번만 만든다.
    private readonly EventHandler<CamFrame> _onInnerFrame;
    private readonly EventHandler<ConnArgs> _onInnerConnection;
    private readonly EventHandler<bool> _onInnerGrabbing;

    private ICam? _inner;
    private bool _closed = true;          // 명시적 Close 게이트 (최초 Open 전에도 닫힘)
    private bool _disposed;
    private bool _connected;              // 데코레이터가 관측한 상태 — 이벤트는 이 값이 바뀔 때만 발화
    private bool _grabbing;
    private bool _wantContinuous;         // 사용자 의도(내부 인스턴스와 분리 보관)
    private int _intentVersion;           // 의도가 바뀔 때마다 증가 — 복원과 사용자 명령의 경합 판정
    private double? _exposureUs;          // 런타임 지정 노출 — 새 인스턴스에 재적용

    private Task? _reconnectTask;
    private CancellationTokenSource? _reconnectCts;
    private bool _reconnectPending;       // 루프가 물러나는 창에 도착한 요청 — 소실되면 영구 미연결이 된다
    private object? _lostInner;           // 재연결을 이미 건 죽은 인스턴스 — 같은 죽음의 두 통지를 두 요청으로 세지 않게

    /// <param name="factory">내부 카메라 생성기. 재연결 때마다 <b>새 인스턴스</b>를 만들어 돌려줘야 한다.</param>
    /// <param name="opt">재시도 정책(생략 시 기본 사다리).</param>
    public ReconnectingCam(Func<ICam> factory, CamReconnectOpt? opt = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _opt = opt ?? new CamReconnectOpt();
        // 프레임 중계도 감싼다 — 여기는 안쪽 구현의 발행 스레드다. 구독자가 던지면 그 예외가 안쪽으로
        // 돌아가고, 안쪽은 그것을 자기 실패로 읽는다(실제로 GevCam 은 "frame conversion failed" 로 적었다).
        _onInnerFrame = (s, f) => { if (IsCurrent(s)) SafeRaise(() => FrameAcquired?.Invoke(this, f), nameof(FrameAcquired)); };
        _onInnerConnection = (s, e) => { if (!e.IsConnected) OnInnerLost(s); };
        // 시작 통지는 중계하지 않는다 — 시작은 우리가 부른 자리에서 이미 낸다. 여기서 한 번 더 받으면
        // 재개 도중 들어온 정지를 "나중에 온 명령이 이긴다" 로 처리하는 길에 켜짐/꺼짐 한 쌍이 덧난다.
        _onInnerGrabbing = (s, on) => { if (!on) OnInnerGrabStopped(s); };
    }

    /// <summary>표시 이름 — 내부 인스턴스가 없는 구간에서도 답해야 하므로 마지막 값을 기억한다.</summary>
    public string Name { get; private set; } = nameof(ReconnectingCam);

    public string ComType { get; private set; } = nameof(ReconnectingCam);

    public bool IsConnected { get { lock (_sync) return _connected; } }

    public bool IsGrabbing { get { lock (_sync) return _grabbing; } }

    public event EventHandler<CamFrame>? FrameAcquired;
    public event EventHandler<ConnArgs>? ConnectionChanged;
    public event EventHandler<bool>? GrabbingChanged;

    // === 라이프사이클 ===

    public void Open()
    {
        lock (_sync) ThrowIfDisposed();

        _gate.Wait();
        try
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                _closed = false;              // 게이트 해제 — 이제부터 재연결이 허용된다
                if (_inner != null) return;   // 이미 열려 있음 (중복 Open 이 인스턴스를 둘로 만들지 않게)
            }
            try
            {
                AttachAndOpen();
            }
            catch (Exception ex) when (_opt.RetryInitialOpen)
            {
                // 기동 시점에 아직 안 붙은 카메라를 영영 죽은 자리로 만들지 않는다 — 재연결 루프는
                // '한 번 열린 뒤의 상실' 에만 돌기 때문에, 여기서 손수 태워 줘야 나중에 붙을 때 합류한다.
                CvLog.Publish(CvLogLevel.Warning, LogSource,
                    $"[{Name}] initial open failed — retrying in the background.", ex);
                lock (_sync)
                {
                    if (!_disposed && !_closed) ScheduleReconnectLocked();
                }
                return;
            }
        }
        finally
        {
            _gate.Release();
        }
        RaiseConnection(true);
    }

    public void Close()
    {
        // 세션 종료 의사 — 진행 중 재연결을 취소하고 이후 기동을 막는다. 취소만 하고 기다리지 않는다.
        CancellationTokenSource? cts;
        lock (_sync)
        {
            if (_disposed) return;
            _closed = true;
            _wantContinuous = false;          // 의도 청산 — 다음 세션이 요청 없는 취득을 부활시키지 않게
            _intentVersion++;
            _reconnectPending = false;
            cts = _reconnectCts;
        }
        try { cts?.Cancel(); } catch (ObjectDisposedException) { /* 루프가 이미 물러남 */ }

        // 시한을 두고 기다린다 — 재연결 사다리가 게이트를 쥔 채 여는 중이면 그 열기는 취소되지 않으므로
        // (열기에 취소 토큰이 없다) 무한정 기다리면 <b>닫기가 부른 쪽의 종료 예산을 넘긴다.</b> 그러면
        // 제어권을 반납하지 못한 채 프로세스가 내려가고, 곧바로 재기동하면 장치가 하트비트 시한을
        // 넘길 때까지 "다른 응용이 잡고 있다" 로 열기가 실패한다. 현장에는 "가끔 재기동이 실패한다" 로만
        // 보인다. 시한을 넘기면 <see cref="Dispose"/> 와 같이 그래도 정리를 진행한다.
        if (_gate.Wait(Math.Max(0, _opt.ShutdownWaitMs)))
        {
            try { RetireInner(); }
            finally { _gate.Release(); }
        }
        else
        {
            RetireInner();   // 전이가 걸려 있어도 닫기는 진행한다
        }

        RaiseGrabbing(false);
        RaiseConnection(false);
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _closed = true;
            _wantContinuous = false;
            _reconnectPending = false;
            cts = _reconnectCts;              // 수거와 기동을 같은 락 아래에서 — 좀비 루프 방지
            task = _reconnectTask;
            _reconnectCts = null;
            _reconnectTask = null;
        }
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        try { task?.Wait(Math.Max(0, _opt.ShutdownWaitMs)); } catch { /* 종료 경로 — 삼킨다 */ }

        if (_gate.Wait(Math.Max(0, _opt.ShutdownWaitMs)))
        {
            try { RetireInner(); }
            finally { _gate.Release(); }
        }
        else
        {
            RetireInner();   // 전이가 걸려 있어도 종료는 진행한다
        }

        _gate.Dispose();
        RaiseGrabbing(false);
        RaiseConnection(false);
    }

    // === 조작 ===

    public void GrabOne()
    {
        var cam = CurrentOrThrow();
        cam.GrabOne();
    }

    public void StartContinuous()
    {
        ICam? cam;
        bool wantedBefore;
        int myVersion;
        lock (_sync)
        {
            ThrowIfDisposed();
            wantedBefore = _wantContinuous;
            _wantContinuous = true;           // 미연결 구간이면 의도만 기록 — 복원 때 반영된다
            myVersion = ++_intentVersion;
            cam = _connected ? _inner : null;
        }
        if (cam is null) return;
        try
        {
            cam.StartContinuous();
        }
        catch
        {
            // 안쪽이 거부했다(단발 그랩이 기다리는 중 등). 부른 쪽은 예외를 받았는데 의도만 남겨 두면
            // 다음 재연결 때 아무도 청하지 않은 연속 취득이 되살아난다 — 그 사이 새 의도가 없을 때만 되돌린다.
            lock (_sync) { if (_intentVersion == myVersion) _wantContinuous = wantedBefore; }
            throw;
        }
        RaiseGrabbing(true);
    }

    public void StopContinuous()
    {
        ICam? cam;
        lock (_sync)
        {
            if (_disposed) return;
            _wantContinuous = false;
            _intentVersion++;
            cam = _connected ? _inner : null;
        }
        cam?.StopContinuous();
        RaiseGrabbing(false);
    }

    public void SetExposureTimeUs(double timeUs)
    {
        ICam? cam;
        lock (_sync)
        {
            ThrowIfDisposed();
            _exposureUs = timeUs;             // 재연결 후 새 인스턴스에 재적용 (초기 옵션값으로 되돌아가지 않게)
            cam = _connected ? _inner : null;
        }
        cam?.SetExposureTimeUs(timeUs);
    }

    // === 내부 인스턴스 장착·청산 ===

    /// <summary>새 인스턴스를 만들어 장착한다. 실패하면 반쯤 만들어진 인스턴스를 남기지 않고 던진다.
    /// <see cref="_gate"/> 보유 전제 — 팩토리와 Open 은 락 밖에서 부른다(사용자 코드).</summary>
    private void AttachAndOpen()
    {
        ICam? cam = null;
        try
        {
            cam = _factory() ?? throw new InvalidOperationException("Camera factory returned null.");
            cam.FrameAcquired += _onInnerFrame;
            cam.ConnectionChanged += _onInnerConnection;
            cam.GrabbingChanged += _onInnerGrabbing;
            cam.Open();

            double? exposure;
            lock (_sync) exposure = _exposureUs;
            if (exposure is { } us) cam.SetExposureTimeUs(us);   // 연속취득 재개보다 먼저

            lock (_sync)
            {
                // _connected 는 여기서 세우지 않는다 — 연결 성립 통지는 RaiseConnection 이 전이로만 발화하므로,
                // 여기서 미리 true 로 만들면 그 발화가 "변화 없음"으로 삼켜진다.
                _inner = cam;
                Name = cam.Name;
                ComType = cam.ComType;
            }
        }
        catch
        {
            if (cam != null)
            {
                cam.FrameAcquired -= _onInnerFrame;
                cam.ConnectionChanged -= _onInnerConnection;
                cam.GrabbingChanged -= _onInnerGrabbing;
                Try(() => cam.Close());
                Try(() => cam.Dispose());     // 반쯤 열린 인스턴스가 장치를 점유하면 이후 재시도가 전부 실패한다
            }
            throw;
        }
    }

    /// <summary>현재 인스턴스를 구독 해제 → 정지·닫기 → 폐기 순으로 청산한다.
    /// 구독 해제가 먼저다 — 죽어 가는 인스턴스의 마지막 통지가 새 세션을 다시 끊는 자기 증식을 막는다.
    /// <see cref="_gate"/> 보유 전제(Dispose 의 마지막 폴백 경로만 예외).</summary>
    private void RetireInner()
    {
        ICam? cam;
        lock (_sync)
        {
            cam = _inner;
            _inner = null;
            _lostInner = null;   // 시체를 치웠다 — 다음 인스턴스의 죽음은 새 사건이다
            _connected = false;
            _grabbing = false;
        }
        if (cam is null) return;

        cam.FrameAcquired -= _onInnerFrame;
        cam.ConnectionChanged -= _onInnerConnection;
        cam.GrabbingChanged -= _onInnerGrabbing;   // 바로 밑에서 우리가 세우는 것이 '스스로 멈췄다' 로 되읽히지 않게
        Try(() => cam.StopContinuous());
        Try(() => cam.Close());
        Try(() => cam.Dispose());
    }

    // === 재연결 ===

    /// <summary>내부에서 온 연결 상실 통지. 통지 스레드(SDK 콜백일 수 있다)를 붙잡지 않고 즉시 반환한다.</summary>
    private void OnInnerLost(object? sender)
    {
        bool lostConn = false, lostGrab = false;
        lock (_sync)
        {
            if (_disposed || _closed) return;                    // 게이트 — 정비 중 부활 금지
            if (!ReferenceEquals(_inner, sender)) return;        // 폐기된 인스턴스의 유령 통지
            if (_connected) { _connected = false; lostConn = true; }
            if (_grabbing) { _grabbing = false; lostGrab = true; }
            // 같은 인스턴스가 죽는 사건 하나에 통지가 여러 번 올 수 있다(취득 정지 + 연결 상실). 요청을
            // 그 수만큼 걸면 뒤엣것이 _reconnectPending 으로 남아, 첫 라운드가 세션을 되살린 직후 둘째
            // 라운드가 그 멀쩡한 세션을 다시 뜯는다. 상위에는 false 없이 연결·취득이 두 번 오고 그사이
            // 프레임이 끊긴다. 통지 순서는 구현마다 다를 수 있으므로 순서가 아니라 <b>죽은 인스턴스</b>로 센다.
            if (!ReferenceEquals(_lostInner, sender))
            {
                _lostInner = sender;
                ScheduleReconnectLocked();
            }
        }
        if (lostGrab) SafeRaise(() => GrabbingChanged?.Invoke(this, false), nameof(GrabbingChanged));
        if (lostConn) SafeRaise(() => ConnectionChanged?.Invoke(this, new ConnArgs(false)), nameof(ConnectionChanged));
    }

    /// <summary>
    /// 내부에서 온 "연속 취득이 멈췄다" 통지.
    ///
    /// <b>연결이 살아 있는데 취득만 죽는 길이 있다</b> — 수신 스트림이 닫히거나 수신이 실패하면 구현은
    /// 연결을 잃지 않은 채 취득을 접고 이 통지를 낸다(<see cref="ICam.GrabbingChanged"/> 가 "사용자가
    /// 시켜서만 나지 않는다" 고 적어 둔 그 경우다). 그 신호를 안 들으면 <b>우리 의도만 true 로 남아
    /// 아무도 다시 켜지 않는다</b> — 데코레이터는 연결이 멀쩡하다고 답하고, 프레임은 영영 오지 않으며,
    /// 상위는 기다리는 것 말고 할 수 있는 일이 없다. 연결 상실만 듣던 때의 구멍이다.
    ///
    /// 되살리는 방법은 <b>세션 교체</b>다(<see cref="OnInnerLost"/> 에 위임). 취득만 다시 걸지 않는 이유는
    /// 둘이다 — ① 취득이 스스로 죽은 인스턴스가 성하다는 보장이 없다 ② 교체 경로에는 백오프 사다리와
    /// 청산·재장착·의도 복원이 이미 다 들어 있다. 교체 구간에는 연결도 실제로 끊기므로 통지도 그대로 정직하다.
    ///
    /// <b>우리가 멈춘 것이면 표시만 맞춘다.</b> 의도가 이미 내려가 있으면(사용자의 <see cref="StopContinuous"/>)
    /// 되살릴 것이 없다 — 그때 되살리면 사용자가 끈 취득이 부활한다.
    /// </summary>
    private void OnInnerGrabStopped(object? sender)
    {
        bool resume;
        lock (_sync)
        {
            if (_disposed || _closed) return;                    // 게이트 — 정비 중 부활 금지
            if (!ReferenceEquals(_inner, sender)) return;        // 폐기된 인스턴스의 유령 통지
            // 연결까지 잃은 것이면 이 통지는 그 사건의 앞 줄일 뿐이다 — 되살리기는 연결 상실 경로가
            // 맡는다(곧 이어 온다). 여기서 같이 나서면 요청이 두 건이 되어 방금 살아난 세션을 다시 뜯고,
            // 로그에는 제어 상실이 "청하지도 않았는데 멈췄다" 로 남아 원인을 엉뚱한 데로 보낸다.
            if (sender is ICam { IsConnected: false }) return;
            resume = _wantContinuous;
        }
        if (!resume)
        {
            // 전이에서만 나가므로 StopContinuous 가 이미 낸 통지와 겹치지 않는다.
            RaiseGrabbing(false);
            return;
        }
        CvLog.Publish(CvLogLevel.Warning, LogSource,
            $"[{Name}] the camera stopped grabbing without being asked — rebuilding the session to resume it.");
        OnInnerLost(sender);
    }

    /// <summary>재연결 루프 기동. 이미 도는 루프가 있으면 새로 만들지 않고 그 루프에 위임한다.
    /// <see cref="_sync"/> 보유 전제 — 기동 판정과 태스크 저장이 원자적이어야 루프가 N개로 불어나지 않는다.</summary>
    private void ScheduleReconnectLocked()
    {
        if (_reconnectTask != null) { _reconnectPending = true; return; }
        var cts = new CancellationTokenSource();
        _reconnectCts = cts;
        _reconnectTask = Task.Run(() => ReconnectLoop(cts));
    }

    private void ReconnectLoop(CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            do
            {
                lock (_sync) _reconnectPending = false;   // 이번 라운드가 흡수한다
                RunAttempts(ct);
            }
            while (ShouldRepeat(ct));
        }
        catch (Exception ex)
        {
            // 태스크 밖으로 예외가 새면 슬롯이 '완료'로 남아 다음 요청이 영영 처리되지 않는다.
            CvLog.Publish(CvLogLevel.Error, LogSource, $"[{Name}] reconnect loop failed.", ex);
        }
        finally
        {
            RetireLoop(cts);
        }
    }

    private void RunAttempts(CancellationToken ct)
    {
        var ladder = _opt.BackoffMs is { Count: > 0 } l ? l : new[] { 1000 };
        for (var attempt = 0; ; attempt++)
        {
            if (ct.IsCancellationRequested) return;
            lock (_sync) { if (_closed || _disposed) return; }

            var delay = Math.Max(0, ladder[Math.Min(attempt, ladder.Count - 1)]);
            if (ct.WaitHandle.WaitOne(delay)) return;   // 취소는 남은 지연을 다 기다리지 않고 즉시 깨어난다

            if (TrySwapIn(ct)) return;

            if (_opt.MaxAttempts > 0 && attempt + 1 >= _opt.MaxAttempts)
            {
                // 포기는 조용한 정지가 아니라 관측 가능한 종단 상태로 남긴다.
                CvLog.Publish(CvLogLevel.Warning, LogSource,
                    $"[{Name}] giving up after {attempt + 1} reconnect attempts — the camera stays disconnected. Call Open() to retry.");
                return;
            }
        }
    }

    /// <summary>한 번의 재장착 시도. 성공하면 true.</summary>
    private bool TrySwapIn(CancellationToken ct)
    {
        if (!_gate.Wait(Math.Max(0, _opt.ShutdownWaitMs))) return false;
        try
        {
            lock (_sync) { if (_closed || _disposed || ct.IsCancellationRequested) return false; }
            RetireInner();
            lock (_sync) { if (_closed || _disposed || ct.IsCancellationRequested) return false; }
            AttachAndOpen();
        }
        catch (Exception ex)
        {
            CvLog.Publish(CvLogLevel.Warning, LogSource, $"[{Name}] reconnect attempt failed.", ex);
            return false;
        }
        finally
        {
            _gate.Release();
        }

        RaiseConnection(true);
        ResumeContinuous();
        return true;
    }

    /// <summary>끊기기 전 의도가 살아 있으면 연속취득을 재개한다.
    /// 재개 도중 사용자가 StopContinuous 를 부를 수 있으므로, 기동 뒤 의도를 다시 확인해
    /// <b>나중에 온 명령이 이기게</b> 한다(정지를 눌렀는데 계속 도는 상황 방지).</summary>
    private void ResumeContinuous()
    {
        ICam? cam;
        int version;
        lock (_sync)
        {
            if (!_wantContinuous || _inner is null || _closed || _disposed) return;
            cam = _inner;
            version = _intentVersion;
        }

        try { cam.StartContinuous(); }
        catch (Exception ex)
        {
            CvLog.Publish(CvLogLevel.Warning, LogSource, $"[{Name}] failed to resume continuous grab after reconnect.", ex);
            return;
        }

        bool undo;
        lock (_sync) undo = _intentVersion != version && !_wantContinuous;
        if (undo)
        {
            Try(() => cam.StopContinuous());   // 재개 도중 들어온 정지 명령이 이긴다
            return;
        }
        RaiseGrabbing(true);
    }

    private bool ShouldRepeat(CancellationToken ct)
    {
        lock (_sync) return _reconnectPending && !_closed && !_disposed && !ct.IsCancellationRequested;
    }

    /// <summary>이번 루프가 물러난다. 물러나는 창(취소 언와인드 포함)에 도착한 요청은 새 루프로 승계한다 —
    /// 여기서 놓치면 아무도 재시도하지 않는 조용한 영구 미연결이 된다.</summary>
    private void RetireLoop(CancellationTokenSource cts)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_reconnectCts, cts)) return;   // 이미 다른 루프가 주인이다
            _reconnectTask = null;
            _reconnectCts = null;
            var pending = _reconnectPending;
            _reconnectPending = false;
            if (pending && !_closed && !_disposed) ScheduleReconnectLocked();
        }
        cts.Dispose();
    }

    // === 잡동사니 ===

    private bool IsCurrent(object? sender)
    {
        lock (_sync) return ReferenceEquals(_inner, sender);
    }

    /// <summary>지금 조작을 받을 수 있는 인스턴스. 미연결 구간이면 던진다 — 죽은 인스턴스로 조용히 흘려보내면
    /// 상위가 오지 않을 프레임을 기다린다. 아직 교체 전이라 인스턴스가 남아 있어도 마찬가지다.</summary>
    private ICam CurrentOrThrow()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_inner is { } cam && _connected) return cam;
            throw new InvalidOperationException(_closed ? "Camera is not opened." : "Camera is reconnecting.");
        }
    }

    /// <summary>상태가 실제로 바뀐 경계에서만 발화한다 — 재시도마다 끊김 알림이 반복되지 않게. 락 밖에서 부른다.</summary>
    private void RaiseConnection(bool connected)
    {
        lock (_sync)
        {
            if (_connected == connected) return;
            _connected = connected;
        }
        SafeRaise(() => ConnectionChanged?.Invoke(this, new ConnArgs(connected)), nameof(ConnectionChanged));
    }

    private void RaiseGrabbing(bool grabbing)
    {
        lock (_sync)
        {
            if (_grabbing == grabbing) return;
            _grabbing = grabbing;
        }
        SafeRaise(() => GrabbingChanged?.Invoke(this, grabbing), nameof(GrabbingChanged));
    }

    /// <summary>
    /// 구독자에게 통지한다 — <b>구독자가 던져도 우리가 하는 일은 달라지지 않는다.</b>
    ///
    /// 감싸지 않으면 두 가지로 샌다. 통지가 <b>재연결 루프</b>에서 나가는 자리가 있어서(재연결 성공 뒤의
    /// 연결·취득 통지), 구독자 예외가 루프 바깥 catch 까지 올라가 <b>"재연결이 실패했다" 로 읽힌다</b> —
    /// 실제로는 성공했는데. 그리고 그 예외가 올라가는 길에 <b>연속취득 재개가 통째로 건너뛰어진다</b>:
    /// 연결은 살아났는데 취득은 안 돌고, 로그에는 호스트 핸들러가 원인이라는 흔적이 없다.
    ///
    /// 예외를 받을 사람이 없는 스레드도 있고(안쪽 구현의 배경 스레드), 받을 사람은 있는데 <b>오해하는</b>
    /// 자리도 있다. 뒤엣것이 더 안 보인다 — catch 가 있으니 훑을 때 그냥 지나가고, 그 catch 가 <b>무엇으로
    /// 읽는지</b>까지 봐야 나온다.
    ///
    /// 삼키지는 않는다 — 남의 결함이지만 흔적은 우리 쪽에만 남는다.
    /// </summary>
    private void SafeRaise(Action raise, string what)
    {
        try { raise(); }
        catch (Exception ex) { CvLog.Publish(CvLogLevel.Error, LogSource, $"[{Name}] a {what} subscriber threw.", ex); }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ReconnectingCam));
    }

    /// <summary>청산 단계는 하나가 실패해도 다음 단계를 막지 않는다.</summary>
    private static void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) { CvLog.Publish(CvLogLevel.Debug, LogSource, "teardown step failed.", ex); }
    }
}
