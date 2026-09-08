// CvPropEditCtrl 회귀 — 코어 Opt POCO 가 attribute 만으로 편집기에 펼쳐지는지, 편집이 backing 에 쓰이고 통지되는지,
// 잘못된 입력이 되돌려지는지. 그리고 예제의 검사기가 합성 이미지에서 기하를 맞게 재는지(문서가 약속한 수치).
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using CvInspect.Controls;
using CvInspect.Demo;
using CvInspect.Vision;
using CvInspect.Vision.Opts;
using Xunit;

namespace CvInspect.Wpf.Tests;

public class CvPropEditCtrlTests
{
    private static void Check(bool cond, string name) => Assert.True(cond, name);

    private static void RunSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static T Row<T>(CvPropEditCtrl ctrl, string propName) where T : CvPropRowVm
        => ctrl.Groups.SelectMany(g => g.Rows).OfType<T>().Single(r => r.Label == CvLoc.T("cv:" + propName));

    [Fact]
    public void CoreOptUnfoldsThroughStandardAttributes() => RunSta(() =>
    {
        CvLoc.Culture = "en";
        var opt = new CvFindLineOpt();
        var ctrl = new CvPropEditCtrl { Source = opt };

        var labels = ctrl.Groups.SelectMany(g => g.Rows).Select(r => r.Label).ToList();
        Check(labels.Count > 0, "rows were built from a core option POCO");
        Check(!labels.Contains("StartX") && !labels.Contains("EndY"), "[Browsable(false)] geometry is hidden");
        Check(labels.Contains(CvLoc.T("cv:NumCalipers")), $"labels come from CvName via CvLoc: {string.Join(", ", labels)}");

        // 그룹 순서는 CvCategory 의 Order(ICvOrderedCategory) — 표시 문자열에 접두가 없어도 정렬된다.
        var headers = ctrl.Groups.Select(g => g.Header).ToList();
        Check(headers[0] == CvLoc.T("cv:CatCaliper") && headers.Contains(CvLoc.T("cv:CatEdge")),
            $"groups ordered by ICvOrderedCategory.Order: {string.Join(" > ", headers)}");
        Check(headers.All(h => !h.Contains(". ")), "headers carry no numeric prefix");
    });

    [Fact]
    public void EditsWriteBackAndNotify() => RunSta(() =>
    {
        CvLoc.Culture = "en";
        var opt = new CvFindLineOpt { NumCalipers = 10 };
        var ctrl = new CvPropEditCtrl { Source = opt };
        var committed = new List<string>();
        ctrl.Committed += (_, e) => committed.Add(e.PropertyName);

        var num = Row<CvPropNumRowVm>(ctrl, "NumCalipers");
        num.Text = "7";
        Check(opt.NumCalipers == 7 && committed.SequenceEqual([nameof(opt.NumCalipers)]), $"numeric commit writes back and notifies once (value={opt.NumCalipers}, committed={committed.Count})");

        num.Text = "abc";
        Check(opt.NumCalipers == 7 && num.Text == "7", $"invalid text is rejected and the row shows the backing value again (text='{num.Text}')");
        Check(committed.Count == 1, "a rejected edit does not notify");

        var pol = Row<CvPropEnumRowVm>(ctrl, "Polarity");
        pol.Value = CvEdgePolarity.DarkToLight;
        Check(opt.Polarity == CvEdgePolarity.DarkToLight && committed.Last() == nameof(opt.Polarity), "enum commit writes the enum value back");

        // Source 교체 뒤의 옛 행은 떼어져 있어야 한다 — 늦은 커밋이 폐기된 POCO 를 오염시키지 않는다.
        ctrl.Source = new CvFindLineOpt();
        num.Text = "3";
        Check(opt.NumCalipers == 7, "a detached row no longer writes to the old POCO");
    });

    [Fact]
    public void ActionRowExecutesTheDelegate() => RunSta(() =>
    {
        CvLoc.Culture = "en";
        var opt = new CvFindCircleOpt { AngleSpanDeg = 360 };
        var ctrl = new CvPropEditCtrl { Source = opt };
        var executed = new List<string>();
        ctrl.ActionExecuted += (_, e) => executed.Add(e.PropertyName);

        var act = Row<CvPropActionRowVm>(ctrl, "ToggleSpan");
        Check(act.ButtonText == CvLoc.T("cv:Execute"), $"action button text comes from cv:Execute ('{act.ButtonText}')");
        act.ExecuteCommand.Execute(null);
        Check(executed.SequenceEqual([nameof(opt.ToggleSpan)]), "ActionExecuted fires once with the property name");
        Check(opt.AngleSpanDeg != 360, $"the delegate ran and changed the option (span={opt.AngleSpanDeg})");
    });

    [Fact]
    public void DemoInspectorMeasuresTheSyntheticPart()
    {
        // 예제 README 가 약속한 것: 합성 판의 윗변은 수평(0°), 구멍 반경은 상수와 일치.
        using var img = DemoImage.Create();
        var r = new DemoInspector().Run(img);
        Check(r.IsOk, "both tools find their feature with default options");
        Check(r.Line is { } l && Math.Abs(l.AngleDeg) < 0.5 && l.RmsPx < 1.0,
            $"plate top edge measured horizontal: angle={r.Line?.AngleDeg:F3} rms={r.Line?.RmsPx:F3}");
        Check(r.Circle is { } c && Math.Abs(c.Radius - DemoImage.HoleRadius) < 1.5
              && Math.Abs(c.CenterX - DemoImage.HoleCenterX) < 1.0 && Math.Abs(c.CenterY - DemoImage.HoleCenterY) < 1.0,
            $"hole measured at its drawn geometry: r={r.Circle?.Radius:F2} center=({r.Circle?.CenterX:F2}, {r.Circle?.CenterY:F2})");
    }
}
