// 프로퍼티 편집기가 Source 를 받을 때의 규칙 — 옛 행 떼기, 빈 화면 판정(센티널), 행 짓기, 호스트 테마 키 이름.
// UI 프레임워크 무관: 편집기 컨트롤(각 UI 패키지)은 이벤트를 자기를 sender 로 올리는 대리자만 넘긴다.

using CvInspect.Controls;

namespace CvInspect.Display;

/// <summary>편집기 Source 규칙.</summary>
internal static class CvPropEditSvc
{
    /// <summary>캡션(설명) 색을 따를 호스트 테마 키 — 없거나 브러시가 아니면 편집기 기본색.</summary>
    internal const string HostDescKey = "SecondaryTextBrush";

    /// <summary>그룹 머리글 색을 따를 호스트 테마 키 — 없거나 브러시가 아니면 편집기 기본색.</summary>
    internal const string HostHeaderKey = "PrimaryBrush";

    /// <summary>펼칠 대상인가 — null 과 plain <see cref="object"/> 는 "typed 설정 없음" 센티널이라 빈 화면이다
    /// (<c>GetType() != typeof(object)</c> 규약).</summary>
    internal static bool IsEditable(object? src) => src is not null && src.GetType() != typeof(object);

    /// <summary>행을 다시 짓는다 — 먼저 옛 행을 떼고(소스 통지·호스트 훅을 끊는다, <see cref="CvPropRowBldr.Detach"/>),
    /// 센티널이면 빈 목록, 아니면 새로 짓는다. 옛 행은 그것을 지은 편집기만 넘긴다(떼기는 되돌릴 수 없다).</summary>
    internal static IReadOnlyList<CvPropGroupVm> Rebuild(
        IReadOnlyList<CvPropGroupVm> old, object? src,
        Action<string> onCommitted, Action<string> onActionExecuting, Action<string> onActionExecuted,
        Action<string, Exception> onActionFailed, string? executeText, bool showDesc)
    {
        // 떼기가 짓기보다 먼저다 — 새 행을 짓는 동안(소스 getter 는 남의 코드) 옛 시각 트리의 늦은 커밋이 폐기된 POCO 로 가지 않게.
        CvPropRowBldr.Detach(old);
        return IsEditable(src)
            ? CvPropRowBldr.Build(src!, onCommitted, onActionExecuting, onActionExecuted, onActionFailed, executeText, showDesc)
            : Array.Empty<CvPropGroupVm>();
    }
}
