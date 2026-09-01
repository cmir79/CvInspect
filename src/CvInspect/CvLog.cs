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
/// 라이브러리 진단 로그의 단일 유출구 — 호스트가 <see cref="Sink"/> 를 연결하면 그쪽으로 흐르고,
/// 없으면 조용히 버려진다 (라이브러리는 스스로 콘솔/파일에 쓰지 않는다).
/// </summary>
public static class CvLog
{
    /// <summary>(level, source, message, exception) 수신 델리게이트 — 호스트 로거 어댑터 연결 지점.</summary>
    public static Action<CvLogLevel, string, string, Exception?>? Sink { get; set; }

    /// <summary>로그 한 줄 발행 — <see cref="Sink"/> 미연결이면 무시된다. message 는 영어로 쓴다.</summary>
    public static void Publish(CvLogLevel level, string source, string message, Exception? exception = null)
        => Sink?.Invoke(level, source, message, exception);

    internal static void Warn(string source, string message) => Publish(CvLogLevel.Warning, source, message);
}
