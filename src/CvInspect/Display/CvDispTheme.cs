// 디스플레이 색 규칙 — 호스트 리소스 키 이름, 고정 기본색, 출처 순서, 키에 엉뚱한 값이 있을 때의 경고 문구.
// UI 프레임워크 무관: 브러시 형과 리소스 조회(키에 묶어 실행 중 사전 교체를 따라가는 슬롯)는 UI 어댑터 몫이고, 여기는 이름·색 값·문구만 둔다.
// 출처 순서: 이 인스턴스에 준 속성 값 → 호스트 리소스 키 → 고정 기본색(영상 영역 바탕만은 고정색이 아니라 컨트롤의 Background 로 떨어진다).

namespace CvInspect.Display;

/// <summary>디스플레이 색 규칙. 키 글자는 공개 계약이다 — UI 패키지의 공개 상수가 이 값을 그대로 낸다(회귀가 글자를 못 박는다).</summary>
internal static class CvDispTheme
{
    internal const string ToolbarBackgroundKey = "CvDispToolbarBackgroundBrush";
    internal const string StatusBarBackgroundKey = "CvDispStatusBarBackgroundBrush";
    internal const string StatusBarForegroundKey = "CvDispStatusBarForegroundBrush";
    internal const string SeparatorBrushKey = "CvDispSeparatorBrush";
    internal const string ViewBackgroundKey = "CvDispViewBackgroundBrush";
    internal const string PlaceholderForegroundKey = "CvDispPlaceholderForegroundBrush";

    /// <summary>강조색 — 호스트 테마가 정의한 컨트롤 라이브러리 키 이름. 있으면 그대로 써서 화면 전체 팔레트를 따르고, 없으면
    /// (테마 미병합·콘솔 하네스) 고정색으로 떨어진다. 이 계층은 UI 라이브러리를 참조하지 않는다 — 키 이름만 안다.
    /// ⏯ 는 늘(정지 = Danger, 라이브 = Success), ✋ 는 눌렸을 때만 Primary 를 쓴다.</summary>
    internal const string PrimaryHostKey = "PrimaryBrush";
    internal const string SuccessHostKey = "SuccessBrush";
    internal const string DangerHostKey = "DangerBrush";

    /// <summary>툴바·상태 줄 바탕 기본색 #FAFAFA.</summary>
    internal static readonly ViPaint DefaultBar = ViPaint.Argb(255, 0xFA, 0xFA, 0xFA);

    /// <summary>상태 줄 글자 기본색 #555555.</summary>
    internal static readonly ViPaint DefaultStatusFg = ViPaint.Argb(255, 0x55, 0x55, 0x55);

    /// <summary>구분선 기본색 #DDDDDD.</summary>
    internal static readonly ViPaint DefaultSep = ViPaint.Argb(255, 0xDD, 0xDD, 0xDD);

    internal static readonly ViPaint DefaultPrimary = ViPaint.Argb(255, 0x15, 0x65, 0xC0);
    internal static readonly ViPaint DefaultSuccess = ViPaint.Argb(255, 0x2E, 0x7D, 0x32);
    internal static readonly ViPaint DefaultDanger = ViPaint.Argb(255, 0xC6, 0x28, 0x28);

    /// <summary>컨트롤 Background 기본 #2A2A2A — 영상 영역 바탕이 속성·키 둘 다 없을 때 비치는 색.</summary>
    internal static readonly ViPaint DefaultControlBack = ViPaint.Argb(255, 0x2A, 0x2A, 0x2A);

    /// <summary>출처 순서 — 인스턴스 값이 그 형이면 그것, 아니면 키 슬롯 값이 그 형이면 그것, 둘 다 아니면 null(호출자가 기본으로 떨어진다).
    /// 형이 다른 값(같은 키 이름에 Color 를 넣은 것 등)은 없는 것으로 본다.</summary>
    internal static T? Pick<T>(object? own, object? slot) where T : class => own as T ?? slot as T;

    /// <summary>우리 키 아래 브러시가 아닌 값이 있을 때의 경고. 호스트가 이 키를 노리고 잘못 넣은 것이다(흔한 모양: Color 를 넣음) —
    /// 말없이 기본색으로 남으면 "키가 안 먹는다" 로만 보이므로 알린다. 강조색 키는 호스트 라이브러리의 이름이라 다른 뜻으로 쓰는
    /// 정상 구성일 수 있어 이 경고를 쓰지 않는다.</summary>
    internal static string NotBrushWarning(string key, string valueTypeName)
        => $"Resource '{key}' is a {valueTypeName}, not a Brush; ignored, "
           + (key == ViewBackgroundKey ? "the control's Background shows instead." : "the default color is used.");

    /// <summary>무이미지 안내 글자 브러시가 단색이 아닐 때의 경고 — 글자는 그리기 목록의 ARGB 로 그려 단색만 옮길 수 있다.</summary>
    internal static string HintBrushWarning(string brushTypeName)
        => $"Placeholder text brush is a {brushTypeName}; only SolidColorBrush is supported, the default color is used.";

    /// <summary>단색 브러시 → 안내 글자 색. 브러시의 불투명도는 [0, 1] 로 자른 뒤 알파에 곱해 반올림한다.</summary>
    internal static ViPaint HintPaint(byte a, byte r, byte g, byte b, double opacity)
        => ViPaint.Argb((byte)Math.Round(a * Math.Clamp(opacity, 0.0, 1.0)), r, g, b);
}
