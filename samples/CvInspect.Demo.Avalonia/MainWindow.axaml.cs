using Avalonia.Controls;

namespace CvInspect.Demo;

public sealed partial class MainWindow : Window
{
    private readonly MainVm? _vm;

    /// <summary>프리뷰어·XAML 로더용 — 명령줄 없이 기본 작업 폴더로 연다.</summary>
    public MainWindow() : this([]) { }

    /// <param name="args">명령줄(--webcam 등, WPF 데모와 같다).</param>
    /// <param name="workDir">합성 부품·저장 대화 상자 첫 폴더. 생략하면 임시 폴더 아래 이 데모 전용 폴더 — WPF 데모와 같은 폴더를 쓰면
    /// 둘을 함께 띄웠을 때 시작하는 쪽이 다른 쪽의 장면 파일을 지운다.</param>
    public MainWindow(string[] args, string? workDir = null)
    {
        InitializeComponent();
        // 디자인 프리뷰어에서는 VM 을 만들지 않는다 — 만들면 프리뷰어 프로세스가 카메라를 열고, 같은 작업 폴더의 part*.png 를 지워
        // 떠 있는 데모의 합성 장면을 망가뜨린다. 프리뷰어에는 빈 창 배치만 보인다.
        if (Design.IsDesignMode) return;
        var vm = new MainVm(new AvaDemoUiAdp(this), args, workDir ?? Path.Combine(Path.GetTempPath(), "CvInspect.Demo.Avalonia"));
        _vm = vm;
        DataContext = vm;
        // 편집기 커밋·액션 완료 → 도형 재생성 + dirty 표시 + 현재 프레임 재검사. 편집기는 값을 이미 Opt 에 써 둔 뒤다.
        Editor.Committed += (_, _) => vm.OptEdited();
        Editor.ActionExecuted += (_, _) => vm.OptEdited();
        Editor.ActionFailed += (_, e) => vm.Status = $"{e.Label}: {e.Error.Message}";
        Closing += (_, _) => vm.Dispose();   // 닫을 때 카메라를 멈추고 닫는다. WPF 판의 OnClosing 과 같은 자리.
    }

    /// <summary>화면 VM — 시험이 창에 묶인 상태를 읽는다. 디자인 모드에서 만든 창에는 없다.</summary>
    public MainVm Vm => _vm ?? throw new InvalidOperationException("the window was created in design mode and has no view-model");
}
