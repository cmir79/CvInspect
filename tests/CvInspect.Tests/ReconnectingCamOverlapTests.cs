using CvInspect.Imaging;
using Xunit;

namespace CvInspect.Tests;

/// <summary>
/// ReconnectingCam 의 겹친 시작·정지 — 끝에 <b>세 값</b>이 서로 맞는지 본다: 표시(IsGrabbing·마지막 GrabbingChanged), 실제(안쪽 IsGrabbing),
/// 의도(다음 재연결이 되살리는 것 — 연결을 끊어 드러낸다). 이 영역은 한 순서를 고치면 반대 순서가 깨진 이력이 있어 고친 순서와 그 반대 순서를
/// 함께 둔다.
///
/// 순서는 시각이 아니라 전용 가짜 카메라(<see cref="GateCam"/>)의 문(신호)과 고리로 강제한다 — 같은 기계에서 다른 빌드·시험이 돌아 바쁠 때 잠깐
/// 자기로 세운 순서는 뒤집힌다. 각 항은 강제한 순서가 실제로 걸렸다는 표식(문에 붙잡혔다, 물렀다, 고리가 돌았다)을 먼저 단언한다 — 그것 없이
/// 결과만 보면 "맞게 고쳐졌다" 와 "순서가 안 걸려 아무것도 시험하지 않았다" 가 갈리지 않는다.
/// </summary>
public class ReconnectingCamOverlapTests
{
    /// <summary>단언 하나 — 실패 메시지가 곧 그 회귀가 존재하는 이유다.</summary>
    private static void Check(bool cond, string name) => Assert.True(cond, name);

    /// <summary>조건이 설 때까지 기다린다 — 순서를 세우는 데 쓰지 않고, 이미 강제된 순서의 결과가 다른 스레드에서 닿기를 기다릴 때만 쓴다.</summary>
    private static bool WaitFor(Func<bool> cond, int ms = 5000)
    {
        var t0 = Environment.TickCount;
        while (!cond() && Environment.TickCount - t0 < ms) Thread.Sleep(5);
        return cond();
    }

    private static readonly CamReconnectOpt Fast = new() { BackoffMs = new[] { 20 }, ShutdownWaitMs = 1000 };

    /// <summary>한 출처의 로그 줄을 "{등급}|{문구}" 로 받는다. 빈 창구를 먼저 한 번 꽂아 보관된 앞 줄을 흘려보내고, 끝나면 원래 창구를 돌려놓는다.</summary>
    private sealed class LogCapture : IDisposable
    {
        private readonly Action<CvLogLevel, string, string, Exception?>? _prev = CvLog.Sink;
        public readonly SyncList<string> Lines = new();
        public LogCapture(string source)
        {
            CvLog.Sink = static (_, _, _, _) => { };
            CvLog.Sink = (lvl, src, msg, _) => { if (src == source) Lines.Add($"{lvl}|{msg}"); };
        }
        public bool Any(string text) => Lines.Snapshot().Any(l => l.Contains(text));
        public override string ToString() => string.Join(" / ", Lines.Snapshot());
        public void Dispose() => CvLog.Sink = _prev;
    }

    /// <summary>
    /// 이 묶음 전용 ICam — 시작·정지의 안쪽 순서를 문으로 붙잡는다. 상태는 락 안에서 정하고 통지는 락 밖에서 낸다(GevCam 의 모양).
    /// 시작은 부를 때 정지 수를 떠 두고, 시작하기 전에 그 수가 바뀌었으면 <b>시작하지 않고 예외 없이</b> 돌아온다(ICam.StartContinuous: 부른 뒤
    /// 온 정지가 이긴다). 이미 돌고 있으면 그 판정보다 먼저 할 일 없이 돌아온다(GevCam 과 같은 순서). 문과 고리는 전부 한 번 쓰면 비워진다.
    /// </summary>
    private sealed class GateCam : ICam
    {
        private readonly object _sync = new();
        private bool _on;
        private bool _connected;
        private bool _disposed;
        private int _stops;

        public string Name => "GateCam";
        public string ComType => "Gate";
        public bool IsConnected { get { lock (_sync) return _connected; } }

        /// <summary>안쪽 상태 — 값을 먼저 뜨고 나서 이 스레드에 걸어 둔 고리를 부른다. 그래서 고리 안에서 바뀐 상태는 이 읽기에 안 보인다
        /// (부른 쪽의 "락 밖에서 읽고 락 안에서 정한다" 사이의 틈을 강제한다). 고리는 정해 둔 스레드에서만 돈다 — 시험 스레드의 읽기는 건드리지 않는다.</summary>
        public bool IsGrabbing
        {
            get
            {
                bool v;
                lock (_sync) v = _on;
                if (Volatile.Read(ref _readHookTid) == Environment.CurrentManagedThreadId && Interlocked.Exchange(ref _readHook, null) is { } hook)
                {
                    Volatile.Write(ref _readHookTid, 0);
                    hook();
                }
                return v;
            }
        }

        public event EventHandler<CamFrame>? FrameAcquired;
        public event EventHandler<ConnArgs>? ConnectionChanged;
        public event EventHandler<bool>? GrabbingChanged;

        private Action? _readHook;
        private int _readHookTid;

        /// <summary>지금 이 스레드의 다음 <see cref="IsGrabbing"/> 읽기에 고리를 건다.</summary>
        private void ArmReadHookHere(Action? hook)
        {
            if (hook is null) return;
            Volatile.Write(ref _readHookTid, Environment.CurrentManagedThreadId);
            Volatile.Write(ref _readHook, hook);
        }

        private ManualResetEventSlim? _startGate;
        /// <summary>다음 시작이 정지 수를 뜬 <b>뒤</b> 이 신호를 기다린다 — 그 사이 온 정지가 그 시작을 무르게 한다. ⚠ finally 에서 세운다.</summary>
        public ManualResetEventSlim? StartGate { set => Volatile.Write(ref _startGate, value); }
        /// <summary>시작이 <see cref="StartGate"/> 에 붙잡혀 있는가.</summary>
        public volatile bool IsStarting;

        private ManualResetEventSlim? _stopNotifyGate;
        /// <summary>다음 정지가 안쪽을 끈 <b>뒤</b>·꺼짐 통지를 내기 전에 이 신호를 기다린다 — 정지를 마무리하는 동안 통지가 늦게 나가는 모양. ⚠ finally 에서 세운다.</summary>
        public ManualResetEventSlim? StopNotifyGate { set => Volatile.Write(ref _stopNotifyGate, value); }
        /// <summary>정지가 꺼짐 통지를 붙잡고 있는가(안쪽은 이미 꺼짐).</summary>
        public volatile bool IsStopping;

        private Action? _onStopEntry;
        /// <summary>다음 정지에 들어오자마자(정지를 세기 전에) 부를 것 — "정지를 불렀지만 안쪽은 아직 세지 않음" 에 끼어든다.</summary>
        public Action? OnStopEntry { set => Volatile.Write(ref _onStopEntry, value); }

        /// <summary>다음 시작 하나를 거절한다(던진다) — GevCam: 그 틈에 단발 그랩이 기다린다.</summary>
        public volatile bool RefuseNextStart;

        private Action? _readHookOnConcede;
        /// <summary>시작을 무른 스레드의 다음 <see cref="IsGrabbing"/> 읽기에 걸 고리 — 부른 쪽이 "안 켜졌다" 를 읽은 뒤·정하기 전에 끼어든다.</summary>
        public Action? ReadHookOnConcede { set => Volatile.Write(ref _readHookOnConcede, value); }

        private Action? _readHookAfterStop;
        /// <summary>다음 정지가 끝나는 스레드의 다음 <see cref="IsGrabbing"/> 읽기에 걸 고리 — 정지 꼬리가 안쪽을 읽은 뒤·정하기 전에 끼어든다.</summary>
        public Action? ReadHookAfterStop { set => Volatile.Write(ref _readHookAfterStop, value); }

        /// <summary>다음 시작 하나는 켜지 않은 채 돌아오고, 다른 스레드에서 켠 뒤 켜짐을 알린다 — 시작을 콜백으로 늦게 알리는 구현의 모양.</summary>
        public volatile bool LateNextStart;
        /// <summary>늦은 시작이 켜기 전에 기다릴 신호.</summary>
        public ManualResetEventSlim? LateLightGate { get; set; }
        /// <summary>늦은 시작이 켠 <b>뒤</b>·켜짐 통지를 내기 전에 기다릴 신호 — 그 사이 다른 스레드의 정지가 안쪽을 끌 수 있다.</summary>
        public ManualResetEventSlim? LateNoticeGate { get; set; }
        /// <summary>늦은 시작이 켜 놓고 켜짐 통지를 붙잡고 있는가.</summary>
        public volatile bool IsHoldingLateNotice;
        private Action? _readHookOnLateNotice;
        /// <summary>늦은 켜짐 통지를 내는 스레드의 다음 <see cref="IsGrabbing"/> 읽기에 걸 고리 — 통지를 받은 쪽이 안쪽을 읽은 뒤·정하기 전에 끼어든다.</summary>
        public Action? ReadHookOnLateNotice { set => Volatile.Write(ref _readHookOnLateNotice, value); }

        private int _startsLit, _startsConceded, _startsAlreadyOn, _startsRefused, _lateNoticesRaised;
        /// <summary>시작이 실제로 켠 수(늦은 시작 제외).</summary>
        public int StartsLit => Volatile.Read(ref _startsLit);
        /// <summary>부른 뒤 온 정지에 져서 시작하지 않고 돌아온 수.</summary>
        public int StartsConceded => Volatile.Read(ref _startsConceded);
        /// <summary>이미 돌고 있어 할 일 없이 돌아온 수.</summary>
        public int StartsAlreadyOn => Volatile.Read(ref _startsAlreadyOn);
        /// <summary>거절(던짐)한 수.</summary>
        public int StartsRefused => Volatile.Read(ref _startsRefused);
        /// <summary>늦은 켜짐 통지를 실제로 낸 수.</summary>
        public int LateNoticesRaised => Volatile.Read(ref _lateNoticesRaised);

        public void Open()
        {
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(GateCam));
                if (_connected) return;
                _connected = true;
            }
            ConnectionChanged?.Invoke(this, new ConnArgs(true));
        }

        public void Close()
        {
            bool wasOn, wasConn;
            lock (_sync) { wasOn = _on; _on = false; wasConn = _connected; _connected = false; }
            if (wasOn) GrabbingChanged?.Invoke(this, false);
            if (wasConn) ConnectionChanged?.Invoke(this, new ConnArgs(false));
        }

        public void Dispose() { lock (_sync) { _disposed = true; _on = false; _connected = false; } }

        public void GrabOne()
        {
            lock (_sync)
            {
                if (_on) throw new InvalidOperationException("fake: continuous acquisition is running");
                if (!_connected) throw new InvalidOperationException("fake: not open");
            }
            FrameAcquired?.Invoke(this, new CamFrame(new byte[4], 2, 2, 2, CamPixelFormat.Mono8));
        }

        public void SetExposureTimeUs(double timeUs) { }

        public void StartContinuous()
        {
            var stopsAtCall = Volatile.Read(ref _stops);
            if (Interlocked.Exchange(ref _startGate, null) is { } gate)
            {
                IsStarting = true;
                try { gate.Wait(); }
                finally { IsStarting = false; }
            }
            bool conceded = false, late = false;
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(GateCam));
                if (!_connected) throw new InvalidOperationException("fake: not open");
                if (_on) { _startsAlreadyOn++; return; }
                if (_stops != stopsAtCall) { _startsConceded++; conceded = true; }
                else if (RefuseNextStart) { RefuseNextStart = false; _startsRefused++; throw new InvalidOperationException("fake: a single grab is waiting"); }
                else if (LateNextStart) { LateNextStart = false; late = true; }
                else { _on = true; _startsLit++; }
            }
            if (conceded) { ArmReadHookHere(Interlocked.Exchange(ref _readHookOnConcede, null)); return; }
            if (late) { LightLater(); return; }
            GrabbingChanged?.Invoke(this, true);
        }

        private void LightLater()
        {
            var lightGate = LateLightGate;
            var noticeGate = LateNoticeGate;
            var hook = Interlocked.Exchange(ref _readHookOnLateNotice, null);
            new Thread(() =>
            {
                lightGate?.Wait();
                lock (_sync)
                {
                    if (_disposed || !_connected) return;
                    _on = true;
                }
                if (noticeGate is not null)
                {
                    IsHoldingLateNotice = true;
                    try { noticeGate.Wait(); }
                    finally { IsHoldingLateNotice = false; }
                }
                ArmReadHookHere(hook);
                GrabbingChanged?.Invoke(this, true);   // 켜기로 정한 그 시작의 통지 — 그사이 꺼졌어도 낸다(상태를 정하고 락 밖에서 알리는 구현의 모양)
                Interlocked.Increment(ref _lateNoticesRaised);
            }) { IsBackground = true, Name = "GateCam.late" }.Start();
        }

        public void StopContinuous()
        {
            try
            {
                if (Interlocked.Exchange(ref _onStopEntry, null) is { } entry) entry();
                Interlocked.Increment(ref _stops);   // GevCam 과 같이 락 전에 센다 — 락을 기다리는 시작이 부를 때 뜬 값과 견준다
                bool stopped;
                lock (_sync)
                {
                    if (_disposed) return;
                    stopped = _on;
                    _on = false;
                }
                if (!stopped) return;
                if (Interlocked.Exchange(ref _stopNotifyGate, null) is { } gate)
                {
                    IsStopping = true;
                    try { gate.Wait(); }
                    finally { IsStopping = false; }
                }
                GrabbingChanged?.Invoke(this, false);
            }
            finally
            {
                ArmReadHookHere(Interlocked.Exchange(ref _readHookAfterStop, null));
            }
        }

        /// <summary>장치 쪽 연결 상실 — 상태를 먼저 내리고 취득 꺼짐 → 연결 끊김 순으로 알린다(ICam 의 구현자 안내).</summary>
        public void LoseConnection()
        {
            bool wasOn;
            lock (_sync) { wasOn = _on; _on = false; _connected = false; }
            if (wasOn) GrabbingChanged?.Invoke(this, false);
            ConnectionChanged?.Invoke(this, new ConnArgs(false));
        }

        /// <summary>연결은 살아 있는데 취득만 죽는다 — 수신 스트림이 접히는 길.</summary>
        public void StopGrabbingOnItsOwn()
        {
            bool wasOn;
            lock (_sync) { wasOn = _on; _on = false; }
            if (wasOn) GrabbingChanged?.Invoke(this, false);
        }
    }

    /// <summary>감싼 카메라 하나와 그 통지·로그 기록.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly SyncList<GateCam> Made = new();
        public readonly SyncList<bool> Grab = new();
        public readonly SyncList<bool> Conn = new();
        public readonly LogCapture Log = new("ReconnectingCam");
        public readonly ReconnectingCam Cam;

        public Rig()
        {
            Cam = new ReconnectingCam(() => Made.AddNew(_ => new GateCam()), Fast);
            Cam.GrabbingChanged += (_, g) => Grab.Add(g);
            Cam.ConnectionChanged += (_, e) => Conn.Add(e.IsConnected);
        }

        public GateCam Inner => Made[0];

        /// <summary>열고 라이브를 켠다 — 첫 상태가 세 값 모두 켜짐인지 확인한다.</summary>
        public GateCam OpenLive(string tag)
        {
            Cam.Open();
            Cam.StartContinuous();
            Check(Cam.IsGrabbing && Inner.IsGrabbing, $"{tag}: premise — live runs and is reported");
            return Inner;
        }

        public void Dispose()
        {
            Cam.Dispose();
            Log.Dispose();
        }
    }

    /// <summary>통지는 전이만 — 같은 값이 두 번 이어 나지 않고, 마지막 통지는 지금 표시와 같은 말을 한다(통지를 거울질하는 화면이 굳지 않는다).</summary>
    private static void CheckNotices(Rig r, string tag)
    {
        var g = r.Grab.Snapshot();
        var now = r.Cam.IsGrabbing;
        Check(g.Zip(g.Skip(1), (x, y) => x != y).All(t => t), $"{tag}: GrabbingChanged fires on transitions only: [{r.Grab}]");
        Check(g.Length > 0 ? g[^1] == now : !now, $"{tag}: the last GrabbingChanged says what IsGrabbing says ({now}): [{r.Grab}]");
    }

    /// <summary>끝 상태 세 값 — 표시·실제가 마지막에 불린 명령과 같은 말을 하고, 멀쩡한 세션은 다시 지어지지 않았으며(교체는 연결 상실만),
    /// 통지는 전이만 났다. 의도는 고장을 일으켜 드러낸다 — 고장 뒤 라이브가 되살아나면 의도가 켜짐이다. 그래서 이 단언은 "진짜 고장은 여전히
    /// 되살아난다" 도 함께 본다. <paramref name="streamCollapse"/> 면 연결 상실 대신 취득만 죽인다(세션 교체로 되살아나야 한다).</summary>
    private static void CheckEndTriple(Rig r, bool on, string tag, bool streamCollapse = false)
    {
        var inner = r.Inner;
        Check(WaitFor(() => r.Cam.IsReconnectIdle), $"{tag}: no reconnect round is left running");
        Check(r.Made.Count == 1 && r.Conn.Snapshot().SequenceEqual(new[] { true }) && !r.Log.Any("without being asked"),
            $"{tag}: the healthy session is not rebuilt — only a lost connection replaces it (instances={r.Made.Count}, connection edges [{r.Conn}]) [{r.Log}]");
        Check(r.Cam.IsGrabbing == on && inner.IsGrabbing == on,
            $"{tag}: the command called last wins — expected {(on ? "live" : "off")} (wrapper={r.Cam.IsGrabbing}, inner={inner.IsGrabbing})");
        CheckNotices(r, tag);

        if (streamCollapse && on) inner.StopGrabbingOnItsOwn();
        else inner.LoseConnection();
        Check(WaitFor(() => r.Made.Count == 2 && r.Cam.IsConnected && r.Cam.IsReconnectIdle),
            $"{tag}: a real fault ({(streamCollapse && on ? "the stream collapsed" : "the connection was lost")}) is still recovered (instances={r.Made.Count})");
        Check(r.Cam.IsGrabbing == on && r.Made[1].IsGrabbing == on,
            $"{tag}: the hidden intent agrees — the recovery {(on ? "resumes" : "does not start")} live (wrapper={r.Cam.IsGrabbing}, new inner={r.Made[1].IsGrabbing})");
        CheckNotices(r, $"{tag} after recovery");
    }

    /// <summary>
    /// (e) LA — 다른 스레드의 정지 Z 가 안쪽을 멈추는 중(안쪽은 꺼졌고 꺼짐 통지는 아직)에 정지 X 가 오고, X 가 안쪽에 닿기 직전 시작(⏯)이
    /// 안쪽 시작 안으로 들어간다. 안쪽은 X 의 정지가 그 시작 뒤에 셌으므로 시작을 조용히 무른다(ICam.StartContinuous). 감싼 쪽의 순서로는 ⏯ 이
    /// 마지막 명령이라 의도는 켜짐이다. 그런데 X 의 꼬리는 "의도가 켜짐 — 나중 시작이 알린다" 로 표시를 안 내리고 되켜기는 아직 도는 Z 에 맡겼으며,
    /// ⏯ 의 꼬리는 "안쪽이 안 켜졌다" 로 아무것도 안 했다 — X 와 ⏯ 가 돌아온 뒤에도 Z 의 꺼짐 통지가 닿을 때까지 꺼진 안쪽 위에 IsGrabbing=true 가
    /// 남았다(이전 실측: 강제 20/20, Z 없으면 0/20). 그 틈에 X 다음의 단발 그랩을 받은 쪽은 라이브가 도는 줄 안다.
    /// </summary>
    [Fact]
    public void LaStartStoodDownBetweenTwoStopsIsNotReportedLive()
    {
        using var r = new Rig();
        var inner = r.OpenLive("LA");
        var zGate = new ManualResetEventSlim();
        var aGate = new ManualResetEventSlim();
        Thread? a = null;
        var aParked = false;
        var z = new Thread(r.Cam.StopContinuous) { IsBackground = true };
        try
        {
            inner.StopNotifyGate = zGate;
            z.Start();
            Check(WaitFor(() => inner.IsStopping), "LA: premise — Z has turned the inner off and holds its off-notice");
            inner.StartGate = aGate;
            inner.OnStopEntry = () =>
            {
                a = new Thread(r.Cam.StartContinuous) { IsBackground = true };
                a.Start();
                aParked = WaitFor(() => inner.IsStarting);
            };
            r.Cam.StopContinuous();                                                    // X
            Check(aParked, "LA: premise — ⏯ waits inside the inner start, entered before X's stop reached the inner");
            Check(!inner.IsGrabbing && inner.IsStopping, "LA: premise — the inner is off and Z still holds its off-notice when X returns");
            Check(r.Cam.IsGrabbing == inner.IsGrabbing,
                $"LA: X returned with IsGrabbing={r.Cam.IsGrabbing} over a stopped inner — another stop still finishing and a later start pending must not keep live reported (IsGrabbing is what runs now)");
            CheckNotices(r, "LA at X's return");
            aGate.Set();
            Check(a!.Join(5000), "LA: ⏯ returns");
            Check(inner.StartsConceded == 1 && inner.IsStopping,
                $"LA: premise — the inner stood ⏯'s start down (a stop counted after it was called) while Z still finishes (conceded={inner.StartsConceded})");
            Check(r.Cam.IsGrabbing == inner.IsGrabbing,
                $"LA: ⏯ returned with IsGrabbing={r.Cam.IsGrabbing} though the inner stood its start down — nothing runs until Z's tail relights");
            CheckNotices(r, "LA at ⏯'s return");
        }
        finally { zGate.Set(); aGate.Set(); }
        Check(z.Join(5000), "LA: Z returns");
        CheckEndTriple(r, on: true, "LA", streamCollapse: true);
    }

    /// <summary>
    /// (e) LA2 — 같은 결함의 두 명령판: 정지 Z 를 부른 뒤(감싼 쪽 순서로는 Z 가 먼저) Z 가 안쪽에 닿기 직전 시작(⏯)이 안쪽 시작 안으로 들어간다.
    /// 안쪽 순서로는 Z 의 정지가 나중이라 ⏯ 의 시작은 물러진다(GevCam: 시작이 부를 때 뜬 정지 수와 락을 잡은 뒤의 정지 수가 다르다). Z 의 꺼짐 통지가
    /// 늦는 동안 ⏯ 는 꺼진 안쪽 위에 앞 라이브의 켜짐을 남기고 돌아왔다.
    /// </summary>
    [Fact]
    public void La2StartStoodDownByACrossingStopIsNotReportedLive()
    {
        using var r = new Rig();
        var inner = r.OpenLive("LA2");
        var zGate = new ManualResetEventSlim();
        var aGate = new ManualResetEventSlim();
        Thread? a = null;
        var aParked = false;
        var z = new Thread(r.Cam.StopContinuous) { IsBackground = true };
        try
        {
            inner.StopNotifyGate = zGate;
            inner.StartGate = aGate;
            inner.OnStopEntry = () =>
            {
                a = new Thread(r.Cam.StartContinuous) { IsBackground = true };
                a.Start();
                aParked = WaitFor(() => inner.IsStarting);
            };
            z.Start();
            Check(WaitFor(() => inner.IsStopping), "LA2: premise — Z has turned the inner off and holds its off-notice");
            Check(aParked, "LA2: premise — ⏯ (called after Z) entered the inner start before Z's stop reached the inner");
            aGate.Set();
            Check(a!.Join(5000), "LA2: ⏯ returns");
            Check(inner.StartsConceded == 1 && inner.IsStopping,
                $"LA2: premise — the inner stood ⏯'s start down while Z still finishes (conceded={inner.StartsConceded})");
            Check(r.Cam.IsGrabbing == inner.IsGrabbing,
                $"LA2: ⏯ returned with IsGrabbing={r.Cam.IsGrabbing} though the inner stood its start down — the display must follow the inner, not the intent");
            CheckNotices(r, "LA2 at ⏯'s return");
        }
        finally { zGate.Set(); aGate.Set(); }
        Check(z.Join(5000), "LA2: Z returns");
        CheckEndTriple(r, on: true, "LA2");
    }

    /// <summary>
    /// (e) LANoZ — 대조: LA 와 같은 순서(⏯ 가 X 의 정지 입구에서 안쪽 시작 안으로 들어간다)인데 다른 정지 Z 가 없다. X 의 꼬리가 나중 시작을 위해
    /// 같은 세션에서 되켜고, 늦게 풀린 ⏯ 는 이미 도는 라이브를 보고 할 일 없이 돌아온다. 고치기 전후 모두 맞아야 한다 — LA 의 고침이 이 순서를
    /// 건드리면 안 된다.
    /// </summary>
    [Fact]
    public void LaNoZSameOrderWithoutAnotherStopStaysConsistent()
    {
        using var r = new Rig();
        var inner = r.OpenLive("LANoZ");
        var aGate = new ManualResetEventSlim();
        Thread? a = null;
        var aParked = false;
        try
        {
            inner.StartGate = aGate;
            inner.OnStopEntry = () =>
            {
                a = new Thread(r.Cam.StartContinuous) { IsBackground = true };
                a.Start();
                aParked = WaitFor(() => inner.IsStarting);
            };
            r.Cam.StopContinuous();                                                    // X
            Check(aParked, "LANoZ: premise — ⏯ waits inside the inner start, entered before X's stop reached the inner");
            Check(inner.StartsLit == 2, $"LANoZ: premise — X's tail relit live for the later start on the same session (lit={inner.StartsLit})");
            Check(r.Cam.IsGrabbing && inner.IsGrabbing,
                $"LANoZ: with no other stop in flight, X returns with live running and reported (wrapper={r.Cam.IsGrabbing}, inner={inner.IsGrabbing})");
            aGate.Set();
            Check(a!.Join(5000), "LANoZ: ⏯ returns");
            Check(inner.StartsAlreadyOn == 1, $"LANoZ: premise — ⏯ found live already running (alreadyOn={inner.StartsAlreadyOn})");
        }
        finally { aGate.Set(); }
        CheckEndTriple(r, on: true, "LANoZ");
    }

    /// <summary>
    /// (e) LB — 토글 정합 순서: LA 와 같은 자리(Z 가 꺼짐 통지를 붙잡고 있고 X 가 안쪽에 닿기 직전)에서 화면의 ⏯ 토글이 눌린다. 토글은 지금 표시를
    /// 읽고 반대 명령을 낸다 — 그 순간 표시는 아직 켜짐이다(Z 의 통지가 안 왔고, 꼬리도 아직 안 돌았다). 그래서 토글은 정지로 가고, 마지막 명령이
    /// 정지이므로 끝은 표시·실제·의도 모두 꺼짐이어야 한다. LA 의 고침(꼬리가 안쪽 사실로 표시를 내린다)이 이 순서의 결말을 바꾸지 않는다.
    /// </summary>
    [Fact]
    public void LbToggleInsideTheStopEndsOffEverywhere()
    {
        using var r = new Rig();
        var inner = r.OpenLive("LB");
        var zGate = new ManualResetEventSlim();
        var aGate = new ManualResetEventSlim();
        string? routed = null;
        var clickDone = false;
        var z = new Thread(r.Cam.StopContinuous) { IsBackground = true };
        try
        {
            inner.StopNotifyGate = zGate;
            z.Start();
            Check(WaitFor(() => inner.IsStopping), "LB: premise — Z has turned the inner off and holds its off-notice");
            inner.StartGate = aGate;                                                   // 토글이 시작으로 가면 LA 와 같이 안쪽 시작에서 붙잡힌다
            inner.OnStopEntry = () =>
            {
                var click = new Thread(() =>
                {
                    // 화면의 ⏯ 토글 — 지금 표시를 읽고 반대 명령을 낸다
                    if (r.Cam.IsGrabbing) { routed = "stop"; r.Cam.StopContinuous(); }
                    else { routed = "start"; r.Cam.StartContinuous(); }
                }) { IsBackground = true };
                click.Start();
                clickDone = click.Join(5000);
            };
            r.Cam.StopContinuous();                                                    // X
            Check(clickDone && routed == "stop",
                $"LB: premise — the toggle inside X's stop read the display as live (Z's off-notice not yet delivered) and routed to stop (routed={routed})");
            Check(!r.Cam.IsGrabbing && !inner.IsGrabbing && inner.IsStopping,
                $"LB: X returns with nothing running and nothing reported live while Z still finishes (wrapper={r.Cam.IsGrabbing}, inner={inner.IsGrabbing})");
            CheckNotices(r, "LB at X's return");
        }
        finally { zGate.Set(); aGate.Set(); }
        Check(z.Join(5000), "LB: Z returns");
        CheckEndTriple(r, on: false, "LB");
    }

    /// <summary>
    /// (f) N4 — 켜짐 통지는 상태를 정한 뒤 락 밖에서 나가므로(GevCam·이 가짜), 감싼 쪽에 닿았을 때 안쪽이 이미 멈춰 있을 수 있다. 감싼 쪽이 그
    /// 통지만 보고 켜짐을 세우면 꺼진 안쪽 위에 IsGrabbing=true 가 서고, 그 안쪽을 멈춘 정지의 꺼짐 통지는 이미 지나가 아무도 내리지 않는다.
    /// 재현: 콜백으로 늦게 켜는 시작 A 가 켠 뒤 통지를 붙잡은 사이, 정지 X 가 오고 X 의 입구에서 시작 ⏯ 이 끝까지 간다(이미 돌고 있어 할 일 없음).
    /// X 가 안쪽을 끄고, X 의 꼬리가 ⏯ 를 위해 되켜려다 거절당한다(경고 — 라이브·표시 꺼짐, 의도 켜짐). 그 뒤 A 의 통지가 닿는다.
    /// </summary>
    [Fact]
    public void N4LateOnNoticeOfAStoppedInnerIsNotMirrored()
    {
        using var r = new Rig();
        r.Cam.Open();
        var inner = r.Inner;
        var lightGate = new ManualResetEventSlim();
        var noticeGate = new ManualResetEventSlim();
        try
        {
            inner.LateNextStart = true;
            inner.LateLightGate = lightGate;
            inner.LateNoticeGate = noticeGate;
            r.Cam.StartContinuous();                                                   // A — 안쪽은 뒤에서 켠다
            Check(!r.Cam.IsGrabbing && !inner.IsGrabbing, "N4: premise — A's start is only pending");
            lightGate.Set();
            Check(WaitFor(() => inner.IsHoldingLateNotice) && inner.IsGrabbing, "N4: premise — the inner lit A's start and holds its on-notice");
            inner.OnStopEntry = () =>
            {
                var start = new Thread(r.Cam.StartContinuous) { IsBackground = true };  // ⏯ — 이미 돌고 있어 할 일 없이 끝나고 켜짐을 알린다
                start.Start();
                start.Join(5000);
                inner.RefuseNextStart = true;                                          // X 꼬리의 되켜기는 거절된다(GevCam: 그 틈에 단발 그랩이 기다린다)
            };
            r.Cam.StopContinuous();                                                    // X
            Check(inner.StartsAlreadyOn == 1 && inner.StartsRefused == 1 && r.Log.Any("could not be restarted"),
                $"N4: premise — ⏯ found live already running, X stopped it, and the relight for ⏯ was refused (alreadyOn={inner.StartsAlreadyOn}, refused={inner.StartsRefused}) [{r.Log}]");
            Check(!r.Cam.IsGrabbing && !inner.IsGrabbing && inner.IsHoldingLateNotice,
                "N4: premise — live is off and reported off while A's on-notice is still held");
            noticeGate.Set();
            Check(WaitFor(() => inner.LateNoticesRaised == 1), "N4: premise — the held on-notice was delivered");
            Check(r.Cam.IsGrabbing == inner.IsGrabbing,
                $"N4: an on-notice that reached the wrapper after the inner had stopped set IsGrabbing={r.Cam.IsGrabbing} over a stopped inner — nothing comes later to lower it");
            CheckNotices(r, "N4");
        }
        finally { lightGate.Set(); noticeGate.Set(); }
        // 의도는 켜짐이다 — 마지막 명령은 ⏯ 이고, 거절된 것은 우리 되켜기다(경고가 "다시 시작하라" 고 남았다). 연결이 끊기면 재연결이 라이브를 되살린다.
        Check(WaitFor(() => r.Cam.IsReconnectIdle) && r.Made.Count == 1 && r.Conn.Snapshot().SequenceEqual(new[] { true }),
            $"N4: a refused relight does not rebuild the healthy session (instances={r.Made.Count}, connection edges [{r.Conn}])");
        inner.LoseConnection();
        Check(WaitFor(() => r.Made.Count == 2 && r.Cam.IsConnected && r.Cam.IsReconnectIdle) && r.Cam.IsGrabbing && r.Made[1].IsGrabbing,
            $"N4: ⏯ was the last command — a reconnect resumes live (wrapper={r.Cam.IsGrabbing})");
        CheckNotices(r, "N4 after reconnect");
    }

    /// <summary>
    /// 반대 순서 R1 — 시작 꼬리가 안쪽을 "꺼짐" 으로 읽은 뒤·정하기 전에, Z 가 꺼짐 통지를 내고 Z 의 꼬리가 같은 세션에서 되켜 켜짐까지 확인했다.
    /// 그 뒤 시작 꼬리가 앞서 읽은 값으로 표시를 내리면 도는 라이브가 꺼짐으로 굳는다(되켜기는 이미 끝나 아무도 다시 알리지 않는다). 내린 뒤 한 번 더
    /// 읽어 맞춰야 한다. (LA2 의 고침이 깨뜨릴 수 있는 자리 — 고치기 전에는 시작 꼬리가 내리지 않으므로 이 순서는 원래 맞다.)
    /// </summary>
    [Fact]
    public void R1StartTailLoweringIsRecheckedAfterARelight()
    {
        using var r = new Rig();
        var inner = r.OpenLive("R1");
        var zGate = new ManualResetEventSlim();
        var aGate = new ManualResetEventSlim();
        Thread? a = null;
        var aParked = false;
        var hooked = false;
        var zDone = false;
        var z = new Thread(r.Cam.StopContinuous) { IsBackground = true };
        try
        {
            inner.StopNotifyGate = zGate;
            inner.StartGate = aGate;
            inner.OnStopEntry = () =>
            {
                a = new Thread(r.Cam.StartContinuous) { IsBackground = true };
                a.Start();
                aParked = WaitFor(() => inner.IsStarting);
            };
            inner.ReadHookOnConcede = () => { hooked = true; zGate.Set(); zDone = z.Join(5000); };
            z.Start();
            Check(WaitFor(() => inner.IsStopping) && aParked, "R1: premise — Z holds its off-notice and ⏯ waits inside the inner start");
            aGate.Set();
            Check(a!.Join(5000), "R1: ⏯ returns");
            Check(hooked && zDone && inner.StartsConceded == 1 && inner.StartsLit == 2,
                $"R1: premise — after ⏯ read the inner as off and before it decided, Z delivered its notice and its tail relit live (conceded={inner.StartsConceded}, lit={inner.StartsLit})");
            Check(r.Cam.IsGrabbing && inner.IsGrabbing,
                $"R1: live runs and is reported after ⏯ returns — a lowering based on a read taken before the relight must be checked again (wrapper={r.Cam.IsGrabbing}, inner={inner.IsGrabbing}) [{r.Grab}]");
            CheckNotices(r, "R1");
        }
        finally { zGate.Set(); aGate.Set(); }
        Check(z.Join(5000), "R1: Z returns");
        CheckEndTriple(r, on: true, "R1");
    }

    /// <summary>
    /// 반대 순서 R2 — LA 의 자리(Z 가 꺼짐 통지를 붙잡고 있다)에서 정지 X 의 꼬리가 안쪽을 "꺼짐" 으로 읽은 뒤·정하기 전에, 다른 스레드의 시작이
    /// 안쪽을 켜고 끝까지 갔다(표시는 앞 라이브의 켜짐이 남아 있어 알릴 것이 없었다). 그 뒤 X 의 꼬리가 앞서 읽은 값으로 내리면, 되켜기를 Z 에
    /// 맡기는 동안 라이브가 도는데 꺼짐으로 표시된다. 내린 뒤 한 번 더 읽어 맞춰야 한다. (LA 의 고침이 깨뜨릴 수 있는 자리 — 고치기 전에는 X 의 꼬리가
    /// 락 전에 안쪽을 읽지 않으므로 이 고리가 걸릴 자리가 없다.)
    /// </summary>
    [Fact]
    public void R2StopTailLoweringIsRecheckedAfterALaterStart()
    {
        using var r = new Rig();
        var inner = r.OpenLive("R2");
        var zGate = new ManualResetEventSlim();
        var hooked = false;
        var started = false;
        var z = new Thread(r.Cam.StopContinuous) { IsBackground = true };
        try
        {
            inner.StopNotifyGate = zGate;
            z.Start();
            Check(WaitFor(() => inner.IsStopping), "R2: premise — Z has turned the inner off and holds its off-notice");
            inner.ReadHookAfterStop = () =>
            {
                hooked = true;
                var later = new Thread(r.Cam.StartContinuous) { IsBackground = true };
                later.Start();
                started = later.Join(5000);
            };
            // X 는 따로 돈다 — 고리는 정지가 끝난 스레드에 걸리므로 시험 스레드의 읽기에 걸리지 않게
            var x = new Thread(r.Cam.StopContinuous) { IsBackground = true };
            x.Start();
            Check(x.Join(5000), "R2: X returns");
            Check(hooked && started && inner.StartsLit == 2 && inner.IsStopping,
                $"R2: premise — after X's tail read the inner as off and before it decided, a later start lit live and returned; Z still finishes (lit={inner.StartsLit})");
            Check(r.Cam.IsGrabbing && inner.IsGrabbing,
                $"R2: X returns with live running and reported — a lowering based on a read taken before the later start must be checked again (wrapper={r.Cam.IsGrabbing}, inner={inner.IsGrabbing}) [{r.Grab}]");
            CheckNotices(r, "R2 at X's return");
        }
        finally { zGate.Set(); }
        Check(z.Join(5000), "R2: Z returns");
        CheckEndTriple(r, on: true, "R2");
    }

    /// <summary>
    /// 반대 순서 R3 — 늦은 켜짐 통지를 받은 쪽이 안쪽을 "켜짐" 으로 읽은 뒤·정하기 전에 정지가 끝까지 갔다. 정지가 나중 명령이다 — 앞서 읽은 켜짐으로
    /// 표시를 세우면 안 된다(정지가 내린 의도가 막는다). (N4 의 고침이 새로 만든 읽기 자리 — 고치기 전에는 통지 처리가 안쪽을 읽지 않아 고리가 걸릴
    /// 자리가 없다.)
    /// </summary>
    [Fact]
    public void R3LateOnNoticeLosesToAStopThatOvertakesIt()
    {
        using var r = new Rig();
        r.Cam.Open();
        var inner = r.Inner;
        var lightGate = new ManualResetEventSlim();
        var hooked = false;
        var stopped = false;
        try
        {
            inner.LateNextStart = true;
            inner.LateLightGate = lightGate;
            inner.ReadHookOnLateNotice = () =>
            {
                hooked = true;
                var stop = new Thread(r.Cam.StopContinuous) { IsBackground = true };
                stop.Start();
                stopped = stop.Join(5000);
            };
            r.Cam.StartContinuous();                                                   // A — 안쪽은 뒤에서 켠다
            Check(!r.Cam.IsGrabbing && !inner.IsGrabbing, "R3: premise — A's start is only pending");
            lightGate.Set();
            Check(WaitFor(() => inner.LateNoticesRaised == 1), "R3: premise — the late on-notice was delivered");
        }
        finally { lightGate.Set(); }
        Check(hooked && stopped,
            "R3: premise — after the notice handler read the inner as live and before it decided, a StopContinuous ran to the end");
        Check(!r.Cam.IsGrabbing && !inner.IsGrabbing,
            $"R3: the stop called last wins over a late on-notice read before it (wrapper={r.Cam.IsGrabbing}, inner={inner.IsGrabbing}) [{r.Grab}]");
        CheckEndTriple(r, on: false, "R3");
    }
}
