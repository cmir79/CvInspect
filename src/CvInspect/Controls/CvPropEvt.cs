// 프로퍼티 편집기 통지 인자 — 편집기(UI 컨트롤)가 호스트에 내는 이벤트의 값. UI 프레임워크와 무관해 코어에 있다.
// 0.33 까지는 CvInspect.Wpf 에 있었다 — Wpf 어셈블리가 같은 이름으로 전달한다.

namespace CvInspect.Controls;

/// <summary>편집 커밋 통지 — 어떤 프로퍼티가 커밋됐는지 (호스트 dirty 마킹 채널). CLR 프로퍼티명이다.</summary>
public sealed record CvPropCommittedEvt(string PropertyName);

/// <summary>실행 버튼 실행 직전 통지 — 호스트가 사전 준비(낡은 학습 체인 재구성 등)를 거는 자리.
/// PropertyName 은 CLR 프로퍼티명.</summary>
public sealed record CvPropActionExecutingEvt(string PropertyName);

/// <summary>실행 버튼 정상 완료 통지 — 파라미터를 바꾸는 액션(스팬 전환 등)의 편집 추적용.
/// 창만 여는 액션까지 dirty 로 오염시키지 않도록, 어느 편집기에 배선할지는 호스트가 정한다.</summary>
public sealed record CvPropActionExecutedEvt(string PropertyName);

/// <summary>실행 버튼 실패 통지 — 표시 방법은 호스트가 정한다. Label 은 행의 번역된 표시명이다
/// (사용자에게 보여줄 제목용 — 코드 분기 키로 쓰지 말 것).</summary>
public sealed record CvPropActionFailedEvt(string Label, Exception Error);
