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
    /// <item><c>null</c> 은 <b>사유 없이 장이 없었다</b>는 뜻이다: 시한이 다했거나, 애초에 답할 수 없어
    /// 이미 경고를 남긴 구현(<c>DeadCam</c> 류)이다.</item>
    /// <item><b>답해야 하는데 답할 수 없는 상태는 던진다</b> — 닫힘·해제·제어 상실·연속 취득 중·이미 기다리는
    /// 그랩이 있음. 이것을 <c>null</c> 로 접으면 사유 채널이 하나로 뭉개지고, 부른 쪽은 오지 않을 프레임을
    /// 계속 기다린다(<see cref="ICam.GrabOne"/> 이 같은 이유로 던진다).</item>
    /// </list></summary>
    Task<CamFrame?> GrabFrameAsync(TimeSpan timeout, CancellationToken ct = default);
}
