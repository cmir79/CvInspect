namespace CvInspect.Vision.Edit;

/// <summary>
/// 편집 도형을 스스로 공급하는 툴 파라미터. 표시 컨트롤은 이 도형을 그리고 드래그할 뿐, 어떤 파라미터가 어떤 도형을
/// 갖는지는 모른다 — 코어의 Opt 가 전부 이걸 구현하고, 호스트가 정의한 파라미터도 같은 방법으로 도형을 낸다.
/// 반환 도형의 Changed 배선(파라미터 되쓰기 + onEdited 호출)은 구현체 소관이다.
/// 도형 좌표 공간 = 그 툴의 입력(단계) 이미지 픽셀 공간(파라미터와 같다).
/// </summary>
public interface ICvShapeSource
{
    /// <summary>편집 도형 목록 — 편집할 것이 없으면(토글 꺼짐 등) null. 만들 때의 값으로 도형을 놓으므로,
    /// 코드 경로로 파라미터를 고쳤으면 다시 만들거나(호스트) 파라미터가 제공하는 동기화 훅으로 도형을 맞춘다.</summary>
    IReadOnlyList<CvEditShape>? CreateShapes(Action onEdited);
}
