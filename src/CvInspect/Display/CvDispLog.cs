// 디스플레이 계층 경고 로그의 출처 이름.

namespace CvInspect.Display;

/// <summary>디스플레이 계층이 <see cref="CvLog"/> 에 남기는 줄의 출처.</summary>
internal static class CvDispLog
{
    /// <summary>디스플레이 컨트롤 이름 그대로 — 호스트 로그 필터가 이 글자로 거른다. UI 패키지마다 컨트롤 이름이 같아 출처도 같다.</summary>
    internal const string Source = "CvDispCtrl";
}
