// CvDispCtrl 본체 — 컨트롤 조립(툴바·상태바·우클릭 메뉴)과 이미지 파일 불러오기/저장.
// 렌더·입력은 내부 서피스(CvDispSurface)가 맡고, 바인딩 표면은 CvDispCtrl.Props.cs 에 있다.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using OpenCvSharp;
using CvInspect.Display;
using CvInspect.Vision.Edit;

namespace CvInspect.Controls;

/// <summary>
/// OpenCV 검사용 WPF 디스플레이 — Mat 표시 + 중립 오버레이(ViOverlay) 렌더 + 편집 도형(CvEditShape) 인터랙션.
/// 오버레이/도형 좌표는 "표시 중인 이미지"의 픽셀 공간 — 원본이든 단계(축소) 이미지든,
/// 호출측이 이미지와 같은 공간의 좌표만 짝지어 바인딩한다.
/// 조작 체계: 상단 툴바(좌: 📸그랩·⏯️라이브 / 우: ⛶Fit·➕➖줌) +
/// 우클릭 이미지 불러오기/저장 + 휠 줌·좌드래그 팬/도형 편집.
/// 툴바·상태 줄·영상 영역 바탕 색은 인스턴스 속성 → 호스트 리소스 키 → 고정색(영상 영역은 컨트롤 Background) 순으로 정해지고 실행 중 사전 교체를 따라온다
/// (<see cref="ToolbarBackgroundKey"/> 등 — CvDispCtrl.Theme.cs).
/// <c>Initialized</c> 는 생성자 안에서 난다(첫 논리 자식이 붙는 순간 — WPF 동작). XAML 의 <c>Initialized="…"</c> 처리기나 생성 뒤의
/// 구독은 불리지 않으니 초기화 훅은 <c>Loaded</c> 에 건다.
/// </summary>
public sealed partial class CvDispCtrl : Grid
{
    private readonly CvDispSurface _surface = new();
    private readonly StackPanel _camPanel;
    private readonly StackPanel _dispPanel;
    private readonly Border _toolbarBorder;
    private readonly Border[] _toolSeps;
    private readonly Border _statusBorder;
    private readonly (CvDispMenuGroup Group, FrameworkElement Elem)[] _menuGroupItems;   // 함께 여닫는 메뉴 자리(구분선 포함) — 코어 CvDispMenu
    private readonly Button _grabBtn;
    private readonly ToggleButton _playBtn;
    private readonly ToggleButton _panBtn;
    private readonly TextBlock _statusLeft;
    private readonly TextBlock _statusRight;
    private readonly EventHandler<EventArgs> _onCamCommandCanExecuteChanged;
    private bool _syncingPlay;

    public CvDispCtrl()
    {
        // 명령 속성 콜백(SwapCamCommand)이 쓰는 처리기 — 앱 사전 스타일이 명령을 주면 그 콜백이 생성자 안에서 돈다(맨 끝 주석).
        _onCamCommandCanExecuteChanged = OnCamCommandCanExecuteChanged;
        Background = new SolidColorBrush(ColorOf(CvDispTheme.DefaultControlBack));
        // 행 정의와 자식은 이 생성자 맨 끝에서 붙인다 — 이유는 그 자리 주석.

        // === 툴바 — 좌: 카메라 조작 / 우: 디스플레이 조작. ToolbarDock 으로 4방향 도킹 (기본 상단) ===
        var toolbar = new DockPanel { LastChildFill = false };

        _camPanel = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(_camPanel, Dock.Left);

        // 활성 여부는 RefreshCamButtons 가 명령의 CanExecute 로 정한다. 클릭에서도 다시 묻는다(코어 CvCamCmd.Click — 실행 뒤에는 다시
        // 묻지 않는 이유도 그쪽). 거절하면 버튼 상태도 다시 정한다(⏯ 는 OnPlayToggle 의 되돌림이 같은 일을 한다): 호스트가 CanExecuteChanged
        // 없이 CanExecute 를 false 로 바꿨다면 이 클릭이 그것을 알려 준 유일한 신호이고, 다시 정하지 않으면 버튼이 살아 보이는데 눌러도
        // 아무 일이 없는 채로 남는다(7650d05 실측 — 회귀 ARefusedGrabClickGreysAStaleButtonLikePlay). 거절 뒤 회색은 다음 알림까지 남지만,
        // 그때는 그것이 명령의 지금 답이다. (⏯ 의 되돌림은 실행 뒤에도 다시 정한다 — IsRunning 을 맞추는 길에 딸려 오는 것이다.)
        _grabBtn = MakeToolButton("📸", "Grab");
        _grabBtn.Click += (_, _) =>
        {
            if (CvCamCmd.Click(GrabCommand) == CvCmdRun.Refused) RefreshCamButtons();
        };
        _camPanel.Children.Add(_grabBtn);

        _playBtn = new ToggleButton
        {
            Content = "⏯️",
            ToolTip = "Play / Stop",
            FontSize = 16,
            Padding = new Thickness(10, 4, 10, 4),
            MinWidth = 44,
            MinHeight = 40,
            Margin = new Thickness(2, 0, 2, 0),
            Foreground = Brushes.White,   // 바탕(강조색)은 ApplyAccents 가 칠한다
        };
        _playBtn.Checked += OnPlayToggle;
        _playBtn.Unchecked += OnPlayToggle;
        _camPanel.Children.Add(_playBtn);
        toolbar.Children.Add(_camPanel);

        _dispPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var dispPanel = _dispPanel;
        DockPanel.SetDock(dispPanel, Dock.Right);
        var fitBtn = MakeToolButton("⛶", "Fit");
        fitBtn.Click += (_, _) => FitToView();
        var zoomInBtn = MakeToolButton("➕", "Zoom In");
        zoomInBtn.Click += (_, _) => _surface.ZoomAtCenter(CvViewXform.ZoomStep);
        var zoomOutBtn = MakeToolButton("➖", "Zoom Out");
        zoomOutBtn.Click += (_, _) => _surface.ZoomAtCenter(1 / CvViewXform.ZoomStep);

        // 팬 모드 토글 — 켜면 드래그가 항상 팬 (편집 도형이 화면을 덮어도 이동/리사이즈 대신 화면 이동)
        var panBtn = new ToggleButton
        {
            Content = "✋",
            ToolTip = "Pan",
            FontSize = 16,
            Padding = new Thickness(10, 4, 10, 4),
            MinWidth = 44,
            MinHeight = 40,
            Margin = new Thickness(2, 0, 2, 0),
        };
        _panBtn = panBtn;
        panBtn.Checked += (_, _) =>
        {
            _surface.IsPanMode = true;
            _surface.Cursor = Cursors.Hand;
            ApplyAccents();
            panBtn.Foreground = Brushes.White;
        };
        panBtn.Unchecked += (_, _) =>
        {
            _surface.IsPanMode = false;
            _surface.ClearValue(CursorProperty);
            // Grid(this)의 Panel.BackgroundProperty 가 아닌 버튼(Control) DP 를 명시 — 스타일 기본색 복원
            panBtn.ClearValue(Control.BackgroundProperty);
            panBtn.ClearValue(Control.ForegroundProperty);
        };

        var clearBtn = MakeToolButton("🧹", "Clear");
        clearBtn.Click += (_, _) => _surface.SetOverlay(null);   // 결과 오버레이만 지움 — 다음 검사가 새 오버레이로 교체

        // 버튼 순서/그룹 = 우클릭 메뉴와 동일 (팬 │ 화면 맞춤 → 줌 │ 클리어)
        _toolSeps = [MakeToolSeparator(), MakeToolSeparator()];
        dispPanel.Children.Add(panBtn);
        dispPanel.Children.Add(_toolSeps[0]);
        dispPanel.Children.Add(fitBtn);
        dispPanel.Children.Add(zoomInBtn);
        dispPanel.Children.Add(zoomOutBtn);
        dispPanel.Children.Add(_toolSeps[1]);
        dispPanel.Children.Add(clearBtn);
        toolbar.Children.Add(dispPanel);

        // 툴바·상태 줄의 바탕·글자·구분선 색은 ApplyTheme 가 칠한다(속성 → 호스트 키 → 기본색) — 여기서 브러시를 박으면 호스트가 못 덮는다.
        _toolbarBorder = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(4, 3, 4, 3),
            Child = toolbar,
        };

        // 툴바 도킹 호스트 — 툴바가 4방향 어디든 붙고 나머지를 서피스가 채움 (상태바는 항상 하단 별도 행)
        var dockHost = new DockPanel();
        DockPanel.SetDock(_toolbarBorder, Dock.Top);
        dockHost.Children.Add(_toolbarBorder);
        dockHost.Children.Add(_surface);
        SetRow(dockHost, 0);

        // === 하단 상태바 — 마우스 이미지 좌표·픽셀 값(좌) + 이미지 크기·줌 배율(우) ===
        var statusFont = new System.Windows.Media.FontFamily("Consolas");
        _statusLeft = new TextBlock { FontFamily = statusFont, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        _statusRight = new TextBlock { FontFamily = statusFont, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_statusRight, Dock.Right);
        var statusPanel = new DockPanel { LastChildFill = false };
        statusPanel.Children.Add(_statusRight);
        statusPanel.Children.Add(_statusLeft);
        _statusBorder = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(8, 2, 8, 2),
            Child = statusPanel,
        };
        SetRow(_statusBorder, 1);

        _surface.MouseMove += (_, e) => UpdateStatus(e.GetPosition(_surface));
        _surface.MouseLeave += (_, _) => UpdateStatus(null);
        // 뷰/프레임 변경(줌·핏·새 프레임) 통지 — 마우스 정지 상태에서도 크기/배율 라벨 최신 유지
        _surface.StatusChanged = () => Dispatcher.BeginInvoke(
            new Action(() => UpdateStatus(_surface.IsMouseOver ? Mouse.GetPosition(_surface) : null)),
            System.Windows.Threading.DispatcherPriority.Background);
        UpdateStatus(null);

        // === 우클릭 메뉴 — 순서·번역 키·함께 여닫는 묶음·활성 조건은 코어 CvDispMenu(근거도 그쪽) ===
        // 항목 구성은 여기서 한 번만 끝내고, 이후엔 Visibility 로만 여닫는다 — 열리는 도중에 Items 를 갈아 끼우면 팝업이 직전 구성의
        // 크기로 열려 늘어난 항목이 배경 장식 밖으로 삐져나온 채 그려진다(장식 없는 맨 배경 위에 한 줄만 떠 보인다).
        var menu = new ContextMenu();
        var menuItems = new List<(CvDispMenuCmd Cmd, MenuItem Item)>();
        var groupItems = new List<(CvDispMenuGroup Group, FrameworkElement Elem)>();
        MenuItem? panItem = null;
        foreach (var entry in CvDispMenu.Layout)
        {
            FrameworkElement elem;
            if (entry.Cmd is { } cmd)
            {
                var item = new MenuItem { IsCheckable = cmd == CvDispMenuCmd.Pan };
                item.Click += cmd switch
                {
                    CvDispMenuCmd.Load => (_, _) => LoadImageFromFile(),
                    CvDispMenuCmd.Save => (_, _) => SaveImageToFile(),
                    CvDispMenuCmd.Pan => (_, _) => panBtn.IsChecked = item.IsChecked,
                    CvDispMenuCmd.Fit => (_, _) => FitToView(),
                    CvDispMenuCmd.ZoomIn => (_, _) => _surface.ZoomAtCenter(CvViewXform.ZoomStep),
                    CvDispMenuCmd.ZoomOut => (_, _) => _surface.ZoomAtCenter(1 / CvViewXform.ZoomStep),
                    _ => (RoutedEventHandler)((_, _) => _surface.SetOverlay(null)),   // Clear — 결과 오버레이만 지움
                };
                if (cmd == CvDispMenuCmd.Pan) panItem = item;
                menuItems.Add((cmd, item));
                elem = item;
            }
            else
            {
                elem = new Separator();
            }
            menu.Items.Add(elem);
            if (entry.Group != CvDispMenuGroup.Always) groupItems.Add((entry.Group, elem));
        }
        _menuGroupItems = groupItems.ToArray();

        void RefreshMenu()
        {
            var hasFrame = Frame is not null;
            foreach (var (cmd, item) in menuItems)
            {
                item.Header = CvLoc.T(CvDispMenu.LabelKey(cmd));   // 매번 다시 읽는다 — 언어 전환 대응
                // 활성은 프레임에 걸린 항목(저장)에만 Local 로 넣는다 — 나머지에 Local 을 넣으면 호스트 MenuItem 스타일의 IsEnabled 를 이긴다.
                if (cmd == CvDispMenuCmd.Save) item.IsEnabled = CvDispMenu.IsEnabled(cmd, hasFrame);
            }
            panItem!.IsChecked = panBtn.IsChecked == true;
        }

        // 팝업이 뜨기 전에 맞춘다. 여기서 손대는 건 라벨·체크·활성 뿐이라 항목 수가 변하지 않는다 —
        // 표시 여부는 DP 가 바뀌는 시점에 이미 정해져 있다.
        ContextMenuOpening += (_, _) => RefreshMenu();
        RefreshMenu();
        ContextMenu = menu;

        // ctor 시점엔 바인딩이 아직 안 붙어 LoadFrameCommand 가 항상 null 이다. 붙는 순간 DP 콜백이 다시 맞추므로
        // 여기서는 "안 붙는 경우"의 기본만 잡아 둔다 — 안 잡으면 불러오기가 없는데 안내 문구만 남는다.
        SyncLoadAvailability(LoadFrameCommand is not null);
        SyncToolbarVisibility(IsToolbarVisible);

        // 색은 요소가 다 만들어진 뒤에 — 묶는 순간 앱 리소스에 키가 있으면 콜백이 생성자 안에서 바로 돈다.
        ApplyTheme();
        BindThemeSlots();

        // 행 정의와 자식 붙이기는 맨 끝이어야 한다. 이 컨트롤의 첫 논리 자식이 붙는 순간 WPF 가 생성자 안에서 Initialized 를
        // 올리고 암시 스타일을 적용한다(그때 보이는 것은 앱 사전뿐이다). 앱 사전의 CvDispCtrl 스타일이 속성(ToolbarBackground·
        // ToolbarDock·IsToolbarVisible 등)을 주면 그 콜백이 여기서 돈다 — 칠하거나 옮길 필드가 아직 null 이면 생성자가 던진다.
        // 0.29.4 까지는 이 줄들이 생성자 첫머리에 있어서, 그 구성(앱 사전 스타일이 ToolbarDock 등을 줌)이면 생성자가
        // NullReferenceException·ArgumentNullException 으로 끝나 그 화면이 뜨지 않았다(실측).
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(dockHost);
        Children.Add(_statusBorder);
    }

    /// <summary>상태바 갱신 — pos 는 surface 기준 마우스 위치 (null = 이미지 밖/마우스 이탈). 글자 형식은 코어 CvDispStatus.</summary>
    private void UpdateStatus(System.Windows.Point? pos)
    {
        _statusRight.Text = CvDispStatus.SizeText(_surface.FrameSize, _surface.ViewScale);
        _statusLeft.Text = CvDispStatus.ProbeText(pos is null ? null : _surface.ProbeImage(pos.Value));
    }

    // MinWidth 44 — 이모지 글리프마다 어드밴스 폭이 달라(컬러 이모지 vs 텍스트 기호) 버튼 폭이 제각각이 되는 것을 흡수.
    private static Button MakeToolButton(string content, string tooltip) => new()
    {
        Content = content,
        ToolTip = tooltip,
        FontSize = 16,
        Padding = new Thickness(10, 4, 10, 4),
        MinWidth = 44,
        MinHeight = 40,
        Margin = new Thickness(2, 0, 2, 0),
    };

    /// <summary>툴바 그룹 구분 세로선 — 우클릭 메뉴의 구분선과 동일 그룹 경계. 색은 ApplyTheme 가 칠한다.</summary>
    private static Border MakeToolSeparator() => new()
    {
        Width = 1,
        Margin = new Thickness(4, 6, 4, 6),
    };

    private void OnPlayToggle(object sender, RoutedEventArgs e)
    {
        ApplyAccents();
        if (_syncingPlay) return;
        try
        {
            // 토글 직후라 눌림이면 시작, 풀림이면 정지다. CanExecute 가 false 면 내지 않는다 — 버튼이 회색이어도 이 길에는 회색이 되기
            // 전에 들어온 클릭이 닿는다. 내지 않아도 아래 되돌림이 버튼을 IsRunning 으로 돌려놓는다(그래서 Click 의 결과는 보지 않는다).
            CvCamCmd.Click(CvCamCmd.PlayClickCmd(_playBtn.IsChecked == true, ContinuousCommand, StopCommand));
        }
        finally
        {
            // 버튼은 누른 사람의 뜻이 아니라 IsRunning(VM 의 카메라 상태)을 보인다. 명령이 라이브를 켜지 못하면(시작 거절·예외·
            // 명령 없음·아직 비동기로 켜는 중) IsRunning 이 그대로라 속성 변경 콜백이 안 돌고 버튼이 눌린 채 굳었다 — VM 이 같은 값을
            // 다시 알려도 속성 값이 안 바뀌어 풀리지 않는다(소비자가 원문 독해로 짚음). 명령 뒤 곧바로 권위 있는 값으로 되돌린다:
            // 동기로 켜졌으면 이미 같은 값이라 아무 일도 없고, 비동기로 켜지면 켜지는 순간 콜백이 다시 누른다. 정지 쪽도 같다.
            SyncPlayState(IsRunning);
        }
    }

    // === 카메라 버튼과 명령의 CanExecute ===
    // 판정(활성·⏯ 가 낼 명령·클릭 실행·던지는 CanExecute 의 한 번 경고)은 코어 CvCamCmd 에 있고, 여기는 구독·스레드 넘기기·버튼 칠하기만 남는다.
    // WPF 버튼과 다른 점 셋(그 버튼의 처리기는 알림을 올린 스레드에서 바로 IsEnabled 를 다시 계산하고, CanExecute 의 예외를 그대로 흘린다 —
    // 원문 확인): 다른 스레드에서 온 알림은 UI 스레드로 넘기고, CanExecute 가 던져도 알림을 올린 쪽으로 돌려보내지 않는다. 알림을 올리는
    // 쪽은 흔히 바쁨 표식을 세운 직후 try 밖에서 올리므로, 여기서 던지면 그 표식이 안 풀린 채 남아 카메라 명령이 영구히 막힌다.
    // 셋째로 거절된 클릭 뒤에 버튼 상태를 다시 정한다(📸 는 Click 처리기, ⏯ 는 OnPlayToggle 의 되돌림) — 알림을 빠뜨린 명령에서 살아 보이는
    // 죽은 버튼이 남지 않게. WPF 버튼의 클릭 길은 CanExecute 가 false 면 Execute 를 건너뛸 뿐 IsEnabled 는 다시 계산하지 않는다(원문 확인).

    /// <summary>명령 속성이 바뀌면 옛 명령의 구독을 놓고 새 명령에 건다. 구독은 <see cref="CanExecuteChangedEventManager"/>(약한 구독)로
    /// 건다 — WPF 버튼이 Command 를 들을 때와 같은 길이다. 명령은 대개 컨트롤보다 오래 사는 view-model 이 들고 있어, 보통 구독이면 버려진
    /// 컨트롤이 백버퍼째 수집되지 않는다. 편집 도형처럼 Loaded/Unloaded 에 걸지 않는 이유: 화면에 올라오기 전이나 가려진 동안에도 버튼
    /// 상태가 맞아야 하는데, 약한 구독은 누수 없이 그것까지 준다. 처리기는 필드 하나에 담아 붙이고 뗄 때 같은 것을 쓴다 — 관리자는 대리자
    /// 같음으로 찾아 떼고, 대리자를 그 대상(이 컨트롤)에 묶어서만 살려 둔다(원문 확인).</summary>
    private void SwapCamCommand(ICommand? oldCmd, ICommand? newCmd)
    {
        if (oldCmd is not null) CanExecuteChangedEventManager.RemoveHandler(oldCmd, _onCamCommandCanExecuteChanged);
        if (newCmd is not null) CanExecuteChangedEventManager.AddHandler(newCmd, _onCamCommandCanExecuteChanged);
        RefreshCamButtons();
    }

    /// <summary>약한 구독의 중계자는 알림을 올린 스레드에서 이 처리기를 부른다. UI 스레드가 아니면 넘기고 돌아간다 — 기다리지 않는다.
    /// 여기서 하는 일은 다시 묻기뿐이고 그 안의 예외는 <see cref="CvCamCmd.CanRun"/> 이 삼키므로 이 처리기는 던지지 않는다.</summary>
    private void OnCamCommandCanExecuteChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess()) RefreshCamButtons();
        else Dispatcher.BeginInvoke(new Action(RefreshCamButtons));
    }

    /// <summary>📸·⏯ 의 활성을 지금의 명령으로 다시 정한다(코어 CvCamCmd.IsEnabled — 명령이 없으면 종전대로 살아 있다). ⏯ 는 눌렀을 때
    /// 낼 명령이 정한다(CvCamCmd.PlayGateCmd). 그래서 IsRunning 이 바뀔 때도 다시 정한다(SyncPlayState). 거절된 클릭 뒤에도 다시
    /// 정한다(📸 Click 처리기·⏯ 되돌림).
    /// 활성만 바꾸고 색은 넣지 않는다 — 회색은 호스트의 버튼 스타일이 그린다. ⏯ 에 넣어 둔 강조색은 그대로 두어도 된다: 기본 버튼 스타일의 비활성 트리거가 그 위를 덮는다(실측, Windows 기본
    /// 테마·96DPI 오프스크린: 멈춘 ⏯ 가 살아 있으면 정지 강조색 화소 1409, 회색이면 0 이고 #F4F4F4 로 그려진다).</summary>
    private void RefreshCamButtons()
    {
        _grabBtn.IsEnabled = CvCamCmd.IsEnabled(GrabCommand);
        _playBtn.IsEnabled = CvCamCmd.IsEnabled(CvCamCmd.PlayGateCmd(IsRunning, ContinuousCommand, StopCommand));
    }

    /// <summary>
    /// 첫 화면(맞춤)으로 되돌린다 — 툴바 ⛶ 와 같다. 사람이 준 줌·이동을 버린다.
    /// 숨겼다 다시 보이는 것은 크기 변경이 아니라 줌이 남는다(<see cref="Frame"/> 설명) — 페이지를 오갈 때 첫 화면으로
    /// 돌아오게 하고 싶은 호스트가 부르는 자리다. 맞춤은 예약만 하고 다음 그리기에서 한다: 숨긴 채 불러도 되고
    /// (보이는 순간 맞춘 그림이 나온다), 프레임이 아직 없으면 첫 프레임이 맞춰서 나온다.
    /// UI 스레드에서 부른다.
    /// </summary>
    public void FitToView() => _surface.FitToView();

    /// <summary>렌더·입력 표면 — 회귀가 좌표로 조작한다(마우스 이벤트는 실제 장치 위치를 읽는다).</summary>
    internal CvDispSurface Surface => _surface;

    // 파일 규칙(확장자 목록·기본 이름·바이트 디코드/인코드·JPEG 알파 접기·Mat 소유권 넘기기)은 코어 CvImgFileIO. 여기는 대화상자와 오류 창만.

    private void LoadImageFromFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = CvImgFileIO.WinFilter(CvImgFileIO.OpenTypes),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            CvImgFileIO.LoadInto(dlg.FileName, LoadFrameCommand);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, CvLoc.T(CvDispMenu.LabelKey(CvDispMenuCmd.Load)), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveImageToFile()
    {
        if (Frame is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = CvImgFileIO.WinFilter(CvImgFileIO.SaveTypes),
            FileName = CvImgFileIO.DefaultSaveName(DateTime.Now),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            CvImgFileIO.WriteFrame(dlg.FileName, _surface.FrameBuf);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, CvLoc.T(CvDispMenu.LabelKey(CvDispMenuCmd.Save)), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
