namespace CvInspect.Imaging;

/// <summary>
/// 연결이 끊기면 카메라를 자동으로 되살리는 <see cref="ICam"/> 데코레이터.
///
/// 내부 인스턴스를 <b>팩토리로 새로 만들어 통째로 교체</b>한다 — 구현체가 "Close 뒤 Open 재개"를 지원하는지에
/// 기대지 않으므로, 아래 두 요건을 지키는 ICam 구현에는 다 붙는다. 구독자는 교체를 알 필요가 없다: 이벤트는 이 데코레이터가
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
/// 상위가 프레임을 영원히 기다린다). 교체 중이 아니라 <b>닫힌</b> 동안(첫 <see cref="Open"/> 전, <see cref="Close"/> 뒤)의
/// <see cref="StartContinuous"/> 는 기록하지 않는다 — 던지지 않고 Info 한 줄만 남긴다(다음 Open 이 라이브를 켜지 않는다).
/// <see cref="SetExposureTimeUs"/> 는 닫힌 동안에도 기록되어 다음 Open 에 적용된다.
///
/// 상실 뒤의 <see cref="Open"/> 은 재연결 루프를 기다리지 않고 곧바로 다시 연다(그만큼 막힌다). 그 열기가 실패해 던져도(<c>RetryInitialOpen</c>
/// 이 꺼져 있을 때) <b>포기가 아니다</b> — 루프가 돌고 있으면 계속 돌고, 라이브 의도도 남는다. ⚠ 루프가 이미 포기했으면(<c>MaxAttempts</c>) 도는 루프가
/// 없으므로 실패한 Open 뒤에 <b>뒤에서 다시 시도하는 것은 없다</b> — Open 을 다시 부르거나 <c>RetryInitialOpen</c> 을 켠다. 처음·닫은 뒤의 열기가
/// 실패해 던지면 닫힌 상태로 돌아간다(루프 없음).
/// <see cref="Open"/> 과 <see cref="Close"/> 가 다른 스레드에서 겹치면 <b>나중에 불린 쪽</b>이 이긴다(게이트를 잡은 순서가 아니다).
///
/// 안쪽 인스턴스의 두 요건 — ① <see cref="ICam.Open"/> 이 정상 반환하면 이미 연결돼 있어야 한다(아니면 열기 실패로 보고 다시
/// 시도한다). 그래서 팩토리가 <see cref="DeadCam"/> 을 돌려주면 첫 <see cref="Open"/> 이 그 사유를 담아 던진다(<c>RetryInitialOpen</c>
/// 이면 던지지 않고 뒤에서 재시도한다 — 영영 안 붙는 자리면 <c>MaxAttempts</c> 로 끝낸다). ② <see cref="ICam.StartContinuous"/> 가
/// 시작했으면 반환 전에 <see cref="ICam.IsGrabbing"/> 이 참이어야 한다(반환 직후 그 값으로 켜짐을 가린다). 시작을 나중에(콜백으로)
/// 알리는 구현은 그 늦은 켜짐 통지를 받아 맞춘다 — 늦게라도 알린다.
///
/// 통지는 상태를 락 안에서 정한 뒤 락 밖에서 낸다(구독자가 되불러도 되게). 그래서 서로 다른 스레드에서 난 전이(예: 재연결의 켜짐과
/// 그 직후의 상실)는 구독자에게 순서가 엇갈려 닿을 수 있다 — <b>권위 있는 값은 <see cref="IsConnected"/>·<see cref="IsGrabbing"/> 이다</b>
/// (<see cref="ICam.IsConnected"/> 가 통지를 거울질하기보다 값을 폴링하라고 권하는 이유).
/// </summary>
public sealed class ReconnectingCam : ICam, ICamGrabAsync
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
    private int _intentFromStart;         // 지금 의도를 세운 StartContinuous 의 판(시작이 아닌 명령 — 정지·닫기·열기 — 이 세웠으면 0)

    /// <summary>결과를 아직 모르는(또는 거절된) 시작의 계보 — 거절된 시작이 의도를 되돌릴 곳을 찾는다.
    /// 거절된 시작은 의도를 "들어올 때의 값" 으로 되돌렸는데, 겹친 뒤 시작의 그 값은 앞 시작(곧 거절될)이 세운 켜짐이라, 둘이 모두 거절되면 어느
    /// 순서로든 의도가 켜진 채 남았다(표시는 꺼짐 — 다음 재연결이 아무도 청하지 않은 라이브를 켰다; 형제 저장소가 알려 옴, 10-R32 로 재현).
    /// 그래서 되돌림은 <b>지금 의도가 이 시작의 결정일 때만</b>(판 번호가 아니라 계보로) 그 앞의 결정으로 하고, 그 결정도 거절된 시작이면 이어서 푼다.</summary>
    private sealed class StartIntent
    {
        public int Version;
        public bool WantedBefore;             // 이 시작 앞의 의도
        public int BeforeFrom;                // 그 앞의 의도를 세운 시작의 판(0 = 시작이 아닌 명령)
        public bool Refused;
    }
    private readonly List<StartIntent> _startIntents = new();
    private double? _exposureUs;          // 런타임 지정 노출 — 새 인스턴스에 재적용
    private int _exposureVersion;         // _exposureUs 가 바뀔 때마다 증가 — 장착 중에 온 노출을 놓치지 않게
    private int _attachedExposureVersion; // 현재 인스턴스에 적용한 노출의 판

    private Task? _reconnectTask;
    private CancellationTokenSource? _reconnectCts;
    private bool _reconnectPending;       // 루프가 물러나는 창에 도착한 요청 — 소실되면 영구 미연결이 된다
    private object? _lostInner;           // 재연결을 이미 건 죽은 인스턴스 — 같은 죽음의 두 통지를 두 요청으로 세지 않게
    private int _openWaiters;             // 게이트 앞에 줄 선 Open 수 — 관측 전용(OpenWaiters)
    private int _lifeSeq;                 // Open·Close 가 <b>불린</b> 순서 — 게이트를 잡은 순서가 아니라 이것으로 누가 이기는지 정한다
    private int _lastCloseSeq;            // 마지막으로 불린 Close 의 번호
    private int _openedSeq;               // 게이트 안 판정을 마지막으로 지난 Open 의 번호("이미 열려 있다" 로 돌아간 것 포함) — 게이트 안에서만 쓴다
    private int _attachGen;               // 세션을 장착할 때마다 증가 — 재연결 루프가 "새 사건" 을 알아본다
    // 지금 우리가 멈추는 중인 안쪽 인스턴스들(같은 인스턴스가 여러 번 들어갈 수 있다) — 그 사이 온 그 인스턴스의 꺼짐 통지는 우리 정지의 메아리다.
    // 인스턴스별로 센다: 하나의 수와 마지막 인스턴스 한 칸으로 두면, 교체된 옛 세션에서 아직 끝나지 않은 정지가 새 세션의 진짜 멈춤까지
    // 메아리로 삼켰다(검토가 가짜 카메라로 재현).
    private readonly List<object> _ownStopCams = new();

    /// <summary>재연결 루프가 없다(돌 일이 끝났다) — 관측 전용. 시험이 "루프가 한 라운드를 마쳤다" 를 시간 대신 이것으로 기다린다:
    /// 잠깐 자고 나서 "더 안 일어났다" 를 보는 부정 단언은 느린 기계에서 루프가 아직 안 돌았는데도 참이 된다.
    /// 루프 기동은 그것을 부른 호출 안에서 락 아래 동기로 걸리고(ScheduleReconnectLocked), 다음 루프로 넘기는 것도 같은 락 아래라
    /// "아직 안 떴다" 와 "끝났다" 가 섞이지 않는다. ⚠ 재장착 뒤의 알림·라이브 재개가 루프 밖으로 옮겨 가면 이 값은 "뒷일까지 끝남" 을
    /// 뜻하지 않게 된다. ⚠ <see cref="Dispose"/> 뒤에는 쓸 수 없다 — 해제가 루프 칸을 먼저 비우므로 루프가 아직 돌아도 참이다.</summary>
    internal bool IsReconnectIdle { get { lock (_sync) return _reconnectTask is null; } }

    /// <summary>게이트 앞에 줄 선 <see cref="Open"/> 수 — 관측 전용. 해제 검사를 지나 게이트 대기에 들어간(들어갈) 호출을 센다.</summary>
    internal int OpenWaiters => Volatile.Read(ref _openWaiters);

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
        // 시작 통지는 대개 중계할 것이 없다 — 시작은 우리가 부른 자리에서, 안쪽이 실제로 켜졌는지 보고 낸다. 다만 시작을 반환 뒤에
        // (콜백으로) 알리는 구현은 반환 시점에 아직 꺼져 있어 거기서 못 알린다 — 그 늦은 켜짐만 받아 맞춘다(OnInnerGrabStarted).
        _onInnerGrabbing = (s, on) => { if (on) OnInnerGrabStarted(s); else OnInnerGrabStopped(s); };
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
        int mySeq;
        lock (_sync) { ThrowIfDisposed(); mySeq = ++_lifeSeq; }

        Interlocked.Increment(ref _openWaiters);
        try { _gate.Wait(); }
        finally { Interlocked.Decrement(ref _openWaiters); }
        ICam? attached;
        Retired retired = default;
        var wasClosed = false;
        try
        {
            bool replace;
            lock (_sync)
            {
                ThrowIfDisposed();
                // 게이트를 기다리는 사이 <b>나중에 불린</b> 닫기가 있었다 — 나중 명령이 이긴다: 열지 않고 닫힌 채 돌아간다. 게이트를 잡은 순서로
                // 정하면 안 된다(나중에 온 쪽이 먼저 잡을 수 있다). 전에는 먼저 게이트를 잡은 이 Open 이 닫힘을 풀고 열었고, 뒤이어 게이트를 잡은
                // 닫기가 그 세션만 걷어, 닫힘도 세션도 재연결 루프도 없는 "재연결 중" 으로 영영 남았다(검토가 찾음). 불린 순서는 락 아래 번호로 센다.
                if (_lastCloseSeq > mySeq) return;
                // 닫힌 동안 기록된 라이브 의도는 버린다 — 이제 Open 이 의도를 되살리므로(AnnounceAttached), 닫아 둔 사이 켜 둔 토글이나
                // 늦은 핸들러가 부른 StartContinuous 가 몇 분 뒤 다음 Open 에서 청하지 않은 라이브를 켰다(검토가 찾음). 0.29.1 도 닫힌 동안의
                // 시작은 Open 에서 무시했다. 상실 뒤의 Open(닫지 않았다)과 여는 중에 온 시작은 그대로 되살린다.
                wasClosed = _closed;
                if (wasClosed) { _wantContinuous = false; _intentVersion++; _intentFromStart = 0; }
                _closed = false;              // 게이트 해제 — 이제부터 재연결이 허용된다
                _openedSeq = mySeq;           // 이 Open 보다 먼저 불린 닫기는 이 세션을 걷지 않는다(Close)
                // 죽음이 접수된 세션이 아직 붙어 있으면(재연결 루프는 백오프를 기다린 뒤에야 그것을 걷는다) 여기서 걷고 새로 연다. 전에는 그것을
                // "이미 열려 있다" 로 읽어 아무것도 안 하고 정상 반환했다(IsConnected=false) — ICam.Open 은 상실 뒤의 Open 이 다시 연다고 약속하는데
                // 첫 백오프(기본 1 s) 동안은 거짓이었다(형제 저장소의 판과 대조하다 검토가 찾음). 루프는 뒤에 와서 살아 있는 세션을 보고 물러난다.
                // 닫기가 불렸는데 세션이 아직 붙어 있는 경우도 새로 연다 — 먼저 불린 닫기가 게이트를 못 잡은 사이 이 Open 이 먼저 잡았다. 그 닫기는
                // 의도를 이미 걷었고 이 Open 에 져서 세션을 안 걷으므로(Close), 여기서 "이미 열려 있다" 로 돌아가면 의도 없는 옛 세션이 남는다 —
                // 차례로 부른 닫기·열기처럼 걷고 새로 연다(검토가 찾음).
                var dead = _inner is { } current && ReferenceEquals(_lostInner, current);
                replace = _inner != null && (dead || wasClosed);
                if (_inner != null && !replace) return;   // 이미 열려 있음 (중복 Open 이 인스턴스를 둘로 만들지 않게)
            }
            if (replace) retired = RetireInner();
            try
            {
                // 여는 사이 닫기가 이겼으면 세션을 버렸다 — 부른 쪽에는 닫힌 채로 돌아간다(연결됨을 알리지 않는다).
                attached = AttachAndOpen();
                if (attached is null) return;
            }
            catch (Exception ex) when (_opt.RetryInitialOpen)
            {
                // 기동 시점에 아직 안 붙은 카메라를 영영 죽은 자리로 만들지 않는다 — 재연결 루프는
                // '한 번 열린 뒤의 상실' 에만 돌기 때문에, 여기서 손수 태워 줘야 나중에 붙을 때 합류한다.
                // 정하고 나서 적는다 — 여는 사이 닫기가 이겼으면 재시도는 안 거는데, 전에는 "뒤에서 재시도한다" 를 먼저 적었다.
                bool retrying;
                lock (_sync)
                {
                    retrying = !_disposed && !_closed;
                    if (retrying) ScheduleReconnectLocked();
                }
                // "initial" 을 붙이지 않는다 — 처음 열기만이 아니라 닫은 뒤·상실 뒤의 열기도 여기로 온다.
                CvLog.Publish(retrying ? CvLogLevel.Warning : CvLogLevel.Info, LogSource,
                    retrying ? $"[{Name}] open failed — retrying in the background."
                             : $"[{Name}] open failed; not retrying because the camera was closed meanwhile.", ex);
                return;
            }
            catch
            {
                // 열기가 실패해 부른 쪽에 던진다. <b>닫혀 있던 것을 연 것이면</b>(처음·닫은 뒤의 열기) 닫힌 상태로 되돌린다 — 안 그러면 닫힘도
                // 세션도 루프도 없는 상태가 남아, 그 사이의 StartContinuous 가 조용히 기록됐다가 다음 Open 에서 라이브를 켰다(검토가 찾음).
                // ⚠ 판별은 "들어올 때 닫혀 있었는가" 다 — "도는 루프가 없는가" 로 가르던 첫 판은, 재연결이 포기한 뒤(루프 없음) 로그가 시키는 대로
                // 부른 Open 이 실패하면 닫아 버려 라이브 의도를 잃었다: 다음 Open 은 붙는데 라이브가 안 켜지고 아무 흔적도 없었다(재검토가 찾음).
                // 상실 뒤의 열기가 실패한 것이면(루프가 돌든 포기했든) 닫지 않는다 — 이 예외는 포기가 아니고 의도는 남는다.
                // (다른 Open 이 끼어들 수 없다 — 이 catch 는 아직 게이트 안이다.)
                lock (_sync)
                {
                    if (!_disposed && wasClosed) MarkClosedLocked();
                }
                throw;
            }
        }
        finally
        {
            _gate.Release();
            RaiseRetired(retired);   // 걷은 죽은 세션의 표시 — 대개 상실 통지가 이미 내려 두어 알릴 것이 없다
        }
        // 게이트를 놓은 뒤라 그 사이 닫기·상실이 끼어들 수 있다 — 장착한 그 인스턴스가 아직 현재일 때만 연결을 알린다.
        // 재연결과 같은 뒷일(노출·연속 취득 의도)도 여기서 한다. 전에는 재연결만 의도를 되살려서, 상실 뒤 사용자가 손수 연 세션이나
        // 여는 사이 들어온 StartContinuous 는 라이브가 안 켜진 채 의도만 남았다(나중의 엉뚱한 재연결이 청하지 않은 라이브를 켰다).
        AnnounceAttached(attached);
    }

    public void Close()
    {
        var mySeq = BeginClose();
        if (mySeq > 0) FinishClose(mySeq);
    }

    /// <summary><see cref="Close"/> 의 앞 절반 — 닫기 의사를 호출 번호와 함께 기록한다(락 안). 해제됐으면 0.
    /// 시험이 "닫기가 불렸지만 아직 게이트를 못 잡은" 순간을 직접 만든다(그 틈은 스케줄링 몇 마이크로초라 시각으로는 못 세운다).</summary>
    internal int BeginClose()
    {
        // 세션 종료 의사 — 진행 중 재연결을 취소하고 이후 기동을 막는다. 취소만 하고 기다리지 않는다.
        lock (_sync)
        {
            if (_disposed) return 0;
            var mySeq = ++_lifeSeq;
            _lastCloseSeq = mySeq;            // 이보다 먼저 불려 게이트에 줄 선 Open 은 열지 않고 물러난다(Open)
            MarkClosedLocked();               // 의도 청산 — 다음 세션이 요청 없는 취득을 부활시키지 않게
            return mySeq;
        }
    }

    /// <summary><see cref="Close"/> 의 뒤 절반 — 게이트를 기다려 세션을 걷는다(나중에 불린 Open 이 이미 열었으면 걷지 않는다).</summary>
    internal void FinishClose(int mySeq)
    {
        // 시한을 두고 기다린다 — 재연결 사다리가 게이트를 쥔 채 여는 중이면 그 열기는 취소되지 않으므로
        // (열기에 취소 토큰이 없다) 무한정 기다리면 <b>닫기가 부른 쪽의 종료 예산을 넘긴다.</b> 그러면
        // 제어권을 반납하지 못한 채 프로세스가 내려가고, 곧바로 재기동하면 장치가 하트비트 시한을
        // 넘길 때까지 "다른 응용이 잡고 있다" 로 열기가 실패한다. 현장에는 "가끔 재기동이 실패한다" 로만
        // 보인다. 시한을 넘기면 <see cref="Dispose"/> 와 같이 그래도 정리를 진행한다(게이트 없이).
        Retired retired = default;
        var gated = _gate.Wait(Math.Max(0, _opt.ShutdownWaitMs));
        try
        {
            // 기다리는 사이 <b>나중에 불린</b> Open 이 먼저 게이트를 잡아 이미 열었다면 그 Open 이 이긴다 — 걷지 않는다. 아니면 닫힘을 다시
            // 세운다: 그 사이 새로 걸린 재연결 루프·의도를 걷는다(나중에 불린 Open 이 아직 안 돌았다면 그것이 뒤에 와서 다시 연다).
            // 판정과 떼어 내기를 한 락에서 한다(RetireInner) — 게이트를 못 잡은 폴백에서는 판정 뒤 떼어 내기 전에 그 Open 이 열어, 나중에 불린
            // Open 의 세션을 먼저 불린 닫기가 걷을 수 있었다(재검토가 찾음).
            retired = RetireInner(closeSeq: mySeq);
        }
        finally
        {
            if (gated) _gate.Release();
        }

        RaiseRetired(retired);
    }

    /// <summary><see cref="_sync"/> 보유 전제. 닫힘으로 표시하고 라이브 의도·밀린 재연결 요청을 걷고, 도는 재연결 루프를 취소한다.
    /// 취소는 락 안에서 한다 — 루프가 물러나며 CTS 를 폐기하는 것도 락 안이라(RetireLoop) 둘이 겹치지 않는다. 폐기와 겹친 취소를 견디는지는
    /// 런타임마다 다르다(.NET 8.0.31 은 견딘다 — 디컴파일 확인, .NET Framework 계열 구현은 확인 못 함). 등록된 콜백은 게이트 대기를 깨우는 것뿐이라
    /// 락 안에서 불러도 이 락을 되잡지 않는다.</summary>
    private void MarkClosedLocked()
    {
        _closed = true;
        _wantContinuous = false;
        _intentVersion++;
        _intentFromStart = 0;
        _reconnectPending = false;
        try { _reconnectCts?.Cancel(); } catch (ObjectDisposedException) { /* 루프가 이미 물러남 */ }
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            MarkClosedLocked();               // 루프 취소도 여기서(락 안)
            cts = _reconnectCts;              // 수거와 기동을 같은 락 아래에서 — 좀비 루프 방지
            task = _reconnectTask;
            _reconnectCts = null;
            _reconnectTask = null;
        }

        // 루프 수거와 게이트 대기가 <b>마감 하나</b>를 나눠 쓴다. 전에는 각각 ShutdownWaitMs 를 기다렸다 — 루프가 취소할 수 없는 느린 열기에
        // 묶여 있으면 둘 다 같은 사건(그 열기가 끝나 게이트를 놓는 것)을 기다렸고, 해제가 카메라당 최대 두 배 걸렸다(형제 저장소의 소비자가 자기
        // 판에서 카메라당 수 초를 실측). ⚠ 대가: 그 열기가 마감과 두 배 마감 사이에 끝나는 경우, 전에는 해제가 돌아가기 전에 늦은 세션이 버려져
        // 장치 제어권이 반납됐는데, 이제는 해제가 먼저 돌아가고 늦은 세션은 뒤에서(스레드 풀) 버려진다. 해제 직후 프로세스가 끝나면 그 세션은
        // 버려지지 못해 장치가 하트비트 시한까지 잡혀 있고, 곧바로 다시 켠 프로세스의 열기가 "다른 응용이 잡고 있다" 로 실패한다. 곧바로 다시
        // 켜야 하면 ShutdownWaitMs 를 최악의 열기 시간(탐색 + 열기)보다 크게 잡는다(CamReconnectOpt.ShutdownWaitMs).
        var budgetMs = Math.Max(0, _opt.ShutdownWaitMs);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try { task?.Wait(budgetMs); } catch { /* 종료 경로 — 삼킨다 */ }
        // 루프가 끝났으면 그 CTS 를 여기서 치운다(칸을 비웠으므로 루프는 치우지 않는다). 안 끝났으면 손대지 않는다 — 루프가 아직 토큰을 쓴다.
        if (task is { IsCompleted: true }) cts?.Dispose();
        var leftMs = (int)Math.Max(0, budgetMs - elapsed.ElapsedMilliseconds);

        Retired retired;
        if (_gate.Wait(leftMs))
        {
            try { retired = RetireInner(); }
            finally { _gate.Release(); }
        }
        else
        {
            retired = RetireInner();   // 전이가 걸려 있어도 종료는 진행한다
        }

        // ⚠ 게이트는 폐기하지 않는다. 게이트를 쥔 채 느린 열기에 묶인 재장착 시도나, 그 뒤에 줄 선 Open 이 남아 있을 수 있다 —
        // 폐기는 이미 기다리던 쪽을 깨우지 않고, 폐기된 게이트의 반납은 던진다. 폐기했을 때 두 갈래로 탈이 났다: 늦게 끝난 재장착의
        // 반납이 던져 루프가 "reconnect loop failed" 오류를 남겼고, 그 반납을 삼키자 이번에는 줄 선 Open 이 영영 깨어나지 못했다.
        // 폐기하지 않으면 늦은 시도는 장착 직전에 해제를 보고 세션을 버리고(AttachAndOpen) 게이트를 반납하며, 줄 선 Open 은 깨어나
        // 해제를 보고 던진다. SemaphoreSlim 은 대기 핸들을 꺼내 쓰지 않는 한 풀 자원이 없다.
        RaiseRetired(retired);
    }

    // === 조작 ===

    public void GrabOne()
    {
        var cam = CurrentOrThrow();
        cam.GrabOne();
    }

    /// <summary>한 장을 찍어 돌려준다 — <see cref="ICamGrabAsync"/>. <b>안쪽으로 그대로 넘긴다</b>:
    /// 안쪽이 표식을 단 구현이면 그쪽 짝짓기 보장이 살아야 하고, 아니면 확장의 기본 절차가 돈다.
    /// 미연결 구간의 경계는 <see cref="GrabOne"/> 과 같다 — 즉시 결과가 필요한 호출이라 <b>던진다</b>.
    /// null 로 접으면 상위가 오지 않을 프레임을 계속 기다린다.</summary>
    public Task<CamFrame?> GrabFrameAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var cam = CurrentOrThrow();
        return cam.GrabFrameAsync(timeout, ct);
    }

    public void StartContinuous()
    {
        ICam? cam;
        int myVersion;
        bool closed;
        StartIntent? mine = null;
        lock (_sync)
        {
            ThrowIfDisposed();
            // 닫힌 동안의 시작은 기록하지 않는다 — 다음 Open 이 어차피 버린다(10-R14). 조용히 버리면 토글을 되살리는 호스트가 왜 라이브가 안
            // 켜졌는지 알 길이 없어 한 줄 남긴다. 정상 구성(열기 전에 저장된 토글을 되살림)도 지나는 길이라 경고가 아니라 Info 다.
            closed = _closed;
            if (closed) { cam = null; myVersion = 0; }
            else
            {
                myVersion = ++_intentVersion;
                mine = new StartIntent { Version = myVersion, WantedBefore = _wantContinuous, BeforeFrom = _intentFromStart };
                _startIntents.Add(mine);
                _wantContinuous = true;           // 미연결 구간이면 의도만 기록 — 복원 때 반영된다
                _intentFromStart = myVersion;
                cam = _connected ? _inner : null;
            }
        }
        if (closed)
        {
            CvLog.Publish(CvLogLevel.Info, LogSource, $"[{Name}] StartContinuous ignored — the camera is closed; call Open() first.");
            return;
        }
        if (cam is null)
        {
            lock (_sync) ForgetStartIntentLocked(mine!);   // 의도만 기록했다 — 이 결정은 선다
            return;
        }
        try
        {
            cam.StartContinuous();
        }
        catch
        {
            bool closedMeanwhile, loweredNow = false, enforceOff = false;
            int offAt = 0;
            lock (_sync)
            {
                // 닫기·해제·교체가 이긴 뒤라면(안쪽이 그 청산으로 닫히거나 폐기돼 던졌다) 부른 쪽에 안쪽의 예외를 올리지 않는다 — 나중 명령이
                // 이긴 것이지 시작이 실패한 것이 아니다. 닫힌 것은 이 데코레이터인데 "폐기된 GevCam" 을 들으면 원인을 엉뚱한 데로 찾는다.
                // 세션이 죽은 것도 같다 — 제어 상실이 안쪽에 막 닿은 순간의 시작은 상실 예외로 던지는데, 그것은 거부가 아니라 상실이다.
                // 의도를 거두면 재연결이 라이브를 안 되살린다. 상실 통지가 이미 접수됐으면 의도를 두고 조용히 돌아간다.
                closedMeanwhile = _closed || _disposed || !ReferenceEquals(_inner, cam) || ReferenceEquals(_lostInner, cam);
                // 안쪽이 거부했다(단발 그랩이 기다리는 중 등). 부른 쪽은 예외를 받았는데 의도만 남겨 두면
                // 다음 재연결 때 아무도 청하지 않은 연속 취득이 되살아난다 — 그 사이 새 의도가 없을 때만 되돌린다.
                // 의도가 바뀌면 판도 바뀐다 — 되돌림도 하나의 명령이다. 판을 두면, 이 시작 뒤를 보고 이미 "나중 시작이 라이브를 원한다" 로 정한
                // 정지 꼬리가 되돌림을 못 알아보고 던진 이 시작의 라이브를 되켰다(검토가 재현).
                // 되돌릴지는 판 번호가 아니라 <b>계보</b>로 가른다 — 지금 의도가 이 시작의 결정이면(뒤 시작이 거절되며 이 시작의 결정으로 되돌려 둔
                // 경우 포함) 되돌리고, 되돌릴 곳의 결정도 이미 거절된 시작이면 이어서 푼다(StartIntent 참조). 정지·닫기가 그 사이 의도를 세웠으면
                // 계보가 끊겨 있어 건드리지 않는다.
                mine!.Refused = true;
                if (!closedMeanwhile && _intentFromStart == mine.Version)
                {
                    var want = mine.WantedBefore;
                    var from = mine.BeforeFrom;
                    while (from != 0 && FindStartIntentLocked(from) is { Refused: true } earlier)
                    {
                        want = earlier.WantedBefore;
                        from = earlier.BeforeFrom;
                    }
                    _wantContinuous = want;
                    _intentFromStart = from;
                    _intentVersion++;
                    // 되돌린 끝이 꺼짐이면 끝 상태도 꺼짐으로 맞춘다 — 이 시작이 안쪽에서 기다리는 사이 우리 정지의 꼬리가 이 시작을 위해 라이브를
                    // 되켰을 수 있다. 그대로 두면 부른 쪽은 예외를 받았는데 라이브가 돌고 켜짐으로 표시되며, 숨은 의도 꺼짐 때문에 다음 재연결은
                    // 그것을 되살리지 않는다(검토가 재현).
                    if (!want)
                    {
                        if (_grabbing) { _grabbing = false; loweredNow = true; }
                        enforceOff = true;
                        offAt = _intentVersion;
                    }
                }
                PruneStartIntentsLocked();
            }
            if (loweredNow) SafeRaise(() => GrabbingChanged?.Invoke(this, false), nameof(GrabbingChanged));
            if (enforceOff && ReadGrabbing(cam)) StopInnerOwned(cam, offAt);
            if (closedMeanwhile) return;
            throw;
        }

        // 켜짐은 <b>안쪽이 실제로 켜졌을 때만</b> 알린다. 안쪽은 시작하지 않고 정상 반환할 수 있다(GevCam: 부른 뒤 온 정지가 이긴다 —
        // ICam.StartContinuous). 반환만 보고 알리면, 안쪽이 무른 시작을 켜짐으로 알려 아무것도 안 도는데 IsGrabbing=true 가 남는다.
        // 우리 의도 순서와 안쪽의 정지 순서가 엇갈려도(두 스레드가 거의 같이 부른 정지·시작) 같은 이유로 거짓이 남는다(검토가 찾음).
        // 안쪽 IsGrabbing 은 계약상 마지막 통지와 같은 말을 하고, 이 저장소의 구현은 전부 반환 전에 세운다 — 락 밖에서 읽는다(안쪽
        // 게터가 제 락을 쥘 수 있다). 읽은 뒤 안쪽이 멈추면 그 통지가 OnInnerGrabStopped 로 와서 맞춰진다.
        // 그 밖에: 정지·닫기가 나중에 왔으면 알리지 않고(나중 명령이 이긴다), 시작하는 사이 세션이 죽었어도 알리지 않는다(끊김 뒤에
        // 켜짐이 나가 IsGrabbing=true·IsConnected=false 로 남았다 — 의도는 남아 재연결이 새 세션에서 되살린다).
        // 판정과 표시를 같은 락에서 한다 — 판정 뒤 표시 전에 끼어든 상실이 다시 틈이 되지 않게.
        var innerGrabbing = ReadGrabbing(cam);
        bool stopInner = false, announce = false;
        int decidedAt;
        lock (_sync)
        {
            ForgetStartIntentLocked(mine!);   // 안쪽이 받아들였다 — 이 결정은 선다(뒤 시작이 거절돼 여기로 되돌아와도 켜짐이 맞다)
            decidedAt = _intentVersion;
            var sameInner = ReferenceEquals(_inner, cam);
            var superseded = (_intentVersion != myVersion && !_wantContinuous) || !sameInner || _closed || _disposed;
            if (superseded) stopInner = sameInner;
            else if (innerGrabbing && _connected && !ReferenceEquals(_lostInner, cam) && !_grabbing) { _grabbing = true; announce = true; }
        }
        // 안쪽이 이미 시작했다면 정지가 이기도록 한 번 더 멈춘다 — 이미 멈춘 안쪽에는 아무 일도 안 한다. 청산된 안쪽은 건드리지 않는다.
        if (stopInner) StopInnerOwned(cam, decidedAt);
        if (announce) SafeRaise(() => GrabbingChanged?.Invoke(this, true), nameof(GrabbingChanged));
    }

    public void StopContinuous()
    {
        ICam? cam;
        int myVersion;
        lock (_sync)
        {
            if (_disposed) return;
            _wantContinuous = false;
            myVersion = ++_intentVersion;
            _intentFromStart = 0;
            cam = _connected ? _inner : null;
            // 우리가 이 안쪽을 멈추는 중이라고 적어 둔다 — 그 사이 온 이 안쪽의 꺼짐 통지는 우리 정지의 메아리다(OnInnerGrabStopped).
            if (cam != null) _ownStopCams.Add(cam);
        }
        try
        {
            cam?.StopContinuous();
        }
        finally
        {
            if (cam != null) lock (_sync) RemoveOwnStopLocked(cam);
        }
        // 꺼짐 표시는 <b>최종 의도</b>로 정한다 — 의도가 꺼져 있으면 내린다(이미 내려가 있으면 아무 일도 없다). 안쪽을 멈추는 사이 더 나중의 시작이
        // 들어와 실제로 켰다면 의도가 켜져 있어 내리지 않는다 — 그 시작이 켜짐을 제 자리에서 알린다. 전에는 "이 정지가 아직 마지막 명령인가(판 일치)"
        // 로 정해서, 그 나중 시작이 안쪽에서 던져 의도를 되돌린 경우 아무도 내리지 않아 켜짐으로 표시된 채 아무것도 안 돌았다(검토가 재현).
        // 더 나중의 시작이 있는데 그 시작이 우리 정지보다 먼저 안쪽에 닿았다면(이미 돌고 있어 할 일 없이 돌아갔다) 우리 정지가 그것을 도로 껐다 —
        // 나중 명령이 이기도록 같은 세션에서 되켠다(StartContinuous 의 "나중 정지가 이기면 한 번 더 멈춘다" 의 거울). 같은 안쪽에 우리 정지가 아직
        // 더 돌고 있으면 그 정지의 끝에 맡긴다(그 정지가 끝나야 되켠 것이 남는다).
        bool lower, relight = false;
        lock (_sync)
        {
            lower = !_wantContinuous && _grabbing;
            if (lower) _grabbing = false;
            else relight = cam != null && _intentVersion != myVersion && _wantContinuous && IsLiveSessionLocked(cam) && !IsOwnStopLocked(cam);
        }
        if (lower) SafeRaise(() => GrabbingChanged?.Invoke(this, false), nameof(GrabbingChanged));
        if (relight) RelightAfterOwnStop(cam!);
    }

    /// <summary>나중 명령(정지)이 이겨 우리가 안쪽을 한 번 더 멈춘다 — <see cref="StopContinuous"/> 와 같이, 멈추는 사이 온 꺼짐 통지를 우리 정지의
    /// 메아리로 표시하고, 멈추는 사이 또 나중의 시작이 들어와 라이브를 원하면 끝에서 되켠다. 되돌리기 정지가 그 나중 시작의 라이브를 끄고,
    /// 그 메아리는 "청하지 않은 정지" 로도 안 읽히면 켜짐으로 표시된 채 아무것도 안 도는 거짓이 남는다.</summary>
    /// <param name="cam">멈출 안쪽 인스턴스.</param>
    /// <param name="decidedAt">멈추기로 정한 순간의 의도 판 — 그 뒤에 바뀌었으면 더 나중 명령이 있다.</param>
    private void StopInnerOwned(ICam cam, int decidedAt)
    {
        lock (_sync) _ownStopCams.Add(cam);
        try { Try(() => cam.StopContinuous()); }
        finally { lock (_sync) RemoveOwnStopLocked(cam); }
        bool relight;
        lock (_sync) relight = _intentVersion != decidedAt && _wantContinuous && IsLiveSessionLocked(cam) && !IsOwnStopLocked(cam);
        if (relight) RelightAfterOwnStop(cam);
    }

    /// <summary><see cref="_sync"/> 보유 전제. 그 판의 시작 기록 — 성공해 지워졌으면 null(그 결정은 선다).</summary>
    private StartIntent? FindStartIntentLocked(int version)
    {
        foreach (var s in _startIntents) if (s.Version == version) return s;
        return null;
    }

    /// <summary><see cref="_sync"/> 보유 전제. 안쪽이 받아들인(또는 의도만 기록한) 시작 — 기록을 지운다. 그 결정으로 되돌아오는 뒤 시작은 거기서 멈춘다.</summary>
    private void ForgetStartIntentLocked(StartIntent s)
    {
        _startIntents.Remove(s);
        PruneStartIntentsLocked();
    }

    /// <summary><see cref="_sync"/> 보유 전제. 결과를 기다리는 시작이 하나도 없으면 거절된 기록도 비운다 — 그것을 되돌릴 곳으로 가리킬 시작이 더는 없다.</summary>
    private void PruneStartIntentsLocked()
    {
        foreach (var s in _startIntents) if (!s.Refused) return;
        _startIntents.Clear();
    }

    /// <summary><see cref="_sync"/> 보유 전제. 이 인스턴스를 지금 우리가 멈추는 중인가(인스턴스 비교).</summary>
    private bool IsOwnStopLocked(object? cam)
    {
        foreach (var c in _ownStopCams) if (ReferenceEquals(c, cam)) return true;
        return false;
    }

    /// <summary><see cref="_sync"/> 보유 전제. 우리 정지 하나가 끝났다 — 그 인스턴스 표시를 하나 뗀다.</summary>
    private void RemoveOwnStopLocked(object cam)
    {
        for (var i = 0; i < _ownStopCams.Count; i++)
            if (ReferenceEquals(_ownStopCams[i], cam)) { _ownStopCams.RemoveAt(i); return; }
    }

    /// <summary><see cref="_sync"/> 보유 전제. 이 인스턴스가 지금 붙어 있고, 연결돼 있고, 죽음이 접수되지 않았고, 닫히지 않았다.</summary>
    private bool IsLiveSessionLocked(ICam cam)
        => ReferenceEquals(_inner, cam) && _connected && !ReferenceEquals(_lostInner, cam) && !_closed && !_disposed;

    /// <summary>우리 정지가 도는 사이 들어온 나중 시작이 이기게 한다 — 안쪽이 꺼져 있으면 같은 세션에서 다시 켠다.
    /// 시작이 우리 정지보다 먼저 안쪽에 닿은 순서에서는(이미 돌고 있어 할 일 없이 돌아갔다) 우리 정지가 그것을 도로 꺼, 여기서 맞추지 않으면 의도는
    /// 켜짐인데 아무것도 안 돈다.
    /// ⚠ 판정은 판 번호가 아니라 <b>의도</b>로 한다 — 의도가 꺼져 있으면 무조건 물러난다(그 나중 시작이 안쪽에서 던져 의도를 되돌렸거나 더 나중 정지가
    /// 왔다). 판만 보던 첫 판은 던진 시작의 라이브를 되켜 아무도 청하지 않은 라이브를 켰다(검토가 재현).
    /// ⚠ 세션은 교체하지 않는다 — 켜 봤는데 안 켜진 것(시작을 늦게 알리는 안쪽, 더 나중 정지에 진 안쪽)도, 거절당한 것(GevCam: 그 틈의 단발 그랩)도
    /// 세션 죽음이 아니다. 늦은 켜짐은 OnInnerGrabStarted 가 받고, 거절은 경고로 알린다(라이브·표시 꺼짐). 정말 죽었으면 연결 상실 통지가 교체한다.</summary>
    private void RelightAfterOwnStop(ICam cam)
    {
        if (ReadGrabbing(cam))
        {
            // 나중 시작이 제대로 켰다. 그 사이 메아리가 표시를 내렸고 그 시작의 알림이 앞섰다면 여기서 맞춘다(전이일 때만).
            AnnounceIfLiveWanted(cam);
            return;
        }
        try { cam.StartContinuous(); }
        catch (Exception ex)
        {
            bool stillWanted;
            lock (_sync) stillWanted = _wantContinuous && IsLiveSessionLocked(cam);
            // 그새 닫기·교체·상실이 이겼거나 의도가 꺼졌으면(더 나중 정지, 그 시작의 되돌림) 할 일이 없다.
            if (!stillWanted) return;
            // 세션은 살아 있다 — 안쪽이 거절한 것이지 죽은 것이 아니다(GevCam: 그 틈에 단발 그랩이 기다린다 — 시작의 거절 사유는 그것과 "안 열림" 뿐이다).
            // 교체하면 그 그랩을 끊고 연결 끊김까지 알린다(첫 판이 그랬다 — 검토가 재현). 정말 죽었으면 안쪽의 연결 상실 통지가 따로 와서 교체한다.
            // 판 번호로 포기를 가르지 않는다 — 그 사이 실패한 다른 시작이 판만 옮기고 의도는 켜진 채 두면, 첫 판은 아무 흔적 없이 물러났다.
            // 라이브는 꺼진 채(표시도 꺼짐) 남는다 — 알린다.
            CvLog.Publish(CvLogLevel.Warning, LogSource,
                $"[{Name}] live could not be restarted after StopContinuous and a later StartContinuous overlapped — " +
                "it stays off; call StartContinuous again.", ex);
            return;
        }
        var on = ReadGrabbing(cam);
        bool stopInner = false, announce = false;
        int decidedAt;
        lock (_sync)
        {
            decidedAt = _intentVersion;
            var superseded = !_wantContinuous || !IsLiveSessionLocked(cam);
            if (superseded) stopInner = ReferenceEquals(_inner, cam) && !_wantContinuous;
            else if (on && !_grabbing) { _grabbing = true; announce = true; }
        }
        if (stopInner) StopInnerOwned(cam, decidedAt);
        if (announce) SafeRaise(() => GrabbingChanged?.Invoke(this, true), nameof(GrabbingChanged));
    }

    /// <summary>라이브를 원하고 이 세션이 살아 있고 안쪽이 돌고 있는데 표시가 꺼져 있으면 켜짐을 알린다(안쪽 상태는 락 밖에서 읽는다).</summary>
    private void AnnounceIfLiveWanted(ICam cam)
    {
        if (!ReadGrabbing(cam)) return;
        bool announce;
        lock (_sync)
        {
            announce = _wantContinuous && IsLiveSessionLocked(cam) && !_grabbing;
            if (announce) _grabbing = true;
        }
        if (announce) SafeRaise(() => GrabbingChanged?.Invoke(this, true), nameof(GrabbingChanged));
    }

    public void SetExposureTimeUs(double timeUs)
    {
        ICam? cam;
        lock (_sync)
        {
            ThrowIfDisposed();
            _exposureUs = timeUs;             // 재연결 후 새 인스턴스에 재적용 (초기 옵션값으로 되돌아가지 않게)
            _exposureVersion++;               // 장착 중에 온 값이면 장착한 쪽이 알아채고 다시 쓴다(ReapplyExposureIfStale)
            cam = _connected ? _inner : null;
            if (cam is not null) _attachedExposureVersion = _exposureVersion;
        }
        cam?.SetExposureTimeUs(timeUs);
    }

    // === 내부 인스턴스 장착·청산 ===

    /// <summary>새 인스턴스를 만들어 장착하고 <b>그 인스턴스를 돌려준다</b> — 부른 쪽은 그것으로 연결을 알린다(<see cref="RaiseConnectedIfCurrent"/>).
    /// 실패하면 반쯤 만들어진 인스턴스를 남기지 않고 던진다. 여는 사이 닫기·해제가 이겼으면 그 세션을 닫고 버린 뒤 null 을
    /// 돌려준다(던지지 않는다 — 실패가 아니라 청산이다).
    /// <see cref="_gate"/> 보유 전제 — 팩토리와 Open 은 락 밖에서 부른다(사용자 코드).</summary>
    private ICam? AttachAndOpen()
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
            int exposureVersion;
            lock (_sync) { exposure = _exposureUs; exposureVersion = _exposureVersion; }
            if (exposure is { } us) cam.SetExposureTimeUs(us);   // 연속취득 재개보다 먼저

            string? wonBy;
            lock (_sync)
            {
                _attachedExposureVersion = exposureVersion;   // 장착 뒤 더 새 값이 와 있으면 ReapplyExposureIfStale 이 다시 쓴다
                // ① 여는 사이 닫기·해제가 이겼다 — 열기에는 취소가 없어 닫기는 게이트를 시한까지만 기다리고 먼저 돌아간다. 여기서 장착하면
                //    닫은 카메라가 연결된 채 남아 다음 닫기까지 장치 제어권을 쥔다(곧 다시 켠 프로세스가 "다른 응용이 잡고 있다" 로 실패한다).
                //    이 확인은 장착과 같은 락 안이어야 한다 — 닫기가 _closed 를 세우는 것도 이 락 아래다.
                wonBy = _disposed ? "Dispose" : _closed ? "Close" : null;
                if (wonBy is null) AttachLocked(cam);
            }
            if (wonBy is not null)
            {
                DiscardUnattached(cam);
                CvLog.Publish(CvLogLevel.Info, LogSource, $"[{Name}] a session finished opening after {wonBy} — discarded.");
                return null;
            }
            return cam;
        }
        catch
        {
            if (cam != null) DiscardUnattached(cam);
            throw;
        }
    }

    /// <summary><see cref="_sync"/> 보유 전제. 열린 인스턴스를 현재 인스턴스로 장착한다 — 연결을 다시 확인하고 나서.</summary>
    private void AttachLocked(ICam cam)
    {
        // ② 열기가 던지지 않았다고 연결된 것은 아니다 — DeadCam 은 열기를 경고만 남기고 돌려준다. 그리고 ③ 열린 직후·장착 전에
        //    끊겼으면 그 상실 통지는 아직 현재 인스턴스의 것이 아니라 유령으로 버려졌다(OnInnerLost 의 ReferenceEquals) — 그대로
        //    장착하면 죽은 인스턴스를 연결됨으로 들고, 버려진 통지는 다시 오지 않아 영영 재연결하지 않는다. 둘 다 여기서 상태를
        //    다시 본다. 같은 락 안이라 이 뒤에 오는 상실 통지는 현재 인스턴스의 것으로 처리된다. ICam 은 "상태를 먼저 내리고 알린다" 를
        //    구현자에게 요구하므로, 통지가 아직 안 왔어도 IsConnected 는 이미 거짓이다(ICam.Open 의 "정상 반환이면 연결됨" 참조).
        if (!cam.IsConnected) throw NotConnectedAfterOpen(cam);
        // _connected 는 여기서 세우지 않는다 — 연결은 게이트를 놓은 뒤 RaiseConnectedIfCurrent 가 다시 확인하고 세운다.
        // 여기서 미리 true 로 만들면 그 알림이 "변화 없음" 으로 삼켜진다.
        _inner = cam;
        _attachGen++;
        Name = cam.Name;
        ComType = cam.ComType;
    }

    /// <summary>열기가 정상 반환했는데 연결 안 됨 — 계약(ICam.Open: 정상 반환이면 연결됨)을 이름으로 댄다. 상태를 늦게(콜백으로)
    /// 세우는 구현도 여기 걸리므로 "잃었다" 로만 단정하지 않는다.</summary>
    private static InvalidOperationException NotConnectedAfterOpen(ICam cam)
        => new(cam is DeadCam dead
            ? $"Camera '{cam.Name}' cannot be opened: {dead.Reason}"
            : $"Camera '{cam.Name}' returned from Open() with IsConnected=false. ICam.Open must return connected; " +
              "either the implementation sets IsConnected later, or the connection was lost right after opening.");

    /// <summary>장착하지 않은 인스턴스를 구독 해제 → 닫기 → 폐기한다 — 반쯤 열린 인스턴스가 장치를 점유하면 이후 재시도가 전부 실패한다.</summary>
    private void DiscardUnattached(ICam cam)
    {
        cam.FrameAcquired -= _onInnerFrame;
        cam.ConnectionChanged -= _onInnerConnection;
        cam.GrabbingChanged -= _onInnerGrabbing;
        Try(() => cam.Close());
        Try(() => cam.Dispose());
    }

    /// <summary>청산이 내린 표시 — 부른 쪽이 게이트를 놓은 뒤 <see cref="RaiseRetired"/> 로 알린다.</summary>
    private readonly record struct Retired(bool WasConnected, bool WasGrabbing);

    /// <summary>현재 인스턴스를 구독 해제 → 정지·닫기 → 폐기 순으로 청산한다.
    /// 구독 해제가 먼저다 — 죽어 가는 인스턴스의 마지막 통지가 새 세션을 다시 끊는 자기 증식을 막는다.
    /// <see cref="_gate"/> 보유 전제(<see cref="Close"/>·<see cref="Dispose"/> 가 게이트를 시한 안에 못 잡았을 때의 폴백만 예외).
    /// <b>내린 표시를 돌려준다</b> — 여기서 조용히 내리고 뒤에서 "전이일 때만 알린다" 로 끊김을 알리면 "이미 거짓이라 전이가 아니다" 로
    /// 삼켜져, 닫기가 끊김을 한 번도 알리지 않았다(이 코드를 옮겨 간 소비자가 찾았다 — 통지로 상태를 거울질하는 화면은 닫은 카메라를 연결됨으로 든다).</summary>
    /// <param name="closeSeq"><see cref="Close"/> 가 부를 때 그 호출 번호 — 그보다 나중에 불린 Open 이 이미 열었으면 아무것도 안 하고, 아니면
    /// 떼어 내기와 같은 락 안에서 닫힘을 다시 세운다.</param>
    private Retired RetireInner(int? closeSeq = null)
    {
        ICam? cam;
        Retired retired;
        lock (_sync)
        {
            if (closeSeq is { } seq)
            {
                if (_openedSeq > seq) return default;
                MarkClosedLocked();
            }
            cam = _inner;
            _inner = null;
            _lostInner = null;   // 시체를 치웠다 — 다음 인스턴스의 죽음은 새 사건이다
            retired = new Retired(_connected, _grabbing);
            _connected = false;
            _grabbing = false;
        }
        if (cam is null) return retired;

        cam.FrameAcquired -= _onInnerFrame;
        cam.ConnectionChanged -= _onInnerConnection;
        cam.GrabbingChanged -= _onInnerGrabbing;   // 바로 밑에서 우리가 세우는 것이 '스스로 멈췄다' 로 되읽히지 않게
        Try(() => cam.StopContinuous());
        Try(() => cam.Close());
        Try(() => cam.Dispose());
        return retired;
    }

    /// <summary>청산이 내린 표시를 알린다 — 취득 먼저, 연결 뒤(안쪽 구현이 내는 순서와 같다). 게이트를 놓은 뒤 부른다.</summary>
    private void RaiseRetired(Retired retired)
    {
        if (retired.WasGrabbing) SafeRaise(() => GrabbingChanged?.Invoke(this, false), nameof(GrabbingChanged));
        if (retired.WasConnected) SafeRaise(() => ConnectionChanged?.Invoke(this, new ConnArgs(false)), nameof(ConnectionChanged));
    }

    /// <summary>장착한 세션의 뒷일 — 연결을 알리고(현재일 때만), 장착 중에 온 노출을 다시 쓰고, 연속 취득 의도를 되살린다.
    /// 첫 열기와 재장착이 같은 길을 탄다.</summary>
    private void AnnounceAttached(ICam? cam)
    {
        if (cam is null || !RaiseConnectedIfCurrent(cam)) return;
        ReapplyExposureIfStale(cam);
        ResumeContinuous(cam);
    }

    /// <summary>장착 중에(노출을 읽은 뒤·연결을 알리기 전) 온 <see cref="SetExposureTimeUs"/> 를 그 세션에 다시 쓴다. 그 사이에는
    /// 연결 전이라 값이 기록만 되고 안쪽으로 안 갔다 — 예외도 로그도 없이 옛 노출로 도는, 계약이 막으라고 적은 바로 그 모양이었다(검토가 찾음).
    /// 다시 쓰는 사이 사용자의 더 새 값이 먼저 들어가면 우리 쓰기가 그것을 덮는다 — 쓴 뒤 판이 움직였으면 최신 값으로 한 번 더 쓴다(판이
    /// 같아질 때까지, 몇 번으로 묶어서).</summary>
    private void ReapplyExposureIfStale(ICam cam)
    {
        for (var round = 0; round < 3; round++)
        {
            double? exposure;
            int version;
            lock (_sync)
            {
                if (!ReferenceEquals(_inner, cam) || _attachedExposureVersion == _exposureVersion) return;
                exposure = _exposureUs;
                version = _exposureVersion;
                _attachedExposureVersion = version;
            }
            if (exposure is { } us)
            {
                try { cam.SetExposureTimeUs(us); }
                catch (Exception ex)
                {
                    CvLog.Publish(CvLogLevel.Warning, LogSource, $"[{Name}] failed to apply exposure {us}us set while the session was being attached.", ex);
                    return;
                }
            }
            lock (_sync)
            {
                if (_exposureVersion == version) return;   // 쓰는 사이 새 값이 없었다
                _attachedExposureVersion = version;         // 새 값이 들어왔다 — 우리 쓰기가 그것을 덮었을 수 있으니 한 번 더
            }
        }
    }

    /// <summary>장착한 인스턴스의 연결을 알린다 — <b>그 인스턴스가 아직 현재이고, 닫히지 않았고, 죽음이 접수되지 않았을 때만.</b>
    /// 판정과 표시는 같은 락에서 한다. 알렸거나 이미 연결됨이면 true, 그사이 닫기·교체·상실이 이겼으면 false(아무것도 안 알린다).
    ///
    /// 연결은 게이트를 놓은 뒤에 알린다(구독자가 되불러도 되게). 그런데 전에는 그 알림이 아무것도 다시 보지 않아, 게이트를 놓은
    /// 틈에 닫기가 들어오면 닫기는 아직 연결 전인 세션을 조용히 청산하고 돌아가고, 뒤이은 알림이 연결됨을 세웠다 — 닫은 뒤에
    /// IsConnected=true·안쪽 없음, 다음 Open 은 "이미 참" 이라 연결을 알리지도 않았다(검토가 구독자로 틈을 넓혀 8/8 재현).
    /// 같은 틈에 온 상실도 _connected 가 아직 거짓이라 끊김 없이 재연결만 걸리고, 이 알림이 죽은 세션을 연결됨으로 세웠다.
    /// <c>internal</c> 인 것은 회귀가 틈을 흉내 내지 않고 규칙을 직접 부르게 하려는 것이다.</summary>
    internal bool RaiseConnectedIfCurrent(ICam? cam)
    {
        if (cam is null) return false;
        lock (_sync)
        {
            if (_closed || _disposed || !ReferenceEquals(_inner, cam) || ReferenceEquals(_lostInner, cam)) return false;
            if (_connected) return true;
            _connected = true;
        }
        SafeRaise(() => ConnectionChanged?.Invoke(this, new ConnArgs(true)), nameof(ConnectionChanged));
        return true;
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

    /// <summary>내부에서 온 "연속 취득이 켜졌다" 통지 — 시작을 반환 뒤에 알리는 구현의 늦은 켜짐만 받아 맞춘다. 그 구현은 반환 시점에
    /// 아직 꺼져 있어 StartContinuous·ResumeContinuous 가 켜짐을 알리지 않았다. 켜짐을 받아들이는 것은 <b>현재 인스턴스이고, 연결돼 있고,
    /// 죽음이 접수되지 않았고, 라이브 의도가 살아 있을 때만</b>이다 — 의도가 내려간 뒤의 켜짐(정지에 진 재개 등)은 받지 않는다. 그래서
    /// 정지가 이긴 재개에서 켜짐/꺼짐 한 쌍이 덧나지 않는다. 우리가 이미 켜짐을 알렸으면 전이가 아니라 아무 일도 없다.</summary>
    private void OnInnerGrabStarted(object? sender)
    {
        lock (_sync)
        {
            if (_disposed || _closed || !ReferenceEquals(_inner, sender) || ReferenceEquals(_lostInner, sender)) return;
            if (!_connected || !_wantContinuous || _grabbing) return;
            _grabbing = true;
        }
        SafeRaise(() => GrabbingChanged?.Invoke(this, true), nameof(GrabbingChanged));
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
        // 안쪽이 지금 실제로 도는가 — 락 밖에서 읽는다(안쪽 게터가 제 락을 쥘 수 있다). 우리 정지의 메아리일 때만 쓴다.
        var innerOn = sender is ICam senderCam && ReadGrabbing(senderCam);
        bool resume, lowered = false, echoWhileWanted = false;
        lock (_sync)
        {
            if (_disposed || _closed) return;                    // 게이트 — 정비 중 부활 금지
            if (!ReferenceEquals(_inner, sender)) return;        // 폐기된 인스턴스의 유령 통지
            // 연결까지 잃은 것이면 이 통지는 그 사건의 앞 줄일 뿐이다 — 되살리기는 연결 상실 경로가
            // 맡는다(곧 이어 온다). 여기서 같이 나서면 요청이 두 건이 되어 방금 살아난 세션을 다시 뜯고,
            // 로그에는 제어 상실이 "청하지도 않았는데 멈췄다" 로 남아 원인을 엉뚱한 데로 보낸다.
            if (sender is ICam { IsConnected: false }) return;
            // 우리 StopContinuous 가 지금 이 안쪽을 멈추는 중이면 이 통지는 그 메아리다 — 의도가 그새 올라가 있어도(다른 스레드의 시작) 청하지
            // 않은 정지가 아니다. 전에는 통지가 닿은 순간의 의도만 보고, 안쪽이 정지를 마무리하는 동안(GevCam 은 최대 약 2 s) 들어온 시작이
            // 의도를 올려 두면 멀쩡한 세션을 경고와 함께 교체했다(끊김 알림, 백오프 동안 그랩 실패 — 검토가 찾음). 나중 시작이 이기는 것은
            // 정지 쪽이 끝에서 맞춘다(RelightAfterOwnStop). ⚠ 정지를 마친 뒤에야(비동기로) 꺼짐을 알리는 구현은 이 표시가 이미 지워져 옛 길로 간다.
            // 메아리여도 <b>표시는 사실대로</b> 둔다: 안쪽이 지금 꺼져 있으면 내린다(켜지는 순간 그 시작이나 정지 꼬리가 다시 알린다). 안쪽이 이미 다시
            // 돌고 있으면(나중 시작이 통지보다 먼저 켰다) 건드리지 않는다. 첫 판은 의도가 켜져 있으면 표시를 그대로 두어, 그 나중 시작이 안쪽에서
            // 던져 의도를 되돌린 경우 켜짐으로 표시된 채 아무것도 안 돌았다(검토가 재현).
            // 이 창 안에서는 진짜 스스로 멈춤(스트림 붕괴)도 메아리로 읽힌다 — 그때 되살리는 것은 교체가 아니라 정지 꼬리의 같은 세션 되켜기다
            // (GevCam 은 붕괴한 스트림이면 되켠 펌프가 다시 스스로 멈추고, 그때는 이 창 밖이라 교체된다).
            if (IsOwnStopLocked(sender))
            {
                resume = false;
                if (_wantContinuous && innerOn) return;
                echoWhileWanted = _wantContinuous;
            }
            else resume = _wantContinuous;
            // 내리기는 판정과 <b>같은 락</b>에서 한다. 락을 놓았다 다시 잡아 내리면(RaiseGrabbing) 그 사이 정지 꼬리가 되켜고 켜짐을 확인한 뒤에
            // 이 내리기가 떨어져, 라이브가 도는데 IsGrabbing=false 로 굳었다(검토가 재현 — 다른 스레드에서 온 스스로 멈춤이 메아리로 읽힌 경우).
            if (!resume && _grabbing) { _grabbing = false; lowered = true; }
        }
        if (!resume)
        {
            // 전이에서만 나가므로 StopContinuous 가 이미 낸 통지와 겹치지 않는다.
            if (lowered) SafeRaise(() => GrabbingChanged?.Invoke(this, false), nameof(GrabbingChanged));
            if (echoWhileWanted)
                CvLog.Publish(CvLogLevel.Debug, LogSource,
                    $"[{Name}] a stop notification during our own StopContinuous was taken as its echo — a later StartContinuous wants live, " +
                    "so the stop restarts it on this session.");
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
                lock (_sync)
                {
                    // 취소된 루프는 요청을 흡수하지 않는다 — 늦게 뜬 태스크가 이미 취소된 채 다음 세션의 상실 요청을 지우면, 아무도 그
                    // 요청을 처리하지 않아 죽은 세션이 영영 안 되살아났다(RetireLoop 가 남은 요청을 새 루프로 넘기는데, 지워 버리면 넘길 것이 없다).
                    if (ct.IsCancellationRequested) break;
                    _reconnectPending = false;   // 이번 라운드가 흡수한다
                }
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

    /// <summary>한 번의 재장착 시도가 어떻게 끝났는가. 루프는 <b>실제로 열어 보고 실패한 것(<see cref="Failed"/>)만</b> 시도로 센다 —
    /// 시도 상한(MaxAttempts)도, 백오프 사다리의 칸도 그 수로 정한다. 전에는 거짓(bool)이면 무엇이든 셌다: 닫아서 그만둔 마지막 시도가
    /// "포기 — 연결 안 된 채 남는다. Open() 으로 다시 시도하라" 경고를 남겼고(해제 뒤에는 따를 수도 없는 지시), 다른 호출(손수 부른 느린
    /// Open)이 게이트를 쥐어 한 번도 못 연 시도가 상한을 소진해 붙는 카메라를 두고 포기하거나 사다리를 올려 다음 실제 시도를 한참 밀었다
    /// (형제 저장소가 짚고 검토가 독해로 확인).</summary>
    private enum SwapResult
    {
        /// <summary>새 세션을 장착했거나, 이미 살아 있는 세션이 있다 — 할 일이 끝났다.</summary>
        Attached,
        /// <summary>열어 봤는데 실패했다 — 한 번의 시도로 센다.</summary>
        Failed,
        /// <summary>닫기·해제·취소가 이겼다 — 조용히 물러난다(세지 않는다).</summary>
        Abandoned,
        /// <summary>게이트를 쥔 다른 호출이 열어 보고 실패한 직후다 — 곧바로 장치를 다시 두드리지 않고 한 칸 쉬었다가 다시 본다(세지 않는다).</summary>
        Deferred,
    }

    /// <summary>미뤘다가 다시 볼 때의 최소 간격 — 사다리 칸이 0 이어도 맴돌지 않게.</summary>
    private const int MinDeferMs = 50;

    private void RunAttempts(CancellationToken ct)
    {
        var ladder = _opt.BackoffMs is { Count: > 0 } l ? l : new[] { 1000 };
        var failures = 0;
        var deferred = false;
        int seenGen;
        lock (_sync) seenGen = _attachGen;
        while (true)
        {
            if (ct.IsCancellationRequested) return;
            lock (_sync) { if (_closed || _disposed) return; }

            var delay = Math.Max(0, ladder[Math.Min(failures, ladder.Count - 1)]);
            if (deferred) delay = Math.Max(delay, MinDeferMs);
            if (ct.WaitHandle.WaitOne(delay)) return;   // 취소는 남은 지연을 다 기다리지 않고 즉시 깨어난다

            switch (TrySwapIn(ct))
            {
                case SwapResult.Attached:
                case SwapResult.Abandoned:
                    return;
                case SwapResult.Deferred:
                    deferred = true;                    // 사다리도 상한도 그대로 — 우리 시도가 아니었다
                    continue;
            }

            deferred = false;
            // 그 사이 누가(손수 부른 Open) 세션을 붙였다가 그것마저 잃었고 이 시도가 그것을 갈아 끼우다 실패했다면 새 사건이다 — 앞 사건의 실패
            // 수와 사다리를 물려받지 않는다. 전에는 물려받아, 한 번 시도한 새 끊김에 "포기" 경고가 나고 다음 시도가 높은 칸(최대 30 s)을 기다렸다
            // (검토가 찾음). ⚠ 새 사건이 들어온 순간 이미 자고 있던 앞 사건의 대기는 깨우지 않는다 — 그 한 번은 옛 칸을 다 기다린다.
            lock (_sync)
            {
                if (_attachGen != seenGen) { failures = 0; seenGen = _attachGen; }
            }
            failures++;
            if (_opt.MaxAttempts > 0 && failures >= _opt.MaxAttempts)
            {
                // 포기는 조용한 정지가 아니라 관측 가능한 종단 상태로 남긴다. 여기 오는 것은 실제로 열어 보고 실패한 시도뿐이다.
                CvLog.Publish(CvLogLevel.Warning, LogSource,
                    $"[{Name}] giving up after {failures} reconnect attempts — the camera stays disconnected. Call Open() to retry.");
                return;
            }
        }
    }

    /// <summary>한 번의 재장착 시도(<see cref="SwapResult"/>).</summary>
    private SwapResult TrySwapIn(CancellationToken ct)
    {
        // 게이트는 시한 없이, 취소로만 깨어나게 기다린다. 전에는 ShutdownWaitMs 까지만 기다리고 못 잡으면 실패로 셌다 — 종료 대기용 값이
        // 재연결 정책을 좌우했고, 닫기·해제가 취소해도 이 대기는 안 깨어나 해제가 그만큼 더 기다렸다. 게이트를 오래 쥐는 것은 손수 부른
        // Open 뿐이다(닫기·해제는 게이트를 잡기 전에 이 루프를 취소한다) — 그 Open 이 끝나기를 기다리는 것이 맞다: 붙었으면 아래에서 할 일
        // 없이 물러나고, 실패했으면 한 칸 쉬었다가 다시 본다.
        // ⚠ 취소 예외는 "못 잡았다" 다 — 반납하지 않는다. 취소와 반납이 겹치면 잡고 정상 반환할 수 있다(.NET 8.0.31 SemaphoreSlim 디컴파일
        //    확인) — 그 경우는 아래 락 안의 취소 확인이 물러나고 finally 가 반납한다.
        var contended = !_gate.Wait(0);
        if (contended)
        {
            try { _gate.Wait(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { return SwapResult.Abandoned; }
        }

        Retired retired = default;
        ICam? attached = null;
        SwapResult result;
        try
        {
            lock (_sync)
            {
                if (_closed || _disposed || ct.IsCancellationRequested) return SwapResult.Abandoned;
                // 이미 살아 있는 세션을 뜯지 않는다. 재장착이 실패해 안쪽이 비어 있는 백오프 사이 사용자·감시 코드가 Open 으로 손수 붙였거나
                // (ICam.Open 이 상실 뒤 다시 열라고 권한다), 실패한 두 번째 Open 이 남긴 요청이 한 라운드를 더 돌면, 이 시도가 멀쩡한 세션을
                // 청산하고 다시 열었다 — 그 사이 그랩은 "재연결 중" 으로 실패하고 라이브가 끊겼다(검토가 재현). 죽음이 접수된 안쪽
                // (_lostInner)만 교체 대상이다. 할 일이 없으니 성공으로 물러난다.
                if (_inner is { } current && !ReferenceEquals(_lostInner, current)) return SwapResult.Attached;
                // 기다린 끝에 안쪽이 비어 있다 — 게이트를 쥐었던 Open 이 방금 열어 보고 실패했다. 곧바로 다시 두드리면 같은 이유로 또 실패해
                // 우리 시도로 세어진다.
                if (contended && _inner is null) return SwapResult.Deferred;
            }
            retired = RetireInner();
            var proceed = true;
            lock (_sync) { if (_closed || _disposed || ct.IsCancellationRequested) proceed = false; }
            // 여는 사이 닫기가 이기면 null — 세션은 이미 닫고 버렸고 그 사실은 Info 로 남았다. 실패가 아니라 청산이다.
            if (proceed) attached = AttachAndOpen();
            result = attached is null ? SwapResult.Abandoned : SwapResult.Attached;
        }
        catch (Exception ex)
        {
            // 닫힌 뒤에 끝난 열기의 실패(끊긴 장치가 뒤늦게 시한을 넘김)는 닫힌 카메라의 결함이 아니다 — 경고로 남기면 엉뚱한 데를 찾는다.
            bool closedMeanwhile;
            lock (_sync) closedMeanwhile = _closed || _disposed || ct.IsCancellationRequested;
            CvLog.Publish(closedMeanwhile ? CvLogLevel.Info : CvLogLevel.Warning, LogSource,
                closedMeanwhile ? $"[{Name}] reconnect attempt abandoned: the camera was closed meanwhile."
                                : $"[{Name}] reconnect attempt failed.", ex);
            result = closedMeanwhile ? SwapResult.Abandoned : SwapResult.Failed;
        }
        finally
        {
            _gate.Release();
        }

        // 청산이 내린 표시는 교체가 성공하든 말든 알린다 — 대개는 상실 통지가 이미 내려 두어 알릴 것이 없다.
        RaiseRetired(retired);
        if (attached is null) return result;
        // 장착은 끝났다. 게이트를 놓은 틈에 닫기가 이겼으면 알리지 않고 루프는 닫힘을 보고 물러난다. 상실이 이겼으면 그 상실이
        // 이미 다음 라운드를 걸어 두었다(_reconnectPending) — 여기서 되살리려 들지 않는다.
        AnnounceAttached(attached);
        return SwapResult.Attached;
    }

    /// <summary>끊기기 전 의도가 살아 있으면 연속취득을 재개한다 — 방금 장착한 그 인스턴스에서만.
    /// 재개 도중 사용자가 StopContinuous 를 부를 수 있으므로, 기동 뒤 의도를 다시 확인해
    /// <b>나중에 온 명령이 이기게</b> 한다(정지를 눌렀는데 계속 도는 상황 방지). 재개하는 사이 그 세션이 죽었으면 켜짐을 알리지 않는다
    /// (StartContinuous 와 같은 이유 — 의도는 남아 다음 세션에서 되살린다).</summary>
    private void ResumeContinuous(ICam cam)
    {
        int version;
        lock (_sync)
        {
            if (!_wantContinuous || !ReferenceEquals(_inner, cam) || _closed || _disposed) return;
            version = _intentVersion;
        }

        try { cam.StartContinuous(); }
        catch (Exception ex)
        {
            // 그사이 닫기·해제·교체·상실이 이겼으면 재개의 실패가 아니다 — 경고로 남기면 로그를 읽는 사람이 엉뚱한 데를 찾는다.
            bool superseded;
            lock (_sync) superseded = _closed || _disposed || !ReferenceEquals(_inner, cam) || ReferenceEquals(_lostInner, cam);
            CvLog.Publish(superseded ? CvLogLevel.Info : CvLogLevel.Warning, LogSource,
                superseded ? $"[{Name}] resuming continuous grab was abandoned: the session was closed or lost meanwhile."
                           : $"[{Name}] failed to resume continuous grab on the newly attached session.", ex);
            return;
        }

        var innerGrabbing = ReadGrabbing(cam);   // 안쪽이 실제로 켜졌을 때만 알린다(StartContinuous 와 같은 이유)
        bool undo = false, announce = false;
        int decidedAt;
        lock (_sync)
        {
            decidedAt = _intentVersion;
            var sameInner = ReferenceEquals(_inner, cam);
            if ((_intentVersion != version && !_wantContinuous) || !sameInner || _closed || _disposed) undo = sameInner;
            else if (innerGrabbing && _connected && !ReferenceEquals(_lostInner, cam) && !_grabbing) { _grabbing = true; announce = true; }
        }
        if (undo) StopInnerOwned(cam, decidedAt);   // 재개 도중 들어온 정지 명령이 이긴다
        if (announce) SafeRaise(() => GrabbingChanged?.Invoke(this, true), nameof(GrabbingChanged));
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
            // 칸이 이 루프의 것일 때만 비우고 승계하고 CTS 를 치운다 — 취소(MarkClosedLocked)도 이 락 안이라 폐기와 겹치지 않는다.
            // 칸이 이 루프의 것이 아니면 해제가 이미 칸을 비웠다 — 그 CTS 는 해제가 이 루프가 끝난 것을 보고 치운다(Dispose).
            if (!ReferenceEquals(_reconnectCts, cts)) return;
            _reconnectTask = null;
            _reconnectCts = null;
            var pending = _reconnectPending;
            _reconnectPending = false;
            if (pending && !_closed && !_disposed) ScheduleReconnectLocked();
            cts.Dispose();
        }
    }

    // === 잡동사니 ===

    private bool IsCurrent(object? sender)
    {
        lock (_sync) return ReferenceEquals(_inner, sender);
    }

    /// <summary>안쪽 취득 상태를 읽는다 — 던지면 "안 켜짐" 으로 읽는다. 상태 게터는 폐기 뒤에도 던지지 않아야 하지만(폴링하는 값이다),
    /// 폐기되면 무엇이든 던지는 구현이 있으면 닫기와 겹친 순간의 이 읽기가 재연결 루프 밖으로 새어 "reconnect loop failed" 오류가 되거나
    /// 닫기가 이긴 시작의 부른 쪽에 예외로 올라갔다(검토가 독해로 찾음 — 이 저장소의 구현은 전부 필드 읽기라 안 던진다). 안 켜짐으로 읽으면
    /// 켜짐을 알리지 않을 뿐이고, 살아 있는 세션이면 늦은 켜짐 통지나 다음 명령이 맞춘다.</summary>
    private static bool ReadGrabbing(ICam cam)
    {
        try { return cam.IsGrabbing; }
        catch { return false; }
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

    // 연결을 세우는 알림은 RaiseConnectedIfCurrent 하나뿐이다(현재 인스턴스인지 다시 보고 세운다). 내리는 알림은 상실(OnInnerLost)과
    // 청산(RaiseRetired)이 각자 판정과 함께 낸다. 확인 없이 세우던 옛 RaiseConnection 은 닫은 뒤에 연결됨을 세운 경로라 걷었다.

    /// <summary>취득 표시를 내린다 — 상태가 실제로 바뀐 경계에서만 발화한다. 켜는 쪽은 판정과 함께 락 안에서 세운다(StartContinuous·
    /// ResumeContinuous) — 여기로 켜면 죽은 세션이나 정지에 진 시작을 켜짐으로 알릴 수 있다.</summary>
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
