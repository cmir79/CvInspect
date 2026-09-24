// CvDispCtrl 의 바인딩 표면 — 의존 속성(프레임·오버레이·편집 도형·명령·툴바 도킹) 정의와
// 값 변경을 서피스/툴바에 반영하는 처리.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CvInspect.Vision.Overlay;
using OpenCvSharp;
using CvInspect.Vision.Edit;

namespace CvInspect.Controls;

public sealed partial class CvDispCtrl
{
    public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(
        nameof(Frame), typeof(object), typeof(CvDispCtrl),
        new PropertyMetadata(null, (d, e) => ((CvDispCtrl)d).OnFrameChanged(e.NewValue)));

    /// <summary>표시할 프레임 — <see cref="Mat"/> 또는 <see cref="ICvPixelSource"/>(취득 프레임 등). null 이면 빈 화면. 치수 변경 시 자동 Fit.
    /// Mat 은 할당 시점에 픽셀이 서피스 자체 버퍼로 복사되므로 이후 호스트가 dispose 해도 안전하다(dispose 된 Mat 을 새로 할당하는 것은 불가).
    /// ICvPixelSource 는 복사하지 않고 배열 참조를 붙잡는다 — 그 계약("발행 뒤 불변")이 이를 보장하며, 프레임당 복사는 WPF 백버퍼로
    /// 가는 한 번뿐이다. 지원 포맷: 8비트 1/3/4채널(CV_8UC1/CV_8UC3/CV_8UC4) — 그 외는 빈 화면. 두 타입이 아닌 값도 빈 화면(경고 로그).
    /// <see cref="Overlay"/>·<see cref="Shapes"/> 처럼 object 로 받는 이유: 바인딩 소스가 표시 계층의 구체 타입에 묶이지 않게 한다.</summary>
    public object? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    private void OnFrameChanged(object? value)
    {
        switch (value)
        {
            case null:
                _surface.SetFrame((Mat?)null);
                break;
            case Mat mat:
                _surface.SetFrame(mat);
                break;
            case ICvPixelSource src:
                _surface.SetFrame(src);
                break;
            default:
                CvLog.Publish(CvLogLevel.Warning, nameof(CvDispCtrl),
                    $"Frame ignored: expected Mat or ICvPixelSource, got {value.GetType().FullName}.");
                _surface.SetFrame((Mat?)null);
                break;
        }
    }

    public static readonly DependencyProperty OverlayProperty = DependencyProperty.Register(
        nameof(Overlay), typeof(object), typeof(CvDispCtrl),
        new PropertyMetadata(null, (d, e) => ((CvDispCtrl)d)._surface.SetOverlay(e.NewValue as ViOverlay)));

    /// <summary>결과 오버레이 — ViOverlay 만 렌더 (그 외 타입 무시). 참조 교체로 갱신.</summary>
    public object? Overlay
    {
        get => GetValue(OverlayProperty);
        set => SetValue(OverlayProperty, value);
    }

    public static readonly DependencyProperty ClipOverlayToImageProperty = DependencyProperty.Register(
        nameof(ClipOverlayToImage), typeof(bool), typeof(CvDispCtrl),
        new PropertyMetadata(false, (d, e) => ((CvDispCtrl)d)._surface.ClipOverlayToImage = (bool)e.NewValue));

    /// <summary>
    /// 결과 오버레이를 이미지가 그려진 사각 안으로 자를지. 기본 false — 표시 영역 경계까지 그린다(종전 동작).
    /// 그래서 이미지 가장자리에 붙은 라벨이 맞춤 여백으로 삐져나와도 끝까지 읽힌다.
    ///
    /// 잘린 영역을 보이는 화면(<c>ViOverlay.CropTo</c> 사본을 얹는 자리)에서는 켠다. 자른 영역에 걸친 도형(탐색 사각·긴 선)과
    /// 영역 밖 라벨이 맞춤 여백에 그려져 이미지가 그 너머로 이어지는 것처럼 보인다 — 소비자 실측(0.26.3): 400×150 프레임을
    /// 400×400 칸에 맞추면 오버레이 6305px 이 위아래 여백에 찍혔다. 프레임과 칸의 비율 차가 클수록 여백이 커져 번짐도 커진다.
    /// 같은 비율이어도 맞춤이 둘레에 몇 px 을 남긴다.
    /// 켜면 이미지 가장자리 라벨은 그 가장자리에서 잘린다. 편집 도형(<see cref="Shapes"/>)은 이 값과 무관하게 자르지 않는다.
    /// </summary>
    public bool ClipOverlayToImage
    {
        get => (bool)GetValue(ClipOverlayToImageProperty);
        set => SetValue(ClipOverlayToImageProperty, value);
    }

    public static readonly DependencyProperty ShapesProperty = DependencyProperty.Register(
        nameof(Shapes), typeof(object), typeof(CvDispCtrl),
        new PropertyMetadata(null, (d, e) => ((CvDispCtrl)d)._surface.SetShapes(e.NewValue as IReadOnlyList<CvEditShape>)));

    /// <summary>편집 도형 목록 — IReadOnlyList&lt;CvEditShape&gt;. 드래그 편집이 도형 값에 즉시 반영(Changed 발화).
    ///
    /// <b>컨트롤은 각 도형의 <c>Changed</c> 를 구독한다</b>(그려야 하니까). 도형 목록은 대개 호스트가 들고
    /// 있어서 컨트롤보다 오래 사는데, 그 구독이 남아 있으면 <b>버려진 컨트롤이 수집되지 않는다</b> —
    /// 컨트롤 하나가 백버퍼·픽셀 배열·비주얼 트리를 통째로 끌고 간다. 그래서 구독은 <b>화면에 올라와 있는
    /// 동안만</b> 유지한다 — <c>Loaded</c> 에 걸고 <c>Unloaded</c> 에 놓으며, 내려가 있는 사이에 이 값을
    /// 갈아끼워도 그때는 걸지 않는다(다시 올라올 때 걸린다). 호스트가 따로 해 줄 일은 없다.
    /// 뒤집어 말하면 <b>한 번도 화면에 올린 적 없는</b> 컨트롤은 구독하지 않으므로, 도형 값을 밖에서 바꿔도
    /// 다시 그리지 않는다(그런 컨트롤은 애초에 그릴 화면이 없다).</summary>
    public object? Shapes
    {
        get => GetValue(ShapesProperty);
        set => SetValue(ShapesProperty, value);
    }

    public static readonly DependencyProperty GrabCommandProperty = DependencyProperty.Register(
        nameof(GrabCommand), typeof(ICommand), typeof(CvDispCtrl));

    /// <summary>단발 그랩 명령.</summary>
    public ICommand? GrabCommand
    {
        get => (ICommand?)GetValue(GrabCommandProperty);
        set => SetValue(GrabCommandProperty, value);
    }

    public static readonly DependencyProperty ContinuousCommandProperty = DependencyProperty.Register(
        nameof(ContinuousCommand), typeof(ICommand), typeof(CvDispCtrl));

    /// <summary>라이브 시작 명령 (⏯️ 토글 ON).</summary>
    public ICommand? ContinuousCommand
    {
        get => (ICommand?)GetValue(ContinuousCommandProperty);
        set => SetValue(ContinuousCommandProperty, value);
    }

    public static readonly DependencyProperty StopCommandProperty = DependencyProperty.Register(
        nameof(StopCommand), typeof(ICommand), typeof(CvDispCtrl));

    /// <summary>라이브 정지 명령 (⏯️ 토글 OFF).</summary>
    public ICommand? StopCommand
    {
        get => (ICommand?)GetValue(StopCommandProperty);
        set => SetValue(StopCommandProperty, value);
    }

    public static readonly DependencyProperty LoadFrameCommandProperty = DependencyProperty.Register(
        nameof(LoadFrameCommand), typeof(ICommand), typeof(CvDispCtrl),
        new PropertyMetadata(null, (d, e) => ((CvDispCtrl)d).SyncLoadAvailability(e.NewValue is not null)));

    /// <summary>파일 로드 프레임 전달 명령 — 파라미터 Mat(소유권은 수신 측 — 다 쓰면 dispose 책임).
    /// VM 이 원본(테스트/학습 입력)으로 취급. 미지정이면 이 디스플레이는 파일을 못 받는다는 뜻이라,
    /// 우클릭 메뉴의 불러오기와 무이미지 안내의 "우클릭 → 불러오기" 문구가 함께 사라진다.</summary>
    public ICommand? LoadFrameCommand
    {
        get => (ICommand?)GetValue(LoadFrameCommandProperty);
        set => SetValue(LoadFrameCommandProperty, value);
    }

    public static readonly DependencyProperty IsRunningProperty = DependencyProperty.Register(
        nameof(IsRunning), typeof(bool), typeof(CvDispCtrl),
        new PropertyMetadata(false, (d, e) => ((CvDispCtrl)d).SyncPlayState((bool)e.NewValue)));

    /// <summary>라이브 동작 상태 — ⏯️ 토글 표시 동기 (VM 의 카메라 상태가 source of truth).</summary>
    public bool IsRunning
    {
        get => (bool)GetValue(IsRunningProperty);
        set => SetValue(IsRunningProperty, value);
    }

    private void SyncPlayState(bool running)
    {
        _syncingPlay = true;
        try { _playBtn.IsChecked = running; }
        finally { _syncingPlay = false; }
    }

    /// <summary>불러오기 가용 여부를 무이미지 안내와 우클릭 메뉴에 함께 반영.
    /// 메뉴 표시 여부를 여는 시점이 아니라 여기서 정해야 팝업이 제 크기로 열린다.</summary>
    private void SyncLoadAvailability(bool canLoad)
    {
        _surface.ShowLoadHint = canLoad;
        _loadItem.Visibility = canLoad ? Visibility.Visible : Visibility.Collapsed;
    }

    public static readonly DependencyProperty IsCamControlVisibleProperty = DependencyProperty.Register(
        nameof(IsCamControlVisible), typeof(bool), typeof(CvDispCtrl),
        new PropertyMetadata(true, (d, e) =>
            ((CvDispCtrl)d)._camPanel.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed));

    /// <summary>카메라 조작 패널 표시 여부 — 오버뷰(홈) 모드에서는 숨김.</summary>
    public bool IsCamControlVisible
    {
        get => (bool)GetValue(IsCamControlVisibleProperty);
        set => SetValue(IsCamControlVisibleProperty, value);
    }

    public static readonly DependencyProperty IsToolbarVisibleProperty = DependencyProperty.Register(
        nameof(IsToolbarVisible), typeof(bool), typeof(CvDispCtrl),
        new PropertyMetadata(true, (d, e) => ((CvDispCtrl)d).SyncToolbarVisibility((bool)e.NewValue)));

    /// <summary>툴바 전체 표시 여부 — 운전 화면처럼 조작을 상위 버튼이 전담하는 자리에서는 숨긴다.
    /// (카메라 조작만 감추려면 <see cref="IsCamControlVisible"/>.)</summary>
    public bool IsToolbarVisible
    {
        get => (bool)GetValue(IsToolbarVisibleProperty);
        set => SetValue(IsToolbarVisibleProperty, value);
    }

    /// <summary>툴바와 짝인 조작을 툴바·우클릭 메뉴 양쪽에서 함께 여닫는다 (메뉴 쪽은 딸린 구분선까지).
    /// 팬·맞춤·확대·축소는 여기에 딸리지 않는다 — 툴바를 감춰도 메뉴에 남는 조작이다.</summary>
    private void SyncToolbarVisibility(bool visible)
    {
        var vis = visible ? Visibility.Visible : Visibility.Collapsed;
        _toolbarBorder.Visibility = vis;
        foreach (var item in _toolMenuItems) item.Visibility = vis;
    }

    public static readonly DependencyProperty ToolbarDockProperty = DependencyProperty.Register(
        nameof(ToolbarDock), typeof(Dock), typeof(CvDispCtrl),
        new PropertyMetadata(Dock.Top, (d, e) => ((CvDispCtrl)d).ApplyToolbarDock((Dock)e.NewValue)));

    /// <summary>툴바 도킹 위치 — 상(기본)/하/좌/우. 좌/우는 버튼이 세로로 흐른다.</summary>
    public Dock ToolbarDock
    {
        get => (Dock)GetValue(ToolbarDockProperty);
        set => SetValue(ToolbarDockProperty, value);
    }

    /// <summary>툴바 도킹 재배치 — 좌/우는 패널 세로 전환 + 버튼 마진/구분선 축 스왑 + 경계선 방향.</summary>
    private void ApplyToolbarDock(Dock dock)
    {
        DockPanel.SetDock(_toolbarBorder, dock);
        var vertical = dock is Dock.Left or Dock.Right;

        _camPanel.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        _dispPanel.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        DockPanel.SetDock(_camPanel, vertical ? Dock.Top : Dock.Left);
        DockPanel.SetDock(_dispPanel, vertical ? Dock.Bottom : Dock.Right);

        _toolbarBorder.BorderThickness = dock switch
        {
            Dock.Bottom => new Thickness(0, 1, 0, 0),
            Dock.Left => new Thickness(0, 0, 1, 0),
            Dock.Right => new Thickness(1, 0, 0, 0),
            _ => new Thickness(0, 0, 0, 1),
        };

        var btnMargin = vertical ? new Thickness(0, 2, 0, 2) : new Thickness(2, 0, 2, 0);
        foreach (var panel in new[] { _camPanel, _dispPanel })
            foreach (var child in panel.Children)
                if (child is Control c) c.Margin = btnMargin;

        foreach (var sep in _toolSeps)
        {
            if (vertical)
            {
                sep.Width = double.NaN;
                sep.Height = 1;
                sep.Margin = new Thickness(6, 4, 6, 4);
            }
            else
            {
                sep.Height = double.NaN;
                sep.Width = 1;
                sep.Margin = new Thickness(4, 6, 4, 6);
            }
        }
    }
}
