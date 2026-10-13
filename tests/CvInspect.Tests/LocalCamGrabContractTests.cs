using System.Reflection;
using CvInspect.Imaging;
using OpenCvSharp;
using Xunit;

namespace CvInspect.Tests;

/// <summary>
/// 로컬 소스(<see cref="VirtualCam"/>·<see cref="VideoCaptureCam"/>)의 단발 그랩 계약 — 그랩이 장을 기다리거나 내는 동안
/// 같은 카메라의 <b>두 번째 그랩과 연속 시작은 던진다</b>(ICam.GrabOne·StartContinuous). 두 구현은 진행 중인 그랩을
/// 추적하지 않아 시작이 그대로 서고 두 그랩이 함께 돌았다(가상 카메라 5/5 실측). 그러면 그랩을 부른 쪽(구독 → 그랩 →
/// 반환)이 받은 장이 제 답인지, 흐르기 시작한 라이브나 남의 그랩의 장인지 가릴 수 없다 — 예외도 경고도 없이.
///
/// 겹침은 시간이 아니라 게이트로 만든다: 그랩을 전용 스레드에서 부르고, 그 그랩이 정해 둔 자리에 붙잡힌 것을 확인한
/// 뒤에 두 번째 조작을 건다. 단언마다 <b>그때 그랩이 정말 붙잡혀 있었는지</b>를 함께 본다 — 겹침이 안 걸렸으면
/// "던지지 않았다" 가 결함인지 시험이 빗나간 것인지 갈리지 않는다.
///
/// 끝의 (5) 는 같은 로컬 소스의 연속 취득 쪽이다 — <see cref="VideoCaptureCam"/> 의 라이브 루프가 정지 요청 없이 끝나면
/// <see cref="ICam.IsGrabbing"/> 이 참으로 굳고 꺼짐 통지가 안 나가, 감싼 <see cref="ReconnectingCam"/> 도 세션을 다시 세우지 않았다.
///
/// (6) 은 위 계약이 기본 절차(<see cref="CamGrabExt.GrabFrameAsync"/>)와 만나는 자리다 — 그 절차가 장을 받자마자 돌아가면, 아직 안 돌아온
/// GrabOne 의 표식에 부른 쪽의 바로 다음 조작이 걸린다.
/// </summary>
public class LocalCamGrabContractTests
{
    private static void Check(bool cond, string name) => Assert.True(cond, name);

    private static bool WaitFor(Func<bool> cond, int ms = 3000)
    {
        var t0 = Environment.TickCount;
        while (!cond() && Environment.TickCount - t0 < ms) Thread.Sleep(5);
        return cond();
    }

    /// <summary>스레드가 대기에 들어가 서 있는가. 락을 다투는 쪽이 없는 자리에서만 "그 대기에 들어갔다" 의 표식으로 쓴다
    /// (락 대기도 같은 상태로 보인다).</summary>
    private static bool IsParked(Thread t) => (t.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0;

    /// <summary>5장짜리 MJPG 동영상을 ASCII 경로에 만든다(파일 소스는 오프라인으로 돈다). 못 만들면 null.
    /// Windows 런타임은 만든다 — 거기서 못 만들면 이 회귀의 동영상 절반이 조용히 빠지므로 실패로 알린다.</summary>
    private static string? ClipOrSkip()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "vcap-grab-contract.avi");
        using (var vw = new VideoWriter(path, FourCC.MJPG, 10, new OpenCvSharp.Size(160, 120), true))
        {
            if (vw.IsOpened())
            {
                using var f = new Mat(120, 160, MatType.CV_8UC3);
                for (var i = 0; i < 5; i++) { f.SetTo(new Scalar(i * 40, 0, 0)); vw.Write(f); }
                return path;
            }
        }
        Check(!OperatingSystem.IsWindows(),
            "the Windows OpenCV runtime could not write an MJPG clip, so the VideoCaptureCam half of the grab contract regression did not run");
        Console.WriteLine("SKIP VideoCaptureCam grab contract — VideoWriter(MJPG) unavailable in this environment");
        return null;
    }

    private static ICam MakeVideoCam(string clip)
        => new VideoCaptureCam(new CamOpt { ComType = "VideoCapture", VideoSource = clip, FrameRate = 30 });

    /// <summary>단발 그랩 하나를 정해 둔 자리에 붙잡아 둔다.
    /// <list type="bullet">
    /// <item><c>load</c> — 가상 카메라: 그랩이 이미지를 읽는 중(발행 전).</item>
    /// <item><c>publish</c> — 그랩이 낸 장을 구독자가 받는 중(발행 안). 두 구현 모두.</item>
    /// <item><c>drain</c> — 가상 카메라: 그랩이 정지된 라이브의 마지막 틱이 끝나기를 기다리는 중(시한 1 s).</item>
    /// </list></summary>
    private sealed class HeldGrab : IDisposable
    {
        public readonly ICam Cam;
        private readonly string _window;
        private readonly ManualResetEventSlim _gate = new();
        private readonly Thread _grabber;
        private Thread? _stopper;
        private int _dirCalls;
        private int _handlerEntered;
        private int _heldLeft;   // 게이트에 붙잡힌 호출(이미지 읽기·핸들러)이 돌아갔다 — 게이트가 풀렸거나 그 시한(5 s)이 지났다
        private int _frames;
        private volatile Exception? _grabError;
        private volatile bool _grabReturned;

        public HeldGrab(string source, string window, string? clip = null)
        {
            _window = window;
            if (source == "virtual")
            {
                Cam = new VirtualCam(new CamOpt { FrameRate = 1 })
                {
                    // load 는 그랩의 이미지 읽기를, drain 은 라이브 첫 틱의 이미지 읽기를 붙잡는다 — 둘 다 첫 호출이다
                    ImageDirProvider = () =>
                    {
                        if (window != "publish" && Interlocked.Increment(ref _dirCalls) == 1)
                        {
                            _gate.Wait(5000);
                            Volatile.Write(ref _heldLeft, 1);
                        }
                        return null;
                    },
                };
            }
            else Cam = MakeVideoCam(clip!);
            Cam.FrameAcquired += (_, _) =>
            {
                Interlocked.Increment(ref _frames);
                if (window == "publish" && Interlocked.Exchange(ref _handlerEntered, 1) == 0)
                {
                    _gate.Wait(5000);
                    Volatile.Write(ref _heldLeft, 1);
                }
            };
            _grabber = new Thread(() =>
            {
                try { Cam.GrabOne(); _grabReturned = true; }
                catch (Exception ex) { _grabError = ex; }
            }) { IsBackground = true, Name = "held-grab" };
            try
            {
                Cam.Open();
                if (window == "drain")
                {
                    Cam.StartContinuous();
                    Check(WaitFor(() => Volatile.Read(ref _dirCalls) == 1), "premise: a live tick is held while it loads its image");
                    _stopper = new Thread(Cam.StopContinuous) { IsBackground = true, Name = "stopper" };
                    _stopper.Start();
                    // 정지가 락을 놓고 그 틱을 기다리는 데까지 가야 한다 — 그래야 아래 그랩의 "대기" 가 락 대기가 아니라 틱 대기다
                    Check(WaitFor(() => !Cam.IsGrabbing && IsParked(_stopper)), "premise: the stop took the timer down and waits for that tick");
                    _grabber.Start();
                    Check(WaitFor(() => IsParked(_grabber)), "premise: the grab waits for the stopped stream's last tick");
                }
                else
                {
                    _grabber.Start();
                    if (window == "load")
                        Check(WaitFor(() => Volatile.Read(ref _dirCalls) == 1), "premise: the grab is held while it loads its image");
                    else
                        Check(WaitFor(() => Volatile.Read(ref _handlerEntered) == 1), "premise: the grab is held inside FrameAcquired with its frame");
                }
            }
            catch
            {
                // 붙잡기에 실패했으면 여기서 정리한다 — 생성자가 던지면 using 이 Dispose 를 부르지 않는다
                Dispose();
                throw;
            }
        }

        public int Frames => Volatile.Read(ref _frames);
        public Exception? GrabError => _grabError;
        public bool GrabReturned => _grabReturned;
        /// <summary>붙잡은 그랩이 아직 <b>그 창 안에</b> 있는가 — 게이트가 서 있는 동안 참이어야 겹침이 걸린 것이다.
        /// 스레드가 살아 있고 게이트가 닫혀 있다는 것만으로는 모자란다: 붙잡기에는 시한이 있어(drain 의 틱 기다림 1 s, 게이트 5 s) 그것이
        /// 지나면 그랩은 다음 단계(제 장 읽기·발행)로 넘어가는데, 그 사이에도 둘은 참이다. 그때 건 조작은 표식이 서 있으니 여전히 던지고
        /// 단언은 초록이지만, 본 것은 시험하려던 창이 아니라 다음 창이다 — 바쁜 기계에서 drain 이 그렇게 빗나가도 아무 신호가 없다.
        /// 그래서 창마다 "그랩이 아직 그 창을 지나지 않았다" 를 함께 본다.</summary>
        public bool GrabStillPending => _grabber.IsAlive && !_gate.IsSet && _window switch
        {
            // 그랩이 붙잡힌 제 이미지 읽기(load)나 제 장의 핸들러(publish)에서 아직 안 돌아왔다
            "load" or "publish" => Volatile.Read(ref _heldLeft) == 0,
            // 그랩이 정지된 라이브의 틱을 기다리는 중 — 붙잡힌 틱이 아직 안 끝났고, 그랩은 제 이미지 읽기에 들어서지도 장을 내지도 않았다
            // (이미지 읽기는 붙잡힌 틱의 한 번뿐이다. 틱 기다림의 시한 1 s 가 지나면 그랩이 읽기에 들어서 이 수가 2 가 된다)
            "drain" => Volatile.Read(ref _heldLeft) == 0 && Volatile.Read(ref _dirCalls) == 1 && Frames == 0,
            _ => false,
        };

        /// <summary>게이트를 풀고 그랩(과 정지)이 돌아오기를 기다린다.</summary>
        public bool Release()
        {
            _gate.Set();
            var grabbed = (_grabber.ThreadState & System.Threading.ThreadState.Unstarted) != 0 || _grabber.Join(5000);
            var stopped = _stopper?.Join(5000) ?? true;
            return grabbed && stopped;
        }

        public override string ToString()
            => $"window={_window} frames={Frames} grabReturned={GrabReturned} grabError={GrabError?.GetType().Name}: {GrabError?.Message} IsGrabbing={Cam.IsGrabbing}";

        public void Dispose()
        {
            Release();
            Cam.Dispose();
        }
    }

    /// <summary>(1)·(3) 단발 그랩이 걸려 있는 동안의 연속 시작은 던지고, 그랩이 돌아온 뒤의 시작은 정상이다.
    /// 가상 카메라는 시작을 그대로 받아, 붙잡힌 그랩의 장과 라이브의 장이 같은 구독으로 섞여 들어왔다.</summary>
    [Theory]
    [InlineData("virtual", "load")]
    [InlineData("virtual", "publish")]
    [InlineData("virtual", "drain")]
    [InlineData("video", "publish")]
    public void StartIsRefusedWhileAGrabIsPending(string source, string window)
    {
        string? clip = null;
        if (source == "video" && (clip = ClipOrSkip()) is null) return;
        using var held = new HeldGrab(source, window, clip);

        Check(held.GrabStillPending, $"{source}/{window}: premise — the grab is pending before StartContinuous is called");
        Exception? startEx = null;
        try { held.Cam.StartContinuous(); } catch (Exception ex) { startEx = ex; }
        var pendingAfter = held.GrabStillPending;
        Check(startEx is InvalidOperationException,
            $"{source}/{window}: StartContinuous while a single grab is pending must throw InvalidOperationException (ICam contract) — " +
            $"a started stream feeds the very subscription that takes any frame as the grab's answer " +
            $"(got {startEx?.GetType().Name ?? "no exception"}, grab still pending={pendingAfter}, {held})");
        Check(pendingAfter, $"{source}/{window}: premise — the grab was still pending when StartContinuous returned ({held})");

        Check(held.Release(), $"{source}/{window}: the held grab returns once released ({held})");
        Check(held.GrabReturned && held.Frames == 1 && !held.Cam.IsGrabbing,
            $"{source}/{window}: the grab ends with its own single frame and nothing was started by the refused call ({held})");

        // (3) 그랩이 돌아온 뒤에는 표식이 내려가 있다 — 정상 경로에서 안 내리면 이 카메라의 시작이 전부 던진다
        Exception? laterEx = null;
        try { held.Cam.StartContinuous(); } catch (Exception ex) { laterEx = ex; }
        Check(laterEx is null && held.Cam.IsGrabbing,
            $"{source}/{window}: once the grab returned, StartContinuous starts normally — the pending mark must come down with the grab " +
            $"(got {laterEx?.GetType().Name}: {laterEx?.Message}, {held})");
        Check(WaitFor(() => held.Frames >= 2), $"{source}/{window}: live frames flow after that start ({held})");
        held.Cam.StopContinuous();
    }

    /// <summary>(2) 단발 그랩이 걸려 있는 동안의 두 번째 단발 그랩은 던진다. 두 구현은 둘 다 진행시켜, 두 호출자의 구독에
    /// 서로의 장이 들어갔다(어느 쪽이 어느 장을 받았는지 아무도 모른다).</summary>
    [Theory]
    [InlineData("virtual", "load")]
    [InlineData("virtual", "publish")]
    [InlineData("virtual", "drain")]
    [InlineData("video", "publish")]
    public void SecondGrabIsRefusedWhileOneIsPending(string source, string window)
    {
        string? clip = null;
        if (source == "video" && (clip = ClipOrSkip()) is null) return;
        using var held = new HeldGrab(source, window, clip);

        Check(held.GrabStillPending, $"{source}/{window}: premise — the first grab is pending before the second is called");
        Exception? secondEx = null;
        try { held.Cam.GrabOne(); } catch (Exception ex) { secondEx = ex; }
        var pendingAfter = held.GrabStillPending;
        Check(secondEx is InvalidOperationException,
            $"{source}/{window}: a second GrabOne while one is pending must throw InvalidOperationException (ICam contract) — " +
            $"two grabs on one camera put each other's frames into each other's subscriptions " +
            $"(got {secondEx?.GetType().Name ?? "no exception"}, first grab still pending={pendingAfter}, {held})");
        Check(pendingAfter, $"{source}/{window}: premise — the first grab was still pending when the second returned ({held})");
        // 거절된 그랩은 남의 표식을 건드리지 않는다 — 진입에서 던지며 표식을 내리면, 앞 그랩이 아직 도는데 시작이 선다
        Exception? startEx = null;
        try { held.Cam.StartContinuous(); } catch (Exception ex) { startEx = ex; }
        Check(startEx is InvalidOperationException && held.GrabStillPending,
            $"{source}/{window}: the refused second grab must leave the first grab's pending mark up — StartContinuous still throws " +
            $"while the first grab is pending (got {startEx?.GetType().Name ?? "no exception"}, {held})");

        Check(held.Release(), $"{source}/{window}: the held grab returns once released ({held})");
        Check(held.GrabReturned && held.Frames == 1,
            $"{source}/{window}: the first grab still ends with its own single frame and the refused grab published nothing ({held})");
    }

    /// <summary>(2) 같은 갈래의 되부름 — 그랩이 낸 장의 핸들러 안에서 같은 카메라를 부르면 던진다(ICam.FrameAcquired:
    /// "같은 카메라를 되불러 그랩하면 이미 기다리는 그랩이 있다 로 던진다"). 두 구현은 그 자리에서 그랩을 한 번 더 돌리거나
    /// 라이브를 켰다 — 바깥 그랩이 아직 안 돌아왔는데 그 구독에 다른 장이 들어간다.
    /// 거절된 그랩 뒤에는 같은 핸들러에서 시작도 걸어 본다 — 거절된 호출이 진입에서 던지며 <b>바깥 그랩의 표식</b>을 내리면, 되부른 그랩이
    /// 던졌는지만 보는 단언은 초록인 채 바로 다음 시작이 바깥 그랩이 안 돌아온 사이에 선다(변이로 확인: 거절된 호출도 표식을 내리게 하면
    /// 두 구현의 GrabOne 행이 이 단언에서 빨개진다).</summary>
    [Theory]
    [InlineData("virtual", "GrabOne")]
    [InlineData("virtual", "StartContinuous")]
    [InlineData("video", "GrabOne")]
    [InlineData("video", "StartContinuous")]
    public void CallsFromTheGrabsOwnFrameAreRefused(string source, string call)
    {
        string? clip = null;
        if (source == "video" && (clip = ClipOrSkip()) is null) return;
        using var cam = source == "virtual" ? new VirtualCam(new CamOpt { FrameRate = 1 }) : MakeVideoCam(clip!);
        var frames = 0;
        var reentered = false;
        var followed = false;
        Exception? innerEx = null;
        Exception? followStartEx = null;
        cam.FrameAcquired += (_, _) =>
        {
            if (Interlocked.Increment(ref frames) != 1) return;
            reentered = true;
            try { if (call == "GrabOne") cam.GrabOne(); else cam.StartContinuous(); }
            catch (Exception ex) { innerEx = ex; }
            if (call != "GrabOne") return;
            followed = true;
            try { cam.StartContinuous(); }
            catch (Exception ex) { followStartEx = ex; }
        };
        cam.Open();
        cam.GrabOne();
        var framesAtReturn = Volatile.Read(ref frames);
        var grabbingAtReturn = cam.IsGrabbing;
        Check(reentered, $"{source}/{call}: premise — the grab's own frame reached the handler that calls back");
        Check(innerEx is InvalidOperationException,
            $"{source}/{call}: {call} from inside the grab's own FrameAcquired must throw InvalidOperationException — the outer grab " +
            $"has not returned, so anything it lets through lands in the outer caller's subscription " +
            $"(got {innerEx?.GetType().Name ?? "no exception"}, frames={framesAtReturn}, IsGrabbing={grabbingAtReturn})");
        if (call == "GrabOne")
            Check(followed && followStartEx is InvalidOperationException,
                $"{source}/{call}: after the refused GrabOne, StartContinuous from the same handler must still throw — a refused call must not " +
                $"lower the outer grab's pending mark, or live starts while the outer grab has not returned " +
                $"(got {followStartEx?.GetType().Name ?? "no exception"}, frames={framesAtReturn}, IsGrabbing={grabbingAtReturn})");
        Check(framesAtReturn == 1 && !grabbingAtReturn,
            $"{source}/{call}: the refused call published and started nothing (frames={framesAtReturn}, IsGrabbing={grabbingAtReturn})");
    }

    /// <summary>(4) 예외로 끝난 그랩도 표식을 내린다. 정지된 라이브의 마지막 틱을 기다리는 그랩을 닫기가 앞지르면 그 그랩은
    /// "열려 있지 않다" 로 던진다 — 그 길에서 표식이 남으면 다시 열어도 이 카메라의 시작과 그랩이 전부 "그랩이 기다리는 중" 으로 던진다.
    /// VideoCaptureCam 은 아래 따로 본다(표식을 읽기 락보다 먼저 세우므로 열기 전 그랩도 그 길을 탄다).</summary>
    [Fact]
    public void VirtualCamGrabThatEndsInAnExceptionLowersItsMark()
    {
        using var held = new HeldGrab("virtual", "drain");
        Check(held.GrabStillPending, "premise: the grab waits for the stopped stream's last tick before Close is called");
        var closer = new Thread(held.Cam.Close) { IsBackground = true, Name = "closer" };
        closer.Start();
        Check(WaitFor(() => !held.Cam.IsConnected), "premise: Close took the camera down while the grab waited");
        Check(held.GrabStillPending, "premise: the grab was past its entry checks (still waiting) when Close came");
        Check(held.Release() && closer.Join(5000), $"the grab and Close return once the tick is released ({held})");
        Check(held.GrabError is InvalidOperationException && !held.GrabReturned && held.Frames == 0,
            $"premise: the grab ended in an exception because Close came while it waited ({held})");

        held.Cam.Open();
        Exception? startEx = null;
        try { held.Cam.StartContinuous(); } catch (Exception ex) { startEx = ex; }
        Check(startEx is null && held.Cam.IsGrabbing,
            $"a grab that ended in an exception must still lower its pending mark — otherwise every later StartContinuous on this " +
            $"camera throws 'a single grab is waiting' (got {startEx?.GetType().Name}: {startEx?.Message})");
        held.Cam.StopContinuous();
        Exception? grabEx = null;
        try { held.Cam.GrabOne(); } catch (Exception ex) { grabEx = ex; }
        Check(grabEx is null && held.Frames >= 1,
            $"and a later GrabOne is not refused as 'already waiting' (got {grabEx?.GetType().Name}: {grabEx?.Message}, {held})");
    }

    /// <summary>(4) VideoCaptureCam 판 — 표식을 읽기 락보다 <b>먼저</b> 세우므로, 열기 전·닫힌 뒤·해제 뒤의 그랩은 전부 표식을 세운 다음
    /// "열려 있지 않다" 로 던진다. 그 길에서 표식을 안 내리면 그 한 번으로 이 인스턴스가 굳는다 — 다시 열어도 시작과 그랩이 "기다리는 중" 으로
    /// 던진다(검토의 변이: 정상 완료 때만 내리게 바꿔도 스위트 전체가 초록이었다).</summary>
    [Fact]
    public void VideoCaptureCamGrabBeforeOpenLowersItsMark()
    {
        var clip = ClipOrSkip();
        if (clip is null) return;
        using var cam = MakeVideoCam(clip);
        Exception? early = null;
        try { cam.GrabOne(); } catch (Exception ex) { early = ex; }
        Check(early is InvalidOperationException && early.Message.Contains("not opened"),
            $"premise: GrabOne before Open fails as not opened (got {early?.GetType().Name}: {early?.Message})");
        cam.Open();
        Exception? startEx = null;
        try { cam.StartContinuous(); } catch (Exception ex) { startEx = ex; }
        Check(startEx is null && cam.IsGrabbing,
            $"a grab that threw after setting its mark must lower it — otherwise StartContinuous on the opened camera throws 'a single grab is waiting' " +
            $"(got {startEx?.GetType().Name}: {startEx?.Message})");
        cam.StopContinuous();
        var frames = 0;
        cam.FrameAcquired += (_, _) => Interlocked.Increment(ref frames);
        Exception? grabEx = null;
        try { cam.GrabOne(); } catch (Exception ex) { grabEx = ex; }
        Check(grabEx is null && frames == 1, $"and GrabOne is not refused as 'already waiting' (got {grabEx?.GetType().Name}: {grabEx?.Message}, frames={frames})");
    }

    // ---- (5) VideoCaptureCam 의 라이브 루프가 스스로 끝날 때 ----

    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

    private static object? VcapField(VideoCaptureCam cam, string name) => typeof(VideoCaptureCam).GetField(name, Hidden)!.GetValue(cam);

    /// <summary>한 출처 묶음의 로그 줄을 "{출처}|{등급}|{문구}" 로 받는다. 빈 창구를 먼저 꽂아 보관된 앞 줄을 흘려보내고, 끝나면 원래 창구를 돌려놓는다.</summary>
    private sealed class LogCapture : IDisposable
    {
        private readonly Action<CvLogLevel, string, string, Exception?>? _prev = CvLog.Sink;
        private readonly SyncList<string> _lines = new();
        public LogCapture(params string[] sources)
        {
            CvLog.Sink = static (_, _, _, _) => { };
            CvLog.Sink = (lvl, src, msg, _) => { if (sources.Contains(src)) _lines.Add($"{src}|{lvl}|{msg}"); };
        }
        public int Count(string prefix, string text) => _lines.Snapshot().Count(l => l.StartsWith(prefix, StringComparison.Ordinal) && l.Contains(text));
        public override string ToString() => string.Join(" / ", _lines.Snapshot());
        public void Dispose() => CvLog.Sink = _prev;
    }

    /// <summary>라이브 루프 안에 예상 못 한 예외를 넣는다 — 오프라인 소스로 루프를 그렇게 죽일 공개 입력이 없어 안쪽을 바꾼다. 되돌리는 동작을 돌려준다.
    /// <list type="bullet">
    /// <item><c>unexpected</c> — 루프가 읽은 장을 다루며 옵션을 보는 단계가 던진다(NullReferenceException — 루프 테두리의 일반 예외 갈래).</item>
    /// <item><c>disposed-capture</c> — 읽기가 놓인 캡처를 만난다(ObjectDisposedException — 정지 경합과 같은 형인데 정지는 없었다).</item>
    /// </list>
    /// 루프는 같은 락 아래에서 네이티브 읽기를 하므로 그 락을 쥐고 바꾼다 — 읽는 도중에 캡처를 놓으면 던지는 대신 프로세스가 죽을 수 있다.</summary>
    private static Action InjectLiveFault(VideoCaptureCam cam, string fault)
    {
        var sync = VcapField(cam, "_sync")!;
        var field = typeof(VideoCaptureCam).GetField(fault == "unexpected" ? "_opt" : "_cap", Hidden)!;
        var original = field.GetValue(cam);
        object? poison = null;
        if (fault != "unexpected")
        {
            var dead = new VideoCapture();
            dead.Dispose();
            poison = dead;
        }
        lock (sync) field.SetValue(cam, poison);
        return () => { lock (sync) field.SetValue(cam, original); };
    }

    /// <summary>(5) 라이브 루프가 정지 요청 없이 끝나면 상태를 내리고 꺼짐을 <b>한 번</b> 알린다 — 상태를 먼저, 통지는 뒤에, 루프 스레드에서.
    /// 종전에는 루프 테두리가 줄 하나(정지 경합 형이면 그것도 없이)만 남기고 루프 스레드 참조를 그대로 두어, <see cref="ICam.IsGrabbing"/> 이
    /// 참으로 굳고 꺼짐이 안 나갔다. 다음 시작은 "이미 도는 중" 으로 읽혀 예외도 로그도 없이 돌아갔다 — 프레임은 영영 안 오는데 아무도 모른다.
    /// 같은 항에 양성 대조를 둔다: 그 뒤의 닫기는 꺼짐을 더 내지 않고, 다시 켠 라이브의 정상 정지는 꺼짐을 정확히 한 번(정지를 부른 스레드에서) 낸다.</summary>
    [Theory]
    [InlineData("unexpected")]
    [InlineData("disposed-capture")]
    public void VideoLiveLoopThatEndsOnItsOwnReportsTheStop(string fault)
    {
        var clip = ClipOrSkip();
        if (clip is null) return;
        using var log = new LogCapture(nameof(VideoCaptureCam));
        using var cam = (VideoCaptureCam)MakeVideoCam(clip);
        var liveThreadName = $"{cam.Name}.Live";
        var frames = 0;
        var ons = 0;
        var offs = new SyncList<(bool IsGrabbing, string? Thread)>();
        cam.FrameAcquired += (_, _) => Interlocked.Increment(ref frames);
        cam.GrabbingChanged += (_, on) =>
        {
            if (on) Interlocked.Increment(ref ons);
            else offs.Add((cam.IsGrabbing, Thread.CurrentThread.Name));
        };
        string State() => $"IsGrabbing={cam.IsGrabbing} ons={Volatile.Read(ref ons)} offs=[{string.Join(", ", offs.Snapshot())}] frames={Volatile.Read(ref frames)} log: {log}";

        cam.Open();
        cam.StartContinuous();
        Check(WaitFor(() => Volatile.Read(ref frames) >= 2), $"{fault}: premise — live frames flow before the fault ({State()})");
        var loop = (Thread)VcapField(cam, "_liveThread")!;
        var restore = InjectLiveFault(cam, fault);
        // 살아있음 — 넣은 고장이 루프를 실제로 끝냈다. 안 끝났으면 아래 "꺼지지 않았다" 는 결함이 아니라 고장이 안 걸린 것이다.
        var ended = loop.Join(5000);
        restore();
        Check(ended, $"{fault}: premise — the injected fault ended the live loop ({State()})");

        // 루프 스레드가 끝났으니 그 스레드가 낼 것은 이미 다 났다(통지는 그 스레드에서 동기로 나간다) — 기다리지 않고 본다.
        var offsNow = offs.Snapshot();
        Check(!cam.IsGrabbing && offsNow.Length == 1,
            $"{fault}: a live loop that ended without a stop request must lower IsGrabbing and raise GrabbingChanged(false) once — otherwise " +
            $"IsGrabbing stays true, the next StartContinuous returns as 'already running', and a wrapping ReconnectingCam never rebuilds ({State()})");
        Check(!offsNow[0].IsGrabbing && offsNow[0].Thread == liveThreadName,
            $"{fault}: the state comes down before the notification, and the notification comes from the live thread itself ({State()})");
        Check(log.Count("VideoCaptureCam|Error|", "the live loop stopped on an unexpected error") == 1
              && log.Count("VideoCaptureCam|Warning|", "the live loop ended without a stop request") == 1,
            $"{fault}: the log names the cause once and says that live acquisition is no longer running ({State()})");

        // 양성 대조 ① — 이미 알린 정지를 닫기가 또 알리지 않는다(전이일 때만 낸다)
        cam.Close();
        Check(offs.Count == 1, $"{fault}: Close after the loop already reported its stop raises no second GrabbingChanged(false) ({State()})");

        // 다시 열고 켜면 정말 다시 돈다 — 종전에는 루프 참조가 남아 이 시작이 조용히 돌아갔다
        cam.Open();
        cam.StartContinuous();
        var before = Volatile.Read(ref frames);
        Check(cam.IsGrabbing && Volatile.Read(ref ons) == 2 && WaitFor(() => Volatile.Read(ref frames) > before + 1),
            $"{fault}: StartContinuous after the self-ended loop starts a new loop and frames flow again ({State()})");

        // 양성 대조 ② — 정상 정지는 꺼짐을 정확히 한 번, 정지를 부른 스레드에서 낸다(루프가 끝나며 한 번 더 내지 않는다)
        var loop2 = (Thread)VcapField(cam, "_liveThread")!;
        cam.StopContinuous();
        Check(loop2.Join(5000), $"{fault}: premise — the stopped loop exited ({State()})");
        offsNow = offs.Snapshot();
        Check(offsNow.Length == 2 && offsNow[1].Thread != liveThreadName
              && log.Count("VideoCaptureCam|Warning|", "the live loop ended without a stop request") == 1,
            $"{fault}: a normal StopContinuous raises GrabbingChanged(false) exactly once, from the stopping thread, and is not read as the loop ending on its own ({State()})");
    }

    /// <summary>(5) 같은 고장을 <see cref="ReconnectingCam"/> 으로 감싸면 세션을 다시 세워 라이브를 되살린다 — 그 데코레이터는 안쪽의 꺼짐 통지를
    /// 듣고서야 "청하지 않은 정지" 를 안다. 종전에는 통지가 없어 데코레이터는 돈다고 답하고 프레임은 영영 오지 않았다.</summary>
    [Fact]
    public void ReconnectingCamRebuildsTheSessionWhenTheVideoLiveLoopEndsOnItsOwn()
    {
        var clip = ClipOrSkip();
        if (clip is null) return;
        using var log = new LogCapture(nameof(VideoCaptureCam), nameof(ReconnectingCam));
        var made = new SyncList<VideoCaptureCam>();
        using var cam = new ReconnectingCam(() =>
        {
            var inner = (VideoCaptureCam)MakeVideoCam(clip);
            made.Add(inner);
            return inner;
        }, new CamReconnectOpt { BackoffMs = new[] { 20 }, ShutdownWaitMs = 1000 });
        var frames = 0;
        cam.FrameAcquired += (_, _) => Interlocked.Increment(ref frames);
        string State() => $"sessions={made.Count} IsGrabbing={cam.IsGrabbing} frames={Volatile.Read(ref frames)} log: {log}";

        cam.Open();
        cam.StartContinuous();
        Check(WaitFor(() => Volatile.Read(ref frames) >= 2), $"premise — live frames flow before the fault ({State()})");
        var first = made.Snapshot()[0];
        var loop = (Thread)VcapField(first, "_liveThread")!;
        // 되돌리지 않는다 — 이 세션은 교체되어 버려진다(해제는 옵션을 보지 않는다)
        InjectLiveFault(first, "unexpected");
        Check(loop.Join(5000) && log.Count("VideoCaptureCam|Error|", "the live loop stopped on an unexpected error") == 1,
            $"premise — the injected fault ended the first session's live loop ({State()})");

        Check(WaitFor(() => made.At(1)?.IsGrabbing == true && cam.IsGrabbing),
            $"the wrapper must rebuild the session and resume live when the inner live loop ends on its own — it learns of that stop only " +
            $"from the inner GrabbingChanged(false) ({State()})");
        var before = Volatile.Read(ref frames);
        Check(WaitFor(() => Volatile.Read(ref frames) > before + 1), $"frames flow again on the rebuilt session ({State()})");
        Check(log.Count("ReconnectingCam|Warning|", "stopped grabbing without being asked") == 1,
            $"the rebuild is logged as an unrequested stop ({State()})");
        cam.StopContinuous();
    }

    // ---- (6)~(8) 기본 절차(CamGrabExt.GrabFrameAsync)가 돌아오는 때와 답 ----

    /// <summary>시험 대상 카메라와 그랩을 실제로 도는 카메라 — 데코레이터면 연 세션(안쪽)이다.</summary>
    private static (ICam Cam, Func<ICam> Target) MakeGrabCam(string source, string? clip, int frameRate = 1)
    {
        ICam? inner = null;
        ICam cam = source switch
        {
            "virtual" => new VirtualCam(new CamOpt { FrameRate = frameRate }),
            "video" => MakeVideoCam(clip!),
            _ => new ReconnectingCam(() => inner = new VirtualCam(new CamOpt { FrameRate = frameRate }), new CamReconnectOpt { ShutdownWaitMs = 1000 }),
        };
        return (cam, () => inner ?? cam);
    }

    private static object SyncOf(ICam target) => target.GetType().GetField("_sync", Hidden)!.GetValue(target)!;

    /// <summary>기본 절차의 호출 하나를 "장은 받았는데 GrabOne 이 아직 안 돌아온" 자리에 붙잡는다 — 카메라의 락을 쥐어 GrabOne 을 진입에서 세워 두고,
    /// 그동안 <b>호출의 구독 뒤에</b> 붙잡는 구독자를 건다. 그러면 호출의 구독자는 장을 받았는데 GrabOne 은 그 뒤 구독자에서 서 있다.
    /// 붙잡는 구독자는 그랩을 실제로 도는 카메라(데코레이터면 안쪽)에 건다: 데코레이터의 구독자는 데코레이터가 안쪽에 먼저 건 전달 핸들러 안에서
    /// 불려 호출의 구독자보다 <b>앞에</b> 서므로, 거기서 붙잡으면 호출이 장을 받기도 전에 선다(틈이 안 열린다).
    /// 게이트는 해제하지 않는다 — 시한으로 끝나는 행에서는 붙잡힌 구독자가 아직 그 위에서 기다린다.</summary>
    private sealed class TailHold : IDisposable
    {
        private readonly ICam _target;
        private readonly ManualResetEventSlim _gate = new();
        private readonly int _holdMs;
        private int _entered;
        private int _left;
        public readonly Task<CamFrame?> Call;

        /// <param name="entryHoldMs">락을 놓기 전에 더 쥐고 있을 시간 — 장이 호출보다 그만큼 늦게 온다.</param>
        public TailHold(ICam cam, ICam target, TimeSpan timeout, CancellationToken ct = default, int holdMs = 5000, int entryHoldMs = 0)
        {
            _target = target;
            _holdMs = holdMs;
            var sync = SyncOf(target);
            Monitor.Enter(sync);
            try
            {
                Call = cam.GrabFrameAsync(timeout, ct);   // 구독을 걸고 그랩을 띄운다 — GrabOne 은 이 락에서 선다
                target.FrameAcquired += Late;             // 그래서 이 구독자는 호출의 구독 뒤에 선다
                if (entryHoldMs > 0) Thread.Sleep(entryHoldMs);
            }
            finally { Monitor.Exit(sync); }
        }

        private void Late(object? _, CamFrame f)
        {
            if (Interlocked.Exchange(ref _entered, 1) != 0) return;
            _gate.Wait(_holdMs);
            Volatile.Write(ref _left, 1);
        }

        /// <summary>그랩이 장을 냈고 호출 뒤의 구독자에서 붙잡혀 있다 — 호출의 구독자는 그 앞이라 이미 장을 받았다.</summary>
        public bool WaitHeld() => WaitFor(() => Volatile.Read(ref _entered) == 1);
        public bool StillHeld => Volatile.Read(ref _entered) == 1 && Volatile.Read(ref _left) == 0;
        public void Release() => _gate.Set();

        public void Dispose()
        {
            _gate.Set();
            _target.FrameAcquired -= Late;
        }
    }

    /// <summary>(6) <see cref="CamGrabExt.GrabFrameAsync"/> 의 기본 절차는 <b>GrabOne 이 돌아온 뒤에</b> 돌아온다. 장은 GrabOne 안에서 발행되므로
    /// 호출의 구독자가 장을 받은 순간에도 GrabOne 은 아직 남은 구독자에게 장을 넘기거나 표식을 내리는 중이다. 종전에는 그 순간 돌아가서,
    /// 부른 쪽이 곧바로 건 그랩·연속 시작이 "이미 기다리는 그랩이 있다" 로 거절됐다(형제 저장소 실측, 가상 카메라: 반환 0 ms 뒤 19/20, 1 ms 뒤 0/20.
    /// 이쪽 재현도 큰 폴더 영상에서만 19/20 이었고 테스트 패턴·동영상은 0/20 — 발행 뒤 디코드한 영상을 놓는 0.4 ms 가 그 틈으로 보인다, 추정).
    /// 틈은 시간이 아니라 게이트로 연다(<see cref="TailHold"/>). 감싼 <see cref="ReconnectingCam"/> 은 안쪽의 기본 절차로 넘기므로 같은 행으로 본다.</summary>
    [Theory]
    [InlineData("virtual")]
    [InlineData("video")]
    [InlineData("reconnecting-virtual")]
    public async Task GrabFrameAsyncReturnsOnlyAfterItsGrabReturned(string source)
    {
        string? clip = null;
        if (source == "video" && (clip = ClipOrSkip()) is null) return;
        var (made, targetOf) = MakeGrabCam(source, clip);
        using var cam = made;
        var frames = 0;   // 호출 전에 건 구독자 — 장이 발행됐는가
        cam.FrameAcquired += (_, _) => Interlocked.Increment(ref frames);
        cam.Open();

        bool returnedWhileHeld;
        bool heldThroughout;
        string grabAtThatMoment = "not tried";
        Task<CamFrame?> call;
        using (var hold = new TailHold(cam, targetOf(), TimeSpan.FromSeconds(5)))
        {
            call = hold.Call;
            Check(hold.WaitHeld(),
                $"{source}: premise — the grab published and is held in the subscriber after the call's own (frames={Volatile.Read(ref frames)})");
            // 여기서 끝나 있으면 GrabOne 이 안 돌아왔는데 돌아간 것이다
            returnedWhileHeld = await Task.WhenAny(call, Task.Delay(300)) == call;
            heldThroughout = hold.StillHeld;
            if (returnedWhileHeld)
            {
                // 진단 — 고치기 전 판에서 부른 쪽이 이 순간 건 그랩이 무엇을 받았는지(거절 사유)를 싣는다
                try { cam.GrabOne(); grabAtThatMoment = "accepted"; }
                catch (Exception ex) { grabAtThatMoment = $"{ex.GetType().Name}: {ex.Message}"; }
            }
            hold.Release();
            Check(heldThroughout, $"{source}: premise — the late subscriber was still held when the check ended (otherwise the window was missed)");
            Check(Volatile.Read(ref frames) >= 1, $"{source}: premise — the frame was published while the call was pending (frames={Volatile.Read(ref frames)})");
            Check(!returnedWhileHeld,
                $"{source}: GrabFrameAsync must not return while the camera's GrabOne is still delivering its frame — the caller's next grab or " +
                $"StartContinuous meets the camera's one-grab mark and is refused as 'already waiting' (a GrabOne at that moment: {grabAtThatMoment})");
            var done = await Task.WhenAny(call, Task.Delay(5000)) == call;
            Check(done, $"{source}: the call returns once the held subscriber lets go");
        }
        var frame = await call;
        Check(frame is not null, $"{source}: and it returns the frame it took");

        // 돌아온 즉시 건 다음 조작은 거절되지 않는다 — 부른 쪽이 기대는 것은 이것이다
        Exception? grabEx = null;
        try { cam.GrabOne(); } catch (Exception ex) { grabEx = ex; }
        Check(grabEx is null, $"{source}: a GrabOne right after the call returned is accepted (got {grabEx?.GetType().Name}: {grabEx?.Message})");
        Exception? startEx = null;
        try { cam.StartContinuous(); } catch (Exception ex) { startEx = ex; }
        Check(startEx is null && cam.IsGrabbing,
            $"{source}: StartContinuous right after the call returned starts (got {startEx?.GetType().Name}: {startEx?.Message})");
        cam.StopContinuous();
    }

    /// <summary>(7) GrabOne 이 진입에서 거절됐으면 그 사이 구독에 장이 들어왔어도 <b>던진다</b>. 라이브가 도는 중이면 그 장은 라이브의 틱이다 —
    /// 종전에는 그 장을 답으로 돌려줘, 거절(ICam: 연속 취득 중의 단발 그랩은 던진다)이 정상 답으로 덮였다. 예외도 경고도 없이 흐르던 장이
    /// 이 그랩의 장으로 판정에 쓰인다. 장치처럼 읽기에 락을 오래 쥐는 소스일수록 잦다(검토 실측: 라이브 중 200회 부른 그랩 중 동영상 소스를
    /// 쉬지 않고 읽게 하면 167~174회가 라이브 장을 받아 왔다).
    /// 겹침은 게이트로 만든다: 락을 쥐어 GrabOne 을 진입에서 세우고, 라이브 틱이 호출의 구독에 닿은 것(뒤에 건 구독자가 받은 것)을 본 뒤 놓는다.</summary>
    [Theory]
    [InlineData("virtual")]
    [InlineData("reconnecting-virtual")]
    public async Task GrabFrameAsyncThrowsWhenItsGrabIsRefusedEvenIfAFrameCameIn(string source)
    {
        var (made, targetOf) = MakeGrabCam(source, null, frameRate: 50);
        using var cam = made;
        cam.Open();
        cam.StartContinuous();
        var target = targetOf();
        var sync = SyncOf(target);
        var after = 0;
        void After(object? _, CamFrame f) => Interlocked.Increment(ref after);
        Task<CamFrame?> call;
        bool tickReached;
        Monitor.Enter(sync);
        try
        {
            call = cam.GrabFrameAsync(TimeSpan.FromSeconds(3));   // GrabOne 은 이 락에서 선다 — 놓으면 "연속 취득 중" 으로 거절된다
            target.FrameAcquired += After;                       // 호출의 구독 뒤 — 이것이 받았으면 호출의 구독도 받았다
            tickReached = WaitFor(() => Volatile.Read(ref after) >= 1);
        }
        finally { Monitor.Exit(sync); }
        string outcome;
        try { var f = await call; outcome = f is null ? "null" : "a frame"; }
        catch (InvalidOperationException ex) { outcome = $"InvalidOperationException: {ex.Message}"; }
        catch (Exception ex) { outcome = $"{ex.GetType().Name}: {ex.Message}"; }
        target.FrameAcquired -= After;
        cam.StopContinuous();
        Check(tickReached, $"{source}: premise — a live frame reached the call's subscription while its GrabOne was held at entry");
        Check(outcome.StartsWith("InvalidOperationException: Continuous acquisition is running", StringComparison.Ordinal),
            $"{source}: a grab refused because live acquisition is running must surface that refusal, not return the live frame its subscription " +
            $"happened to pick up — a free-running frame would be judged as this grab's (got {outcome})");
    }

    /// <summary>(8) (6) 의 기다림은 <b>시한까지만</b>이고 답을 바꾸지 않는다 — 뒤의 구독자가 장을 시한 넘게 붙잡아도 장은 시한에 돌아오고
    /// (무한 시한이면 1초 유예), 그 사이 취소돼도 받은 장이 돌아온다(종전과 같다). (6) 은 "너무 일찍 돌아온다" 쪽만 본다 — 시한 없이 기다리게
    /// 바꿔도 (6) 은 초록이고 호출은 붙잡힌 구독자를 따라 영영 선다(검토의 변이로 확인).</summary>
    [Theory]
    [InlineData("timeout")]
    [InlineData("infinite")]
    [InlineData("cancel")]
    public async Task GrabFrameAsyncTailWaitIsBoundedAndKeepsTheFrame(string row)
    {
        var (made, targetOf) = MakeGrabCam("virtual", null);
        using var cam = made;
        cam.Open();
        using var cts = new CancellationTokenSource();
        var timeout = row switch { "timeout" => TimeSpan.FromMilliseconds(400), "infinite" => Timeout.InfiniteTimeSpan, _ => TimeSpan.FromSeconds(10) };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // 시한 행은 장을 200 ms 늦게 들인다 — 장이 곧바로 오면 "부른 순간부터 센 시한" 과 "장이 온 뒤 새로 센 시한" 이 같은 자리(≈400 ms)에
        // 떨어져 갈리지 않는다(검토의 변이: 꼬리를 새 시한으로 기다리게 바꿔도 초록이었다). 늦추면 앞은 ≈400, 뒤는 ≈600 이다.
        using var hold = new TailHold(cam, targetOf(), timeout, cts.Token, holdMs: 8000, entryHoldMs: row == "timeout" ? 200 : 0);
        Check(hold.WaitHeld(), $"{row}: premise — the call's subscriber has the frame and GrabOne is held behind it");
        var heldAtMs = sw.ElapsedMilliseconds;
        if (row == "cancel") cts.Cancel();
        var cancelAtMs = sw.ElapsedMilliseconds;
        var returned = await Task.WhenAny(hold.Call, Task.Delay(4000)) == hold.Call;
        var tookMs = sw.ElapsedMilliseconds;
        var stillHeld = hold.StillHeld;
        CamFrame? frame = null;
        Exception? err = null;
        if (returned)
        {
            try { frame = await hold.Call; } catch (Exception ex) { err = ex; }
        }
        var when = $"held at {heldAtMs} ms, returned at {(returned ? tookMs.ToString() : "never (4 s)")} ms";
        Check(returned, $"{row}: the call comes back while a later subscriber still holds the frame — the wait for GrabOne is bounded ({when})");
        Check(stillHeld, $"{row}: premise — the later subscriber was still holding the frame when the call returned ({when})");
        Check(frame is not null && err is null,
            $"{row}: and it returns the frame it took, without an exception — the bound or a cancellation never turns a delivered frame into " +
            $"null or a throw (got {(frame is null ? "null" : "a frame")}, {err?.GetType().Name}: {err?.Message})");
        switch (row)
        {
            case "timeout":
                // 시한은 부른 순간부터 센다 — 하한은 타이머 한 눈금(15.6 ms) 넉넉히, 상한은 "장이 온 뒤(≈200 ms) 새로 센 400 ms" 보다 앞
                Check(heldAtMs >= 180, $"{row}: premise — the frame came about 200 ms after the call ({when})");
                Check(tookMs is >= 370 and < 560,
                    $"{row}: it returns at the 400 ms deadline counted from the call, not 400 ms after the frame came ({when})");
                break;
            case "infinite":
                Check(tookMs - heldAtMs >= CamGrabExt.LatePublishGraceMs - 50 && tookMs - heldAtMs < CamGrabExt.LatePublishGraceMs + 1500,
                    $"{row}: with InfiniteTimeSpan it waits the one-second grace for GrabOne, not forever and not zero ({when})");
                break;
            case "cancel":
                Check(tookMs - cancelAtMs < 1000, $"{row}: a cancellation during the wait returns the frame promptly ({when}, cancelled at {cancelAtMs} ms)");
                break;
        }
    }

    /// <summary>(9) <see cref="VideoCaptureCam"/> 는 읽는 동안 카메라 락을 쥔다 — 그 동안 온 두 번째 그랩과 라이브 중 그랩은 그 락 뒤에 줄 서지 않고
    /// <b>그 자리에서</b> 던진다. 종전에는 겹침 검사가 그 락 안에 있어 두 번째 그랩이 앞 그랩의 읽기를 기다렸다가, 앞 그랩이 표식을 내린 뒤
    /// 받아들여졌다 — 그 호출자의 구독에는 이미 앞 그랩의 장이 들어 있어 남의 장을 예외 없이 제 답으로 받았다(검토 실측: 겹친 GrabFrameAsync
    /// 두 개가 같은 장을 받은 쌍 200 중 196~200, 기본 절차를 안 쓴 손 대기도 같다). 라이브 중 그랩도 라이브 루프의 읽기 뒤에 줄 서, 짧은 시한이면
    /// 거절이 나기 전에 시한이 다해 라이브 장이 답으로 나갔다.
    /// 읽기는 시험이 카메라 락을 쥐어 흉내낸다 — 그 동안 건 그랩이 300 ms 안에 거절로 돌아오는지 본다(줄 서면 락을 놓을 때까지 안 돌아온다).</summary>
    [Theory]
    [InlineData("second-grab")]
    [InlineData("live")]
    public void VideoCaptureCamRefusesAtOnceInsteadOfQueuingBehindTheRead(string row)
    {
        var clip = ClipOrSkip();
        if (clip is null) return;
        using var cam = (VideoCaptureCam)MakeVideoCam(clip);
        var frames = 0;
        cam.FrameAcquired += (_, _) => Interlocked.Increment(ref frames);
        cam.Open();
        if (row == "live")
        {
            cam.StartContinuous();
            Check(WaitFor(() => Volatile.Read(ref frames) >= 1), $"{row}: premise — live frames flow");
        }
        var sync = SyncOf(cam);
        Thread? first = null;
        Exception? firstErr = null;
        var firstReturned = false;
        Exception? err = null;
        var returnedAtOnce = false;
        var held = true;
        Monitor.Enter(sync);
        try
        {
            if (row == "second-grab")
            {
                // 앞 그랩 — 표식을 세우고 읽기 락에서 선다(시험이 쥔 락이 "읽는 중" 이다)
                first = new Thread(() =>
                {
                    try { cam.GrabOne(); firstReturned = true; }
                    catch (Exception ex) { firstErr = ex; }
                }) { IsBackground = true, Name = "first-grab" };
                first.Start();
                Check(WaitFor(() => IsParked(first)), $"{row}: premise — the first grab is waiting on the read lock");
            }
            var second = new Thread(() =>
            {
                try { cam.GrabOne(); }
                catch (Exception ex) { err = ex; }
            }) { IsBackground = true, Name = "second-grab" };
            second.Start();
            returnedAtOnce = second.Join(300);
            // 락을 놓는다 — 줄 선 그랩(고치기 전 판)과 앞 그랩이 마저 돌게. 아래 단언은 이미 정해졌다.
            Monitor.Exit(sync);
            held = false;
            second.Join(5000);
        }
        finally { if (held) Monitor.Exit(sync); }
        var expected = row == "live" ? "Continuous acquisition is running" : "A single grab is already waiting";
        Check(returnedAtOnce && err is InvalidOperationException && err.Message.StartsWith(expected, StringComparison.Ordinal),
            $"{row}: a grab that cannot answer must be refused at once ('{expected}…'), not queued behind the read lock — queued, it is accepted " +
            $"after the other grab lowers its mark and returns that grab's frame as its own (returned within 300 ms={returnedAtOnce}, " +
            $"got {err?.GetType().Name}: {err?.Message})");
        if (first is not null)
        {
            Check(first.Join(5000) && firstReturned && firstErr is null, $"{row}: the first grab still completes with its frame ({firstErr?.Message})");
        }
        if (row == "live") cam.StopContinuous();
    }

    /// <summary>(9) 같은 갈래의 나머지 반 — VideoCaptureCam 의 라이브 검사는 두 번이다: 짧은 락에서 대략 한 번, 읽기 락 안에서 다시 한 번.
    /// 뒤의 것이 막는 순서: 연속 시작이 표식을 아직 안 섰을 때 보고 지나간 뒤, 그랩의 앞 검사(라이브 없음)가 지나고 나서 라이브를 세운다 — 그러면
    /// 그랩은 받아들여지고 라이브의 장이 그 구독으로 흘러 남의 장을 예외 없이 돌려준다(검토의 변이: 뒤 검사를 지우면 스위트 전체가 초록이고,
    /// 시작·그랩 경합 2000회 중 541~665회가 라이브 장을 받았다). 앞 검사 옆에 있어 중복처럼 보이는 줄이라 못 박는다.
    /// 순서는 게이트로 만든다: 읽기 락을 쥐어 표식을 세운 그랩을 그 락에서 세우고, 그 사이 라이브가 선 상태를 넣은 뒤 놓는다.</summary>
    [Fact]
    public void VideoCaptureCamRechecksLiveUnderTheReadLock()
    {
        var clip = ClipOrSkip();
        if (clip is null) return;
        using var cam = (VideoCaptureCam)MakeVideoCam(clip);
        var frames = 0;
        cam.FrameAcquired += (_, _) => Interlocked.Increment(ref frames);
        cam.Open();
        var sync = SyncOf(cam);
        var liveField = typeof(VideoCaptureCam).GetField("_liveThread", Hidden)!;
        Exception? err = null;
        var returned = false;
        var grab = new Thread(() =>
        {
            try { cam.GrabOne(); returned = true; }
            catch (Exception ex) { err = ex; }
        }) { IsBackground = true, Name = "grab-after-mark" };
        Monitor.Enter(sync);
        try
        {
            grab.Start();
            Check(WaitFor(() => IsParked(grab)), "premise: the grab set its mark and waits on the read lock");
            liveField.SetValue(cam, new Thread(() => { }));   // 그 사이 연속 시작이 선 것 — 표식을 보기 전에 지나간 시작이다
        }
        finally { Monitor.Exit(sync); }
        Check(grab.Join(5000), "the grab returns once the read lock is free");
        lock (sync) liveField.SetValue(cam, null);
        Check(!returned && err is InvalidOperationException && err.Message.StartsWith("Continuous acquisition is running", StringComparison.Ordinal) && frames == 0,
            $"a grab whose early check saw no live stream must look again under the read lock and refuse once live started — otherwise it is accepted " +
            $"and live frames flow into its subscription as its answer (returned={returned}, got {err?.GetType().Name}: {err?.Message}, frames={frames})");
        Exception? later = null;
        try { cam.GrabOne(); } catch (Exception ex) { later = ex; }
        Check(later is null && frames == 1, $"and the refused grab lowered its mark — the next GrabOne runs (got {later?.GetType().Name}: {later?.Message})");
    }

    /// <summary>구독되는 순간 남의 장(라이브 틱·앞 그랩의 장)이 들어오는 카메라 — 구독과 그랩 시작 사이의 틈을 결정적으로 연다.</summary>
    private sealed class EagerCam : ICam
    {
        public readonly CamFrame Foreign = new(new byte[4], 2, 2, 2, CamPixelFormat.Mono8);
        public readonly CamFrame Own = new(new byte[4], 2, 2, 2, CamPixelFormat.Mono8);
        private EventHandler<CamFrame>? _frames;
        public int GrabCalls;
        public string Name => "eager";
        public string ComType => "Fake";
        public bool IsConnected => true;
        public bool IsGrabbing => false;
        public event EventHandler<CamFrame>? FrameAcquired
        {
            add { _frames += value; value?.Invoke(this, Foreign); }
            remove { _frames -= value; }
        }
        public event EventHandler<ConnArgs>? ConnectionChanged { add { } remove { } }
        public event EventHandler<bool>? GrabbingChanged { add { } remove { } }
        public void Open() { }
        public void Close() { }
        public void GrabOne()
        {
            Interlocked.Increment(ref GrabCalls);
            _frames?.Invoke(this, Own);
        }
        public void StartContinuous() { }
        public void StopContinuous() { }
        public void SetExposureTimeUs(double timeUs) { }
        public void Dispose() { }
    }

    /// <summary>(10) 기본 절차는 이 호출의 GrabOne 이 <b>시작되기 전에</b> 온 장을 집지 않는다 — GrabOne 은 "이 호출 뒤에 오는 프레임" 을 내므로 그 장은
    /// 답일 수 없다. 종전에는 구독과 그랩 시작 사이(풀 스레드가 본문을 집기까지)에 들어온 장을 답으로 냈다 — 시작 전에 취소된 호출은 GrabOne 을
    /// 한 번도 안 부르고 그 장을 돌려줬다(검토 실측 20/20).
    /// 0.30.1 에서는 두 행 모두 늘 빨갛다(검토 실측 41/41 — 구독 순간 장이 들어와 tcs 가 이미 끝나 있으면 WhenAny 가 배열 순서로 그것을 고른다).
    /// 개발 중 거절 검사(IsCanceled)만 먼저 들어간 판에서는 <c>pre-cancelled</c> 행이 19/20 이었다 — 취소된 토큰의 Task.Run 이 돌아오는 순간
    /// 이미 취소로 끝나 있으면 그 판도 취소로 던졌다. 시점에 기대지 않고 못 박는 것은 <c>plain</c> 행이다.</summary>
    [Theory]
    [InlineData("plain")]
    [InlineData("pre-cancelled")]
    public async Task GrabFrameAsyncIgnoresFramesFromBeforeItsGrabStarted(string row)
    {
        using var cam = new EagerCam();
        using var cts = new CancellationTokenSource();
        if (row == "pre-cancelled") cts.Cancel();
        CamFrame? got = null;
        Exception? err = null;
        try { got = await cam.GrabFrameAsync(TimeSpan.FromSeconds(2), cts.Token); }
        catch (Exception ex) { err = ex; }
        var what = got is null ? $"{err?.GetType().Name ?? "null"}" : ReferenceEquals(got, cam.Foreign) ? "the foreign frame" : ReferenceEquals(got, cam.Own) ? "its own frame" : "some other frame";
        if (row == "plain")
            Check(ReferenceEquals(got, cam.Own) && cam.GrabCalls == 1,
                $"{row}: the call returns the frame its GrabOne published, not the one that arrived when it subscribed (got {what}, GrabOne calls={cam.GrabCalls})");
        else
            Check(err is OperationCanceledException && cam.GrabCalls == 0,
                $"{row}: a call cancelled before its GrabOne ran ends as cancelled — it never ran a grab, so no frame can be its answer " +
                $"(got {what}, GrabOne calls={cam.GrabCalls})");
    }
}
