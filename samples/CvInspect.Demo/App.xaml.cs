using System.Diagnostics;
using System.Windows;

namespace CvInspect.Demo;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 라이브러리는 스스로 로그를 남기지 않는다 — 싱크를 꽂아야 경고(미지원 프레임 값 등)가 보인다.
        CvLog.Sink = (level, source, message, ex) =>
            Debug.WriteLine($"[{level}] {source}: {message}{(ex is null ? "" : " | " + ex.GetType().Name + ": " + ex.Message)}");
        base.OnStartup(e);
    }
}
