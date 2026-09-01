// CvDispCtrl 의 바인딩 표면 — 의존 속성(프레임·오버레이·편집 도형·명령·툴바 도킹) 정의와
// 값 변경을 서피스/툴바에 반영하는 처리.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CvInspect.Vision.Overlay;
using OpenCvSharp;

namespace CvInspect.Controls;

public sealed partial class CvDispCtrl
{
    public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(
        nameof(Frame), typeof(Mat), typeof(CvDispCtrl),
        new PropertyMetadata(null, (d, e) => ((CvDispCtrl)d)._surface.SetFrame((Mat?)e.NewValue)));

    /// <summary>표시할 프레임 — null 이면 빈 화면. 치수 변경 시 자동 Fit.
    /// 할당 시점에 픽셀이 서피스 자체 버퍼로 복사되므로 이후 호스트가 Mat 을 dispose 해도 안전하다
    /// (dispose 된 Mat 을 새로 할당하는 것은 불가). 지원 포맷: CV_8UC1/CV_8UC3/CV_8UC4 — 그 외는 빈 화면.</summary>
    public Mat? Frame
    {
        get => (Mat?)GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
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

    public static readonly DependencyProperty ShapesProperty = DependencyProperty.Register(
        nameof(Shapes), typeof(object), typeof(CvDispCtrl),
        new PropertyMetadata(null, (d, e) => ((CvDispCtrl)d)._surface.SetShapes(e.NewValue as IReadOnlyList<CvEditShape>)));

    /// <summary>편집 도형 목록 — IReadOnlyList&lt;CvEditShape&gt;. 드래그 편집이 도형 값에 즉시 반영(Changed 발화).</summary>
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
