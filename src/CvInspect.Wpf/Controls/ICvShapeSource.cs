namespace CvInspect.Controls;

/// <summary>
/// 툴 파라미터가 자기 편집 도형을 직접 공급하는 확장점 — <see cref="CvShapeBinder"/> 의 타입 스위치는
/// 공용 툴(패턴/라인/블랍 등)만 알므로, 검사 구현 전용 파라미터(호스트 정의 POCO)는 이 인터페이스로
/// 도형을 스스로 만든다. 반환 도형의 Changed 배선(파라미터 되write + onEdited 호출)도 구현체 소관.
/// </summary>
public interface ICvShapeSource
{
    /// <summary>편집 도형 목록 — 편집할 것이 없으면(토글 꺼짐 등) null.</summary>
    IReadOnlyList<CvEditShape>? CreateShapes(Action onEdited);
}
