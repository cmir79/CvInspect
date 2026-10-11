using System.Reflection;
using System.Runtime.CompilerServices;
using CvInspect.Imaging;
using CvInspect.Imaging.Gev;
using Xunit;

namespace CvInspect.Tests;

/// <summary>
/// GevCam 의 닫기·해제 통지가 <see cref="ICam.GrabbingChanged"/> 의 "전이일 때만 낸다" 를 지키는지 본다.
///
/// 장치 없이 여는 길이 없어(열기는 탐색 방송부터 한다) <b>열린 세션의 모양만</b> 리플렉션으로 세운다 — 장치 참조와 연결 표시,
/// 도는 취득이면 펌프 참조까지. 장치 참조는 생성자를 거치지 않은 빈 껍데기다. 닫기의 장치 정리(<c>CloseCoreAsync</c>)는 단계마다
/// 그 껍데기에서 실패하는데, 단계가 전부 실패를 삼키는 자리라 닫기 자체는 끝까지 간다(노드 맵이 없어 AcquisitionStop 은 건너뛰고,
/// 전송 잠금 해제·장치 버리기는 빈 필드에서 던져 Debug 한 줄씩 남는다). 이 시험이 보는 것은 그 뒤의 상태와 통지뿐이다 —
/// 장치 정리가 실제로 되는지는 장비가 있어야 본다.
///
/// 살아있음: "꺼짐 0건" 만으로는 고쳐진 것과 닫기가 아무것도 안 한 것(세움이 안 먹어 "열린 적 없음" 으로 돌아간 것)이 갈리지 않는다.
/// 그래서 같은 닫기가 <b>연결 끊김을 한 번 냈고 장치 참조를 걷었는지</b>를 함께 보고, 도는 취득을 닫는 양성 대조가 같은 세움으로
/// 꺼짐을 정확히 한 번 내는지 본다. 통지는 전부 부른 스레드에서 동기로 나가는 길이라(닫기·해제·정지) 기록에 락이 필요 없다.
/// </summary>
public class GevCamNotifyTests : IDisposable
{
    /// <summary>단언 하나 — 실패 메시지가 곧 그 회귀가 존재하는 이유다.</summary>
    private static void Check(bool cond, string name) => Assert.True(cond, name);

    // GevCam 생성은 프로세스 전역 상태를 바꾼다 — 취득 라이브러리 로그 창구가 비어 있으면 꽂고 "꽂았다" 를 기억한다(GevLogBridge).
    // 그 자동 연결을 시험하는 항(SmokeTests 11-0)은 그 생성이 프로세스의 첫 GevCam 이라고 전제해서, 이 묶음이 먼저 돌자 그 항이
    // 빨개졌다(실측: 전체 스위트에서 이 묶음이 앞 순서). 그래서 시험마다 그 두 값을 들어올 때로 되돌린다.
    private static readonly FieldInfo BridgeAttached =
        typeof(GevLogBridge).GetField("_attached", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _bridgeAttachedBefore = BridgeAttached.GetValue(null);
    private readonly Action<GevSharp.GevLogLevel, string, string, Exception?>? _gevSinkBefore = GevSharp.GevLog.Sink;

    public void Dispose()
    {
        GevSharp.GevLog.Sink = _gevSinkBefore;
        BridgeAttached.SetValue(null, _bridgeAttachedBefore);
    }

    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly FieldInfo DevField = typeof(GevCam).GetField("_dev", Hidden)!;
    private static readonly FieldInfo PumpField = typeof(GevCam).GetField("_pump", Hidden)!;

    /// <summary>장치 없이 "열린 세션" 을 세운다. <paramref name="grabbing"/> 이면 펌프 참조도 세운다 — 이미 끝난 스레드라
    /// 정지 경로의 Join 이 곧바로 돌아온다(시작하지 않은 스레드를 넣으면 Join 이 던진다).</summary>
    private static GevCam OpenWithoutDevice(bool grabbing)
    {
        var cam = new GevCam(new CamOpt { Name = "notify", SerialNumber = "x" });
        DevField.SetValue(cam, RuntimeHelpers.GetUninitializedObject(typeof(GevSharp.GevDevice)));
        typeof(GevCam).GetProperty(nameof(GevCam.IsConnected))!.SetValue(cam, true);
        if (grabbing)
        {
            var pump = new Thread(() => { }) { IsBackground = true };
            pump.Start();
            pump.Join();
            PumpField.SetValue(cam, pump);
        }
        // 세움이 먹었는지 — 안 먹으면 아래 닫기는 "열린 적 없음" 으로 아무것도 안 하고, 0 건 단언이 거저 통과한다.
        Check(cam.IsConnected && cam.IsGrabbing == grabbing,
            $"the fake open session took (connected={cam.IsConnected}, grabbing={cam.IsGrabbing})");
        return cam;
    }

    /// <summary>통지를 순서대로 적는다. 취득 통지에는 그 순간의 상태를 붙인다 — 상태를 먼저 내리고 알리는지 본다.</summary>
    private static List<string> Record(GevCam cam)
    {
        var log = new List<string>();
        cam.GrabbingChanged += (s, on) =>
        {
            var c = (GevCam)s!;
            log.Add($"grab:{on} (IsGrabbing={c.IsGrabbing}, IsConnected={c.IsConnected})");
        };
        cam.ConnectionChanged += (_, e) => log.Add($"conn:{e.IsConnected}");
        return log;
    }

    private static int Stops(List<string> log) => log.Count(l => l.StartsWith("grab:False", StringComparison.Ordinal));
    private static int Disconnects(List<string> log) => log.Count(l => l == "conn:False");
    private static string Show(List<string> log) => log.Count == 0 ? "(none)" : string.Join(" | ", log);

    /// <summary>닫힌 뒤의 판정이 바로 그 닫기에서 왔다는 표식 — 끊김을 한 번 냈고, 연결 표시가 내려갔고, 장치 참조가 걷혔다.</summary>
    private static void CheckTornDown(GevCam cam, List<string> log, string what)
        => Check(Disconnects(log) == 1 && !cam.IsConnected && !cam.IsGrabbing && DevField.GetValue(cam) is null,
            $"liveness: {what} ran its teardown and reported the disconnect once ({Show(log)})");

    private const string ClosedWhileGrabbing = "grab:False (IsGrabbing=False, IsConnected=False)";

    /// <summary>10-G-17) 그랩하지 않던 카메라를 닫으면 꺼짐을 알리지 않는다. 닫기가 꺼짐을 <b>무조건</b> 내서, 돌고 있지 않던 것이 멈췄다고
    /// 알렸다 — 통지를 거울질하는 화면은 닫을 때마다 "라이브 정지" 를 한 번 더 찍었다(ICam.GrabbingChanged: 전이일 때만 낸다).</summary>
    [Fact]
    public void GevCamCloseReportsNoStopWhenNothingWasGrabbing()
    {
        using var cam = OpenWithoutDevice(grabbing: false);
        var log = Record(cam);
        cam.Close();
        CheckTornDown(cam, log, "Close");
        Check(Stops(log) == 0,
            $"closing a camera that was not grabbing reports no stop — GrabbingChanged fires on transitions only, and a mirroring " +
            $"UI would log a phantom 'live stopped' on every close ({Show(log)})");
    }

    /// <summary>10-G-18) 라이브를 멈추고 닫으면 꺼짐은 한 번이다 — 화면의 가장 흔한 순서다. 닫기가 정지가 이미 낸 꺼짐을 한 번 더 냈다.</summary>
    [Fact]
    public void GevCamStopThenCloseReportsTheStopOnce()
    {
        using var cam = OpenWithoutDevice(grabbing: true);
        var log = Record(cam);
        cam.StopContinuous();
        var afterStop = log.ToList();
        cam.Close();
        Check(afterStop.Count == 1 && afterStop[0].StartsWith("grab:False", StringComparison.Ordinal),
            $"liveness: the fake pump was real — StopContinuous reported the stop ({Show(afterStop)})");
        CheckTornDown(cam, log, "Close");
        Check(Stops(log) == 1,
            $"stopping live and then closing reports the stop once — the close must not repeat a stop that was already reported ({Show(log)})");
    }

    /// <summary>10-G-19) 양성 대조 — 그랩하던 카메라를 닫으면 꺼짐을 정확히 한 번, 끊김보다 먼저, 상태를 내린 뒤에 낸다.
    /// 이것이 안 나오면 위 두 항의 "0 건·한 번" 은 고친 것이 아니라 통지를 통째로 지운 것이다.</summary>
    [Fact]
    public void GevCamCloseWhileGrabbingReportsTheStop()
    {
        using var cam = OpenWithoutDevice(grabbing: true);
        var log = Record(cam);
        cam.Close();
        Check(log.SequenceEqual(new[] { ClosedWhileGrabbing, "conn:False" }),
            $"closing a grabbing camera reports one stop, before the disconnect, with both states already lowered ({Show(log)})");
    }

    /// <summary>10-G-20) 해제도 닫기와 같은 규칙이다 — 해제는 닫기와 같은 통지 자리를 쓴다. 그랩하지 않던 것은 꺼짐 없음, 그랩하던 것은 한 번(양성 대조).</summary>
    [Fact]
    public void GevCamDisposeReportsAStopOnlyIfGrabbing()
    {
        var idle = OpenWithoutDevice(grabbing: false);
        var idleLog = Record(idle);
        idle.Dispose();

        var live = OpenWithoutDevice(grabbing: true);
        var liveLog = Record(live);
        live.Dispose();

        Check(liveLog.SequenceEqual(new[] { ClosedWhileGrabbing, "conn:False" }),
            $"positive control: disposing a grabbing camera reports one stop, before the disconnect ({Show(liveLog)})");
        CheckTornDown(idle, idleLog, "Dispose");
        Check(Stops(idleLog) == 0,
            $"disposing a camera that was not grabbing reports no stop — the same transition-only rule as Close ({Show(idleLog)})");
    }
}
