// 디스플레이 우클릭 메뉴의 구성 — 항목 순서, 번역 키, 함께 여닫는 묶음, 활성 조건.
// UI 프레임워크 무관: UI 어댑터는 Layout 순서대로 항목·구분선을 한 번만 만들고, 이후엔 표시 여부만 바꾼다.
// 항목 수를 메뉴가 열리는 도중에 바꾸면 팝업이 직전 구성의 크기로 열려 늘어난 항목이 배경 밖으로 삐져나온다 — 그래서 묶음 표시는
// 열 때가 아니라 그 조건(불러오기 명령·툴바 표시)이 바뀔 때 정한다.

namespace CvInspect.Display;

/// <summary>메뉴 항목이 하는 일.</summary>
internal enum CvDispMenuCmd { Load, Save, Pan, Fit, ZoomIn, ZoomOut, Clear }

/// <summary>함께 여닫는 묶음. 감출 때는 그 자리에 딸린 구분선까지 한 묶음으로 감춘다 — 항목만 빼면 구분선이 겹쳐 남는다.</summary>
internal enum CvDispMenuGroup
{
    /// <summary>늘 보인다. 팬·맞춤·확대·축소는 툴바와 무관하게 남긴다 — 툴바를 감춘 화면에서 배율이 어긋나면 되돌릴 수단이 이 메뉴뿐이다.</summary>
    Always,

    /// <summary>불러오기 — 받을 곳(불러오기 명령)이 없으면 감춘다.</summary>
    Load,

    /// <summary>툴바 버튼과 짝인 조작(그래픽 지우기와 그 앞 구분선) — 툴바를 감추면 같이 사라진다.</summary>
    Toolbar,
}

/// <summary>메뉴 한 자리 — <see cref="Cmd"/> 가 null 이면 구분선.</summary>
internal readonly record struct CvDispMenuEntry(CvDispMenuCmd? Cmd, CvDispMenuGroup Group);

/// <summary>우클릭 메뉴 구성. 순서는 툴바 버튼 순서와 같다(팬 │ 맞춤·확대·축소 │ 지우기) — 파일 두 항목이 그 앞에 온다.</summary>
internal static class CvDispMenu
{
    /// <summary>위에서 아래로의 순서: 불러오기, 저장 │ 팬 │ 맞춤, 확대, 축소 │ 그래픽 지우기.</summary>
    internal static readonly CvDispMenuEntry[] Layout =
    [
        new(CvDispMenuCmd.Load, CvDispMenuGroup.Load),
        new(CvDispMenuCmd.Save, CvDispMenuGroup.Always),
        new(null, CvDispMenuGroup.Always),
        new(CvDispMenuCmd.Pan, CvDispMenuGroup.Always),
        new(null, CvDispMenuGroup.Always),
        new(CvDispMenuCmd.Fit, CvDispMenuGroup.Always),
        new(CvDispMenuCmd.ZoomIn, CvDispMenuGroup.Always),
        new(CvDispMenuCmd.ZoomOut, CvDispMenuGroup.Always),
        new(null, CvDispMenuGroup.Toolbar),
        new(CvDispMenuCmd.Clear, CvDispMenuGroup.Toolbar),
    ];

    /// <summary>항목 라벨의 번역 키. 라벨은 메뉴가 열릴 때마다 다시 읽는다 — 언어 전환 대응.</summary>
    internal static string LabelKey(CvDispMenuCmd cmd) => cmd switch
    {
        CvDispMenuCmd.Load => "cv:LoadImage",
        CvDispMenuCmd.Save => "cv:SaveImage",
        CvDispMenuCmd.Pan => "cv:Pan",
        CvDispMenuCmd.Fit => "cv:FitView",
        CvDispMenuCmd.ZoomIn => "cv:ZoomIn",
        CvDispMenuCmd.ZoomOut => "cv:ZoomOut",
        CvDispMenuCmd.Clear => "cv:ClearGraphics",
        _ => throw new ArgumentOutOfRangeException(nameof(cmd), cmd, null),
    };

    /// <summary>묶음이 보이는가.</summary>
    internal static bool IsVisible(CvDispMenuGroup group, bool canLoad, bool isToolbarVisible) => group switch
    {
        CvDispMenuGroup.Load => canLoad,
        CvDispMenuGroup.Toolbar => isToolbarVisible,
        _ => true,
    };

    /// <summary>항목이 살아 있는가 — 저장만 프레임이 있어야 한다. 저장은 "지금 그릴 게 없다" 는 일시 상태라 감추지 않고 회색으로 남긴다
    /// (불러오기와 달리 화면의 성격 문제가 아니다).</summary>
    internal static bool IsEnabled(CvDispMenuCmd cmd, bool hasFrame) => cmd != CvDispMenuCmd.Save || hasFrame;
}
