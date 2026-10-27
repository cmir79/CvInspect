// 프로퍼티 편집기 행 VM 의 코어 회귀 — 행 VM·빌더·약한 구독 다리(CvPropWeakSub)가 코어(netstandard2.1)로 내려와
// WPF 없이 돈다. 그래서 Linux CI 에서도 같은 회귀가 돈다. 편집기 컨트롤(시각 트리·테마)은 Windows 스위트(CvInspect.Wpf.Tests) 몫이다.
// 여기서 못 박는 것: 소스 통지가 낸 스레드에서 동기로 행을 갱신한다 · 이름 필터 · 약한 구독(행이 소스에 붙잡히지 않는다)과
// 그 정리 시점 · Detach 가 구독과 늦은 커밋을 끊는다 · 다리 Dispose 는 몇 번·몇 스레드가 불러도 한 번만 푼다.
// 구독 수는 시험용 소스가 add/remove 호출을 직접 세어 본다 — 표준 이벤트는 없는 처리기를 빼도 조용하므로, 이중 해제는
// "remove 호출 수 > add 호출 수"(구독 수 음수)로만 드러난다.
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CvInspect.Controls;
using Xunit;

namespace CvInspect.Tests;

public class CvPropCoreTests
{
    /// <summary>단언 하나 — 실패 메시지가 곧 그 회귀가 존재하는 이유다.</summary>
    private static void Check(bool cond, string name) => Assert.True(cond, name);

    private static List<CvPropRowVm> Rows(IReadOnlyList<CvPropGroupVm> groups) => groups.SelectMany(g => g.Rows).ToList();

    private static T Row<T>(IReadOnlyList<CvPropGroupVm> groups, string label) where T : CvPropRowVm
        => Rows(groups).OfType<T>().Single(r => r.Label == label);

    private static IReadOnlyList<CvPropGroupVm> Build(object src, List<string>? committed = null)
        => CvPropRowBldr.Build(src, committed is null ? null : committed.Add, null, null, null);

    // ───────────── 통지 경로 ─────────────

    /// <summary>행은 소스가 PropertyChanged 를 낸 그 스레드에서, 그 호출이 돌아오기 전에 갱신된다. 다리는 디스패처를 모른다 —
    /// UI 스레드로 넘기는 일은 UI 패키지(편집기 컨트롤)의 몫이고, 코어가 비동기로 미루면 "소스를 바꾸고 바로 읽은 표시값" 이 옛 값이 된다.</summary>
    [Fact]
    public void SourceNotificationUpdatesTheRowSynchronouslyOnTheRaisingThread()
    {
        var src = new SampleSrc();
        var gain = Row<CvPropNumRowVm>(Build(src), "Gain");
        Check(gain.Text == "5", $"the row starts from the source value (text='{gain.Text}')");

        int? notifiedOn = null;
        gain.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CvPropNumRowVm.Text)) notifiedOn = Environment.CurrentManagedThreadId; };

        var workerId = 0;
        string? textRightAfter = null;
        var t = new Thread(() =>
        {
            workerId = Environment.CurrentManagedThreadId;
            src.Gain = 42;
            src.Raise(nameof(SampleSrc.Gain));
            textRightAfter = gain.Text;   // Raise 가 돌아온 직후 — 동기 갱신이면 이미 새 값이다
        });
        t.Start();
        t.Join();

        Check(notifiedOn is not null, "the row raised its own change notification at all (otherwise the thread checks below prove nothing)");
        Check(textRightAfter == "42", $"the row is updated before Raise returns on the raising thread — no deferral (text='{textRightAfter}')");
        Check(notifiedOn == workerId && workerId != Environment.CurrentManagedThreadId,
            $"the row notifies on the thread that raised the source event, not on the test thread (row={notifiedOn}, worker={workerId}, test={Environment.CurrentManagedThreadId})");
    }

    /// <summary>행은 자기 프로퍼티 이름의 통지만 받는다. 빈 이름(null 포함)은 "전부 바뀌었다" 라는 표준 의미라 모두 다시 읽는다.
    /// 필터가 없으면 아무 통지에나 모든 행이 소스를 다시 읽는다 — 값은 맞지만 getter 가 장치를 읽는 소스에서 통지 한 번이 행 수만큼의 읽기가 된다.</summary>
    [Fact]
    public void OnlyTheMatchingPropertyNameOrAnEmptyNameRefreshesTheRow()
    {
        var src = new SampleSrc();
        var gain = Row<CvPropNumRowVm>(Build(src), "Gain");

        src.Gain = 9;   // 통지 없이 backing 만 바꾼다 — 행이 다시 읽었는지가 Text 로 드러난다
        src.Raise(nameof(SampleSrc.Name));
        Check(gain.Text == "5", $"a notification for another property does not make this row re-read the source (text='{gain.Text}')");

        src.Raise(nameof(SampleSrc.Gain));
        Check(gain.Text == "9", $"its own property name does refresh it — the filter is not simply dropping everything (text='{gain.Text}')");

        src.Gain = 11;
        src.Raise(string.Empty);
        Check(gain.Text == "11", $"an empty property name means 'everything changed' and refreshes every row (text='{gain.Text}')");

        src.Gain = 12;
        src.Raise(null);
        Check(gain.Text == "12", $"a null property name means the same as an empty one (text='{gain.Text}')");
    }

    // ───────────── 약한 구독 ─────────────

    /// <summary>행을 만들고 참조를 모두 버린다 — 생성을 따로 떼어 둔 것은 JIT 가 시험 메서드 안의 지역 변수를 메서드 끝까지 살려 두는
    /// 일(Debug 빌드, 디버거 부착)을 피하려는 것이다. 돌려주는 것은 약한 참조뿐이다.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference> BuildAndDrop(object src)
        => Rows(Build(src)).Select(r => new WeakReference(r)).ToList();

    private static bool CollectUntilDead(List<WeakReference> refs)
    {
        for (var i = 0; i < 10 && refs.Any(w => w.IsAlive); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        return refs.All(w => !w.IsAlive);
    }

    /// <summary>오래 사는 소스(검사 인스턴스가 쥔 옵션 POCO)가 편집기를 붙잡으면 안 된다 — 편집기는 Detach 없이 화면에서 사라지기도 한다
    /// (창을 닫기만 하는 호스트). 소스는 다리를 강하게 쥐고 다리는 행을 약하게 쥐므로, 버린 행은 수거되고 다리는 소스가 다음 통지를
    /// 낼 때 스스로 풀린다(CvPropWeakSub 비고의 정리 시점). 대조군: 같은 런에서 강하게 쥔 행은 같은 GC 를 견디고 계속 갱신된다 —
    /// 그게 없으면 "수거됐다" 와 "애초에 아무것도 안 붙었다" 가 구별되지 않는다.</summary>
    [Fact]
    public void ARowNobodyHoldsIsCollectedAndItsBridgeLeavesOnTheNextNotification()
    {
        var dropped = new SampleSrc();
        var weak = BuildAndDrop(dropped);
        Check(weak.Count == 3 && dropped.SubCount() == 3, $"each built row subscribed to the source (rows={weak.Count}, subscriptions={dropped.SubCount()})");

        // 대조군 — 같은 빌더, Detach 하지 않은 채 강하게 쥔다.
        var held = new SampleSrc();
        var heldGroups = Build(held);
        var heldWeak = Rows(heldGroups).Select(r => new WeakReference(r)).ToList();

        var allDead = CollectUntilDead(weak);
        Check(allDead, $"rows nobody holds are collected even though the long-lived source still raises events — the source must not keep the editor alive (alive={weak.Count(w => w.IsAlive)}/{weak.Count})");
        Check(heldWeak.All(w => w.IsAlive), "control: rows still held survive the same collections (otherwise the line above could pass because the GC loop kills everything)");

        Check(dropped.SubCount() == 3,
            $"documented cleanup timing: the small bridges stay subscribed until the source next raises — if this changes, update the CvPropWeakSub remarks (subscriptions={dropped.SubCount()})");
        dropped.Raise(nameof(SampleSrc.Gain));
        Check(dropped.SubCount() == 0, $"the next notification finds the rows gone and every bridge unsubscribes itself (subscriptions={dropped.SubCount()})");
        dropped.Raise(nameof(SampleSrc.Gain));   // 풀린 뒤의 통지는 아무 일도 없다(다리가 남아 있으면 또 Dispose 하려 든다)
        Check(dropped.RemoveCalls() == 3, $"each bridge unsubscribed exactly once (removes={dropped.RemoveCalls()})");

        held.Gain = 77;
        held.Raise(nameof(SampleSrc.Gain));
        var heldGain = Row<CvPropNumRowVm>(heldGroups, "Gain");
        Check(heldGain.Text == "77" && held.SubCount() == 3,
            $"control: a held row keeps its subscription through the collections and still follows the source (text='{heldGain.Text}', subscriptions={held.SubCount()})");
        GC.KeepAlive(heldGroups);
    }

    // ───────────── Detach ─────────────

    /// <summary>편집기가 Source 를 바꾸거나 화면에서 내릴 때 옛 행을 떼어 낸다. 떼어진 행은 소스 통지를 더는 받지 않고(구독이 바로 풀린다 —
    /// 약한 구독의 "다음 통지 때" 를 기다리지 않는다), 화면에 남은 시각 트리가 늦게 커밋해도 폐기된 POCO 를 바꾸지 않는다.
    /// 중첩 행은 자식까지 뗀다. 두 번 불러도 된다(Source 교체와 언로드가 겹치는 호스트).</summary>
    [Fact]
    public void DetachUnsubscribesAndIgnoresLateCommitsDownToNestedChildren()
    {
        var src = new NestSrc();
        var child = (ChildSrc)src.Detail;
        var committed = new List<string>();
        var groups = Build(src, committed);
        var level = Row<CvPropNumRowVm>(groups, "Level");
        var name = Row<CvPropStrRowVm>(groups, "Tag");
        var on = Row<CvPropBoolRowVm>(groups, "On");
        var nested = Row<CvPropNestedRowVm>(groups, "Detail");
        var width = (CvPropNumRowVm)nested.Children.Single();
        Check(src.SubCount() == 4 && child.SubCount() == 1,
            $"before Detach the rows are subscribed — parent rows to the parent, the nested child row to the child (parent={src.SubCount()}, child={child.SubCount()})");

        CvPropRowBldr.Detach(groups);
        Check(src.SubCount() == 0 && child.SubCount() == 0,
            $"Detach unsubscribes at once, nested children included (parent={src.SubCount()}, child={child.SubCount()})");

        src.Level = 8;
        src.Raise(nameof(NestSrc.Level));
        Check(level.Text == "1", $"a detached row no longer follows the source (text='{level.Text}')");

        level.Text = "3";
        name.Value = "late";
        on.Value = true;
        width.Text = "99";
        Check(src.Level == 8 && src.Tag == "t" && !src.On && child.Width == 10,
            $"late commits from a detached row do not write into the discarded POCO (level={src.Level}, tag='{src.Tag}', on={src.On}, width={child.Width})");
        Check(committed.Count == 0, $"and they do not notify the host either (committed={string.Join(",", committed)})");

        CvPropRowBldr.Detach(groups);
        Check(src.SubCount() == 0 && child.SubCount() == 0 && src.RemoveCalls() == 4 && child.RemoveCalls() == 1,
            $"detaching twice is harmless — nothing is unsubscribed a second time (removes parent={src.RemoveCalls()}, child={child.RemoveCalls()})");
    }

    /// <summary>중첩 값이 다른 인스턴스로 바뀌면 자식을 다시 세우고, 옛 자식은 떼어 낸다(옛 인스턴스 구독이 바로 풀린다).
    /// 값만 갱신하면 옛 타입의 행이 새 인스턴스를 붙잡고 남아 화면은 그대로인데 쓰기가 엉뚱한 곳으로 간다 — Wpf 스위트의 같은 절을 VM 수준으로 옮긴 것.</summary>
    [Fact]
    public void SwappingTheNestedInstanceRebuildsAndDetachesTheOldChildren()
    {
        var src = new NestSrc();
        var oldChild = (ChildSrc)src.Detail;
        var nested = Row<CvPropNestedRowVm>(Build(src), "Detail");
        var oldWidth = (CvPropNumRowVm)nested.Children.Single();

        var ring = new RingSrc();
        src.Detail = ring;
        src.Raise(nameof(NestSrc.Detail));

        Check(nested.Children.Count == 1 && nested.Children.Single().Label == "Radius",
            $"child rows follow the new instance's type: {string.Join(", ", nested.Children.Select(c => c.Label))}");
        Check(oldChild.SubCount() == 0 && ring.SubCount() == 1,
            $"the replaced instance's rows are unsubscribed and the new one's are subscribed (old={oldChild.SubCount()}, new={ring.SubCount()})");
        oldWidth.Text = "99";
        Check(oldChild.Width == 10, $"a late edit on an old child row no longer reaches the discarded object (width={oldChild.Width})");
    }

    /// <summary>숫자 칸 커밋은 backing 에 쓰고 한 번 통지한다. 못 읽는 글자는 되돌려 backing 값을 다시 보이고 통지하지 않는다
    /// ("화면에 보이는 값 = 저장될 값"). Wpf 스위트의 EditsWriteBackAndNotify 를 VM 수준으로 옮긴 것 — 이제 Linux 에서도 돈다.</summary>
    [Fact]
    public void NumericCommitWritesBackOnceAndInvalidTextReverts()
    {
        var src = new SampleSrc();
        var committed = new List<string>();
        var gain = Row<CvPropNumRowVm>(Build(src, committed), "Gain");

        gain.Text = "7";
        Check(src.Gain == 7 && committed.SequenceEqual([nameof(SampleSrc.Gain)]),
            $"numeric commit writes back and notifies once (value={src.Gain}, committed={committed.Count})");

        gain.Text = "abc";
        Check(src.Gain == 7 && gain.Text == "7", $"invalid text is rejected and the row shows the backing value again (text='{gain.Text}')");
        Check(committed.Count == 1, "a rejected edit does not notify");
    }

    // ───────────── 다리 Dispose ─────────────

    /// <summary>다리의 Dispose 는 수거 감지(소스 통지 스레드)와 Detach(UI 스레드)가 동시에 부를 수 있다. 구독은 한 번만 풀려야 한다 —
    /// 두 번 풀면 시험용 소스에서는 구독 수가 음수가 되고, 실제 소스에서는 같은 처리기를 다른 경로로 한 번 더 붙인 경우 남의 구독을 떼어 낸다.
    /// 순차 이중 호출(가드 자체가 없는 변이)은 결정적으로 잡고, 동시 호출은 remove 를 늦춰 "확인 → 해제 → 표시" 순서로 짠 비원자 변이의 창을 벌린다.</summary>
    [Fact]
    public void BridgeDisposeUnsubscribesExactlyOnceEvenWhenRacing()
    {
        var row = new CvPropBoolRowVm();

        var seq = new SampleSrc();
        var s = new CvPropWeakSub(seq, row);
        Check(seq.SubCount() == 1, $"the bridge subscribes on construction (subscriptions={seq.SubCount()})");
        s.Dispose();
        s.Dispose();
        Check(seq.SubCount() == 0 && seq.RemoveCalls() == 1, $"disposing twice in a row unsubscribes once (removes={seq.RemoveCalls()})");

        const int rounds = 300;
        var over = 0;
        for (var i = 0; i < rounds; i++)
        {
            var src = new SampleSrc { RemoveSpinIters = 2000 };
            var sub = new CvPropWeakSub(src, row);
            using var gate = new Barrier(2);
            var a = new Thread(() => { gate.SignalAndWait(); sub.Dispose(); });
            var b = new Thread(() => { gate.SignalAndWait(); sub.Dispose(); });
            a.Start(); b.Start();
            a.Join(); b.Join();
            if (src.RemoveCalls() != 1 || src.SubCount() != 0) over++;
        }
        Check(over == 0, $"two threads disposing the same bridge at once unsubscribe once, never twice (bad rounds={over}/{rounds})");
        GC.KeepAlive(row);
    }

    /// <summary>Detach 직전에 이미 대리자를 떠 간 통지(다른 스레드에서 출발한 통지)는 떼어진 행을 갱신하지 않는다 —
    /// 중첩 행이면 그 갱신이 새 자식을 세워 아무도 떼지 않는 구독을 남긴다. 표준 필드형 이벤트 소스로 돈다
    /// (나머지 시험은 전부 add/remove 를 세는 손수 짠 접근자라 컴파일러가 만든 이벤트 경로를 따로 지난다).</summary>
    [Fact]
    public void ANotificationAlreadyInFlightWhenDetachRunsDoesNotRefreshTheDetachedRow()
    {
        var src = new PlainSrc();
        var groups = Build(src);
        var gain = Row<CvPropNumRowVm>(groups, "Gain");

        var inFlight = src.Handlers();
        Check(inFlight is not null, "building rows on a field-like event source subscribes to it");

        src.Gain = 8;
        inFlight!(src, new PropertyChangedEventArgs(nameof(PlainSrc.Gain)));
        Check(gain.Text == "8", $"control: the captured delegate does reach a live row (text='{gain.Text}')");

        CvPropRowBldr.Detach(groups);
        Check(src.Handlers() is null, "Detach removes every bridge from a standard field-like event");

        src.Gain = 9;
        inFlight(src, new PropertyChangedEventArgs(nameof(PlainSrc.Gain)));
        Check(gain.Text == "8", $"a notification captured before Detach must not refresh the detached row (text='{gain.Text}')");
    }

    // ───────────── 시험용 소스 ─────────────

    /// <summary>컴파일러가 만든 필드형 이벤트 소스. 처리기 목록은 메서드로만 내놓는다(공개 프로퍼티면 빌더가 행으로 펼친다).</summary>
    public sealed class PlainSrc : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        [DisplayName("Gain")] public int Gain { get; set; } = 5;

        internal PropertyChangedEventHandler? Handlers() => PropertyChanged;
    }

    /// <summary>구독 수를 세는 소스 — add/remove 호출을 직접 센다. 세는 값은 메서드로만 내놓는다(공개 프로퍼티면 빌더가 행으로 펼친다).</summary>
    public abstract class CountingSrc : INotifyPropertyChanged
    {
        private readonly object _gate = new();
        private PropertyChangedEventHandler? _handlers;
        private int _adds;
        private int _removes;

        /// <summary>remove 를 이만큼 늦춘다 — 동시 Dispose 의 경합 창을 벌리는 데만 쓴다.</summary>
        internal int RemoveSpinIters;

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { lock (_gate) { _handlers += value; _adds++; } }
            remove
            {
                if (RemoveSpinIters > 0) Thread.SpinWait(RemoveSpinIters);
                lock (_gate) { _handlers -= value; _removes++; }
            }
        }

        public int SubCount() { lock (_gate) return _adds - _removes; }

        public int RemoveCalls() { lock (_gate) return _removes; }

        /// <summary>통지 — 처리기 목록을 락 밖에서 부른다(처리기 안에서 다리가 remove 를 부르면 같은 락을 다시 잡는다).</summary>
        public void Raise(string? propertyName)
        {
            PropertyChangedEventHandler? h;
            lock (_gate) h = _handlers;
            h?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class SampleSrc : CountingSrc
    {
        [DisplayName("Gain")] public int Gain { get; set; } = 5;
        [DisplayName("Name")] public string Name { get; set; } = "a";
        [DisplayName("On")] public bool On { get; set; }
    }

    public sealed class NestSrc : CountingSrc
    {
        [DisplayName("Level")] public int Level { get; set; } = 1;
        [DisplayName("Tag")] public string Tag { get; set; } = "t";
        [DisplayName("On")] public bool On { get; set; }

        [DisplayName("Detail")]
        [TypeConverter(typeof(ExpandableObjectConverter))]
        public object Detail { get; set; } = new ChildSrc();
    }

    public sealed class ChildSrc : CountingSrc
    {
        [DisplayName("Width")] public int Width { get; set; } = 10;
    }

    public sealed class RingSrc : CountingSrc
    {
        [DisplayName("Radius")] public double Radius { get; set; } = 4.5;
    }
}
