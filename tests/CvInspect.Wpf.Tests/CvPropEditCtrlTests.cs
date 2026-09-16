// CvPropEditCtrl 회귀 — 코어 Opt POCO 가 attribute 만으로 편집기에 펼쳐지는지, 편집이 backing 에 쓰이고 통지되는지,
// 잘못된 입력이 되돌려지는지. 그리고 예제의 검사기가 합성 이미지에서 기하를 맞게 재는지(문서가 약속한 수치).
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using CvInspect.Controls;
using CvInspect.Demo;
using CvInspect.Vision.Overlay;
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
    public void TipLeadsWithTheLabelThenTheDescription() => RunSta(() =>
    {
        // 라벨은 좁은 판에서 말줄임으로 잘리는 쪽이다 — 툴팁은 라벨 전문으로 시작하고, 설명이 있으면 그 아래 붙는다.
        CvLoc.Culture = "en";
        var ctrl = new CvPropEditCtrl { Source = new CvPatternOpt() };
        var rows = ctrl.Groups.SelectMany(g => g.Rows).ToList();
        Check(rows.Count > 0 && rows.All(r => r.Tip.StartsWith(r.Label + (r.Desc.Length > 0 ? "\n" : ""), StringComparison.Ordinal)), "every tip starts with the full label");
        Check(rows.Where(r => r.Desc.Length > 0).All(r => r.Tip.EndsWith("\n" + r.Desc, StringComparison.Ordinal)), "a description follows the label on its own line");
        Check(rows.Where(r => r.Desc.Length == 0).All(r => r.Tip == r.Label), "no description — the tip is just the label");
        Check(rows.Any(r => r.Desc.Length > 0), "the sample option has described rows, so the description branch was exercised");
    });

    [Fact]
    public void NestedObjectsAndListsBecomeRows() => RunSta(() =>
    {
        // 행 종류가 다섯(Action·bool·enum·수치·문자)뿐이던 동안 중첩 객체와 나열은 null 로 떨어져
        // 화면에서 통째로 사라졌다 — 모드별 세부 파라미터를 중첩으로 둔 POCO 는 편집할 방법이 없었다.
        var opt = new NestSampleOpt();
        var ctrl = new CvPropEditCtrl { Source = opt };
        var rows = ctrl.Groups.SelectMany(g => g.Rows).ToList();
        var committed = new List<string>();
        ctrl.Committed += (_, e) => committed.Add(e.PropertyName);

        var nested = rows.OfType<CvPropNestedRowVm>().Single(r => r.Label == "Detail");
        Check(nested.HasChildren && nested.Children.Single().Label == "Width",
            $"[TypeConverter(ExpandableObjectConverter)] unfolds into child rows: {string.Join(", ", nested.Children.Select(c => c.Label))}");
        Check(nested.IsEditable && rows.OfType<CvPropListRowVm>().All(r => r.IsEditable),
            "getter-only nested/list rows are not locked rows — they show or unfold, they do not assign the value itself");

        var box = (NestSampleOpt.BoxDetail)opt.Detail;
        ((CvPropNumRowVm)nested.Children.Single()).Text = "24";
        Check(box.Width == 24 && committed.SequenceEqual(["Width"]),
            $"a child row writes into the nested instance and notifies with the child property name (width={box.Width}, committed={string.Join(", ", committed)})");

        var list = rows.OfType<CvPropListRowVm>().Single(r => r.Label == "Ops");
        Check(list.Text == "blur" + Environment.NewLine + "threshold", $"an enumerable is listed line by line: '{list.Text}'");
        Check(rows.OfType<CvPropListRowVm>().Single(r => r.Label == "Spare").Text == "(empty)",
            "an empty list says (empty) — 'empty' and 'not read yet' must not look the same");
    });

    [Fact]
    public void SwappingTheNestedInstanceRebuildsItsChildren() => RunSta(() =>
    {
        // 모드 전환은 세부 파라미터 객체를 다른 타입으로 갈아 끼운다. 값만 갱신하면 옛 타입의 행이
        // 새 인스턴스를 붙잡고 남아 화면은 그대로인데 쓰기가 엉뚱한 곳으로 간다.
        var opt = new NestSampleOpt();
        var ctrl = new CvPropEditCtrl { Source = opt };
        var nested = ctrl.Groups.SelectMany(g => g.Rows).OfType<CvPropNestedRowVm>().Single(r => r.Label == "Detail");
        var box = (NestSampleOpt.BoxDetail)opt.Detail;
        var oldChild = (CvPropNumRowVm)nested.Children.Single();

        opt.Mode = NestSampleOpt.SampleMode.Ring;

        Check(nested.Children.Count == 1 && nested.Children.Single().Label == "Radius",
            $"child rows follow the new instance's type: {string.Join(", ", nested.Children.Select(c => c.Label))}");
        oldChild.Text = "99";
        Check(box.Width == 10, $"rows of the replaced instance are detached — a late edit no longer reaches the discarded object (width={box.Width})");
    });

    [Fact]
    public void SelfReferencingNestingStopsAtTheDepthLimit() => RunSta(() =>
    {
        // 순환을 못 끊으면 편집기를 여는 것만으로 스택이 넘친다. 한도에 닿은 자리는 아예 안 그린다 —
        // 자식 없는 소제목만 남기면 "펼칠 것이 없는 것"과 구별되지 않는다.
        var ctrl = new CvPropEditCtrl { Source = new SelfNestOpt() };
        var row = ctrl.Groups.SelectMany(g => g.Rows).OfType<CvPropNestedRowVm>().SingleOrDefault();

        var depth = 0;
        CvPropNestedRowVm? last = null;
        while (row is not null)
        {
            depth++;
            last = row;
            row = row.Children.OfType<CvPropNestedRowVm>().SingleOrDefault();
        }
        Check(depth == 3 && last is { HasChildren: false },
            $"a self-referencing expandable property stops at the nesting limit and leaves no half-drawn block (levels={depth})");
    });

    /// <summary>중첩·나열 회귀용 표본 — 모드에 따라 세부 파라미터가 다른 타입의 인스턴스로 교체된다.</summary>
    public sealed class NestSampleOpt : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public enum SampleMode { Box, Ring }

        private SampleMode _mode = SampleMode.Box;
        private object _detail = new BoxDetail();

        [DisplayName("Mode")]
        public SampleMode Mode
        {
            get => _mode;
            set
            {
                _mode = value;
                _detail = value == SampleMode.Box ? new BoxDetail() : new RingDetail();
                // 교체를 알리는 통지는 바뀐 프로퍼티(Detail) 로 나간다 — 중첩 행이 그것을 보고 자식을 다시 세운다.
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
            }
        }

        [DisplayName("Detail")]
        [TypeConverter(typeof(ExpandableObjectConverter))]
        public object Detail => _detail;

        [DisplayName("Ops")]
        public string[] Ops { get; } = ["blur", "threshold"];

        [DisplayName("Spare")]
        public string[] Spare { get; } = [];

        public sealed class BoxDetail
        {
            [DisplayName("Width")] public int Width { get; set; } = 10;
        }

        public sealed class RingDetail
        {
            [DisplayName("Radius")] public double Radius { get; set; } = 4.5;
        }
    }

    /// <summary>자기 자신을 값으로 들고 있는 표본 — 깊이 한도가 없으면 편집기를 여는 것만으로 끝나지 않는다.</summary>
    public sealed class SelfNestOpt
    {
        [DisplayName("Self")]
        [TypeConverter(typeof(ExpandableObjectConverter))]
        public object Self => this;
    }

    [Fact]
    public void DefaultRecipeMeasuresTheSyntheticPart()
    {
        // 예제 README 가 약속한 것: 기준 자세의 합성 판에서(픽스처 미학습이라 티칭 기하 그대로) 윗변은 수평(0°),
        // 구멍은 그린 자리·반경에, 판 안쪽의 어두운 영역은 구멍 하나뿐.
        using var img = DemoImage.Create();
        var recipe = DemoRecipe.Default();
        using var run = DemoRecipeRunner.Run(recipe, img);
        DemoToolResult Res(string key) => run.Tools.First(t => t.Tool.Key == key);
        Check(run.Tools.Where(t => t.Tool.Kind != DemoToolKind.Pattern).All(t => t.Ok), "line, circle and blob find their feature with default options");
        Check(!Res("fixture").Ok && Res("fixture").Summary.Contains("not trained"), $"the untrained fixture says so: {Res("fixture").Summary}");

        var seg = Res("top-edge").Graphic.Items.OfType<ViOverlaySeg>().Single(s => s.Color == ViOverlayColor.Green);
        var angle = Math.Atan2(seg.Y2 - seg.Y1, seg.X2 - seg.X1) * 180 / Math.PI;
        Check(Math.Abs(angle) < 0.5, $"plate top edge measured horizontal: angle={angle:F3}");

        var circle = Res("hole").Graphic.Items.OfType<ViOverlayPoly>().Single();
        var cx = circle.Points.Average(p => p.X);
        var cy = circle.Points.Average(p => p.Y);
        var radius = circle.Points.Average(p => Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy)));
        Check(Math.Abs(radius - DemoImage.HoleRadius) < 1.5 && Math.Abs(cx - DemoImage.HoleCenterX) < 1.0 && Math.Abs(cy - DemoImage.HoleCenterY) < 1.0,
            $"hole measured at its drawn geometry: r={radius:F2} center=({cx:F2}, {cy:F2})");

        var expectedArea = Math.PI * DemoImage.HoleRadius * DemoImage.HoleRadius;
        var blob = Res("hole-area");
        var area = double.Parse(System.Text.RegularExpressions.Regex.Match(blob.Graphic.Items.OfType<ViOverlayLabel>().Single().Text, @"[\d.]+").Value);
        Check(blob.Summary.StartsWith("n 1") && Math.Abs(area - expectedArea) / expectedArea < 0.05,
            $"hole is the single dark blob inside the plate: {blob.Summary} (expected area {expectedArea:F0})");
    }
}
