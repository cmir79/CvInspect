using System.ComponentModel;

namespace CvInspect;

/// <summary>
/// PropertyGrid 설명문을 <see cref="CvLoc"/> 로 해석하는 attribute — 인자는 <c>"scope:key"</c>.
/// <see cref="DescriptionAttribute.Description"/> 은 매 조회 재평가라 언어 전환이 즉시 반영된다.
/// </summary>
[AttributeUsage(AttributeTargets.All, AllowMultiple = false, Inherited = true)]
public sealed class CvDescAttribute : DescriptionAttribute
{
    /// <summary>설명문 번역 키를 지정한다 (<c>"scope:key"</c>).</summary>
    public CvDescAttribute(string scopedKey) : base(scopedKey)
    {
        ScopedKey = scopedKey;
    }

    /// <summary>번역 키 원문 (<c>"scope:key"</c>).</summary>
    public string ScopedKey { get; }

    public override string Description => CvLoc.T(ScopedKey);
}
