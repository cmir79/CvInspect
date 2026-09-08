using System.ComponentModel;
using System.Windows;
using CvInspect.Controls;

namespace CvInspect.Demo;

public partial class MainWindow : Window
{
    private readonly MainVm _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    // 편집기 커밋·액션 완료 → dirty 표시 + 현재 프레임 재검사. 편집기는 값을 이미 Opt 에 써 둔 뒤라 VM 은 다시 돌리기만 한다.
    private void OnOptCommitted(object? sender, CvPropCommittedEvt e) => _vm.MarkDirty(rerun: true);
    private void OnOptActionExecuted(object? sender, CvPropActionExecutedEvt e) => _vm.MarkDirty(rerun: true);
    private void OnOptActionFailed(object? sender, CvPropActionFailedEvt e) => _vm.Status = $"{e.Label}: {e.Error.Message}";

    protected override void OnClosing(CancelEventArgs e)
    {
        _vm.Dispose();
        base.OnClosing(e);
    }
}
