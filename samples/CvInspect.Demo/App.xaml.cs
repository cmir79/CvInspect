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
        // 문화권도 고른다 — 기본값 그대로 두면 "안 골랐다" 로 보이고, 첫 번역 조회 때 진단 추적에 한 줄 남는다.
        CvLoc.Culture = "en";
        base.OnStartup(e);
    }
}
