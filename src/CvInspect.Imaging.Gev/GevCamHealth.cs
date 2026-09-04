namespace CvInspect.Imaging.Gev;

/// <summary>
/// 카메라 하나의 취득 건강 — 한 번의 호출로 뜬 <b>원자적 스냅샷</b>이다.
/// 항목을 따로 읽으면 서로 다른 시점 값이 섞여, 견주는 순간 없는 사건이 만들어진다.
///
/// 값은 전부 <b>스트림이 선 뒤의 누적 계수</b>다. 비율이나 창을 여기서 정하지 않는다 —
/// 소비자마다 폴 주기가 다르고(설비 회신 주기와 화면 갱신 주기가 서로 다르다), 미리 고른 창은
/// 대개 그중 어느 쪽과도 안 맞는다. 두 스냅샷을 빼서 자기 창을 만들어 쓴다.
///
/// <b>뺄 때는 <see cref="StreamStartedUtc"/> 를 먼저 견준다.</b> 재연결로 스트림이 다시 서면 계수가
/// 0 부터 시작하는데, 그것을 모르고 빼면 음수가 나온다. "값이 줄면 리셋" 같은 추측 규칙은
/// 취득이 잠깐 멈춘 경우와 구분되지 않으므로 쓰지 않는다 — 이 시각이 달라졌으면 리셋이다.
///
/// <b>경보는 무엇으로 거는가.</b> 재전송 요청 수로 걸지 않는다. 패킷이 순서가 뒤바뀐 채 늦게, 그러나
/// 결국 도착하면 요청만 남고 실제 누락은 0 이다(실측: 8시간에 6,392건 요청, 누락 0). 시한을 늘려도
/// 줄지 않는다. 판정은 <see cref="MissingPackets"/> 와 <see cref="IncompleteFrames"/> 로 한다.
/// </summary>
/// <param name="StreamStartedUtc">이 계수들이 시작된 시점. <b>달라졌으면 계수가 리셋된 것</b>이라
/// 이전 스냅샷과 빼면 안 된다.</param>
/// <param name="CompletedFrames">완성된 프레임 수.</param>
/// <param name="IncompleteFrames">패킷이 모자라 완성되지 못한 프레임 수. <b>경보의 한 축이다.</b></param>
/// <param name="DroppedNoBuffer">받아 둘 버퍼가 없어 버린 프레임 수. 0 이 아니면 <b>받아 가는 쪽이
/// 밀린 것</b>이다 — 취득이 아니라 소비자를 본다.</param>
/// <param name="DroppedError">오류로 버린 프레임 수.</param>
/// <param name="DroppedUnsupported">다루지 못하는 화소 형식이라 버린 프레임 수. 0 이 아니면 설정 문제다.</param>
/// <param name="MissingPackets">끝내 오지 않은 패킷 수. <b>경보의 다른 한 축이다.</b></param>
/// <param name="ResendRequests">재전송을 요청한 횟수. <b>이것으로 경보를 걸지 않는다</b>(위 설명 참조).</param>
/// <param name="ResendRecovered">재전송으로 실제로 메운 패킷 수.</param>
/// <param name="NeverArrivedFrames">발행한 프레임 사이에서 장치 번호가 건너뛴 장수 —
/// <b>여기까지 오지 못한 프레임</b>이다. 단발 그랩이 대기열을 비우며 일부러 버린 것은 세지 않는다.</param>
public readonly record struct GevCamHealth(
    DateTime StreamStartedUtc,
    long CompletedFrames,
    long IncompleteFrames,
    long DroppedNoBuffer,
    long DroppedError,
    long DroppedUnsupported,
    long MissingPackets,
    long ResendRequests,
    long ResendRecovered,
    long NeverArrivedFrames);
