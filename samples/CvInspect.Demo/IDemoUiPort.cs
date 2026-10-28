namespace CvInspect.Demo;

/// <summary>
/// 화면 VM(<see cref="MainVm"/>)이 UI 프레임워크에 기대는 두 자리 — UI 스레드로 넘기기와 폴더 고르기.
/// VM 은 이 포트만 보고, 창(WPF 판·Avalonia 판)이 자기 프레임워크로 채워 넣는다. 그래서 두 데모가 같은 VM 소스를 쓴다.
/// </summary>
public interface IDemoUiPort
{
    /// <summary>작업을 UI 스레드 큐 뒤에 넣는다(기다리지 않는다). 카메라 스레드에서 불린다.</summary>
    void Post(Action action);

    /// <summary>폴더를 고르게 한다. 취소하면 null. UI 스레드에서 불린다.</summary>
    Task<string?> PickFolderAsync(string title, string? initialDir);
}
