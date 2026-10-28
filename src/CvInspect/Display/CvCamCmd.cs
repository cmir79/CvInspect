// 디스플레이 카메라 버튼(📸 그랩·⏯ 라이브)과 명령의 CanExecute 판정 — 버튼 활성, ⏯ 가 낼 명령, 클릭 실행, 던지는 CanExecute 처리.
// UI 프레임워크 무관(ICommand 는 System.ObjectModel 의 것이라 어느 UI 의 버튼이든 같은 형이다). 알림 구독(약한 구독)과
// UI 스레드로 넘기기는 UI 어댑터 몫이다.
//
// 근거: 0.30.0 까지는 두 버튼이 Execute 를 직접 불러 CanExecute 를 한 번도 묻지 않았다 — 호스트가 촬영 중에 명령을 막아 둬도 버튼이
// 살아 있고 눌렸다. 지금은 표준 버튼의 Command 와 같은 뜻이다: false 면 회색이고 실행하지 않는다. 표준 버튼과 다른 점 —
// CanExecute 가 던져도 알림을 올린 쪽으로 돌려보내지 않고(실행 가능으로 본다, CanRun), 거절된 클릭 뒤에 버튼 상태를 다시 정한다
// (Click 이 Refused 를 돌려주면 어댑터가 다시 묻는다) — 알림을 빠뜨린 명령에서 살아 보이는 죽은 버튼이 남지 않게.

using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace CvInspect.Display;

/// <summary>클릭 한 번의 결과.</summary>
internal enum CvCmdRun
{
    /// <summary>명령이 없다 — 버튼은 살아 있고 아무 일도 없다(종전 동작).</summary>
    NoCommand,

    /// <summary>CanExecute 가 허락해 Execute 를 불렀다.</summary>
    Ran,

    /// <summary>CanExecute 가 거절해 실행하지 않았다 — 어댑터는 버튼 상태를 다시 정한다.</summary>
    Refused,
}

/// <summary>카메라 버튼 명령 판정.</summary>
internal static class CvCamCmd
{
    /// <summary>CanExecute 가 던졌다고 이미 경고한 명령 인스턴스. 약한 키라 명령의 수명을 늘리지 않고, 참조로 찾으므로 명령이 Equals 를
    /// 바꿔도 인스턴스별이다. 정적이라 같은 명령을 여러 컨트롤에 묶어도 한 번이다 — 단 이 형은
    /// UI 패키지마다 링크 사본으로 따로 컴파일되므로 "한 번" 은 UI 패키지(어셈블리) 안에서다. 두 UI 패키지에 같은 명령을 묶으면 각각 한 번씩 남는다. 값은 처음 던진 예외의 형식이다(디버거에서 볼 진단용 —
    /// 코드는 읽지 않는다).</summary>
    private static readonly ConditionalWeakTable<ICommand, object> _throwWarned = new();

    /// <summary>CanExecute 를 null 로 묻는다 — 버튼이 Execute 에 넘기는 값도 늘 null 이다. 던지면 실행 가능으로 본다: CanExecute 를 묻기
    /// 전(0.30.0 까지)의 동작이 그것이고(버튼은 살아 있고 클릭은 Execute 에 닿는다 — 고장 난 명령이면 거기서 종전대로 던진다),
    /// 회색으로 바꾸면 요란하던 실패가 조용히 굳은 버튼이 된다.
    /// 경고는 명령 인스턴스마다 한 번만 남긴다. 버튼 상태는 대입·알림·IsRunning 변화·클릭마다 다시 묻고, 입력마다 재질의를 권하는 명령이면
    /// 입력마다 묻는다 — 물을 때마다 남기면 같은 줄이 로그를 덮는다(실측: 던지는 명령 하나를 📸 에 걸고 대입 1·알림 3·IsRunning 왕복 2·
    /// 클릭 2 → 8번 물었고 7650d05 는 경고 8줄을 남겼다 — WPF 회귀 AThrowingCanExecuteIsWarnedOncePerCommandInstance). 그 뒤의 예외는
    /// 남기지 않으니 문구에 그것을 밝힌다. 문구는 상태 갱신 길과 클릭 길 모두에서 참인 것만 적는다 — 7650d05 의 "the button is left
    /// enabled" 는 클릭 길에서 실제로 일어난 일(그대로 실행)을 말하지 않았다.</summary>
    internal static bool CanRun(ICommand cmd)
    {
        try
        {
            return cmd.CanExecute(null);
        }
        catch (Exception ex)
        {
            if (TryMarkWarned(cmd, ex.GetType()))
                CvLog.Publish(CvLogLevel.Warning, CvDispLog.Source,
                    $"{cmd.GetType().Name}.CanExecute threw; treating the command as runnable, so the throw does not grey its button " +
                    "and clicks still reach Execute. Logged once per command instance; later throws from it are not logged.", ex);
            return true;
        }
    }

    /// <summary>처음 경고하는 인스턴스면 표에 넣고 true. 같은 명령을 여러 스레드가 동시에 물어도 한 번만 true 다(표가 원자적으로 넣는다).</summary>
    private static bool TryMarkWarned(ICommand cmd, object firstThrow)
    {
#if NET
        return _throwWarned.TryAdd(cmd, firstThrow);
#else
        // netstandard2.1 의 표에는 TryAdd 가 없다 — Add 가 이미 있는 키에 던지는 것으로 같은 원자성을 얻는다.
        try
        {
            _throwWarned.Add(cmd, firstThrow);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
#endif
    }

    /// <summary>버튼 활성 — 명령이 없으면 살아 있고(종전대로), 있으면 <see cref="CanRun"/>.</summary>
    internal static bool IsEnabled(ICommand? cmd) => cmd is null || CanRun(cmd);

    /// <summary>⏯ 의 활성을 정하는 명령 — 눌렀을 때 낼 명령이다: 돌고 있으면 정지, 멈춰 있으면 시작. 시작 명령 하나로 정하면 시작이
    /// 막힌 사이에 라이브를 끌 수 없게 된다. 그래서 IsRunning 이 바뀔 때도 다시 정해야 한다.</summary>
    internal static ICommand? PlayGateCmd(bool isRunning, ICommand? continuous, ICommand? stop) => isRunning ? stop : continuous;

    /// <summary>⏯ 토글 직후 낼 명령 — 눌림이면 시작, 풀림이면 정지.</summary>
    internal static ICommand? PlayClickCmd(bool isCheckedNow, ICommand? continuous, ICommand? stop) => isCheckedNow ? continuous : stop;

    /// <summary>클릭 한 번 — 클릭에서도 CanExecute 를 다시 묻는다. 회색이 되기 전에 이미 들어와 있던 클릭(자동화·큐에 쌓인 입력)이
    /// 막힌 명령을 실행하면 안 된다. 실행한 뒤에는 다시 묻지 않는다 — 바쁨 표식을 Execute 안에서 세우는 명령이면 매 클릭이 버튼을
    /// 회색으로 만들고, 그 명령이 풀 때 알리지 않으면 회색이 다음 알림까지 남는다.</summary>
    internal static CvCmdRun Click(ICommand? cmd)
    {
        if (cmd is null) return CvCmdRun.NoCommand;
        if (!CanRun(cmd)) return CvCmdRun.Refused;
        cmd.Execute(null);
        return CvCmdRun.Ran;
    }
}
