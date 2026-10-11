// CvDispCtrl 카메라 버튼(📸 단발 · ⏯ 라이브)이 호스트가 넣은 명령의 CanExecute 를 따르는지.
// 0.30.0 까지는 두 버튼이 Execute 를 직접 불렀다 — CanExecute 가 false 여도 실행됐고, 버튼은 회색이 되지 않았고,
// CanExecuteChanged 도 듣지 않았다. 촬영 중에 명령을 막아 둔 화면에서도 버튼이 살아 있는 채로 눌렸다.
// 클릭은 두 길로 부른다: 자동화 피어(사람의 클릭처럼 IsEnabled 를 먼저 본다)와, IsEnabled 를 건너뛰고 처리기에 바로 닿는 길
// (Click 이벤트 직접 발생 · IsChecked 직접 대입) — 뒤쪽은 버튼이 회색이 되기 전에 이미 들어와 있던 클릭이 지나는 길이다.
// WPF 요소는 STA 스레드에서만 만들 수 있어 본문을 STA 로 감싼다.
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CvInspect.Controls;
using Xunit;

namespace CvInspect.Wpf.Tests;

public class CvDispCtrlCommandTests
{
    /// <summary>단언 하나 — 실패 메시지가 곧 그 회귀가 존재하는 이유다.</summary>
    private static void Check(bool cond, string name) => Assert.True(cond, name);

    /// <summary>STA 스레드에서 실행 — 본문의 예외는 원래 스택 그대로 밖으로 던진다.</summary>
    private static void RunSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>디스패처 큐를 비운다 — 자동화 피어의 클릭과 다른 스레드에서 넘어온 갱신이 여기서 돈다.</summary>
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    /// <summary>배치까지 마친 컨트롤과 두 카메라 버튼(내부에서 만들어져 이름이 없어 툴팁으로 찾는다).</summary>
    private static (CvDispCtrl Ctrl, Button Grab, ToggleButton Play) Make()
    {
        var ctrl = new CvDispCtrl();
        ctrl.Measure(new Size(800, 600));
        ctrl.Arrange(new Rect(0, 0, 800, 600));
        ctrl.UpdateLayout();
        var grab = FindButton(ctrl, "Grab");
        var play = FindToggle(ctrl, "Play / Stop");
        Check(grab is not null && play is not null, "the 📸 and ⏯ buttons are found");
        return (ctrl, grab!, play!);
    }

    /// <summary>사람의 📸 클릭. 피어는 회색 버튼이면 ElementNotEnabledException 을 던지고, 아니면 클릭을 Input 우선순위로 큐에
    /// 넣는다(WPF 원문 확인) — 그래서 큐까지 비운다. 비우지 않으면 실행 횟수가 늘 0 으로 읽혀 "막았다" 가 거짓으로 통과한다.</summary>
    private static Exception? PeerClick(Button b)
    {
        var ex = Record.Exception(() => ((IInvokeProvider)new ButtonAutomationPeer(b)).Invoke());
        Pump();
        return ex;
    }

    /// <summary>사람의 ⏯ 클릭 — 피어는 동기로 토글한다(회색이면 던진다).</summary>
    private static Exception? PeerToggle(ToggleButton t)
        => Record.Exception(() => ((IToggleProvider)new ToggleButtonAutomationPeer(t)).Toggle());

    /// <summary>IsEnabled 를 건너뛰고 📸 처리기에 바로 닿는 클릭.</summary>
    private static void RawClick(Button b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static string Name(Exception? ex) => ex?.GetType().Name ?? "nothing";

    /// <summary>(1) CanExecute 가 false 인 명령은 실행되지 않고 버튼이 회색이다. ⏯ 는 지금 보이는 쪽의 명령이 정한다 — 멈춰 있으면
    /// ContinuousCommand, 돌고 있으면 StopCommand. 시작 명령이 막혔다고 정지까지 막으면 라이브를 끌 길이 없어진다.</summary>
    [Fact]
    public void AFalseCanExecuteGreysTheButtonAndRunsNothing() => RunSta(() =>
    {
        var (ctrl, grab, play) = Make();

        // 대조 — 같은 두 클릭 길이 실행 가능한 명령에서는 실제로 Execute 에 닿는다. 이것이 0 이면 아래의 0 은 아무것도 증명하지 않는다.
        var okCmd = new ProbeCmd { Can = true };
        ctrl.GrabCommand = okCmd;
        RawClick(grab);
        var okPeer = PeerClick(grab);
        Check(okPeer is null && okCmd.Runs == 2,
            $"control: both click paths reach Execute for a runnable command — ran {okCmd.Runs}x (want 2), peer click threw {Name(okPeer)}");

        var grabCmd = new ProbeCmd { Can = false };
        var liveCmd = new ProbeCmd { Can = false };
        var stopCmd = new ProbeCmd { Can = true };
        ctrl.GrabCommand = grabCmd;
        ctrl.ContinuousCommand = liveCmd;
        ctrl.StopCommand = stopCmd;

        // 📸
        var grabEnabled = grab.IsEnabled;
        var peer = PeerClick(grab);
        RawClick(grab);
        Check(!grabEnabled && peer is ElementNotEnabledException && grabCmd.Runs == 0,
            $"a Grab command whose CanExecute is false greys 📸 out and never runs — enabled={grabEnabled}, peer click threw {Name(peer)}, Execute ran {grabCmd.Runs}x (want 0)");

        // ⏯ 멈춤 쪽 — ContinuousCommand 가 정한다. IsChecked 직접 대입은 토글이 하는 일 그대로다(OnToggle).
        var stoppedEnabled = play.IsEnabled;
        var peerT = PeerToggle(play);
        play.IsChecked = true;
        Check(!stoppedEnabled && peerT is ElementNotEnabledException && liveCmd.Runs == 0 && play.IsChecked == false,
            $"a stopped ⏯ whose Continuous command cannot run is greyed out, starts nothing and stays released — enabled={stoppedEnabled}, " +
            $"peer toggle threw {Name(peerT)}, Execute ran {liveCmd.Runs}x (want 0), checked={play.IsChecked}");

        // ⏯ 도는 쪽 — StopCommand 가 정한다
        ctrl.IsRunning = true;
        Check(play.IsChecked == true && play.IsEnabled,
            $"a running ⏯ is governed by the Stop command, not the blocked Continuous one — a blocked start must not block stopping (checked={play.IsChecked}, enabled={play.IsEnabled})");
        stopCmd.Can = false;
        stopCmd.Raise();
        var runningEnabled = play.IsEnabled;
        var peerS = PeerToggle(play);
        play.IsChecked = false;
        Check(!runningEnabled && peerS is ElementNotEnabledException && stopCmd.Runs == 0 && play.IsChecked == true,
            $"a running ⏯ whose Stop command cannot run is greyed out, stops nothing and stays pressed — enabled={runningEnabled}, " +
            $"peer toggle threw {Name(peerS)}, Execute ran {stopCmd.Runs}x (want 0), checked={play.IsChecked}");
    });

    /// <summary>(2) CanExecuteChanged 를 듣는다 — 막혔던 명령이 풀렸다고 알리면 회색이 풀리고 클릭이 한 번 실행된다. 듣지 않으면 한 번
    /// 회색이 된 버튼은 그 알림 말고는 풀릴 신호가 없어 굳는다.</summary>
    [Fact]
    public void CanExecuteChangedReenablesTheButtonAndTheClickRunsOnce() => RunSta(() =>
    {
        var (ctrl, grab, play) = Make();
        var grabCmd = new ProbeCmd { Can = false };
        var liveCmd = new ProbeCmd { Can = false };
        ctrl.GrabCommand = grabCmd;
        ctrl.ContinuousCommand = liveCmd;
        Check(!grab.IsEnabled && !play.IsEnabled,
            $"both buttons start greyed out — else the re-enable below proves nothing (📸 enabled={grab.IsEnabled}, ⏯ enabled={play.IsEnabled})");

        grabCmd.Can = true;
        grabCmd.Raise();
        liveCmd.Can = true;
        liveCmd.Raise();
        Check(grab.IsEnabled && play.IsEnabled,
            $"CanExecuteChanged turning true re-enables both buttons (📸 enabled={grab.IsEnabled}, ⏯ enabled={play.IsEnabled})");

        var peer = PeerClick(grab);
        var peerT = PeerToggle(play);
        Check(peer is null && grabCmd.Runs == 1, $"the re-enabled 📸 runs its command exactly once per click (ran {grabCmd.Runs}x, peer threw {Name(peer)})");
        Check(peerT is null && liveCmd.Runs == 1 && play.IsChecked == false,
            $"the re-enabled ⏯ runs Continuous exactly once and still shows IsRunning, which nothing set (ran {liveCmd.Runs}x, checked={play.IsChecked}, peer threw {Name(peerT)})");

        grabCmd.Can = false;
        grabCmd.Raise();
        Check(!grab.IsEnabled, $"CanExecuteChanged turning false greys 📸 out again (enabled={grab.IsEnabled})");
    });

    /// <summary>(3) 명령을 갈아 끼우면 옛 명령의 구독을 놓는다 — 명령은 대개 컨트롤보다 오래 사는 view-model 이 들고 있다. 걸었는지부터
    /// 센다: 걸지 않는 판은 "놓았다" 가 공짜로 참이 된다. 속성마다 다른 명령 인스턴스를 쓴다(한 명령을 두 속성에 주면 구독이 둘이라 셈이
    /// 섞인다). null 로 돌리면 버튼은 종전처럼 살아 있다.</summary>
    [Theory]
    [InlineData(nameof(CvDispCtrl.GrabCommand))]
    [InlineData(nameof(CvDispCtrl.ContinuousCommand))]
    [InlineData(nameof(CvDispCtrl.StopCommand))]
    public void SwappingACommandReleasesTheOldSubscription(string prop) => RunSta(() =>
    {
        var dp = prop switch
        {
            nameof(CvDispCtrl.GrabCommand) => CvDispCtrl.GrabCommandProperty,
            nameof(CvDispCtrl.ContinuousCommand) => CvDispCtrl.ContinuousCommandProperty,
            _ => CvDispCtrl.StopCommandProperty,
        };
        var (ctrl, grab, play) = Make();
        if (dp == CvDispCtrl.StopCommandProperty) ctrl.IsRunning = true;   // ⏯ 는 도는 동안 StopCommand 를 따른다
        ButtonBase button = dp == CvDispCtrl.GrabCommandProperty ? grab : play;

        var a = new ProbeCmd { Can = true };
        var b = new ProbeCmd { Can = false };
        ctrl.SetValue(dp, a);
        Check(a.Subscribers == 1,
            $"{prop}: assigning a command subscribes to its CanExecuteChanged exactly once — without it the button never hears the command change its mind (subscribers={a.Subscribers}, adds={a.Adds})");

        ctrl.SetValue(dp, b);
        Check(a.Subscribers == 0 && a.Removes == a.Adds && b.Subscribers == 1,
            $"{prop}: replacing it releases the old command's subscription — a long-lived command would otherwise keep reaching a control it no longer belongs to " +
            $"(old: subscribers={a.Subscribers} adds={a.Adds} removes={a.Removes}; new: subscribers={b.Subscribers})");
        Check(!button.IsEnabled, $"{prop}: the button follows the new command at once (enabled={button.IsEnabled}, the new one cannot run)");

        ctrl.SetValue(dp, null);
        Check(b.Subscribers == 0 && button.IsEnabled,
            $"{prop}: clearing it releases that subscription too and the button is live again, as with no command (subscribers={b.Subscribers}, enabled={button.IsEnabled})");
    });

    /// <summary>(4) 명령이 없으면 종전 그대로다 — 두 버튼은 살아 있고, 눌러도 아무 일도 없고, ⏯ 는 IsRunning 을 보인다.</summary>
    [Fact]
    public void NullCommandsKeepTheOldBehaviour() => RunSta(() =>
    {
        var (ctrl, grab, play) = Make();
        Check(grab.IsEnabled && play.IsEnabled, $"with no commands both buttons stay enabled (📸 {grab.IsEnabled}, ⏯ {play.IsEnabled})");
        var peer = PeerClick(grab);
        Check(peer is null, $"clicking 📸 with no command does nothing and throws nothing ({Name(peer)})");
        var t1 = PeerToggle(play);
        Check(t1 is null && play.IsChecked == false, $"⏯ with no Continuous command starts nothing and shows IsRunning (released) — threw {Name(t1)}, checked={play.IsChecked}");
        ctrl.IsRunning = true;
        var t2 = PeerToggle(play);
        Check(t2 is null && play.IsEnabled && play.IsChecked == true,
            $"...and while running with no Stop command it stays enabled and pressed — threw {Name(t2)}, enabled={play.IsEnabled}, checked={play.IsChecked}");
    });

    /// <summary>구독이 컨트롤을 붙잡지 않는다 — 명령은 대개 화면보다 오래 사는 view-model 이 들고 있고, 붙잡힌 컨트롤은 백버퍼·비주얼
    /// 트리째 수집되지 않는다(편집 도형 구독에서 실측한 모양). 대조: 같은 명령에 직접 건 구독자는 같은 수집에서 살아남는다 — 이 탐침이
    /// 명령을 통한 붙잡힘을 볼 수 있다는 증거다. 구독이 실제로 걸렸는지도 센다(안 걸린 판은 수집이 공짜로 된다).
    /// 수집 전에 디스패처 큐를 비운다 — 생성 중에 WPF 가 큐에 넣은 일이 컨트롤을 잠시 붙잡아, 비우지 않으면 명령이 없는 컨트롤도
    /// 살아 있다(실측: Grid·Button·ContextMenu 단독은 비우지 않아도 수집되고, CvDispCtrl 은 비운 뒤에만 수집된다). 실제 화면의 UI 스레드는
    /// 늘 큐를 돌린다.</summary>
    [Fact]
    public void ALongLivedCommandDoesNotKeepADroppedControlAlive() => RunSta(() =>
    {
        var grab = new ProbeCmd { Can = true };
        var live = new ProbeCmd { Can = true };
        var stop = new ProbeCmd { Can = true };
        var (ctrlRef, subscribed) = DropBoundControl(grab, live, stop);
        var listenerRef = DropDirectListener(grab);
        Pump();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var seen = $"subscribed={subscribed} (want 3), direct listener alive={listenerRef.IsAlive}, control alive={ctrlRef.IsAlive}";
        Check(listenerRef.IsAlive, $"control: a listener subscribed directly to the command survives the same collection — the probe sees retention through the command ({seen})");
        Check(subscribed == 3, $"the dropped control had subscribed to all three commands — else its collection proves nothing ({seen})");
        Check(!ctrlRef.IsAlive, $"a control bound to long-lived commands is collected once dropped — the commands do not keep it alive ({seen})");
        GC.KeepAlive(grab);
        GC.KeepAlive(live);
        GC.KeepAlive(stop);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Ctrl, int Subscribed) DropBoundControl(ProbeCmd grab, ProbeCmd live, ProbeCmd stop)
    {
        var ctrl = new CvDispCtrl { GrabCommand = grab, ContinuousCommand = live, StopCommand = stop };
        return (new WeakReference(ctrl), grab.Subscribers + live.Subscribers + stop.Subscribers);
    }

    private sealed class Listener
    {
        public void OnChanged(object? sender, EventArgs e) { }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DropDirectListener(ProbeCmd cmd)
    {
        var listener = new Listener();
        cmd.CanExecuteChanged += listener.OnChanged;
        return new WeakReference(listener);
    }

    /// <summary>알림을 올린 쪽에 예외를 돌려보내지 않는다. 명령 상태를 바꾸는 쪽은 흔히 바쁨 표식을 세운 직후 try 밖에서 알림을 올린다 —
    /// 여기서 던지면 그 표식이 안 풀린 채 남아 카메라 명령이 영구히 막힌다. 두 길을 본다: 다른 스레드에서 올린 알림(WPF 버튼의 기본
    /// 처리기는 그 스레드에서 바로 IsEnabled 를 다시 계산해 스레드 예외를 던진다 — 원문 확인)과, CanExecute 자체가 던지는 명령. 뒤쪽은
    /// 종전 동작(버튼은 살아 있고 클릭은 Execute 에 닿는다)을 지키고 경고로 알린다.</summary>
    [Fact]
    public void ACanExecuteChangedRaiseNeverThrowsIntoTheRaiser() => RunSta(() =>
    {
        var (ctrl, grab, _) = Make();
        var cmd = new ProbeCmd { Can = true };
        ctrl.GrabCommand = cmd;
        cmd.Can = false;
        Exception? raiserSaw = null;
        var worker = new Thread(() => { try { cmd.Raise(); } catch (Exception ex) { raiserSaw = ex; } });
        worker.Start();
        worker.Join();
        Pump();
        Check(raiserSaw is null && !grab.IsEnabled,
            $"a CanExecuteChanged raised on a worker thread throws nothing into the raiser and still greys 📸 out once the dispatcher runs — raiser saw {Name(raiserSaw)}, enabled={grab.IsEnabled}");

        var warnings = new List<string>();
        var prev = CvLog.Sink;
        CvLog.Sink = (level, src, msg, ex) => { if (level == CvLogLevel.Warning) lock (warnings) warnings.Add($"{src}: {msg}"); };
        lock (warnings) warnings.Clear();   // 붙는 순간 그동안 붙잡혀 있던 줄이 흘러든다 — 세는 것은 그 뒤의 것이다
        try
        {
            var bad = new ProbeCmd { Can = true, Throws = true };
            var onAssign = Record.Exception(() => ctrl.GrabCommand = bad);
            var onRaise = Record.Exception(() => bad.Raise());
            RawClick(grab);
            Check(onAssign is null && onRaise is null,
                $"a CanExecute that throws does not escape into the assignment or the raiser (assign threw {Name(onAssign)}, raise threw {Name(onRaise)})");
            Check(grab.IsEnabled && bad.Runs == 1,
                $"...and keeps the old behaviour: 📸 stays enabled and the click reaches Execute (enabled={grab.IsEnabled}, ran {bad.Runs}x)");
            lock (warnings)
                Check(warnings.Any(w => w.Contains("CanExecute")), $"...and the throw is logged as a warning instead of vanishing: [{string.Join(" | ", warnings)}]");
        }
        finally { CvLog.Sink = prev; }
    });

    /// <summary>시험용 명령 — CanExecute 값을 바꾸고 알림을 직접 올리며, 구독을 실제로 보관해 그 수를 센다.</summary>
    private sealed class ProbeCmd : ICommand
    {
        private EventHandler? _changed;
        public bool Can { get; set; }
        public bool Throws { get; init; }
        public int Runs { get; private set; }
        public int Adds { get; private set; }
        public int Removes { get; private set; }
        public int Subscribers => _changed?.GetInvocationList().Length ?? 0;

        public event EventHandler? CanExecuteChanged
        {
            add { Adds++; _changed += value; }
            remove { Removes++; _changed -= value; }
        }

        public bool CanExecute(object? parameter) => Throws ? throw new InvalidOperationException("probe CanExecute failure") : Can;
        public void Execute(object? parameter) => Runs++;
        public void Raise() => _changed?.Invoke(this, EventArgs.Empty);
    }

    private static ToggleButton? FindToggle(DependencyObject root, string tooltip)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is ToggleButton t && Equals(t.ToolTip, tooltip)) return t;
            if (FindToggle(c, tooltip) is { } found) return found;
        }
        return null;
    }

    private static Button? FindButton(DependencyObject root, string tooltip)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is Button b && Equals(b.ToolTip, tooltip)) return b;
            if (FindButton(c, tooltip) is { } found) return found;
        }
        return null;
    }
}
