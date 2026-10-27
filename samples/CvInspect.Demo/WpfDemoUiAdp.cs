using System.Windows;
using Microsoft.Win32;

namespace CvInspect.Demo;

/// <summary>WPF 판 UI 포트 — 디스패처 BeginInvoke 와 OpenFolderDialog(동기 대화 상자를 끝난 작업으로 감싼다).</summary>
internal sealed class WpfDemoUiAdp : IDemoUiPort
{
    public void Post(Action action) => Application.Current.Dispatcher.BeginInvoke(action);

    public Task<string?> PickFolderAsync(string title, string? initialDir)
    {
        var dlg = new OpenFolderDialog { Title = title, InitialDirectory = initialDir };
        return Task.FromResult(dlg.ShowDialog() == true ? dlg.FolderName : null);
    }
}
