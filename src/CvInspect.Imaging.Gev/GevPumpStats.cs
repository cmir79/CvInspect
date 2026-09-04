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
    private readonly long _intervalTicks;
    private long _windowStart;

    private int _frames;
    private long _handlerTicks;
    private long _handlerMaxTicks;

    private long _bestOffsetTicks = long.MaxValue;   // 구간 전체에서 관측한 최선(= 지연 0 의 추정)
    private long _excessTicks;
    private long _excessMaxTicks;
    private int _timedFrames;

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
    public string? Add(long handlerTicks, long deliveredAt, TimeSpan? capture)
    {
        _frames++;
        _handlerTicks += handlerTicks;
        if (handlerTicks > _handlerMaxTicks) _handlerMaxTicks = handlerTicks;

        if (capture is { } c)
        {
            // 두 시계의 기준점 차이 — 그 자체는 의미가 없고, 최선보다 초과한 만큼이 대기 시간이다.
            var offset = deliveredAt - (long)(c.TotalSeconds * Freq);
            if (offset < _bestOffsetTicks) _bestOffsetTicks = offset;
            var excess = offset - _bestOffsetTicks;
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

        line += _timedFrames > 0
            ? $" | capture->deliver {(_excessTicks / (double)_timedFrames / Freq * 1000.0):F1}ms avg, "
              + $"{(_excessMaxTicks / Freq * 1000.0):F1}ms max above the best seen"
            : " | the camera reports no timestamp, so queueing time cannot be measured";

        _windowStart = deliveredAt;
        _frames = 0;
        _handlerTicks = 0;
        _handlerMaxTicks = 0;
        _excessTicks = 0;
        _excessMaxTicks = 0;
        _timedFrames = 0;
        return line;
    }
}
