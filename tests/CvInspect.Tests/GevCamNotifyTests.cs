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
/// 꺼짐을 정확히 한 번 내는지 본다. 통지는 대개 부른 스레드에서 동기로 나가지만(닫기·해제·정지·제어 상실을 직접 부른 것),
/// 펌프가 스스로 끝나는 길은 펌프 스레드에서 오므로 기록은 락 아래에서 한다.
///
/// 장치 명령이 무엇으로 끝나는지를 봐야 하는 항(10-G-21~23·25)은 노드 맵을 하나 더 세운다 — 명령 노드 몇 개를 파싱하고, 그 포트가
/// 쓰기마다 정해진 예외를 던진다(<see cref="ThrowingPort"/>). 장치 계층이 실제로 그 예외를 내는지는 그 라이브러리 원문으로 확인했다
/// (0.5.0 디컴파일: 해제된 장치의 접근은 <c>ThrowIfClosed</c> 가 <see cref="ObjectDisposedException"/>, 제어를 잃은 장치는
/// <c>GevControlLostException</c>, 닫힌 제어 채널의 요청은 <see cref="ObjectDisposedException"/>). 여기서는 그 결과만 흉내낸다.
/// </summary>
public class GevCamNotifyTests : IDisposable
{
    /// <summary>단언 하나 — 실패 메시지가 곧 그 회귀가 존재하는 이유다.</summary>
    private static void Check(bool cond, string name) => Assert.True(cond, name);

    // GevCam 생성은 프로세스 전역 상태를 바꾼다 — 취득 라이브러리 로그 창구가 비어 있으면 꽂고 "꽂았다" 를 기억한다(GevLogBridge).
    // 그 자동 연결을 시험하는 항(SmokeTests 11-0)이 그 생성을 프로세스의 첫 GevCam 이라고 전제하던 때, 이 묶음이 먼저 돌자 그 항이
    // 빨개졌다(실측: 전체 스위트에서 이 묶음이 앞 순서). 지금은 그 항이 시작할 때 표식을 스스로 비운다. 여기서 두 값을 들어올 때로
    // 되돌리는 것은 남겨 둔다 — 전역 상태를 건드린 시험이 제 뒤를 치우는 것은 순서 운을 만들지 않는 기본값이다.
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
    private static readonly FieldInfo NodesField = typeof(GevCam).GetField("_nodes", Hidden)!;
    private static readonly FieldInfo StreamField = typeof(GevCam).GetField("_stream", Hidden)!;
    private static readonly MethodInfo OnControlLost = typeof(GevCam).GetMethod("OnControlLost", Hidden)!;

    /// <summary>장치 없이 "열린 세션" 을 세운다. <paramref name="grabbing"/> 이면 펌프 참조도 세운다 — 이미 끝난 스레드라
    /// 정지 경로의 Join 이 곧바로 돌아온다(시작하지 않은 스레드를 넣으면 Join 이 던진다). <paramref name="nodes"/> 를 주면 장치 명령이
    /// 그 노드 맵으로 나간다 — 안 주면 노드 맵이 없어 명령 단계를 건너뛴다.</summary>
    private static GevCam OpenWithoutDevice(bool grabbing, GevSharp.GenApi.GenApiNodeMap? nodes = null, GevCamOpt? gev = null)
    {
        var cam = new GevCam(new CamOpt { Name = "notify", SerialNumber = "x" }, gev);
        DevField.SetValue(cam, RuntimeHelpers.GetUninitializedObject(typeof(GevSharp.GevDevice)));
        typeof(GevCam).GetProperty(nameof(GevCam.IsConnected))!.SetValue(cam, true);
        if (nodes is not null) NodesField.SetValue(cam, nodes);
        if (grabbing) PumpField.SetValue(cam, FinishedThread());
        // 세움이 먹었는지 — 안 먹으면 아래 닫기는 "열린 적 없음" 으로 아무것도 안 하고, 0 건 단언이 거저 통과한다.
        Check(cam.IsConnected && cam.IsGrabbing == grabbing,
            $"the fake open session took (connected={cam.IsConnected}, grabbing={cam.IsGrabbing})");
        return cam;
    }

    /// <summary>펌프 참조로 세울 스레드 — 이미 끝나 있어 정지 경로의 Join 이 곧바로 돌아온다.</summary>
    private static Thread FinishedThread()
    {
        var pump = new Thread(() => { }) { IsBackground = true };
        pump.Start();
        pump.Join();
        return pump;
    }

    /// <summary>통지를 순서대로 적는다. 취득 통지에는 그 순간의 상태를 붙인다 — 상태를 먼저 내리고 알리는지 본다.
    /// 락 아래에서 적는다 — 펌프가 스스로 끝나는 길은 펌프 스레드에서 알린다.</summary>
    private static List<string> Record(GevCam cam)
    {
        var log = new List<string>();
        cam.GrabbingChanged += (s, on) =>
        {
            var c = (GevCam)s!;
            lock (log) log.Add($"grab:{on} (IsGrabbing={c.IsGrabbing}, IsConnected={c.IsConnected})");
        };
        cam.ConnectionChanged += (_, e) => { lock (log) log.Add($"conn:{e.IsConnected}"); };
        return log;
    }

    private static List<string> Snap(List<string> log) { lock (log) return log.ToList(); }

    /// <summary>시험이 끝난 카메라를 버린다 — 노드 맵을 먼저 걷어 버리기의 장치 명령이 시험용 포트에 닿지 않게 한다. 안 걷으면
    /// 버리기가 같은 예외를 다시 받아, 앞선 단언 실패를 그 예외가 덮는다(실패 문구가 사라진다).</summary>
    private static void Discard(GevCam cam)
    {
        NodesField.SetValue(cam, null);
        cam.Dispose();
    }

    /// <summary>쓰기마다 정해진 예외를 던지는 포트 — 장치에 닿는 명령이 무엇으로 끝나는지만 흉내낸다. 읽기는 0 으로 답한다
    /// (명령 실행 앞의 접근 판정이 읽기를 하더라도 거기서 다른 갈래로 새지 않게). 쓰기를 세어 "명령이 장치까지 갔다" 를 단언한다.</summary>
    private sealed class ThrowingPort : GevSharp.IGevPort
    {
        private readonly Func<Exception> _onWrite;
        public int Writes;

        public ThrowingPort(Func<Exception> onWrite) => _onWrite = onWrite;

        public ValueTask ReadAsync(ulong address, Memory<byte> buffer, CancellationToken ct = default)
        {
            buffer.Span.Clear();
            return default;
        }

        public ValueTask WriteAsync(ulong address, ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Writes);
            throw _onWrite();
        }
    }

    /// <summary>명령 노드만 든 노드 맵 — 명령마다 쓰기 전용 레지스터 하나. 그 밖의 노드(AcquisitionMode 등)는 없다.</summary>
    private static GevSharp.GenApi.GenApiNodeMap CommandMap(GevSharp.IGevPort port, params string[] commands)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>")
          .Append("<RegisterDescription ModelName=\"NotifyFixture\" VendorName=\"CvInspect\" ToolTip=\"t\" StandardNameSpace=\"GEV\" ")
          .Append("SchemaMajorVersion=\"1\" SchemaMinorVersion=\"1\" SchemaSubMinorVersion=\"0\" MajorVersion=\"1\" MinorVersion=\"0\" SubMinorVersion=\"0\" ")
          .Append("ProductGuid=\"0f0e0d0c-0b0a-4908-8706-050403020100\" VersionGuid=\"1f1e1d1c-1b1a-4918-8716-151413121110\" ")
          .Append("xmlns=\"http://www.genicam.org/GenApi/Version_1_1\">")
          .Append("<Category Name=\"Root\" NameSpace=\"Standard\">");
        foreach (var c in commands) sb.Append("<pFeature>").Append(c).Append("</pFeature>");
        sb.Append("</Category>");
        for (var i = 0; i < commands.Length; i++)
        {
            var c = commands[i];
            sb.Append("<Command Name=\"").Append(c).Append("\" NameSpace=\"Standard\"><pValue>").Append(c)
              .Append("Reg</pValue><CommandValue>1</CommandValue></Command>")
              .Append("<IntReg Name=\"").Append(c).Append("Reg\"><Address>0x").Append((0x100 + 4 * i).ToString("X"))
              .Append("</Address><Length>4</Length><AccessMode>WO</AccessMode><pPort>Device</pPort>")
              .Append("<Cachable>NoCache</Cachable><Sign>Unsigned</Sign><Endianess>BigEndian</Endianess></IntReg>");
        }
        sb.Append("<Port Name=\"Device\"/></RegisterDescription>");
        return GevSharp.GenApi.GenApiNodeMap.Parse(sb.ToString(), port);
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

    /// <summary>10-G-21) 정지의 AcquisitionStop 이 장치 계층에서 <see cref="ObjectDisposedException"/> 으로 끝나도 정지는 던지지 않고
    /// 꺼짐을 한 번 낸다. 장치 명령 자리가 GevException 계열만 잡아서, 그 예외가 펌프를 세운 뒤에 빠져나갔다 — IsGrabbing=false 인데
    /// 마지막 통지는 true 였다. 장치 계층의 오류 계약이 그 예외를 든다(해제된 장치의 접근, 닫기와 겹쳐 채널에 늦게 닿은 요청).</summary>
    [Fact]
    public void GevCamStopReportsTheStopWhenTheDeviceAnswersDisposed()
    {
        var port = new ThrowingPort(() => new ObjectDisposedException("GevDevice"));
        var cam = OpenWithoutDevice(grabbing: true, CommandMap(port, "AcquisitionStop"));
        try
        {
            var log = Record(cam);
            Exception? thrown = null;
            try { cam.StopContinuous(); } catch (Exception ex) { thrown = ex; }
            Check(port.Writes == 1,
                $"liveness: StopContinuous sent AcquisitionStop to the device and the device answered ObjectDisposedException (writes={port.Writes})");
            Check(thrown is null,
                "ObjectDisposedException is in the device layer's error contract, so StopContinuous takes it like a control loss instead of " +
                $"throwing after the pump already stopped (threw {thrown?.GetType().Name ?? "nothing"}, notifications: {Show(log)})");
            Check(Stops(log) == 1 && !cam.IsGrabbing,
                $"the stop that already happened is reported once — IsGrabbing is false, so the last notification must be false too ({Show(log)})");
        }
        finally { Discard(cam); }
    }

    /// <summary>10-G-22) 닫기의 AcquisitionStop 이 <see cref="ObjectDisposedException"/> 으로 끝나도 닫기는 끝까지 간다 — "각 단계는 실패해도
    /// 다음을 막지 않는다". 그 예외가 정리 도중에 빠져나가, 장치 참조는 이미 걷혔는데 연결 표시는 참으로 남고 끊김도 안 나갔다.
    /// 그 상태에서는 다시 열기도 "끊긴 세션 접기" 를 타지 않는다(장치 참조가 없다).</summary>
    [Fact]
    public void GevCamCloseCompletesWhenTheDeviceAnswersDisposed()
    {
        var port = new ThrowingPort(() => new ObjectDisposedException("GevDevice"));
        var cam = OpenWithoutDevice(grabbing: false, CommandMap(port, "AcquisitionStop"));
        try
        {
            var log = Record(cam);
            Exception? thrown = null;
            try { cam.Close(); } catch (Exception ex) { thrown = ex; }
            Check(port.Writes == 1,
                $"liveness: Close sent AcquisitionStop to the device and the device answered ObjectDisposedException (writes={port.Writes})");
            Check(thrown is null,
                "a teardown step that fails with an error from the device layer's contract does not abort the close " +
                $"(threw {thrown?.GetType().Name ?? "nothing"}, IsConnected={cam.IsConnected}, notifications: {Show(log)})");
            CheckTornDown(cam, log, "Close");
        }
        finally { Discard(cam); }
    }

    /// <summary>10-G-23) 보험 — 펌프를 세운 뒤의 장치 단계가 계약 밖의 예외로 끝나도 꺼짐은 나가고, 예외는 그대로 올라간다.
    /// 실제로 그런 예외를 본 적은 없다(장치 계층의 계약 안 예외는 명령 자리가 남기고 삼킨다 — 10-G-21). 10-G-21 의 결함이 "명령
    /// 자리가 잡는 종류 하나가 빠지자 꺼짐이 통째로 사라진 것" 이라, 종류를 하나 더 빠뜨려도 통지가 안 빠지게 정지를 finally 로 묶었다.
    /// 이 항은 그 finally 를 못 박는다 — 걷으면 빨개진다.</summary>
    [Fact]
    public void GevCamStopReportsTheStopEvenIfALaterStepThrowsUnexpectedly()
    {
        var port = new ThrowingPort(() => new InvalidOperationException("fake port failure outside the device layer's error contract"));
        var cam = OpenWithoutDevice(grabbing: true, CommandMap(port, "AcquisitionStop"));
        try
        {
            var log = Record(cam);
            Exception? thrown = null;
            try { cam.StopContinuous(); } catch (Exception ex) { thrown = ex; }
            Check(port.Writes == 1 && thrown is InvalidOperationException { Message: "fake port failure outside the device layer's error contract" },
                $"liveness: the unexpected failure came from the AcquisitionStop write and was not swallowed (writes={port.Writes}, " +
                $"threw {thrown?.GetType().Name ?? "nothing"}: {thrown?.Message})");
            Check(Stops(log) == 1 && !cam.IsGrabbing,
                "once the pump is stopped the stop is reported even if a later device step throws — otherwise IsGrabbing is false while " +
                $"the last notification says true ({Show(log)})");
        }
        finally { Discard(cam); }
    }

    /// <summary>10-G-24) 제어를 잃은 뒤 닫거나 버리면 끊김을 다시 내지 않는다 — 끊김은 제어 상실이 이미 냈다. 닫기가 끊김을 무조건 내서
    /// 연결이 false 로 두 번 떨어졌다(ICam 수명 계약: 이미 그 상태면 이벤트 없이). 꺼짐도 상실이 낸 한 번뿐이다.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void GevCamCloseAfterControlLossReportsEachEdgeOnce(bool grabbing, bool dispose)
    {
        var cam = OpenWithoutDevice(grabbing);
        var dev = DevField.GetValue(cam);
        var log = Record(cam);
        OnControlLost.Invoke(cam, new object?[] { dev, null });
        var afterLoss = Snap(log);
        // 상실이 실제로 먹었는가 — 끊김(과 도는 취득이면 꺼짐)을 냈고, 죽은 세션의 장치 참조는 닫기가 접도록 남아 있다.
        Check(Disconnects(afterLoss) == 1 && Stops(afterLoss) == (grabbing ? 1 : 0) && !cam.IsConnected && DevField.GetValue(cam) is not null,
            $"liveness: the control loss was taken and left the dead session for the close to fold ({Show(afterLoss)})");

        if (dispose) cam.Dispose(); else cam.Close();
        var what = dispose ? "Dispose" : "Close";
        Check(DevField.GetValue(cam) is null, $"liveness: {what} tore down the dead session");
        Check(Disconnects(log) == 1,
            $"{what} after a control loss does not report the disconnect again — the loss already did, and a second false edge " +
            $"reads as a second drop ({Show(log)})");
        Check(Stops(log) == (grabbing ? 1 : 0),
            $"{what} after a control loss does not repeat the stop the loss already reported ({Show(log)})");
        if (!dispose) cam.Dispose();
    }

    /// <summary>10-G-26) 제어 상실 때 꺼짐 구독자가 던져도 끊김은 나간다. 상실이 두 통지를 감싸지 않고 차례로 내서, 꺼짐 구독자 하나가 던지면
    /// 끊김이 아예 안 나갔다. 전에는 뒤이은 닫기가 끊김을 무조건 내서 한 번은 나갔지만, 닫기가 "이미 끊김" 이면 내지 않게 되자(10-G-24)
    /// 그 받침이 사라져 끊김이 끝내 0번이 됐다 — 통지로 연결 표시를 거울질하는 화면은 다시 열 때까지 "연결됨" 에 머문다.</summary>
    [Fact]
    public void GevCamControlLossReportsTheDisconnectEvenIfAStopSubscriberThrows()
    {
        var cam = OpenWithoutDevice(grabbing: true);
        var dev = DevField.GetValue(cam);
        var log = Record(cam);
        var thrown = 0;
        cam.GrabbingChanged += (_, on) =>
        {
            if (on) return;
            Interlocked.Increment(ref thrown);
            throw new InvalidOperationException("fake: a GrabbingChanged subscriber failed");
        };
        Exception? escaped = null;
        try { OnControlLost.Invoke(cam, new object?[] { dev, null }); }
        catch (TargetInvocationException tie) { escaped = tie.InnerException; }
        var afterLoss = Snap(log);
        Check(thrown == 1 && Stops(afterLoss) == 1 && !cam.IsConnected,
            $"liveness: the loss was taken and the throwing stop subscriber ran once (thrown={thrown}, notifications: {Show(afterLoss)})");
        Check(escaped is null,
            $"a subscriber's exception does not escape the control-loss notification (escaped {escaped?.GetType().Name ?? "nothing"})");
        Check(Disconnects(afterLoss) == 1,
            "the disconnect is reported even if a stop subscriber throws — the close no longer repeats it, so skipping it here leaves " +
            $"mirrors showing 'connected' until the next open ({Show(afterLoss)})");
        cam.Close();
        Check(Disconnects(log) == 1 && DevField.GetValue(cam) is null,
            $"the close after the loss tears down the dead session and does not report the disconnect again ({Show(log)})");
        cam.Dispose();
    }

    /// <summary>10-G-25) 제어를 잃은 뒤 다시 열 때 접는 죽은 세션에 펌프가 돌고 있으면 꺼짐을 낸다. 접기가 "펌프는 제어 상실이 이미
    /// 접었다" 고 보고 꺼짐을 버렸는데, 상실 뒤에 불린 시작이 펌프를 새로 세우는 갈래가 있다 — 앞 절이 그 갈래를 실제로 돌린다
    /// (AcquisitionMode 노드가 없어 모드 쓰기가 장치에 안 닿고, AcquisitionStart 의 제어 상실 예외는 명령 자리가 삼킨다).
    /// 그 펌프를 접으며 꺼짐을 빼면 IsGrabbing=false 인데 마지막 통지는 true 로 남는다 — 열기가 실패해도 마찬가지다.</summary>
    [Fact]
    public void GevCamReopenAfterControlLossReportsTheStopOfAPumpStartedAfterTheLoss()
    {
        // 앞 절(전제) — 상실 뒤의 시작이 던지지 않고 켜짐을 알린다. 스트림은 빈 껍데기라 펌프가 곧 스스로 끝나므로(스트림 닫힘으로
        // 읽는다) 순서가 아니라 "켜짐을 알렸다" 만 본다.
        var lostPort = new ThrowingPort(() => new GevSharp.GevControlLostException("fake: control of the camera was lost"));
        var premise = OpenWithoutDevice(grabbing: false, CommandMap(lostPort, "AcquisitionStart"));
        StreamField.SetValue(premise, RuntimeHelpers.GetUninitializedObject(typeof(GevSharp.GevStream)));
        var premiseLog = Record(premise);
        OnControlLost.Invoke(premise, new object?[] { DevField.GetValue(premise), null });
        var lostBeforeStart = !premise.IsConnected;
        Exception? startThrew = null;
        try { premise.StartContinuous(); } catch (Exception ex) { startThrew = ex; }
        var afterStart = Snap(premiseLog);
        Discard(premise);
        Check(lostBeforeStart && lostPort.Writes == 1 && startThrew is null && afterStart.Any(l => l.StartsWith("grab:True", StringComparison.Ordinal)),
            "premise: after a control loss, StartContinuous on a camera without an AcquisitionMode node reaches the dead device, swallows " +
            $"its control-loss error and reports the grab on — so a pump can be running when Open folds the dead session " +
            $"(writes={lostPort.Writes}, threw {startThrew?.GetType().Name ?? "nothing"}, notifications: {Show(afterStart)})");

        // 본 절 — 그 상태(제어 상실 + 펌프)에서 다시 연다. 탐색 시한 0 은 탐색 방송을 보내기 전에 인자 검사로 던진다 — 망을 건드리지
        // 않고 "접은 뒤 열기를 시도했다" 를 확인하는 표식이다.
        var cam = OpenWithoutDevice(grabbing: false, gev: new GevCamOpt { DiscoveryTimeoutMs = 0 });
        var log = Record(cam);
        OnControlLost.Invoke(cam, new object?[] { DevField.GetValue(cam), null });
        PumpField.SetValue(cam, FinishedThread());   // 상실 뒤 시작이 세운 펌프 — 앞 절이 그 갈래를 돌렸다
        Check(!cam.IsConnected && cam.IsGrabbing && Disconnects(Snap(log)) == 1,
            $"liveness: the dead session with a pump started after the loss took ({Show(Snap(log))})");

        Exception? openThrew = null;
        try { cam.Open(); } catch (Exception ex) { openThrew = ex; }
        Check(openThrew is ArgumentOutOfRangeException && DevField.GetValue(cam) is null,
            $"liveness: Open folded the dead session and then tried to reopen (threw {openThrew?.GetType().Name ?? "nothing"}: {openThrew?.Message})");
        Check(Stops(log) == 1 && log.Single(l => l.StartsWith("grab:False", StringComparison.Ordinal)).Contains("IsGrabbing=False"),
            "folding a dead session whose pump was running reports the stop once, even when the reopen fails — the pump is gone, so a " +
            $"last notification of true would leave mirrors showing live forever ({Show(log)})");
        Check(Disconnects(log) == 1 && !log.Contains("conn:True"),
            $"the fold does not report the disconnect again (the loss did) and a failed reopen reports no connect ({Show(log)})");
        cam.Dispose();
    }
}
