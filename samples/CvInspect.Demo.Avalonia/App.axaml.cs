using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace CvInspect.Demo;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // 라이브러리는 스스로 로그를 남기지 않는다 — 싱크를 꽂아야 경고(미지원 프레임 값 등)가 보인다.
        CvLog.Sink = (level, source, message, ex) =>
            Debug.WriteLine($"[{level}] {source}: {message}{(ex is null ? "" : " | " + ex.GetType().Name + ": " + ex.Message)}");
        // 문화권도 고른다 — 기본값 그대로 두면 "안 골랐다" 로 보이고, 첫 번역 조회 때 진단 추적에 한 줄 남는다.
        CvLoc.Culture = "en";
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(desktop.Args ?? []);
        base.OnFrameworkInitializationCompleted();
    }
}
