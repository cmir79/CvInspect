namespace CvInspect.Imaging;

/// <summary><see cref="ReconnectingCam"/> 의 재시도 정책.</summary>
public sealed class CamReconnectOpt
{
    /// <summary>재시도 간격 사다리(ms). 시도마다 다음 칸으로 올라가고 <b>마지막 칸에서 고정</b>된다.
    /// 비우면 1초 고정으로 친다. 첫 시도도 이 지연을 거친다 — 끊긴 직후의 즉시 재시도는 장치가 아직
    /// 세션을 놓지 않아 대개 실패하고, 그 실패가 사다리 한 칸을 헛되이 소모한다.</summary>
    public IReadOnlyList<int> BackoffMs { get; set; } = new[] { 1000, 2000, 5000, 10000, 30000 };

    /// <summary>한 번의 끊김에 대한 최대 시도 횟수. 0 = 무제한.
    /// 포기해도 조용히 사라지지 않는다 — 경고를 남기고 미연결 상태로 머물며, <see cref="ICam.Open"/> 재호출로 수동 재개할 수 있다.</summary>
    public int MaxAttempts { get; set; }

    /// <summary>정리(폐기·취소) 대기 상한(ms) — <see cref="IDisposable.Dispose"/> 가 재연결 루프를 수거할 때 쓴다.
    /// 무한 대기는 앱 종료를 붙잡으므로 두지 않는다.</summary>
    public int ShutdownWaitMs { get; set; } = 3000;
}
