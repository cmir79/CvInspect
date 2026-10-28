// 속성 편집기의 UI 무관 타입 14개는 0.34 에 코어로 옮겼고, Wpf 어셈블리는 그 이름들을 코어로 전달한다(CvPropForwards.cs).
// 0.33 에 대고 컴파일한 소비자(코드·BAML)는 이 타입들을 "CvInspect.Wpf 안의" 타입으로 찾는다 — 전달 한 줄이 빠지면
// 우리 빌드와 시험은 초록인 채로 그 소비자만 실행 중에 TypeLoadException 으로 죽는다. 그래서 목록을 0.33 기준 고정값으로 못 박는다.
using CvInspect.Controls;
using Xunit;

namespace CvInspect.Wpf.Tests;

public class CvPropForwardTests
{
    // 0.33 의 CvInspect.Wpf 가 정의하던 공개 타입 중 0.34 에 코어로 옮긴 것 — 줄이지 않는다.
    private static readonly string[] Moved =
    [
        "CvInspect.Controls.CvPropGroupVm",
        "CvInspect.Controls.CvPropRowVm",
        "CvInspect.Controls.CvPropBoolRowVm",
        "CvInspect.Controls.CvPropNumRowVm",
        "CvInspect.Controls.CvPropStrRowVm",
        "CvInspect.Controls.CvPropEnumRowVm",
        "CvInspect.Controls.CvPropActionRowVm",
        "CvInspect.Controls.CvPropListRowVm",
        "CvInspect.Controls.CvPropNestedRowVm",
        "CvInspect.Controls.CvPropRowBldr",
        "CvInspect.Controls.CvPropCommittedEvt",
        "CvInspect.Controls.CvPropActionExecutingEvt",
        "CvInspect.Controls.CvPropActionExecutedEvt",
        "CvInspect.Controls.CvPropActionFailedEvt",
    ];

    [Fact]
    public void WpfAssemblyForwardsEveryMovedTypeToTheCore()
    {
        var wpf = typeof(CvPropEditCtrl).Assembly;
        var core = typeof(CvPropRowBldr).Assembly;
        Assert.NotSame(wpf, core);

        var forwarded = wpf.GetForwardedTypes().Select(t => t.FullName!).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.True(forwarded.SequenceEqual(Moved.OrderBy(n => n, StringComparer.Ordinal)),
            "CvInspect.Wpf must forward exactly the 14 types that moved to the core in 0.34 — a missing forward makes consumers built against 0.33 fail with TypeLoadException at run time, while our own build stays green. Forwarded now: "
            + string.Join(", ", forwarded));

        foreach (var name in Moved)
        {
            // 소비자 런타임이 하는 것과 같은 조회 — Wpf 어셈블리에 이름으로 물으면 코어 타입이 나와야 한다.
            var t = wpf.GetType(name, throwOnError: false);
            Assert.True(t is not null && t.Assembly == core,
                $"{name} looked up through CvInspect.Wpf must resolve to the CvInspect core type (old binaries and BAML resolve it this way)");
        }

        // 살아 있음 대조: Wpf 에 그대로 남은 타입은 Wpf 에서 정의로 나오고, 없는 이름은 null 이다.
        Assert.Same(wpf, wpf.GetType("CvInspect.Controls.CvPropEditCtrl", throwOnError: false)?.Assembly);
        Assert.Null(wpf.GetType("CvInspect.Controls.CvPropNoSuchRowVm", throwOnError: false));
    }
}
