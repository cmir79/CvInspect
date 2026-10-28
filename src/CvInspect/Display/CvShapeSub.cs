// 디스플레이가 편집 도형의 Changed 를 언제 걸고 언제 놓는지 — 화면에 올라와 있는 동안만 건다.
// UI 프레임워크 무관: "지금 화면에 올라와 있는가" 와 올라옴·내려감 통지는 UI 어댑터가 준다.
//
// 근거: 도형 목록은 대개 호스트(VM)가 들고 있어서 컨트롤보다 오래 산다. 그 목록이 우리 처리기를 붙잡고 있으면 버려진 컨트롤이
// 수집되지 않는다(실측: 한 목록에 컨트롤 40개를 붙였다 버리면 +799MB, 도형 없는 같은 40개는 +50MB — 컨트롤 하나가 백버퍼·픽셀
// 배열·화면 트리를 통째로 끌고 간다). 탭·페이지 전환처럼 뷰만 다시 세우는 배치가 그 자리다. 다시 올라올 때 되건다 — 내려갔다
// 올라오는 것은 흔하고, 그때 구독을 잃으면 드래그 편집이 조용히 죽는다.

using CvInspect.Vision.Edit;

namespace CvInspect.Display;

/// <summary>편집 도형 목록과 그 변경 구독.</summary>
internal sealed class CvShapeSub
{
    private readonly EventHandler _onChanged;
    private bool _attached;

    internal CvShapeSub(EventHandler onChanged) => _onChanged = onChanged;

    /// <summary>지금 들고 있는 도형 목록(null = 없음).</summary>
    internal IReadOnlyList<CvEditShape>? Shapes { get; private set; }

    /// <summary>지금 도형 변경을 구독 중인가.</summary>
    internal bool IsAttached => _attached;

    /// <summary>목록을 갈아 끼운다 — 옛 목록의 구독을 놓고, 화면에 올라와 있으면 새 목록에 건다.</summary>
    internal void Swap(IReadOnlyList<CvEditShape>? shapes, bool isOnScreen)
    {
        Attach(false, isOnScreen);
        Shapes = shapes;
        Attach(true, isOnScreen);
    }

    /// <summary>구독을 걸거나 푼다 — 이미 그 상태면 아무것도 안 한다(올라옴 통지가 두 번 나도 이중 구독이 없다).
    /// <b>거는 것은 화면에 올라와 있을 때만</b>이다 — 내려간 사이에 목록만 갈아끼워도 구독이 되살아나면 (그 뒤로 내려감 통지가
    /// 다시 나지 않으므로) 그대로 누수가 된다. 올라올 때 어댑터가 다시 건다.</summary>
    internal void Attach(bool attach, bool isOnScreen)
    {
        if (attach && !isOnScreen) return;
        if (Shapes is null || attach == _attached) return;
        foreach (var s in Shapes)
        {
            if (attach) s.Changed += _onChanged;
            else s.Changed -= _onChanged;
        }
        _attached = attach;
    }
}
