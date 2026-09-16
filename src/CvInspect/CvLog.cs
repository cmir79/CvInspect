namespace CvInspect;

/// <summary>라이브러리 진단 로그 수준.</summary>
public enum CvLogLevel
{
    /// <summary>가장 상세한 추적.</summary>
    Trace,

    /// <summary>개발 진단.</summary>
    Debug,

    /// <summary>정상 동작 알림.</summary>
    Info,

    /// <summary>이상 징후 — 동작은 계속된다.</summary>
    Warning,

    /// <summary>실패 — 해당 작업이 수행되지 못했다.</summary>
    Error,
}

/// <summary>
/// 라이브러리 진단 로그의 단일 유출구 — 호스트가 <see cref="Sink"/> 를 연결하면 그쪽으로 흐른다
/// (라이브러리는 스스로 콘솔/파일에 쓰지 않는다).
///
/// <b>싱크가 없는 동안의 줄은 조용히 사라지지 않는다.</b> 마지막 <see cref="HoldCapacity"/> 줄을 붙잡아 두었다가
/// 싱크가 붙는 순간 "붙잡아 둔 줄을 재생한다" 는 안내 한 줄 뒤에 순서대로 흘려보내고, 자리가 없어 밀려난 줄은
/// <see cref="DroppedCount"/> 에 센다. 미연결 구간마다 첫 줄이 붙잡히는 순간 한 번, 진단 추적
/// (<see cref="System.Diagnostics.Trace"/>)에 경고를 남긴다.
///
/// 한 소비자가 싱크를 배선하지 않은 채 이틀을 돌았고, 그 사이 제어 상실 사유·프레임 유실·무시된 조작 경고가 전부
/// 사라졌다 — 취득 계층의 다리가 여기까지는 이어져 있어 연결된 것처럼 보였다. 배선은 여전히 호스트 몫이지만,
/// <b>안 했다는 사실이 보이게</b> 한다. 기동 검사에서 <see cref="IsAttached"/> 를 단언한다.
/// </summary>
public static class CvLog
{
    /// <summary>싱크가 없는 동안 붙잡아 두는 줄 수. 넘치면 오래된 것부터 밀려나 <see cref="DroppedCount"/> 에 세어진다.</summary>
    public const int HoldCapacity = 64;

    private static readonly object _gate = new();
    private static Action<CvLogLevel, string, string, Exception?>? _sink;
    private static readonly Queue<(CvLogLevel Level, string Source, string Message, Exception? Exception)> _held = new();
    private static long _dropped;        // 누계 — DroppedCount
    private static long _heldDropped;    // 지금 미연결 구간에서 밀려난 수 — 재생 안내 줄에 적는다
    private static bool _noticed;        // 지금 미연결 구간에서 진단 추적 경고를 냈는가

    /// <summary>(level, source, message, exception) 수신 델리게이트 — 호스트 로거 어댑터 연결 지점.
    /// 붙이는 순간 붙잡아 둔 줄이 안내 한 줄 뒤에 이 델리게이트로 순서대로 흘러간다(부른 스레드에서, 락 밖에서).
    /// 그래서 <b>호스트 로거가 받을 준비가 된 뒤에</b> 붙인다 — 재생분은 붙이는 순간 그 로거로 가므로, 로거가 아직
    /// 못 받으면 거기서 사라지고 아무도 세지 않는다. 늦게 붙이는 쪽은 잃지 않는다: 그 사이의 줄이 여기 붙잡혀 있다.
    /// <b>재생 중 싱크가 던져도 그 예외는 이 대입문 밖으로 나가지 않는다</b> — 아직 못 보낸 줄은 도로 붙잡혀
    /// 다음 싱크를 기다린다. 아직 받을 준비가 안 된 로거에 먼저 붙인 경우가 그것이라, 그 사정으로 기동이 깨지지도
    /// 진단이 사라지지도 않게 한다.</summary>
    public static Action<CvLogLevel, string, string, Exception?>? Sink
    {
        get { lock (_gate) return _sink; }
        set
        {
            (CvLogLevel Level, string Source, string Message, Exception? Exception)[] replay;
            long dropped;
            lock (_gate)
            {
                _sink = value;
                if (value is null) return;
                _noticed = false;                 // 다시 떼이면 그 구간의 첫 줄이 새로 한 번 알린다
                dropped = _heldDropped;
                _heldDropped = 0;
                if (_held.Count == 0) return;
                replay = _held.ToArray();
                _held.Clear();
            }
            // 락 밖에서 부른다 — 호스트 싱크가 이 안에서 다시 Publish 해도 맞물리지 않는다. 안내 줄이 앞서므로
            // 호스트 로거가 찍는 시각이 지금이어도 재생분이 "지금 난 일" 로 읽히지 않는다.
            var sent = 0;
            try
            {
                value(CvLogLevel.Info, nameof(CvLog),
                    $"replaying {replay.Length} lines held before a sink was attached" +
                    (dropped > 0 ? $"; {dropped} older lines were dropped" : "") + ".", null);
                for (; sent < replay.Length; sent++)
                {
                    var e = replay[sent];
                    value(e.Level, e.Source, e.Message, e.Exception);
                }
            }
            catch (Exception ex)
            {
                // 여기서 잡지 않으면 예외가 프로퍼티 대입문에서 튀어나온다 — 붙이는 쪽에는 try 를 둘 이유가 없던
                // 자리라 기동이 그대로 깨진다. 진단을 지키려던 기능이 진단을 붙이는 행위를 위험하게 만드는 꼴이다.
                // 못 보낸 줄은 버리지 않고 도로 붙잡는다: 이 싱크가 못 받은 것이지 없어도 되는 줄이 아니다.
                // 잡되 삼키지 않는다 — 이 실패는 싱크로 알릴 수 없으므로(던진 쪽이 그 싱크다) 미배선 경고와 같은
                // 통로인 진단 추적으로 낸다.
                lock (_gate)
                    for (var i = sent; i < replay.Length; i++) HoldLocked(replay[i]);
                System.Diagnostics.Trace.TraceWarning(
                    "CvInspect: the CvLog sink threw while replaying held diagnostics (" +
                    ex.GetType().Name + ": " + ex.Message + "). " + (replay.Length - sent) +
                    " lines were put back and will replay when a sink is attached again. " +
                    "Attach the sink after the host logger can receive.");
            }
        }
    }

    /// <summary>싱크가 붙어 있는가. 기동 검사에서 단언할 값이다 — 없으면 이 라이브러리의 진단은 어디에도 남지 않는다.</summary>
    public static bool IsAttached { get { lock (_gate) return _sink is not null; } }

    /// <summary>싱크가 없는 동안 붙잡을 자리도 없어 버려진 줄의 누계. 0 이 아니면 배선이 늦었거나 없었다는 뜻이다.</summary>
    public static long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>로그 한 줄 발행. <see cref="Sink"/> 가 없으면 붙잡아 둔다. message 는 영어로 쓴다.</summary>
    public static void Publish(CvLogLevel level, string source, string message, Exception? exception = null)
    {
        Action<CvLogLevel, string, string, Exception?>? sink;
        var first = false;
        lock (_gate)
        {
            sink = _sink;
            if (sink is null)
            {
                HoldLocked((level, source, message, exception));
                if (!_noticed) { _noticed = true; first = true; }
            }
        }
        if (sink is not null)
        {
            sink(level, source, message, exception);
            return;
        }
        // 미연결 구간의 첫 줄에 한 번 — 이 라이브러리의 로그 자체가 배선을 기다리는 쪽이라 진단 추적으로 낸다.
        if (first)
            System.Diagnostics.Trace.TraceWarning(
                "CvInspect: CvLog.Sink is not set. Library diagnostics are being held (last " + HoldCapacity +
                " lines) and will replay when a sink is attached; older lines are counted in CvLog.DroppedCount. " +
                "Attach a sink at startup and assert CvLog.IsAttached.");
    }

    /// <summary>붙잡아 두기 — 호출자가 <c>_gate</c> 를 잡고 있어야 한다. 한도를 넘으면 오래된 것부터 밀어내고 센다.
    /// 발행 경로와 재생 실패 시 되돌리는 경로가 같은 셈법을 쓰도록 한곳에 둔다.</summary>
    private static void HoldLocked((CvLogLevel Level, string Source, string Message, Exception? Exception) entry)
    {
        if (_held.Count >= HoldCapacity)
        {
            _held.Dequeue();
            Interlocked.Increment(ref _dropped);
            _heldDropped++;
        }
        _held.Enqueue(entry);
    }

    internal static void Warn(string source, string message) => Publish(CvLogLevel.Warning, source, message);
}
