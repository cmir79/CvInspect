namespace CvInspect.Imaging;

/// <summary>
/// 단발 그랩을 <b>프레임을 돌려주는</b> 형태로 내놓는 구현이 다는 표식.
///
/// <see cref="ICam.GrabOne"/> 은 프레임을 <see cref="ICam.FrameAcquired"/> 로만 발행하므로, 한 장이 필요한
/// 호출자는 <b>구독을 걸고 · 부르고 · 기다리고 · 푸는</b> 네 단계를 직접 짜야 한다. 실제로 소비자마다 그것을
/// 각자 만들어 썼다 — 라이브러리가 빠뜨린 자리라는 뜻이다. <see cref="CamGrabExt.GrabFrameAsync"/> 가 그
/// 네 단계를 대신 해 주고, <b>이 인터페이스를 구현한 카메라는 그 기본 절차 대신 자기 구현이 불린다.</b>
///
/// <b>왜 <see cref="ICam"/> 에 안 넣고 따로 두는가</b> — 기본 구현이 달린 인터페이스 멤버(default interface
/// member)로 넣으면 구현체를 안 고쳐도 되지만, 그건 <b>런타임이 받쳐 줘야</b> 한다. 이 패키지는
/// <c>netstandard2.1</c> 을 함께 겨냥하고 그 이유가 유니티 연동인데, 유니티의 IL2CPP 는 기본 인터페이스 구현을
/// 실행하지 못해 빌드가 깨진다. 그래서 <see cref="ICam"/> 표면은 그대로 두고, <b>확장 메서드 + 선택 인터페이스</b>
/// 로 같은 것을 준다. 기존 구현은 한 줄도 안 고쳐도 되고, 잘할 수 있는 구현만 이 표식을 단다.
///
/// <b>표식을 달아야 하는 구현</b> — 프레임이 <b>다른 스레드의 콜백으로</b> 들어오는 취득 계층. 기본 절차는
/// "이 호출이 부른 그랩의 프레임" 과 "때마침 도착한 다른 프레임" 을 <b>구조적으로 가르지 못한다</b>. 가를 수 있는
/// 것은 장치 쪽 짝짓기 근거(프레임 번호·티켓)를 쥔 구현 자신뿐이다.
/// </summary>
public interface ICamGrabAsync
{
    /// <summary>한 장을 찍어 돌려준다. 프레임은 <b><see cref="ICam.FrameAcquired"/> 로도 나간다</b> —
    /// 같은 취득이고, 이미 구독 중인 표시 경로가 이 장만 못 보는 일이 없어야 한다.
    ///
    /// <paramref name="timeout"/> 이 <b>구현의 설정값보다 우선한다</b>(호출 자리의 사정이 더 최신이다).
    /// 구현 자신의 시한에 맡기려면 <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> 을 준다.
    ///
    /// <b>돌려주는 값과 던지는 것의 경계</b> — 이것이 이 API 의 핵심이다.
    /// <list type="bullet">
    /// <item><c>null</c> 은 <b>이 호출의 장이 없었고, 던질 사유도 없다</b>는 뜻이다: 기본 절차
    /// (<see cref="CamGrabExt.GrabFrameAsync"/>)의 시한 만료, 애초에 답할 수 없어 이미 경고를 남긴 구현
    /// (<c>DeadCam</c> 류), 받은 장을 쓸 수 없어 버리고 경고를 남긴 구현(<c>GevCam</c> — 지원하지 않는 픽셀 포맷).
    /// <b>시한 전에도 온다</b> — <c>null</c> 을 곧 "시한 만료" 로 읽으면 다른 원인을 시한으로 오독한다.</item>
    /// <item><b>시한 만료를 알리는 방식은 구현마다 다르다.</b> 왜 안 왔는지 모르는 기본 절차는 <c>null</c> 이고,
    /// 짚을 곳을 아는 구현은 그 안내를 실어 <see cref="TimeoutException"/> 으로 던진다(<c>GevCam</c> — 기다리는 동안 버려진 블록이
    /// 있었으면 그것을, 없었으면 열 때 남긴 카메라 상태 줄의 트리거 모드·청크 모드 경고를 가리킨다. <see cref="ICam.GrabOne"/> 과
    /// 몸통이 같다). 백엔드를 가리지 않는 호출자는
    /// <c>null</c> 과 <see cref="TimeoutException"/> 을 둘 다 "장이 없었다" 로 받는다.</item>
    /// <item><b>답해야 하는데 답할 수 없는 상태는 던진다</b> — 열려 있지 않음·닫힘·해제·제어 상실·연속 취득 중·
    /// 이미 기다리는 그랩이 있음. 이것을 <c>null</c> 로 접으면 사유 채널이 하나로 뭉개지고, 부른 쪽은 오지 않을
    /// 프레임을 계속 기다린다(<see cref="ICam.GrabOne"/> 이 같은 이유로 던진다).</item>
    /// </list></summary>
    /// <exception cref="TimeoutException">시한 안에 장이 안 왔고 구현이 그 사유를 안다 — 구현에 따라(위 둘째 항).</exception>
    /// <exception cref="InvalidOperationException">열려 있지 않음·연속 취득 중·이미 기다리는 그랩이 있음·기다리는 도중
    /// 닫힘이나 제어 상실.</exception>
    /// <exception cref="ObjectDisposedException">이미 해제됐다.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> 로 취소됐다 — 시한 만료와 갈라 알 수 있다.</exception>
    /// <remarks>그 밖에 전송·장치 계층의 실패는 구현 고유의 예외로 올 수 있다(<c>GevCam</c>: 취득 라이브러리의 예외 계열 —
    /// 그랩을 걸기 전에 제어를 잃었으면 제어 상실 예외 등).</remarks>
    Task<CamFrame?> GrabFrameAsync(TimeSpan timeout, CancellationToken ct = default);
}
