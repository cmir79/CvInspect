namespace CvInspect;

/// <summary>
/// 카테고리 표시 순번 계약 — 그리드/에디터가 구체 attribute 타입을 몰라도
/// <see cref="System.ComponentModel.CategoryAttribute"/> 인스턴스에서 순번을 읽게 한다.
/// </summary>
public interface ICvOrderedCategory
{
    /// <summary>오름차순 정렬 키. 무번호는 <see cref="int.MaxValue"/>(맨 뒤).</summary>
    int Order { get; }
}
