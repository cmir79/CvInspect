namespace CvInspect.Imaging;

/// <summary>
/// 연결이 끊기면 카메라를 자동으로 되살리는 <see cref="ICam"/> 데코레이터.
///
/// 내부 인스턴스를 <b>팩토리로 새로 만들어 통째로 교체</b>한다 — 구현체가 "Close 뒤 Open 재개"를 지원하는지에
/// 기대지 않으므로 어떤 ICam 구현에도 붙는다. 구독자는 교체를 알 필요가 없다: 이벤트는 이 데코레이터가
/// 계속 발행하므로 한 번만 구독하면 된다.
///
/// 되살리는 것 — 연결, 끊기기 전의 <b>연속취득 의도</b>, 런타임에 지정한 <b>노출 시간</b>.
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

    /// <param name="factory">내부 카메라 생성기. 재연결 때마다 <b>새 인스턴스</b>를 만들어 돌려줘야 한다.</param>
    /// <param name="opt">재시도 정책(생략 시 기본 사다리).</param>
    public ReconnectingCam(Func<ICam> factory, CamReconnectOpt? opt = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _opt = opt ?? new CamReconnectOpt();
        _onInnerFrame = (s, f) => { if (IsCurrent(s)) FrameAcquired?.Invoke(this, f); };
        _onInnerConnection = (s, e) => { if (!e.IsConnected) OnInnerLost(s); };
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
            AttachAndOpen();
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

        _gate.Wait();
        try { RetireInner(); }
        finally { _gate.Release(); }

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
        lock (_sync)
        {
            ThrowIfDisposed();
            _wantContinuous = true;           // 미연결 구간이면 의도만 기록 — 복원 때 반영된다
            _intentVersion++;
            cam = _connected ? _inner : null;
        }
        if (cam is null) return;
        cam.StartContinuous();
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
            _connected = false;
            _grabbing = false;
        }
        if (cam is null) return;

        cam.FrameAcquired -= _onInnerFrame;
        cam.ConnectionChanged -= _onInnerConnection;
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
            ScheduleReconnectLocked();
        }
        if (lostGrab) GrabbingChanged?.Invoke(this, false);
        if (lostConn) ConnectionChanged?.Invoke(this, new ConnArgs(false));
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
        ConnectionChanged?.Invoke(this, new ConnArgs(connected));
    }

    private void RaiseGrabbing(bool grabbing)
    {
        lock (_sync)
        {
            if (_grabbing == grabbing) return;
            _grabbing = grabbing;
        }
        GrabbingChanged?.Invoke(this, grabbing);
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
