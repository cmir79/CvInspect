using GevSharp;

namespace CvInspect.Imaging.Gev;

/// <summary>
/// 취득 라이브러리의 진단을 <see cref="CvLog"/> 로 흘려보낸다.
///
/// 그 라이브러리는 로그 창구가 비어 있으면 <b>진단을 조용히 버린다</b> — 쓸 만한 NIC 이 없다,
/// 장치가 하트비트 시한을 거부했다, 협상된 패킷 크기가 이만큼이다, OS 가 소켓 버퍼를 요청보다 적게 줬다,
/// 카메라 XML 을 두 번째 주소에서 받았다 같은 것들이 전부 그렇다. 그것들이 없으면 설비에서 볼 수 있는 것은
/// 예외 문구뿐이고, 예외가 안 나는 성능 문제는 아예 보이지 않는다.
///
/// <see cref="GevCam"/> 이 처음 만들어질 때 <b>창구가 비어 있는 경우에만</b> 자동으로 연결한다 —
/// 호스트가 이미 자기 창구를 꽂았으면 건드리지 않는다. 수준을 바꾸거나 명시적으로 연결하려면
/// <see cref="AttachToCvLog"/> 를 호스트 기동 때 한 번 부른다.
///
/// <b>이 다리는 <see cref="CvLog"/> 까지만 잇는다.</b> 호스트가 <see cref="CvLog.Sink"/> 를 붙이지 않았으면 그 끝은
/// 비어 있다 — 취득 계층 쪽에서는 창구가 차 있어 끝까지 이어진 것처럼 읽히기 쉽다. 그 상태는
/// <see cref="CvLog.IsAttached"/> 가 말하고, 붙기 전의 줄은 <see cref="CvLog"/> 가 붙잡아 둔다.
/// </summary>
public static class GevLogBridge
{
    private static readonly object _sync = new();
    private static bool _attached;

    /// <summary>취득 라이브러리 로그를 <see cref="CvLog"/> 로 연결한다(중복 호출 안전).</summary>
    /// <param name="minLevel">이 수준 미만은 라이브러리가 만들지도 않는다. 문제를 쫓을 때만 Debug 로 내린다 —
    /// 프레임마다 도는 경로가 있어 Trace/Debug 는 로그가 매우 많아진다.</param>
    public static void AttachToCvLog(GevLogLevel minLevel = GevLogLevel.Info)
    {
        lock (_sync)
        {
            GevLog.MinLevel = minLevel;
            GevLog.Sink = Forward;
            _attached = true;
        }
    }

    /// <summary>창구가 비어 있을 때만 연결한다 — 호스트가 꽂아 둔 것을 덮지 않는다.</summary>
    internal static void AttachIfUnset()
    {
        lock (_sync)
        {
            if (_attached || GevLog.Sink is not null) return;
            GevLog.Sink = Forward;
            _attached = true;
        }
    }

    private static void Forward(GevLogLevel level, string source, string message, Exception? ex)
        => CvLog.Publish(Map(level), source, message, ex);

    private static CvLogLevel Map(GevLogLevel level) => level switch
    {
        GevLogLevel.Trace => CvLogLevel.Trace,
        GevLogLevel.Debug => CvLogLevel.Debug,
        GevLogLevel.Info => CvLogLevel.Info,
        GevLogLevel.Warn => CvLogLevel.Warning,
        _ => CvLogLevel.Error,
    };
}
