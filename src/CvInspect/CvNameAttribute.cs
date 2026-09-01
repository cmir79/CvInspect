using System.ComponentModel;

namespace CvInspect;

/// <summary>
/// PropertyGrid 표시명을 <see cref="CvLoc"/> 로 해석하는 attribute — 인자는 <c>"scope:key"</c>.
/// <see cref="DisplayNameAttribute.DisplayName"/> 은 매 조회 재평가라 언어 전환이 즉시 반영된다.
/// </summary>
[AttributeUsage(AttributeTargets.All, AllowMultiple = false, Inherited = true)]
public sealed class CvNameAttribute : DisplayNameAttribute
{
    /// <summary>표시명 번역 키를 지정한다 (<c>"scope:key"</c>).</summary>
    public CvNameAttribute(string scopedKey) : base(scopedKey)
    {
        ScopedKey = scopedKey;
    }

    /// <summary>번역 키 원문 (<c>"scope:key"</c>).</summary>
    public string ScopedKey { get; }

    public override string DisplayName => CvLoc.T(ScopedKey);
}
