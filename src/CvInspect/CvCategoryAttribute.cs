using System.ComponentModel;

namespace CvInspect;

/// <summary>
/// PropertyGrid 카테고리를 <see cref="CvLoc"/> 로 해석하는 attribute — 인자는 <c>"scope:key"</c>.
/// <see cref="CategoryAttribute"/> 파생이라 표준 컴포넌트 모델을 읽는 어떤 편집기에서도 무수정 해석된다.
///
/// 표시 순서는 <see cref="Order"/> 로만 노출한다 — 카테고리 문자열에 순번을 섞지 않으므로,
/// 정렬은 이 값을 읽는 소비자(그리드) 책임이다. 알파벳 정렬만 하는 그리드에서는 선언 순서가 보장되지 않는다.
///
/// 주의: <see cref="CategoryAttribute.Category"/> 는 인스턴스별 첫 조회 때 한 번만 번역을 거쳐
/// 캐시된다(베이스 동작 — Category 가 virtual 이 아니라 우회 불가). TypeDescriptor 계열 소비자
/// (전형적 PropertyGrid)는 그 인스턴스를 프로세스 수명 동안 재사용하며 TypeDescriptor.Refresh 로도
/// 버려지지 않는다. 실행 중 언어 전환을 카테고리에 반영해야 하면 Category 문자열 대신
/// <see cref="ScopedKey"/> 를 <see cref="CvLoc.T"/> 로 직접 해석하거나, 호출마다 새 인스턴스를 주는
/// PropertyInfo.GetCustomAttribute 경로로 읽는다. 속성별 인스턴스가 서로 다른 시점에 굳으면 같은
/// 카테고리가 두 그룹으로 갈릴 수 있으니, 전환 반영은 타입 단위로 한꺼번에 한다.
/// </summary>
[AttributeUsage(AttributeTargets.All, AllowMultiple = false, Inherited = true)]
public sealed class CvCategoryAttribute : CategoryAttribute, ICvOrderedCategory
{
    /// <summary>무번호 센티넬 — 순번 없는 카테고리는 맨 뒤로 정렬된다.</summary>
    public const int Unordered = int.MaxValue;

    /// <summary>순번 없는 카테고리 — <see cref="Order"/> 는 <see cref="Unordered"/>(맨 뒤).</summary>
    public CvCategoryAttribute(string scopedKey) : base(scopedKey)
    {
        ScopedKey = scopedKey;
        Order = Unordered;
    }

    /// <summary>순번 있는 카테고리 — 오름차순 정렬 키. 클래스(툴) 단위로 <b>1부터</b> 매긴다.
    /// 0 이하는 무번호(<see cref="Unordered"/> = 맨 뒤)로 접는다 — "0 = 무번호" 로 쓰던 부착부가
    /// 기계 치환으로 넘어와도 그 그룹이 맨 앞으로 튀지 않게 한다.</summary>
    public CvCategoryAttribute(string scopedKey, int order) : base(scopedKey)
    {
        ScopedKey = scopedKey;
        Order = order <= 0 ? Unordered : order;
    }

    /// <summary>번역 키 원문 (<c>"scope:key"</c>).</summary>
    public string ScopedKey { get; }

    /// <inheritdoc />
    public int Order { get; }

    protected override string GetLocalizedString(string value) => CvLoc.T(ScopedKey);
}
