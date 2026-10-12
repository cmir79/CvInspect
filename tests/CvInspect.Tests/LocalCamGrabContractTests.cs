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
    /// (VideoCaptureCam 은 표식을 세운 뒤 오프라인으로 던지게 할 길이 없다 — 같은 try/finally 모양을 독해로만 확인한다.)</summary>
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
}
