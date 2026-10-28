// 헤드리스 시험 앱 — 모든 [AvaloniaFact]/[AvaloniaTheory] 가 이 앱의 UI 스레드에서 돈다.
// 화면을 잡으려면 Skia 렌더러에 헤드리스 그리기를 꺼야 한다(켜 두면 그리기가 픽셀을 남기지 않아 캡처가 던진다).
// 기본 글꼴은 Inter 로 고정한다 — 기본 글꼴을 OS 에 맡기면 Windows·Linux 러너의 글자 폭이 달라 같은 시험의 픽셀이 갈린다.
// 데이터 라벨·상태 줄은 컨트롤이 정한 고정폭 글꼴 목록(Consolas → DejaVu Sans Mono …)을 그대로 쓰므로 OS 마다 다르다 —
// 그것을 재는 시험은 비율·상대 비교로 판정한다.

using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(CvInspect.Avalonia.Tests.TestApp))]

// 로그 싱크(CvLog.Sink)·번역 문화권·명령 경고 표는 프로세스 전역이다. 병렬로 돌리면 한 시험이 바꿔 둔 로그 싱크를 다른 시험이
// 보게 되어 경고 건수 단언이 흔들린다. 헤드리스 세션도 UI 스레드 하나라 병렬로 얻을 것이 없다.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace CvInspect.Avalonia.Tests;

/// <summary>시험용 앱 — 표준 컨트롤에 템플릿을 주려고 Fluent 테마를 싣는다(컨트롤 패키지는 테마를 강제하지 않는다).</summary>
public sealed class TestApp : Application
{
    /// <summary>기본 글꼴 이름 — Inter 패키지가 등록하는 글꼴 모음 주소.</summary>
    public const string DefaultFont = "fonts:Inter#Inter";

    public override void Initialize() => Styles.Add(new FluentTheme());

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseSkia()
        .WithInterFont()
        .With(new FontManagerOptions { DefaultFamilyName = DefaultFont })
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
