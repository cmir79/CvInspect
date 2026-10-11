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
                        if (window != "publish" && Interlocked.Increment(ref _dirCalls) == 1) _gate.Wait(5000);
                        return null;
                    },
                };
            }
            else Cam = MakeVideoCam(clip!);
            Cam.FrameAcquired += (_, _) =>
            {
                Interlocked.Increment(ref _frames);
                if (window == "publish" && Interlocked.Exchange(ref _handlerEntered, 1) == 0) _gate.Wait(5000);
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
        /// <summary>붙잡은 그랩이 아직 안 돌아왔는가 — 게이트가 서 있는 동안 참이어야 겹침이 걸린 것이다.</summary>
        public bool GrabStillPending => _grabber.IsAlive && !_gate.IsSet;

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
    /// 라이브를 켰다 — 바깥 그랩이 아직 안 돌아왔는데 그 구독에 다른 장이 들어간다.</summary>
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
        Exception? innerEx = null;
        cam.FrameAcquired += (_, _) =>
        {
            if (Interlocked.Increment(ref frames) != 1) return;
            reentered = true;
            try { if (call == "GrabOne") cam.GrabOne(); else cam.StartContinuous(); }
            catch (Exception ex) { innerEx = ex; }
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
}
