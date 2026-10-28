// CvPropEditCtrl 회귀 — 코어 Opt POCO 가 attribute 만으로 편집기에 펼쳐지는지, 편집이 backing 에 쓰이고 통지되는지,
// 잘못된 입력이 되돌려지는지. 행·빌더는 코어 것이라 CvInspect.Wpf 판과 같은 계약이다 — 이 판은 Avalonia 편집기가 그 행을 같은 뜻으로
// 들고 있는지와, 캡션·머리글 색이 호스트 테마 키를 따르는지를 본다.

using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using CvInspect.Avalonia.Controls;
using CvInspect.Controls;
using CvInspect.Vision;
using CvInspect.Vision.Edit;
using CvInspect.Vision.Opts;
using Xunit;
using static CvInspect.Avalonia.Tests.AvaTest;

namespace CvInspect.Avalonia.Tests;

public class CvPropEditCtrlTests
{
    private static T Row<T>(CvPropEditCtrl ctrl, string propName) where T : CvPropRowVm
        => ctrl.Groups.SelectMany(g => g.Rows).OfType<T>().Single(r => r.Label == CvLoc.T("cv:" + propName));

    /// <summary>실행 중 언어를 바꾼 뒤 편집기를 다시 지으면 머리글도 새 언어다. 머리글은 카테고리 attribute 인스턴스가 첫 조회 때 번역을
    /// 담아 두고 빌더가 그 인스턴스를 프로세스 수명 동안 쥐어, 그 타입을 처음 그린 언어로 굳었다 — 행 라벨은 새 언어가 되어 한 편집기
    /// 안에 두 언어가 섞였다. 코어의 CvCategory 와, GetLocalizedString 으로 번역하는 다른 카테고리 attribute 둘 다 본다.
    /// 머리글을 짓는 것은 코어 빌더지만, 편집기가 다시 지을 때 같은 길을 타는지까지 본다.</summary>
    [AvaloniaFact]
    public void HeadersFollowTheCultureOnRebuild()
    {
        var before = CvLoc.Culture;
        try
        {
            CvLoc.Culture = "en";
            SwitchingCategoryAttribute.Lang = "en";
            var ctrl = new CvPropEditCtrl { Source = new LocPoco() };
            var enHeaders = ctrl.Groups.Select(g => g.Header).ToList();
            Check(enHeaders.Contains("Threshold") && enHeaders.Contains("Others"), $"English headers first: [{string.Join(", ", enHeaders)}]");

            CvLoc.Culture = "ko";
            SwitchingCategoryAttribute.Lang = "ko";
            ctrl.Source = new LocPoco();
            var koHeaders = ctrl.Groups.Select(g => g.Header).ToList();
            var koLabel = ctrl.Groups.SelectMany(g => g.Rows).First().Label;
            Check(koLabel == "탐색", $"the row label follows the new culture (control, as before): {koLabel}");
            Check(koHeaders.Contains("임계값") && koHeaders.Contains("나머지") && !koHeaders.Contains("Threshold") && !koHeaders.Contains("Others"),
                $"after switching to Korean the rebuilt headers are Korean too, not frozen at English: [{string.Join(", ", koHeaders)}]");
        }
        finally
        {
            CvLoc.Culture = before;
            SwitchingCategoryAttribute.Lang = "en";
        }
    }

    private sealed class LocPoco
    {
        [CvCategory("cv:CatThreshold", 1), CvName("cv:CatSearch")] public int A { get; set; }
        [SwitchingCategory] public int B { get; set; }
    }

    /// <summary>표준 방식(GetLocalizedString)으로 번역하는 호스트 쪽 카테고리 attribute 의 모양.</summary>
    private sealed class SwitchingCategoryAttribute : CategoryAttribute
    {
        public static string Lang = "en";
        public SwitchingCategoryAttribute() : base("x") { }
        protected override string GetLocalizedString(string value) => Lang == "ko" ? "나머지" : "Others";
    }

    /// <summary>캡션·머리글 색은 호스트 테마 키(SecondaryTextBrush / PrimaryBrush)를 따르고, 실행 중 사전을 바꾸면 따라 바뀌며, 키가 없으면
    /// 기본색이다. 한 번 복사해 두면 테마를 바꿔도 처음 색으로 굳는다(실행 중 라이트/다크를 바꾸는 소비자가 있다).</summary>
    [AvaloniaFact]
    public void CaptionAndHeaderColorsFollowAHostThemeSwap()
    {
        CvLoc.Culture = "en";
        var ctrl = new CvPropEditCtrl { Source = new CvPatternOpt() };
        var host = new Grid();
        host.Children.Add(ctrl);
        static ResourceDictionary Theme(byte v) => new()
        {
            ["SecondaryTextBrush"] = new SolidColorBrush(Color.FromRgb(v, 0x11, 0x11)),
            ["PrimaryBrush"] = new SolidColorBrush(Color.FromRgb(v, 0x22, 0x22)),
        };
        Color? Fg(Func<TextBlock, bool> pick)
            => ctrl.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => !string.IsNullOrEmpty(t.Text) && t.IsEffectivelyVisible && pick(t))?.Foreground is
                ISolidColorBrush b ? b.Color : null;
        Color? Desc() => Fg(t => t.Classes.Contains("cvPropDesc"));
        Color? Head() => Fg(t => t.FontWeight == FontWeight.Bold);

        host.Resources.MergedDictionaries.Add(Theme(0x40));
        Shoot(host, 400, 2000);
        Check(Desc() is not null && Head() is not null, "the sample option draws a caption and a group header (otherwise nothing below is checked)");
        Check(Desc() == Color.FromRgb(0x40, 0x11, 0x11) && Head() == Color.FromRgb(0x40, 0x22, 0x22),
            $"captions and headers take the host keys: desc={Desc()} head={Head()}");

        host.Resources.MergedDictionaries[0] = Theme(0x80);
        Shoot(host, 400, 2000);
        Check(Desc() == Color.FromRgb(0x80, 0x11, 0x11) && Head() == Color.FromRgb(0x80, 0x22, 0x22),
            $"a theme swap at run time repaints them, not frozen at the first theme: desc={Desc()} head={Head()}");

        host.Resources.MergedDictionaries.Clear();
        Shoot(host, 400, 2000);
        Check(Desc() == Color.FromRgb(0x80, 0x80, 0x80) && Head() == Color.FromRgb(0x15, 0x65, 0xC0),
            $"without the keys they fall back to the fixed colors: desc={Desc()} head={Head()}");
    }

    [AvaloniaFact]
    public void CoreOptUnfoldsThroughStandardAttributes()
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
    }

    [AvaloniaFact]
    public void EditsWriteBackAndNotify()
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
    }

    [AvaloniaFact]
    public void GripDragUpdatesTheNumberRowLive()
    {
        // 밴드 그립을 끌면 opt 값은 바뀌는데 숫자 칸은 그대로였다 — 행은 소스의 PropertyChanged 로만 다시 그리는데 opt 가 통지하지 않았다.
        // 그립 → 칸, 칸 → opt 두 길을 같이 본다(통지가 setter 에서 나면 칸 입력이 제 통지로 되돌아 들어온다 — 그래도 커밋은 한 번,
        // 칸은 정규 표기여야 한다).
        CvLoc.Culture = "en";
        var opt = new CvRingFillOpt { RMinPx = 20, RMaxPx = 60 };
        var ctrl = new CvPropEditCtrl { Source = opt };
        var committed = new List<string>();
        ctrl.Committed += (_, e) => committed.Add(e.PropertyName);
        var rows = ctrl.Groups.SelectMany(g => g.Rows).OfType<CvPropNumRowVm>().ToList();
        var rMin = rows.Single(r => r.Label == CvLoc.T("cv:RingRMin"));
        var rMax = rows.Single(r => r.Label == CvLoc.T("cv:RingRMax"));
        Check(rMin.Text == "20" && rMax.Text == "60", $"rows start from the opt (min '{rMin.Text}', max '{rMax.Text}')");

        var ring = Assert.IsType<CvEditRing>(Assert.Single(CvShapeBinder.For(opt, () => { })!));
        ring.RMax = 75;
        ring.RMin = 33;
        Check(rMin.Text == "33" && rMax.Text == "75", $"dragging the band grips updates both number rows at once (min '{rMin.Text}', max '{rMax.Text}')");
        Check(committed.Count == 0, "a grip drag is not a row commit — the host already hears it through onEdited");

        rMin.Text = "25.0";
        Check(opt.RMinPx == 25 && rMin.Text == "25", $"typing still writes back and shows the canonical text (value={opt.RMinPx}, text='{rMin.Text}')");
        Check(committed.SequenceEqual([nameof(opt.RMinPx)]), $"one typed value is one commit even though the opt now notifies (commits={string.Join(",", committed)})");
    }

    [AvaloniaFact]
    public void ActionRowExecutesTheDelegate()
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
    }

    [AvaloniaFact]
    public void TipLeadsWithTheLabelThenTheDescription()
    {
        // 라벨은 좁은 판에서 말줄임으로 잘리는 쪽이다 — 툴팁은 라벨 전문으로 시작하고, 설명이 있으면 그 아래 붙는다.
        CvLoc.Culture = "en";
        var ctrl = new CvPropEditCtrl { Source = new CvPatternOpt() };
        var rows = ctrl.Groups.SelectMany(g => g.Rows).ToList();
        Check(rows.Count > 0 && rows.All(r => r.Tip.StartsWith(r.Label + (r.Desc.Length > 0 ? "\n" : ""), StringComparison.Ordinal)), "every tip starts with the full label");
        Check(rows.Where(r => r.Desc.Length > 0).All(r => r.Tip.EndsWith("\n" + r.Desc, StringComparison.Ordinal)), "a description follows the label on its own line");
        Check(rows.Where(r => r.Desc.Length == 0).All(r => r.Tip == r.Label), "no description — the tip is just the label");
        Check(rows.Any(r => r.Desc.Length > 0), "the sample option has described rows, so the description branch was exercised");
    }

    [AvaloniaFact]
    public void NestedObjectsAndListsBecomeRows()
    {
        // 중첩 객체와 나열이 null 로 떨어지면 화면에서 통째로 사라진다 — 모드별 세부 파라미터를 중첩으로 둔 POCO 는 편집할 방법이 없다.
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
    }

    [AvaloniaFact]
    public void SwappingTheNestedInstanceRebuildsItsChildren()
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
    }

    [AvaloniaFact]
    public void SelfReferencingNestingStopsAtTheDepthLimit()
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
    }

    [AvaloniaFact]
    public void LongListsStopAtTheItemLimit()
    {
        // 상한이 없으면 큰 배열이 닿는 순간 항목마다 한 줄이 되어 편집기를 여는 것만으로 화면이 굳는다. 끝까지 세지 않는 것이 핵심이다 —
        // 지연 열거가 무한이면 세는 순간 갇힌다(그래서 이 절은 상한이 있는 판에서만 끝난다. 상한이 없으면 실패가 아니라 정지로 나타난다).
        var ctrl = new CvPropEditCtrl { Source = new LongListSampleOpt() };
        var rows = ctrl.Groups.SelectMany(g => g.Rows).OfType<CvPropListRowVm>().ToList();
        string[] Lines(string label) => rows.Single(r => r.Label == label).Text.Split(Environment.NewLine);

        var big = Lines("Big");
        Check(big.Length == 21 && big[^1] == "… (+980)",
            $"a 1000-item array is cut at the limit and says how many are left: {big.Length} lines, last='{big[^1]}'");

        var endless = Lines("Endless");
        Check(endless.Length == 21 && endless[^1] == "…",
            $"an endless lazy sequence is cut without being counted to the end: {endless.Length} lines, last='{endless[^1]}'");

        Check(Lines("Tiny").Length == 3, $"a list under the limit is still shown whole ({Lines("Tiny").Length} lines)");
    }

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

    [AvaloniaFact]
    public void APropertyThatThrowsWhenReadIsSkippedInsteadOfKillingTheEditor()
    {
        // 나열 행이 있으면 편집기는 아무도 읽지 않던 getter 를 읽고 그 자리에서 열거한다. 가드가 없으면 그 예외가 Source 대입 밖으로 나가고,
        // 대개 잡는 사람이 없어 앱이 그대로 죽는다.
        var warned = new List<string>();
        var prev = CvLog.Sink;
        CvLog.Sink = (lv, _, msg, _) => { if (lv == CvLogLevel.Warning) warned.Add(msg); };
        warned.Clear();   // 붙는 순간 그동안 붙잡혀 있던 줄이 흘러든다
        try
        {
            // 이 대입이 던지면 이 절은 예외로 끝난다 — 그 자체가 회귀 신호다.
            var ctrl = new CvPropEditCtrl { Source = new ThrowingSampleOpt() };
            var labels = ctrl.Groups.SelectMany(g => g.Rows).Select(r => r.Label).ToList();

            Check(labels.Contains("Good"), $"the rows that can be read are still built: {string.Join(", ", labels)}");
            Check(!labels.Contains("Bad") && !labels.Contains("Racing"),
                $"a property that throws while being read is left out rather than shown with a value nobody read: {string.Join(", ", labels)}");
            Check(warned.Count(m => m.Contains("Bad")) == 1 && warned.Count(m => m.Contains("Racing")) == 1,
                $"each skipped row says which property failed and why — caught, not swallowed: [{string.Join(" | ", warned)}]");
        }
        finally
        {
            CvLog.Sink = prev;
        }
    }

    /// <summary>읽기 가드 회귀용 표본 — 장치를 그때 읽다 실패하는 프로퍼티와, 열거 도중 깨지는 목록.</summary>
    public sealed class ThrowingSampleOpt
    {
        [DisplayName("Good")] public int Good { get; set; } = 3;

        [DisplayName("Bad")] public string[] Bad => throw new InvalidOperationException("device not ready");

        [DisplayName("Racing")] public IEnumerable<int> Racing => Racy();

        // 다른 스레드가 채우는 목록을 편집기가 여는 순간 열거하는 자리를 본뜬다.
        private static IEnumerable<int> Racy()
        {
            yield return 1;
            yield return 2;
            throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
        }
    }

    /// <summary>나열 상한 회귀용 표본 — 상한을 넘는 배열과, 끝까지 세면 갇히는 무한 지연 열거.</summary>
    public sealed class LongListSampleOpt
    {
        [DisplayName("Big")] public byte[] Big { get; } = new byte[1000];

        [DisplayName("Endless")] public IEnumerable<int> Endless => Forever();

        [DisplayName("Tiny")] public byte[] Tiny { get; } = new byte[3];

        private static IEnumerable<int> Forever()
        {
            for (var i = 0; ; i++) yield return i;
        }
    }

    /// <summary>자기 자신을 값으로 들고 있는 표본 — 깊이 한도가 없으면 편집기를 여는 것만으로 끝나지 않는다.</summary>
    public sealed class SelfNestOpt
    {
        [DisplayName("Self")]
        [TypeConverter(typeof(ExpandableObjectConverter))]
        public object Self => this;
    }
}
