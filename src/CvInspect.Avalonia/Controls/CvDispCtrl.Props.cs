// CvDispCtrl 의 바인딩 표면 — 스타일 속성(프레임·오버레이·편집 도형·명령·툴바 도킹) 정의와
// 값 변경을 표면/툴바에 반영하는 처리(OnPropertyChanged 한 곳에서 나눈다).
//
// 콜백은 값이 바뀔 때마다 돈다 — 호스트 스타일이 준 값이 컨트롤을 떼어 낼 때 걷히며 기본값으로 다시 부르는 것도 포함이다.
// 그래서 각 반영은 어느 값에서 어느 값으로 가도 같은 끝 상태가 되게(멱등) 짜여 있다.

using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using CvInspect.Display;
using CvInspect.Vision.Edit;
using CvInspect.Vision.Overlay;
using OpenCvSharp;

namespace CvInspect.Avalonia.Controls;

public sealed partial class CvDispCtrl
{
    public static readonly StyledProperty<object?> FrameProperty =
        AvaloniaProperty.Register<CvDispCtrl, object?>(nameof(Frame));

    /// <summary>표시할 프레임 — <see cref="Mat"/> 또는 <see cref="ICvPixelSource"/>(취득 프레임 등). null 이면 빈 화면. 치수 변경 시 자동 맞춤.
    /// Mat 은 할당 시점에 픽셀이 표면 자체 버퍼로 복사되므로 이후 호스트가 dispose 해도 안전하다(dispose 된 Mat 을 새로 할당하는 것은 불가).
    /// ICvPixelSource 는 복사하지 않고 배열 참조를 붙잡는다 — 그 계약("발행 뒤 불변")이 이를 보장하며, 프레임당 복사는 화면 비트맵으로
    /// 가는 변환 한 번뿐이다. 지원 포맷: 8비트 1/3/4채널(CV_8UC1/CV_8UC3/CV_8UC4) — 그 외는 빈 화면. 두 타입이 아닌 값도 빈 화면(경고 로그).
    /// 숨겼다 다시 보이는 것은 크기 변경이 아니라 줌이 남는다 — 첫 화면으로 돌리려면 <see cref="FitToView"/>.
    /// <see cref="Overlay"/>·<see cref="Shapes"/> 처럼 object 로 받는 이유: 바인딩 소스가 표시 계층의 구체 타입에 묶이지 않게 한다.
    /// 화면 비트맵은 4채널 한 장을 치수가 바뀔 때까지 다시 쓴다 — 1·3채널 프레임은 대입마다 4채널로 변환한다(5MP 실측 약 2~3.5 ms).</summary>
    public object? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    public static readonly StyledProperty<object?> OverlayProperty =
        AvaloniaProperty.Register<CvDispCtrl, object?>(nameof(Overlay));

    /// <summary>결과 오버레이 — ViOverlay 만 렌더 (그 외 타입 무시). 참조 교체로 갱신.</summary>
    public object? Overlay
    {
        get => GetValue(OverlayProperty);
        set => SetValue(OverlayProperty, value);
    }

    public static readonly StyledProperty<bool> ClipOverlayToImageProperty =
        AvaloniaProperty.Register<CvDispCtrl, bool>(nameof(ClipOverlayToImage));

    /// <summary>
    /// 결과 오버레이를 이미지가 그려진 사각 안으로 자를지. 기본 false — 표시 영역 경계까지 그린다.
    /// 그래서 이미지 가장자리에 붙은 라벨이 그쪽에 맞춤 여백이 있으면 그 여백으로 삐져나와 읽힌다(여백이 없는 쪽 —
    /// 칸과 같은 비율이면 둘레 1.5% 남짓, 확대하면 0 — 은 켜든 끄든 표시 영역 경계에서 잘린다).
    ///
    /// 잘린 영역을 보이는 화면(<c>ViOverlay.CropTo</c> 사본을 얹는 자리)에서는 켠다. 끄면 자른 영역에 걸친 선과 영역 밖
    /// 도형·라벨이 맞춤 여백에 그려져 이미지가 그 너머로 이어지는 것처럼 보인다. 번지는 양은 그려 넣은 것에 달렸다 — 여백이 넓을수록
    /// 번질 자리가 넓을 뿐이다.
    ///
    /// 켜면 오버레이 항목이 이미지 가장자리에서 잘린다. 라벨 글자는 화면 고정 크기라, 이미지가 작게 그려질수록(작은 타일,
    /// 축소) 가장자리 라벨이 더 많이 잘린다. 예외 둘:
    /// 모서리 요약 HUD(<c>ViOverlayLabel.IsHud</c>, 모서리 정렬)는 자르지 않고 나머지 항목 뒤에 그린다(맨 위에 온다) — 이미지보다
    /// 커진 블록을 자르면 판정 사유 줄이 사라지고 남은 헤더 한 줄이 온전한 HUD 처럼 보인다. 편집 도형(<see cref="Shapes"/>)도 이미지 사각으로는 자르지
    /// 않는다 — 가장자리 핸들을 잡을 수 있어야 한다(둘 다 표시 영역 경계에서는 잘린다).
    /// </summary>
    public bool ClipOverlayToImage
    {
        get => GetValue(ClipOverlayToImageProperty);
        set => SetValue(ClipOverlayToImageProperty, value);
    }

    public static readonly StyledProperty<double> OverlayLabelScaleProperty =
        AvaloniaProperty.Register<CvDispCtrl, double>(nameof(OverlayLabelScale), 1.0, validate: s => double.IsFinite(s) && s > 0);

    /// <summary>
    /// 결과 오버레이 라벨의 글자 배율 — <b>이 컨트롤에서만</b>. 기본 1.0.
    /// 한 화면(멀리서 보는 운영 창 등)에서만 검사 데이터 글자를 키울 때 쓴다. 같은 <see cref="Overlay"/> 를 이력·다른 화면이 함께 쥐고
    /// 있으므로 오버레이를 고쳐 쓰지 않고 그리는 쪽에서 키운다. 호스트가 매 프레임 라벨 사본을 만들어 넣는 방식은 넣는 자리를 하나만
    /// 빠뜨려도 그 화면이 예외도 경고도 없이 원래 크기로 그려진다.
    ///
    /// 바뀌는 것은 <b>글자 크기뿐</b>이다 — 기준점(X·Y)과 정렬은 그대로이고, 배경 상자는 글자의 잉크 경계를 따라 함께 커진다.
    /// 그대로인 것: 모서리 요약 HUD(<c>ViOverlayLabel.IsHud</c> 이고 모서리 정렬 — <see cref="ClipOverlayToImage"/>·<c>ViOverlay.CropTo</c> 와
    /// 같은 판정. 이미지 모서리를 차지하는 블록이라 키우면 이미지를 그만큼 더 가린다), 도형·선, 편집 도형(<see cref="Shapes"/>)의 이름표.
    /// 키운 라벨은 <see cref="ClipOverlayToImage"/> 를 켠 화면에서 가장자리가 그만큼 더 잘리고, 아래 정렬 라벨은 글자 아래 여백(하강부)도
    /// 함께 커져 기준점과 잉크 사이가 벌어진다.
    /// 0 이하·NaN·무한대는 거절한다 — 코드로 넣으면 <see cref="ArgumentException"/>. 바인딩이 그런 값을 내면 던지지 않고 기본값 1.0 으로
    /// 떨어진다 — 직전 값도, 스타일 우선순위로 깔린 값도 아니다(Avalonia 12.1.4 헤드리스 실측: 2.0 뒤 −1·0·NaN·∞ 모두 1, 스타일 3.0 이
    /// 깔려 있어도 1). 거절하지 않는 아주 큰·작은 값은 그리는 자리에서 그릴 수 있는 글자 크기 범위로 잘린다.
    /// </summary>
    public double OverlayLabelScale
    {
        get => GetValue(OverlayLabelScaleProperty);
        set => SetValue(OverlayLabelScaleProperty, value);
    }

    public static readonly StyledProperty<object?> ShapesProperty =
        AvaloniaProperty.Register<CvDispCtrl, object?>(nameof(Shapes));

    /// <summary>편집 도형 목록 — IReadOnlyList&lt;CvEditShape&gt;. 드래그 편집이 도형 값에 즉시 반영(Changed 발화).
    ///
    /// <b>컨트롤은 각 도형의 <c>Changed</c> 를 구독한다</b>(그려야 하니까). 도형 목록은 대개 호스트가 들고
    /// 있어서 컨트롤보다 오래 사는데, 그 구독이 남아 있으면 <b>버려진 컨트롤이 수집되지 않는다</b> —
    /// 컨트롤 하나가 화면 비트맵·픽셀 배열·비주얼 트리를 통째로 끌고 간다. 그래서 구독은 <b>화면에 올라와 있는
    /// 동안만</b> 유지한다 — 올라올 때(Loaded) 걸고 내려갈 때(Unloaded) 놓으며, 내려가 있는 사이에 이 값을
    /// 갈아끼워도 그때는 걸지 않는다(다시 올라올 때 걸린다). 호스트가 따로 해 줄 일은 없다.
    /// 뒤집어 말하면 <b>한 번도 화면에 올린 적 없는</b> 컨트롤은 구독하지 않으므로, 도형 값을 밖에서 바꿔도
    /// 다시 그리지 않는다(그런 컨트롤은 애초에 그릴 화면이 없다).
    /// <b>도형 값 쓰기와 Changed 는 UI 스레드에서</b> 한다 — 그리기가 UI 스레드에서 도형 값을 읽는 동안 작업 스레드가 쓰면 한 도형의
    /// 값이 반쯤 바뀐 채 그려질 수 있다. 다시 그리기 요청을 UI 스레드로 넘기는 처리는 있지만 그것은 값 읽기·쓰기의 겹침을 막지 않는다.</summary>
    public object? Shapes
    {
        get => GetValue(ShapesProperty);
        set => SetValue(ShapesProperty, value);
    }

    // 카메라 명령 셋은 표준 버튼의 Command 와 같은 뜻이다 — CanExecute(null) 이 false 면 버튼이 회색이고 눌러도 실행하지 않으며,
    // CanExecuteChanged 가 오면 다시 묻는다(어느 스레드에서 올려도 된다 — UI 스레드로 넘겨 갱신하고, 올린 쪽으로 예외를 돌려보내지 않는다).
    // 거절된 클릭 뒤에도 다시 묻는다 — 알림 없이 false 가 된 명령이면 그 클릭에서 회색이 된다. CanExecute 가 던지면 실행 가능으로 보고
    // 명령 인스턴스마다 한 번 경고한다. 구독은 약한 구독이라 명령이 컨트롤보다 오래 살아도 컨트롤을 붙잡지 않는다. null 이면 버튼은
    // 살아 있고 누르면 아무 일도 없다.
    // 근거와 세부는 CvDispCtrl.cs 의 SwapCamCommand·RefreshCamButtons, 판정은 코어 CvCamCmd.

    public static readonly StyledProperty<ICommand?> GrabCommandProperty =
        AvaloniaProperty.Register<CvDispCtrl, ICommand?>(nameof(GrabCommand));

    /// <summary>단발 그랩 명령 (📸). CanExecute 가 false 인 동안 📸 는 회색이다. false 로 바꾸고 CanExecuteChanged 를 올리지 않았으면
    /// 거절된 첫 클릭에서 회색이 된다. 다시 true 로 바꿀 때도 CanExecuteChanged 를 올려야 확실히 풀린다(그 밖에는 다른 재계산 —
    /// 다른 카메라 명령의 알림·IsRunning 변화 — 이 우연히 풀 때뿐이다).</summary>
    public ICommand? GrabCommand
    {
        get => GetValue(GrabCommandProperty);
        set => SetValue(GrabCommandProperty, value);
    }

    public static readonly StyledProperty<ICommand?> ContinuousCommandProperty =
        AvaloniaProperty.Register<CvDispCtrl, ICommand?>(nameof(ContinuousCommand));

    /// <summary>라이브 시작 명령 (⏯️ 토글 ON). ⏯ 의 활성은 눌렀을 때 낼 명령이 정한다 — 멈춰 있는(<see cref="IsRunning"/>=false)
    /// 동안은 이 명령의 CanExecute, 도는 동안은 <see cref="StopCommand"/> 의 것. 그래서 시작을 막아도 정지는 막히지 않는다.</summary>
    public ICommand? ContinuousCommand
    {
        get => GetValue(ContinuousCommandProperty);
        set => SetValue(ContinuousCommandProperty, value);
    }

    public static readonly StyledProperty<ICommand?> StopCommandProperty =
        AvaloniaProperty.Register<CvDispCtrl, ICommand?>(nameof(StopCommand));

    /// <summary>라이브 정지 명령 (⏯️ 토글 OFF). <see cref="IsRunning"/> 이 true 인 동안 ⏯ 의 활성을 이 명령의 CanExecute 가 정한다.</summary>
    public ICommand? StopCommand
    {
        get => GetValue(StopCommandProperty);
        set => SetValue(StopCommandProperty, value);
    }

    public static readonly StyledProperty<ICommand?> LoadFrameCommandProperty =
        AvaloniaProperty.Register<CvDispCtrl, ICommand?>(nameof(LoadFrameCommand));

    /// <summary>파일 로드 프레임 전달 명령 — 파라미터 Mat(소유권은 수신 측 — 다 쓰면 dispose 책임). UI 스레드에서 실행된다.
    /// VM 이 원본(테스트/학습 입력)으로 취급. 미지정이면 이 디스플레이는 파일을 못 받는다는 뜻이라,
    /// 우클릭 메뉴의 불러오기와 무이미지 안내의 "우클릭 → 불러오기" 문구가 함께 사라진다. 컨트롤이 붙은 최상위 창이 파일 열기 선택기를
    /// 주지 못해도 같다(<see cref="ImageFileFailed"/> 설명).
    /// 카메라 명령 셋과 달리 CanExecute 는 묻지 않는다 — 넘길 매개변수(Mat)가 파일 선택기를 닫은 뒤에야 생겨 메뉴를 열 때 물을 값이
    /// 없으므로 메뉴 항목은 CanExecute 와 무관하게 살아 있다. 바쁜 동안 막으려면 Execute 안에서 거절하되, 그때도 받은 Mat 은 해제한다.</summary>
    public ICommand? LoadFrameCommand
    {
        get => GetValue(LoadFrameCommandProperty);
        set => SetValue(LoadFrameCommandProperty, value);
    }

    public static readonly StyledProperty<bool> IsRunningProperty =
        AvaloniaProperty.Register<CvDispCtrl, bool>(nameof(IsRunning));

    /// <summary>라이브 동작 상태 — ⏯️ 토글 표시 동기 (VM 의 카메라 상태가 source of truth). ⏯ 는 클릭이 아니라 이 값을 보인다.</summary>
    public bool IsRunning
    {
        get => GetValue(IsRunningProperty);
        set => SetValue(IsRunningProperty, value);
    }

    public static readonly StyledProperty<bool> IsCamControlVisibleProperty =
        AvaloniaProperty.Register<CvDispCtrl, bool>(nameof(IsCamControlVisible), true);

    /// <summary>카메라 조작 패널(📸·⏯) 표시 여부 — 오버뷰(홈) 모드에서는 숨김.</summary>
    public bool IsCamControlVisible
    {
        get => GetValue(IsCamControlVisibleProperty);
        set => SetValue(IsCamControlVisibleProperty, value);
    }

    public static readonly StyledProperty<bool> IsToolbarVisibleProperty =
        AvaloniaProperty.Register<CvDispCtrl, bool>(nameof(IsToolbarVisible), true);

    /// <summary>툴바 전체 표시 여부 — 운전 화면처럼 조작을 상위 버튼이 전담하는 자리에서는 숨긴다.
    /// 우클릭 메뉴의 구분선+"그래픽 지우기" 도 함께 감춘다. 팬·맞춤·확대·축소는 메뉴에 남는다 — 툴바를 감춘 화면에서 배율이 어긋나면
    /// 되돌릴 수단이 그 메뉴뿐이다. (카메라 조작만 감추려면 <see cref="IsCamControlVisible"/>.)</summary>
    public bool IsToolbarVisible
    {
        get => GetValue(IsToolbarVisibleProperty);
        set => SetValue(IsToolbarVisibleProperty, value);
    }

    public static readonly StyledProperty<Dock> ToolbarDockProperty =
        AvaloniaProperty.Register<CvDispCtrl, Dock>(nameof(ToolbarDock), Dock.Top);

    /// <summary>툴바 도킹 위치 — 상(기본)/하/좌/우. 좌/우는 버튼이 세로로 흐른다. 형은 이 프레임워크의 <see cref="Dock"/> 이다.</summary>
    public Dock ToolbarDock
    {
        get => GetValue(ToolbarDockProperty);
        set => SetValue(ToolbarDockProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        var p = change.Property;
        // 형 분기(두 형이 아니면 경고 + 빈 화면)와 공급자 계약 검사는 코어 CvFrameBuf.Set 이 한다.
        if (p == FrameProperty) _surface.SetFrame(change.NewValue);
        else if (p == OverlayProperty) _surface.SetOverlay(change.NewValue as ViOverlay);
        else if (p == ClipOverlayToImageProperty) _surface.ClipOverlayToImage = change.GetNewValue<bool>();
        else if (p == OverlayLabelScaleProperty) _surface.OverlayLabelScale = change.GetNewValue<double>();
        else if (p == ShapesProperty) _surface.SetShapes(change.NewValue as IReadOnlyList<CvEditShape>);
        else if (p == GrabCommandProperty) SwapCamCommand(_grabSub, change.OldValue as ICommand, change.NewValue as ICommand);
        else if (p == ContinuousCommandProperty) SwapCamCommand(_continuousSub, change.OldValue as ICommand, change.NewValue as ICommand);
        else if (p == StopCommandProperty) SwapCamCommand(_stopSub, change.OldValue as ICommand, change.NewValue as ICommand);
        else if (p == LoadFrameCommandProperty) ApplyMenuGroups();
        else if (p == IsRunningProperty) SyncPlayState(change.GetNewValue<bool>());
        else if (p == IsCamControlVisibleProperty) _camPanel.IsVisible = change.GetNewValue<bool>();
        else if (p == IsToolbarVisibleProperty) SyncToolbarVisibility(change.GetNewValue<bool>());
        else if (p == ToolbarDockProperty) ApplyToolbarDock(change.GetNewValue<Dock>());
        else OnThemePropertyChanged(change);
    }

    private void SyncPlayState(bool running)
    {
        _syncingPlay = true;
        try { _playBtn.IsChecked = running; }
        finally { _syncingPlay = false; }
        RefreshCamButtons();   // ⏯ 의 활성을 정하는 명령이 도는 쪽/멈춘 쪽에 따라 바뀐다
    }

    /// <summary>우클릭 메뉴의 묶음(불러오기 / 툴바와 짝인 지우기·구분선)과 파일 항목, 무이미지 안내의 불러오기 줄을 함께 맞춘다 — 어떤 묶음이
    /// 언제 보이는지는 코어 CvDispMenu.IsVisible. 불러오기는 받을 곳(명령)과 파일 열기 선택기가 둘 다 있어야, 저장은 파일 저장 선택기가 있어야
    /// 보인다. 메뉴 표시 여부를 여는 시점이 아니라 조건이 바뀌는 시점에 정해야 팝업이 제 크기로 열린다.</summary>
    private void ApplyMenuGroups()
    {
        var canLoad = LoadFrameCommand is not null && _canOpenFile;
        _surface.ShowLoadHint = canLoad;
        var isToolbarVisible = IsToolbarVisible;
        foreach (var (group, elem) in _menuGroupItems)
            elem.IsVisible = CvDispMenu.IsVisible(group, canLoad, isToolbarVisible);
        _saveItem.IsVisible = _canSaveFile;
        _fileSep.IsVisible = _loadItem.IsVisible || _saveItem.IsVisible;
    }

    /// <summary>툴바와 짝인 조작을 툴바·우클릭 메뉴 양쪽에서 함께 여닫는다 (메뉴 쪽은 딸린 구분선까지).
    /// 팬·맞춤·확대·축소는 여기에 딸리지 않는다 — 툴바를 감춰도 메뉴에 남는 조작이다.</summary>
    private void SyncToolbarVisibility(bool visible)
    {
        _toolbarBorder.IsVisible = visible;
        ApplyMenuGroups();
    }

    /// <summary>툴바 도킹 재배치 — 좌/우는 패널 세로 전환 + 버튼 여백/구분선 축 스왑 + 가장자리선 방향.</summary>
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

        // 버튼만 — 구분선(Border)도 이 프레임워크에서는 Control 이라 형으로 거른다. 구분선 여백은 아래에서 따로 정한다.
        var btnMargin = vertical ? new Thickness(0, 2, 0, 2) : new Thickness(2, 0, 2, 0);
        foreach (var panel in new[] { _camPanel, _dispPanel })
            foreach (var child in panel.Children)
                if (child is Button b) b.Margin = btnMargin;

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
