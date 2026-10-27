// 행 VM 이 소스 POCO 의 INotifyPropertyChanged 를 약하게 구독하는 다리.
// 소스는 이 다리를 강하게 쥐고(이벤트 대리자), 다리는 행을 약하게 쥔다 — 그래서 행·편집기는 소스보다 먼저 수거될 수 있다.

using System.ComponentModel;

namespace CvInspect.Controls;

/// <summary>소스 → 행 약한 구독. 통지는 소스가 낸 스레드에서 동기로 행에 전한다.</summary>
/// <remarks>
/// UI 프레임워크의 약한 이벤트 관리자(WPF PropertyChangedEventManager 등)를 쓰지 않는 것은 이 계층이 코어(netstandard2.1)에 있어서다.
/// 정리 시점이 다르다: 행이 수거된 뒤 남은 이 다리(작은 객체 하나)는 <b>소스가 다음 통지를 낼 때</b> 스스로 구독을 푼다.
/// 통지를 거의 안 내는 오래 사는 소스에 편집기를 Detach 없이 수없이 열었다 버리면 그만큼 다리가 쌓인다 — 행·컨트롤은 붙잡지 않는다.
/// 정상 경로(편집기가 Source 를 바꾸거나 행을 떼어 낼 때)는 <see cref="Dispose"/> 가 바로 푼다.
/// </remarks>
internal sealed class CvPropWeakSub : IDisposable
{
    private readonly INotifyPropertyChanged _src;
    private readonly WeakReference<CvPropRowVm> _row;
    private int _disposed;

    internal CvPropWeakSub(INotifyPropertyChanged src, CvPropRowVm row)
    {
        _src = src;
        _row = new WeakReference<CvPropRowVm>(row);
        src.PropertyChanged += OnChanged;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_row.TryGetTarget(out var row)) row.OnSourceChanged(sender, e);
        else Dispose();   // 행이 이미 수거됐다 — 소스에 남은 이 다리를 걷는다
    }

    public void Dispose()
    {
        // 통지 스레드(수거 감지)와 UI 스레드(Detach)가 동시에 부를 수 있다 — 한 번만 푼다.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _src.PropertyChanged -= OnChanged;
    }
}
