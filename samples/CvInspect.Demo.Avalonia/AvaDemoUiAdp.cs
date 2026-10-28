using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace CvInspect.Demo;

/// <summary>Avalonia 판 UI 포트 — UI 디스패처 Post 와 창의 저장소 공급자 폴더 선택(비동기, 창을 붙잡지 않는다).</summary>
internal sealed class AvaDemoUiAdp(TopLevel owner) : IDemoUiPort
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public async Task<string?> PickFolderAsync(string title, string? initialDir)
    {
        var sp = owner.StorageProvider;
        if (!sp.CanPickFolder) return null;   // 폴더 선택이 없는 플랫폼(헤드리스 등) — 취소와 같게 둔다
        IStorageFolder? start = null;
        if (initialDir is not null && Directory.Exists(initialDir))
            start = await sp.TryGetFolderFromPathAsync(initialDir);
        var picked = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false, SuggestedStartLocation = start });
        // 로컬 경로가 없는 폴더(샌드박스 공급자 등)는 레시피 저장소가 파일 경로로 다루지 못한다 — 취소로 본다.
        return picked.Count == 0 ? null : picked[0].TryGetLocalPath();
    }
}
