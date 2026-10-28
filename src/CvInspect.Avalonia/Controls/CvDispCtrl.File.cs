// CvDispCtrl 의 우클릭 이미지 파일 불러오기/저장 — 파일 선택기(최상위 창의 StorageProvider)와 실패 알림.
// 파일 규칙(확장자 목록·기본 이름·바이트 디코드/인코드·JPEG 알파 접기·Mat 소유권 넘기기)은 코어 CvImgFileIO. 여기는 선택기·스트림·알림만.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CvInspect.Display;

namespace CvInspect.Avalonia.Controls;

/// <summary>실패한 파일 조작.</summary>
public enum CvDispFileOp
{
    /// <summary>우클릭 메뉴의 이미지 불러오기.</summary>
    Load,

    /// <summary>우클릭 메뉴의 이미지 저장.</summary>
    Save,
}

/// <summary><see cref="CvDispCtrl.ImageFileFailed"/> 의 인자 — 무엇을 하다가, 어느 파일에서, 무엇 때문에 실패했는지.</summary>
public sealed class CvDispFileErrEvt : EventArgs
{
    public CvDispFileErrEvt(CvDispFileOp op, string? fileName, Exception error)
    {
        Op = op;
        FileName = fileName;
        Error = error;
    }

    /// <summary>불러오기인지 저장인지.</summary>
    public CvDispFileOp Op { get; }

    /// <summary>선택한 파일의 이름(경로가 아니다 — 저장소에 따라 경로가 없다). 선택기 자체가 실패했으면 null.</summary>
    public string? FileName { get; }

    /// <summary>실패 원인. 디코드할 수 없는 바이트는 <see cref="InvalidOperationException"/>, 인코드·읽기·쓰기 실패는 대개 <see cref="IOException"/>,
    /// 불러오기 명령의 Execute 가 던진 예외는 그대로 온다.</summary>
    public Exception Error { get; }
}

public sealed partial class CvDispCtrl
{
    // 붙은 최상위 창이 파일 선택기를 주는가 — 붙을 때마다 다시 본다. 붙기 전에는 준다고 본다(메뉴를 열 수 있는 것은 붙은 뒤뿐이다).
    private bool _canOpenFile = true;
    private bool _canSaveFile = true;
    private bool _fileBusy;

    /// <summary>
    /// 우클릭 메뉴의 이미지 불러오기·저장이 실패했을 때 UI 스레드에서 난다. 실패는 이와 함께 <see cref="CvLog"/> 경고 한 줄로도 남는다.
    /// 이 프레임워크에는 내장 메시지 창이 없고, 컨트롤이 창을 그리면 호스트의 창·테마 규칙을 건너뛰게 된다 — 그래서 사람에게 보일지,
    /// 어떻게 보일지는 호스트가 정한다(구독하지 않으면 로그에만 남는다).
    /// 처리기가 던진 예외는 삼키고 오류 로그로 남긴다 — 메뉴 조작은 비동기로 끝나 그 예외를 받을 호출자가 없고, 흘리면 앱이 죽는다.
    /// 파일 선택기를 주지 않는 최상위 창(헤드리스 등)에서는 해당 메뉴 항목이 아예 감춰져 이 이벤트가 나지 않는다.
    /// </summary>
    public event EventHandler<CvDispFileErrEvt>? ImageFileFailed;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        var canOpen = sp?.CanOpen == true;
        var canSave = sp?.CanSave == true;
        if (canOpen == _canOpenFile && canSave == _canSaveFile) return;
        _canOpenFile = canOpen;
        _canSaveFile = canSave;
        ApplyMenuGroups();
    }

    private IStorageProvider? PickerOf(bool save)
    {
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        return sp is not null && (save ? sp.CanSave : sp.CanOpen) ? sp : null;
    }

    private static List<FilePickerFileType> PickerTypes(IEnumerable<CvImgFileType> types)
        => types.Select(t => new FilePickerFileType(t.Name) { Patterns = t.Patterns }).ToList();

    // async void — 메뉴 클릭 처리기에서 부른다. 안에서 나는 예외는 전부 잡아 알림으로 바꾼다(흘리면 디스패처가 앱을 끝낸다).
    // 선택기가 열려 있는 동안 메뉴를 다시 열어 겹쳐 부르면 무시한다 — 선택기가 모달이 아닌 플랫폼에서 두 번째 결과가 첫 번째를 덮지 않게.
    private async void LoadImageFromFile()
    {
        if (_fileBusy || PickerOf(save: false) is not { } sp) return;
        _fileBusy = true;
        string? name = null;
        try
        {
            var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = PickerTypes(CvImgFileIO.OpenTypes),
            });
            if (files.Count == 0) return;
            byte[] bytes;
            using (var file = files[0])
            {
                name = file.Name;
                await using var src = await file.OpenReadAsync();
                using var buf = new MemoryStream();
                await src.CopyToAsync(buf);
                bytes = buf.ToArray();
            }
            // 명령은 지금(선택기를 닫은 뒤)의 것을 쓴다. 받을 곳이 없어졌으면 코어가 Mat 을 해제한다.
            CvImgFileIO.LoadBytesInto(bytes, LoadFrameCommand);
        }
        catch (Exception ex)
        {
            ReportFileError(CvDispFileOp.Load, name, ex);
        }
        finally
        {
            _fileBusy = false;
        }
    }

    private async void SaveImageToFile()
    {
        if (Frame is null || _fileBusy || PickerOf(save: true) is not { } sp) return;
        _fileBusy = true;
        string? name = null;
        try
        {
            var defaultName = CvImgFileIO.DefaultSaveName(DateTime.Now);
            using var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = defaultName,
                DefaultExtension = Path.GetExtension(defaultName).TrimStart('.'),
                FileTypeChoices = PickerTypes(CvImgFileIO.SaveTypes),
                ShowOverwritePrompt = true,
            });
            if (file is null) return;
            name = file.Name;
            // 형식은 고른 이름의 확장자가 정한다(없으면 PNG — 코어 Encode). 인코드를 먼저 끝내고 연다 — 실패해도 기존 파일을 비우지 않게.
            var bytes = CvImgFileIO.EncodeFrame(file.Name, _surface.FrameBuf);
            await using var dst = await file.OpenWriteAsync();
            if (dst.CanSeek) dst.SetLength(0);   // 더 긴 기존 파일 위에 쓰면 꼬리가 남는다
            await dst.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            ReportFileError(CvDispFileOp.Save, name, ex);
        }
        finally
        {
            _fileBusy = false;
        }
    }

    private void ReportFileError(CvDispFileOp op, string? fileName, Exception ex)
    {
        var what = op == CvDispFileOp.Load ? "load" : "save";
        var where = fileName is null ? "" : $" '{fileName}'";
        CvLog.Publish(CvLogLevel.Warning, CvDispLog.Source, $"Image file {what}{where} failed: {ex.Message}", ex);
        if (ImageFileFailed is not { } handler) return;
        try
        {
            handler(this, new CvDispFileErrEvt(op, fileName, ex));
        }
        catch (Exception hx)
        {
            CvLog.Publish(CvLogLevel.Error, CvDispLog.Source,
                $"An {nameof(ImageFileFailed)} handler threw; the exception was swallowed because the menu action has no caller to receive it.", hx);
        }
    }
}
