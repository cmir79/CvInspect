using System.Linq;

namespace CvInspect.Vision.Overlay;

/// <summary>요약 HUD 가 붙는 이미지 모서리.</summary>
public enum ViHudPos
{
    /// <summary>좌상단 — 기본.</summary>
    TopLeft,

    /// <summary>우상단.</summary>
    TopRight,

    /// <summary>좌하단 — 검사 그래픽이 위쪽에 몰리는 화면에서 HUD 가 그것을 가리지 않게 내리는 용도.</summary>
    BottomLeft,

    /// <summary>우하단.</summary>
    BottomRight,
}

/// <summary>
/// 이미지 모서리 검사 결과 요약 HUD — 판정 헤더 + 상세 줄 (검사 공통 규약, 호스트 공용).
/// 단일 라벨(줄바꿈 포함) 블록으로 발행 — 라벨 폰트는 화면 고정 크기라 여러 라벨을
/// 이미지 좌표로 쌓으면 축소 배율에서 줄이 겹친다. 블록 색 = 판정색 (OK 녹 / NG 적).
/// 배경은 항상 검은 박스(라벨 HasBackground) — 밝은 이미지 위에서도 가독 보장.
/// 기본 자리는 좌상단이고, <see cref="ViHudPos"/> 갈래로 다른 모서리에 붙일 수 있다 —
/// 블록은 앵커 모서리에서 이미지 안쪽으로 펼쳐지므로(렌더러의 Align 해석) 배율과 무관하게 그 모서리에 머문다.
/// </summary>
public static class ViHud
{
    /// <summary>기본 시작 높이 — 이미지 맨 위에서 조금 내린 자리.</summary>
    private const double DefaultTopY = 10;

    /// <summary>가로 여백 — 이미지 왼쪽/오른쪽 변에서 안쪽으로.</summary>
    private const double MarginX = 12;

    public static void AddSummary(this ViOverlay overlay, bool isOk, string title, params string[] lines)
        => overlay.AddSummary(DefaultTopY, isOk, title, lines);

    /// <summary>
    /// 줄마다 색을 달리하는 갈래 — 한 블록 안에서 뜻이 다른 줄(채택분 vs 미사용분 등)을 가르는 용도.
    /// 색이 null 인 줄은 판정색을 그대로 쓰고, 전 줄이 null 이면 종전과 똑같은 단색 블록이 된다.
    /// HUD 는 이미지 위에 겹쳐 그려지므로 줄 수와 길이는 최소로 — 길면 검사 그래픽을 가린다.
    /// </summary>
    public static void AddSummary(this ViOverlay overlay, bool isOk, string title,
        IReadOnlyList<(string Text, ViOverlayColor? Color)> lines)
        => overlay.AddSummary(ViHudPos.TopLeft, 0, 0, isOk, title, lines);

    /// <summary>
    /// 시작 높이를 지정하는 갈래 — 화면이 이미지 좌상단에 자기 표기(카메라 이름 등)를 겹쳐 그리는
    /// 경우에 쓴다. 그 표기는 화면 좌표에 붙어 있고 HUD 는 이미지 좌표라, 확대 배율에 따라 겹치는
    /// 정도가 달라진다. 이미지 높이에 비례한 값을 넘기면 해상도가 달라도 같은 자리에서 시작한다.
    /// </summary>
    public static void AddSummary(this ViOverlay overlay, double topY, bool isOk, string title, params string[] lines)
    {
        var header = $"[{(isOk ? "OK" : "NG")}] {title}";
        var text = lines.Length == 0 ? header : header + "\n" + string.Join("\n", lines);
        overlay.Add(new ViOverlayLabel
        {
            Text = text,
            FontSize = 12f,
            Color = isOk ? ViOverlayColor.Green : ViOverlayColor.Red,
            X = MarginX,
            Y = topY,
            Align = ViOverlayAlign.TopLeft,
            HasBackground = true,
        });
    }

    /// <summary>
    /// 모서리를 지정하는 갈래(단색) — 검사 그래픽이 어느 한쪽에 몰리는 화면에서 HUD 를 반대편으로 보낸다.
    /// <paramref name="imgW"/>/<paramref name="imgH"/> 는 원본 이미지 크기(오버레이 좌표 공간) —
    /// 오른쪽·아래 모서리의 앵커를 잡는 데 쓰며 좌상단이면 무시된다.
    /// </summary>
    public static void AddSummary(this ViOverlay overlay, ViHudPos pos, double imgW, double imgH,
        bool isOk, string title, params string[] lines)
        => overlay.AddSummary(pos, imgW, imgH, isOk, title,
            lines.Select(l => (l, (ViOverlayColor?)null)).ToList());

    /// <summary>모서리 지정 + 줄별 색 갈래 — 두 기능을 합친 형태. 규약은 각 갈래 설명과 같다.</summary>
    public static void AddSummary(this ViOverlay overlay, ViHudPos pos, double imgW, double imgH,
        bool isOk, string title, IReadOnlyList<(string Text, ViOverlayColor? Color)> lines)
    {
        var texts = new List<string>(lines.Count + 1) { $"[{(isOk ? "OK" : "NG")}] {title}" };
        var colors = new List<ViOverlayColor?>(lines.Count + 1) { null };
        foreach (var (text, color) in lines)
        {
            texts.Add(text);
            colors.Add(color);
        }

        // 앵커는 고른 모서리에서 여백만큼 안쪽 — Align 이 같은 모서리를 가리키므로 블록은 이미지 안으로 펼쳐진다.
        var right = pos is ViHudPos.TopRight or ViHudPos.BottomRight;
        var bottom = pos is ViHudPos.BottomLeft or ViHudPos.BottomRight;
        overlay.Add(new ViOverlayLabel
        {
            Text = string.Join("\n", texts),
            FontSize = 12f,
            Color = isOk ? ViOverlayColor.Green : ViOverlayColor.Red,
            X = right ? imgW - MarginX : MarginX,
            Y = bottom ? imgH - DefaultTopY : DefaultTopY,
            Align = (right, bottom) switch
            {
                (false, false) => ViOverlayAlign.TopLeft,
                (true, false) => ViOverlayAlign.TopRight,
                (false, true) => ViOverlayAlign.BottomLeft,
                (true, true) => ViOverlayAlign.BottomRight,
            },
            HasBackground = true,
            LineColors = colors.Any(c => c is not null) ? colors : null,
        });
    }
}
