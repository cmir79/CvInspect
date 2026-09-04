namespace CvInspect.Imaging.Gev;

/// <summary>
/// 취득 펌프가 어디에 시간을 쓰는지 주기적으로 한 줄로 남긴다.
///
/// 프레임 이벤트는 <b>펌프 스레드에서 동기로</b> 발화한다. 그래서 구독자가 하나라도 느리면 취득이
/// 통째로 그만큼 막히는데, 호스트가 자기 핸들러 안에서만 재면 <b>그 구간이 안 보인다</b> —
/// "받은 뒤로는 1ms" 라는 측정과 "화면이 밀린다" 는 체감이 동시에 성립한다.
///
/// 두 숫자가 그것을 가른다.
/// <list type="bullet">
/// <item><b>핸들러 점유</b> — 펌프가 호스트 핸들러 안에서 보낸 시간의 비율. 100%에 가까우면
/// 취득 속도를 정하는 것은 카메라가 아니라 호스트다.</item>
/// <item><b>초과 지연</b> — 촬영에서 발행까지 걸린 시간이 <b>그 구간의 최선보다</b> 얼마나 더 걸렸나.
/// 장치 시계와 호스트 시계는 기준점이 달라 차이 자체는 의미가 없지만, 차이의 <b>변화</b>는
/// 프레임이 어딘가에 앉아 있던 시간이다.</item>
/// </list>
/// </summary>
internal sealed class GevPumpStats
{
    /// <summary>이보다 큰 "초과" 는 대기가 아니라 장치 시계가 튄 것이다 — 한 프레임이 10분을 기다릴 수는 없다.</summary>
    private static readonly long ClockJumpGuardTicks = (long)(600.0 * System.Diagnostics.Stopwatch.Frequency);

    private readonly long _intervalTicks;
    private long _windowStart;

    private int _frames;
    private long _handlerTicks;
    private long _handlerMaxTicks;

    private ulong _lastFrameId;
    private int _gaps;
    private long _missed;

    private long _bestOffsetTicks = long.MaxValue;   // 구간 전체에서 관측한 최선(= 지연 0 의 추정)
    private long _excessTicks;
    private long _excessMaxTicks;
    private int _timedFrames;

    private long _waitTicks;
    private long _waitMinTicks = long.MaxValue;
    private int _clockJumps;

    /// <param name="intervalMs">0 이하면 끈다.</param>
    public GevPumpStats(int intervalMs)
    {
        Enabled = intervalMs > 0;
        _intervalTicks = (long)(intervalMs / 1000.0 * Freq);
        _windowStart = Now;
    }

    public bool Enabled { get; }

    private static long Now => System.Diagnostics.Stopwatch.GetTimestamp();
    private static double Freq => System.Diagnostics.Stopwatch.Frequency;

    /// <summary>
    /// 프레임 하나를 반영하고, 구간이 찼으면 남길 줄을 돌려준다(아니면 null).
    /// </summary>
    /// <param name="handlerTicks">이 프레임의 이벤트 발화에 걸린 시간.</param>
    /// <param name="deliveredAt">발행 시각(<see cref="Now"/> 기준).</param>
    /// <param name="capture">장치가 찍은 촬영 시각. 없으면 초과 지연은 세지 않는다.</param>
    /// <param name="frameId">장치가 매긴 프레임 번호. 0 이면 세지 않는다.</param>
    /// <param name="waitTicks">수신에서 기다린 시간. <b>대기열이 있는지를 이것이 가른다</b> —
    /// 0 에 가까우면 완성 프레임이 이미 줄 서 있다는 뜻이고, 프레임 주기만큼이면 줄이 비어 있다는 뜻이다.</param>
    public string? Add(long handlerTicks, long waitTicks, long deliveredAt, TimeSpan? capture, ulong frameId = 0)
    {
        _frames++;

        // 번호가 건너뛰면 카메라가 보낸 것이 여기까지 오지 못한 것이다 —
        // "늦게 온다" 와 "아예 안 온다" 는 대응이 다르므로 갈라 센다.
        if (frameId != 0)
        {
            if (_lastFrameId != 0 && frameId > _lastFrameId + 1)
            {
                _gaps++;
                _missed += (long)(frameId - _lastFrameId - 1);
            }
            _lastFrameId = frameId;
        }
        _handlerTicks += handlerTicks;
        if (handlerTicks > _handlerMaxTicks) _handlerMaxTicks = handlerTicks;
        _waitTicks += waitTicks;
        if (waitTicks < _waitMinTicks) _waitMinTicks = waitTicks;

        if (capture is { } c)
        {
            // 두 시계의 기준점 차이 — 그 자체는 의미가 없고, 최선보다 초과한 만큼이 대기 시간이다.
            var offset = deliveredAt - (long)(c.TotalSeconds * Freq);
            if (offset < _bestOffsetTicks) _bestOffsetTicks = offset;
            var excess = offset - _bestOffsetTicks;

            // 장치 시계가 튀면(재설정·되감김) 기준이 무의미해진다. 그대로 두면 몇 시간짜리 "지연" 을
            // 태연히 찍는데, 틀린 숫자는 없는 것보다 나쁘다 — 기준을 다시 잡고 튀었다고 말한다.
            if (excess > ClockJumpGuardTicks)
            {
                _bestOffsetTicks = offset;
                _clockJumps++;
                excess = 0;
            }

            _excessTicks += excess;
            if (excess > _excessMaxTicks) _excessMaxTicks = excess;
            _timedFrames++;
        }

        var elapsed = deliveredAt - _windowStart;
        if (elapsed < _intervalTicks) return null;

        var seconds = elapsed / Freq;
        var fps = _frames / seconds;
        var handlerAvgMs = _handlerTicks / (double)_frames / Freq * 1000.0;
        var handlerMaxMs = _handlerMaxTicks / Freq * 1000.0;
        // 펌프가 호스트 핸들러 안에 머문 비율 — 계산이지 추정이 아니다.
        var duty = _handlerTicks / (double)elapsed * 100.0;

        var line = $"pump: {fps:F1} fps over {seconds:F1}s | host handlers {handlerAvgMs:F1}ms avg, "
                 + $"{handlerMaxMs:F1}ms max, {duty:F0}% of the pump's time";

        if (_gaps > 0)
            line += $" | {_missed} frame(s) never arrived in {_gaps} gap(s)";

        // 수신 대기 — 대기열이 서 있는지를 가르는 숫자다.
        line += $" | receive wait {(_waitTicks / (double)_frames / Freq * 1000.0):F1}ms avg, "
              + $"{(_waitMinTicks == long.MaxValue ? 0 : _waitMinTicks / Freq * 1000.0):F1}ms min";

        if (_clockJumps > 0)
            line += $" | the device clock jumped {_clockJumps} time(s), so the numbers below restart from there";

        line += _timedFrames > 0
            ? $" | variation {(_excessTicks / (double)_timedFrames / Freq * 1000.0):F1}ms avg, "
              + $"{(_excessMaxTicks / Freq * 1000.0):F1}ms max above the best seen"
            : " | the camera reports no timestamp, so latency cannot be measured";

        _windowStart = deliveredAt;
        _frames = 0;
        _gaps = 0;
        _missed = 0;
        _handlerTicks = 0;
        _handlerMaxTicks = 0;
        _excessTicks = 0;
        _excessMaxTicks = 0;
        _timedFrames = 0;
        _waitTicks = 0;
        _waitMinTicks = long.MaxValue;
        _clockJumps = 0;
        return line;
    }
}
