// 디스플레이 상태 줄의 글자 — 오른쪽(이미지 크기·배율)과 왼쪽(커서 아래 이미지 좌표·픽셀 값).
// UI 프레임워크 무관: UI 어댑터는 이 글자를 상태 줄 글상자에 넣기만 한다. 픽셀 값 라벨 자체는 CvFrameBuf.ValueAt 이 만든다.

namespace CvInspect.Display;

/// <summary>상태 줄 글자 조립. 두 칸 사이 간격은 공백 둘이다(고정폭 글꼴에서 칸이 읽히게).</summary>
internal static class CvDispStatus
{
    /// <summary>오른쪽 칸 — <c>"{W}×{H}  {배율%}"</c>. 배율은 백분율을 정수로 반올림한다. 프레임이 없으면 빈 문자열.</summary>
    internal static string SizeText((int W, int H)? size, double viewScale)
        => size is null ? "" : $"{size.Value.W}×{size.Value.H}  {Math.Round(viewScale * 100)}%";

    /// <summary>왼쪽 칸 — <c>"X {x}  Y {y}  {값}"</c>, 값 라벨이 없으면 <c>"X {x}  Y {y}"</c>. 커서가 이미지 밖이면(probe 가 null) 빈 문자열.</summary>
    internal static string ProbeText((int X, int Y, string? Val)? probe)
        => probe is null
            ? ""
            : probe.Value.Val is { } v
                ? $"X {probe.Value.X}  Y {probe.Value.Y}  {v}"
                : $"X {probe.Value.X}  Y {probe.Value.Y}";
}
