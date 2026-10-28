using Avalonia;
using Avalonia.Media;

namespace CvInspect.Demo;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // 디자인 프리뷰어도 이 메서드를 찾아 부른다 — 이름과 모양을 바꾸지 않는다.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        // 데모 창 버튼의 ↻ ▲ ▼ 같은 기호는 Inter 에 없다(표시 컨트롤 툴바 아이콘은 벡터 경로라 글꼴과 무관하다). Avalonia 는 글리프 없는
        // 글자의 OS 폰트 자동 대체가 약해 그대로 두면 두부(□)로 뜬다 — 대체 사슬을 명시한다. 글리프가 없을 때만 쓰이니 본문 글자에는 영향이 없다.
        .With(new FontManagerOptions
        {
            FontFallbacks =
            [
                new FontFallback { FontFamily = new FontFamily("Segoe UI Emoji") },
                new FontFallback { FontFamily = new FontFamily("Segoe UI Symbol") },
                new FontFallback { FontFamily = new FontFamily("Apple Color Emoji") },
                new FontFallback { FontFamily = new FontFamily("Noto Color Emoji") },
            ],
        })
        .LogToTrace();
}
