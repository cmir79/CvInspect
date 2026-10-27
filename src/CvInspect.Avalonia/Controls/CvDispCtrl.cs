// CvDispCtrl 본체 — 컨트롤 조립(툴바·상태 줄·우클릭 메뉴)과 카메라 버튼의 명령 배선.
// 렌더·입력은 내부 표면(CvDispSurface)이, 바인딩 표면은 CvDispCtrl.Props.cs, 색은 CvDispCtrl.Theme.cs, 파일 불러오기·저장은
// CvDispCtrl.File.cs 가 맡는다. 판정 규칙(메뉴 구성·상태 줄 글자·명령 활성·색 출처 순서)은 UI 중립 계층(CvInspect.Display — 코어 소스 링크)에 있다.

using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Utilities;
using CvInspect.Display;

namespace CvInspect.Avalonia.Controls;

/// <summary>
/// OpenCV 검사용 Avalonia 디스플레이 — Mat 표시 + 중립 오버레이(ViOverlay) 렌더 + 편집 도형(CvEditShape) 인터랙션.
/// 오버레이/도형 좌표는 "표시 중인 이미지"의 픽셀 공간 — 원본이든 단계(축소) 이미지든,
/// 호출측이 이미지와 같은 공간의 좌표만 짝지어 바인딩한다.
/// 조작 체계: 툴바(📸그랩·⏯️라이브 / ✋팬 │ ⛶맞춤·➕➖줌 │ 🧹지우기, <see cref="ToolbarDock"/> 로 4방향) +
/// 우클릭 이미지 불러오기/저장 + 휠 줌·좌드래그 팬/도형 편집.
/// 툴바·상태 줄·영상 영역 바탕 색은 인스턴스 속성 → 호스트 리소스 키 → 고정색(영상 영역은 컨트롤 Background) 순으로 정해지고
/// 실행 중 사전·테마 변형 교체를 따라온다(<see cref="ToolbarBackgroundKey"/> 등 — CvDispCtrl.Theme.cs).
/// 테마를 강제하지 않는다 — 버튼·메뉴는 호스트 테마의 표준 컨트롤이라, 테마(Fluent·Simple 등)를 싣지 않은 앱에서는 보이지 않는다.
/// UI 스레드에서 만들고 쓴다.
/// </summary>
public sealed partial class CvDispCtrl : Grid
{
    private static readonly FontFamily StatusFont = new(CvDispDrawAdp.DataFontNames);

    private readonly CvDispSurface _surface = new();
    private readonly StackPanel _camPanel;
    private readonly StackPanel _dispPanel;
    private readonly Border _toolbarBorder;
    private readonly Border[] _toolSeps;
    private readonly Border _statusBorder;
    private readonly (CvDispMenuGroup Group, Control Elem)[] _menuGroupItems;   // 함께 여닫는 메뉴 자리(구분선 포함) — 코어 CvDispMenu
    private readonly MenuItem _loadItem;
    private readonly MenuItem _saveItem;
    private readonly Separator _fileSep;   // 파일 두 항목 뒤 구분선 — 둘 다 감추면 맨 위에 혼자 남지 않게 함께 감춘다
    private readonly Button _grabBtn;
    private readonly ToggleButton _playBtn;
    private readonly ToggleButton _panBtn;
    private readonly TextBlock _statusLeft;
    private readonly TextBlock _statusRight;
    private readonly CamCmdSub _grabSub;
    private readonly CamCmdSub _continuousSub;
    private readonly CamCmdSub _stopSub;
    private Cursor? _handCursor;
    private Point? _pointer;   // 표면 기준 마지막 포인터 자리 — 포인터가 표면 밖이면 null(상태 줄 탐침 칸)
    private bool _syncingPlay;

    static CvDispCtrl()
    {
        // 컨트롤 바탕 기본 #2A2A2A — 영상 영역 바탕(속성·키)이 없을 때 비치는 색. 지역 값이 아니라 기본값으로 둔다: 호스트의 직접 값과 스타일 세터 모두 이긴다.
        BackgroundProperty.OverrideDefaultValue<CvDispCtrl>(BrushOf(CvDispTheme.DefaultControlBack));
    }

    public CvDispCtrl()
    {
        // 생성자에서 ApplyStyling 을 부르지 않는다 — 앱 스타일은 논리 트리에 붙을 때 적용되고(실측), 여기서 부르면 그것이 영구히 빠진다.
        // 그래서 요소를 만드는 순서에 스타일 콜백이 끼어들 일이 없다.
        _grabSub = new CamCmdSub(this);
        _continuousSub = new CamCmdSub(this);
        _stopSub = new CamCmdSub(this);

        // === 툴바 — 카메라 조작 / 디스플레이 조작. ToolbarDock 으로 4방향 도킹 (기본 상단) ===
        var toolbar = new DockPanel { LastChildFill = false };

        _camPanel = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(_camPanel, Dock.Left);

        // 활성 여부는 RefreshCamButtons 가 명령의 CanExecute 로 정한다. 클릭에서도 다시 묻는다(코어 CvCamCmd.Click — 실행 뒤에는 다시
        // 묻지 않는 이유도 그쪽). 거절하면 버튼 상태도 다시 정한다(⏯ 는 OnPlayToggle 의 되돌림이 같은 일을 한다): 호스트가 CanExecuteChanged
        // 없이 CanExecute 를 false 로 바꿨다면 이 클릭이 그것을 알려 준 유일한 신호이고, 다시 정하지 않으면 버튼이 살아 보이는데 눌러도
        // 아무 일이 없는 채로 남는다.
        _grabBtn = MakeToolButton(new Button(), CvToolIconKind.Camera, "Grab");
        _grabBtn.Click += (_, _) =>
        {
            if (CvCamCmd.Click(GrabCommand) == CvCmdRun.Refused) RefreshCamButtons();
        };
        _camPanel.Children.Add(_grabBtn);

        _playBtn = MakeToolButton(new ToggleButton(), CvToolIconKind.PlayPause, "Play / Stop");
        _playBtn.Foreground = Brushes.White;   // 바탕(강조색)은 ApplyAccents 가 칠한다
        PinAccent(_playBtn, checkedOnly: false);
        _playBtn.IsCheckedChanged += OnPlayToggle;
        _camPanel.Children.Add(_playBtn);
        toolbar.Children.Add(_camPanel);

        _dispPanel = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(_dispPanel, Dock.Right);
        var fitBtn = MakeToolButton(new Button(), CvToolIconKind.Fit, "Fit");
        fitBtn.Click += (_, _) => FitToView();
        var zoomInBtn = MakeToolButton(new Button(), CvToolIconKind.ZoomIn, "Zoom In");
        zoomInBtn.Click += (_, _) => _surface.ZoomAtCenter(CvViewXform.ZoomStep);
        var zoomOutBtn = MakeToolButton(new Button(), CvToolIconKind.ZoomOut, "Zoom Out");
        zoomOutBtn.Click += (_, _) => _surface.ZoomAtCenter(1 / CvViewXform.ZoomStep);

        // 팬 모드 토글 — 켜면 드래그가 항상 팬 (편집 도형이 화면을 덮어도 이동/리사이즈 대신 화면 이동)
        var panBtn = MakeToolButton(new ToggleButton(), CvToolIconKind.Hand, "Pan");
        _panBtn = panBtn;
        PinAccent(panBtn, checkedOnly: true);
        panBtn.IsCheckedChanged += (_, _) =>
        {
            if (panBtn.IsChecked == true)
            {
                _surface.IsPanMode = true;
                _surface.Cursor = _handCursor ??= new Cursor(StandardCursorType.Hand);
                ApplyAccents();
                panBtn.Foreground = Brushes.White;
            }
            else
            {
                _surface.IsPanMode = false;
                _surface.ClearValue(CursorProperty);
                // 강조색을 걷어 버튼 스타일 색으로 돌린다
                panBtn.ClearValue(TemplatedControl.BackgroundProperty);
                panBtn.ClearValue(TemplatedControl.ForegroundProperty);
            }
        };

        var clearBtn = MakeToolButton(new Button(), CvToolIconKind.Broom, "Clear");
        clearBtn.Click += (_, _) => _surface.SetOverlay(null);   // 결과 오버레이만 지움 — 다음 검사가 새 오버레이로 교체

        // 버튼 순서/그룹 = 우클릭 메뉴와 동일 (팬 │ 화면 맞춤 → 줌 │ 클리어)
        _toolSeps = [MakeToolSeparator(), MakeToolSeparator()];
        _dispPanel.Children.Add(panBtn);
        _dispPanel.Children.Add(_toolSeps[0]);
        _dispPanel.Children.Add(fitBtn);
        _dispPanel.Children.Add(zoomInBtn);
        _dispPanel.Children.Add(zoomOutBtn);
        _dispPanel.Children.Add(_toolSeps[1]);
        _dispPanel.Children.Add(clearBtn);
        toolbar.Children.Add(_dispPanel);

        // 툴바·상태 줄의 바탕·글자·구분선 색은 ApplyTheme 가 칠한다(속성 → 호스트 키 → 기본색).
        _toolbarBorder = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(4, 3, 4, 3),
            Child = toolbar,
        };

        // 툴바 도킹 호스트 — 툴바가 4방향 어디든 붙고 나머지를 표면이 채움 (상태 줄은 항상 하단 별도 행)
        var dockHost = new DockPanel();
        DockPanel.SetDock(_toolbarBorder, Dock.Top);
        dockHost.Children.Add(_toolbarBorder);
        dockHost.Children.Add(_surface);
        SetRow(dockHost, 0);

        // === 하단 상태 줄 — 커서 아래 이미지 좌표·픽셀 값(좌) + 이미지 크기·줌 배율(우) ===
        _statusLeft = new TextBlock { FontFamily = StatusFont, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        _statusRight = new TextBlock { FontFamily = StatusFont, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
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

        // 포인터 자리는 직접 기억한다 — 이 프레임워크에는 "지금 포인터가 어디 있나" 를 묻는 전역 조회가 없다. 뷰/프레임 변경 통지(줌·맞춤·
        // 새 프레임)는 포인터가 멈춰 있어도 크기/배율 칸을 새로 쓰고, 탐침 칸은 기억한 자리로 다시 읽는다.
        _surface.PointerMoved += (_, e) =>
        {
            _pointer = e.GetPosition(_surface);
            UpdateStatus();
        };
        _surface.PointerExited += (_, _) =>
        {
            _pointer = null;
            UpdateStatus();
        };
        // 표면이 늘 UI 디스패처(Background 우선순위)를 거쳐 부르므로 여기서 다시 미루지 않는다.
        _surface.StatusChanged = UpdateStatus;
        UpdateStatus();

        // === 우클릭 메뉴 — 순서·번역 키·함께 여닫는 묶음·활성 조건은 코어 CvDispMenu(근거도 그쪽) ===
        // 항목 구성은 여기서 한 번만 끝내고, 이후엔 IsVisible 로만 여닫는다 — 근거는 코어 CvDispMenu 머리 주석.
        var menu = new ContextMenu();
        var menuItems = new List<(CvDispMenuCmd Cmd, MenuItem Item)>();
        var groupItems = new List<(CvDispMenuGroup Group, Control Elem)>();
        MenuItem? panItem = null, loadItem = null, saveItem = null;
        Separator? fileSep = null;
        foreach (var entry in CvDispMenu.Layout)
        {
            Control elem;
            if (entry.Cmd is { } cmd)
            {
                var item = new MenuItem { ToggleType = cmd == CvDispMenuCmd.Pan ? MenuItemToggleType.CheckBox : MenuItemToggleType.None };
                item.Click += cmd switch
                {
                    CvDispMenuCmd.Load => (_, _) => LoadImageFromFile(),
                    CvDispMenuCmd.Save => (_, _) => SaveImageToFile(),
                    // 팬은 툴바 ✋ 를 뒤집는다 — 메뉴 항목 자신의 체크가 클릭 전후 어느 쪽에서 바뀌는지에 기대지 않는다(열 때 ✋ 로 맞춰 둔다).
                    CvDispMenuCmd.Pan => (_, _) => panBtn.IsChecked = panBtn.IsChecked != true,
                    CvDispMenuCmd.Fit => (_, _) => FitToView(),
                    CvDispMenuCmd.ZoomIn => (_, _) => _surface.ZoomAtCenter(CvViewXform.ZoomStep),
                    CvDispMenuCmd.ZoomOut => (_, _) => _surface.ZoomAtCenter(1 / CvViewXform.ZoomStep),
                    _ => (EventHandler<RoutedEventArgs>)((_, _) => _surface.SetOverlay(null)),   // Clear — 결과 오버레이만 지움
                };
                if (cmd == CvDispMenuCmd.Pan) panItem = item;
                else if (cmd == CvDispMenuCmd.Load) loadItem = item;
                else if (cmd == CvDispMenuCmd.Save) saveItem = item;
                menuItems.Add((cmd, item));
                elem = item;
            }
            else
            {
                var sep = new Separator();
                fileSep ??= sep;   // 첫 구분선 = 파일 두 항목 뒤
                elem = sep;
            }
            menu.Items.Add(elem);
            if (entry.Group != CvDispMenuGroup.Always) groupItems.Add((entry.Group, elem));
        }
        _menuGroupItems = groupItems.ToArray();
        _loadItem = loadItem!;
        _saveItem = saveItem!;
        _fileSep = fileSep!;

        void RefreshMenu()
        {
            var hasFrame = Frame is not null;
            foreach (var (cmd, item) in menuItems)
            {
                item.Header = CvLoc.T(CvDispMenu.LabelKey(cmd));   // 매번 다시 읽는다 — 언어 전환 대응
                // 활성은 프레임에 걸린 항목(저장)에만 지역 값으로 넣는다 — 나머지에 넣으면 호스트 MenuItem 스타일의 IsEnabled 를 이긴다.
                if (cmd == CvDispMenuCmd.Save) item.IsEnabled = CvDispMenu.IsEnabled(cmd, hasFrame);
            }
            panItem!.IsChecked = panBtn.IsChecked == true;
        }

        // 팝업이 뜨기 전에 맞춘다. 여기서 손대는 건 라벨·체크·활성 뿐이라 항목 수가 변하지 않는다 —
        // 표시 여부는 그 조건(불러오기 명령·툴바 표시·파일 선택기)이 바뀌는 시점에 이미 정해져 있다.
        menu.Opening += (_, _) => RefreshMenu();
        RefreshMenu();
        ContextMenu = menu;

        // 속성 콜백은 값이 바뀔 때만 돈다 — 기본값 그대로인 구성의 메뉴·안내 문구 상태를 여기서 한 번 잡아 둔다.
        ApplyMenuGroups();
        _toolbarBorder.IsVisible = IsToolbarVisible;

        // 색 — 요소가 다 만들어진 뒤에. 슬롯을 묶는 순간 현재 값이 들어와 콜백이 돈다.
        ApplyTheme();
        BindThemeSlots();

        RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Children.Add(dockHost);
        Children.Add(_statusBorder);
    }

    /// <summary>상태 줄 갱신 — 탐침 칸은 기억한 포인터 자리(null = 표면 밖)로 읽는다. 글자 형식은 코어 CvDispStatus.</summary>
    private void UpdateStatus()
    {
        _statusRight.Text = CvDispStatus.SizeText(_surface.FrameSize, _surface.ViewScale);
        _statusLeft.Text = CvDispStatus.ProbeText(_pointer is { } p ? _surface.ProbeImage(p) : null);
    }

    // 아이콘은 글자가 아니라 벡터 경로(CvToolIcon — 근거는 그 파일 머리)라 버튼 가운데에 같은 크기로 앉는다. MinWidth 44·MinHeight 40 은
    // WPF 판 툴바와 같은 버튼 크기다.
    // 툴팁 문구는 번역하지 않는 고정 영어다 — 회귀가 이 문구로 버튼을 찾는다. 그림만 있는 버튼이라 같은 문구를 접근성 이름으로도 준다
    // (안 주면 화면 읽기 프로그램에는 이름 없는 버튼으로 읽힌다).
    private static T MakeToolButton<T>(T btn, CvToolIconKind icon, string tooltip) where T : Button
    {
        btn.Content = new CvToolIcon(icon);
        btn.Padding = new Thickness(10, 4, 10, 4);
        btn.MinWidth = 44;
        btn.MinHeight = 40;
        btn.Margin = new Thickness(2, 0, 2, 0);
        ToolTip.SetTip(btn, tooltip);
        AutomationProperties.SetName(btn, tooltip);
        return btn;
    }

    /// <summary>강조색 고정 — 토글 버튼의 템플릿 안쪽 바탕·글자를 버튼 자신의 Background·Foreground 로 묶는 스타일을 그 버튼에 단다.
    /// 이유: Fluent ToggleButton 테마(12.1.4)는 :pointerover·:pressed 와 :checked(그 안의 :pointerover·:pressed 포함) 상태에서
    /// <c>/template/ ContentPresenter#PART_ContentPresenter</c> 의 Background·Foreground 를 직접 칠한다. 그래서 버튼 Background 에
    /// 넣은 강조색(⏯ 라이브 Success·정지 Danger, ✋ Primary)이 눌린 상태와 포인터 올림에서 테마의 강조색에 가려진다.
    /// 버튼 자신의 Styles 는 ControlTheme 보다 우선순위가 높아 이 묶음이 이긴다. :disabled 는 건드리지 않는다(테마의 비활성 모양을 둔다).
    /// 헤드리스 실측(Fluent 12.1.4): 묶음 없는 ToggleButton 은 Background=Red·:checked 에서 안쪽 바탕이 #0078D7(테마 강조)로 덮였고,
    /// 묶은 ⏯ 는 정지·라이브·라이브+:pointerover·:pressed, ✋ 는 :checked·+:pointerover 에서 안쪽 바탕이 버튼 Background 와 같았다.
    /// <paramref name="checkedOnly"/> 면 눌렸을 때만 묶는다 — 풀린 ✋ 는 테마의 평소·포인터 올림 색을 그대로 쓴다.</summary>
    private static void PinAccent(ToggleButton btn, bool checkedOnly)
    {
        Selector Part(Selector? s)
        {
            var on = s.OfType<ToggleButton>().Not(x => x.Class(":disabled"));
            if (checkedOnly) on = on.Class(":checked");
            return on.Template().OfType<ContentPresenter>().Name("PART_ContentPresenter");
        }
        btn.Styles.Add(new Style(Part)
        {
            Setters =
            {
                new Setter(ContentPresenter.BackgroundProperty, new TemplateBinding(TemplatedControl.BackgroundProperty)),
                new Setter(ContentPresenter.ForegroundProperty, new TemplateBinding(TemplatedControl.ForegroundProperty)),
            },
        });
    }

    /// <summary>툴바 그룹 구분 세로선 — 우클릭 메뉴의 구분선과 동일 그룹 경계. 색은 ApplyTheme 가 칠한다.</summary>
    private static Border MakeToolSeparator() => new()
    {
        Width = 1,
        Margin = new Thickness(4, 6, 4, 6),
    };

    private void OnPlayToggle(object? sender, RoutedEventArgs e)
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
            // 명령 없음·아직 비동기로 켜는 중) IsRunning 이 그대로라 속성 변경 콜백이 안 돌고 버튼이 눌린 채 굳는다 — VM 이 같은 값을
            // 다시 알려도 속성 값이 안 바뀌어 풀리지 않는다. 명령 뒤 곧바로 권위 있는 값으로 되돌린다: 동기로 켜졌으면 이미 같은 값이라
            // 아무 일도 없고, 비동기로 켜지면 켜지는 순간 콜백이 다시 누른다. 정지 쪽도 같다.
            SyncPlayState(IsRunning);
        }
    }

    // === 카메라 버튼과 명령의 CanExecute ===
    // 판정(활성·⏯ 가 낼 명령·클릭 실행·던지는 CanExecute 의 한 번 경고)은 코어 CvCamCmd 에 있고, 여기는 구독·스레드 넘기기·버튼 칠하기만 남는다.
    // 표준 버튼의 Command 와 다른 점 셋: 다른 스레드에서 온 알림은 UI 스레드로 넘기고(컨트롤은 UI 스레드 밖에서 만지면 던진다),
    // CanExecute 가 던져도 알림을 올린 쪽으로 돌려보내지 않는다 — 알림을 올리는 쪽은 흔히 바쁨 표식을 세운 직후 try 밖에서 올리므로,
    // 여기서 던지면 그 표식이 안 풀린 채 남아 카메라 명령이 영구히 막힌다. 셋째로 거절된 클릭 뒤에 버튼 상태를 다시 정한다
    // (📸 는 Click 처리기, ⏯ 는 OnPlayToggle 의 되돌림) — 알림을 빠뜨린 명령에서 살아 보이는 죽은 버튼이 남지 않게.

    /// <summary>명령 속성이 바뀌면 그 자리의 구독자를 옛 명령에서 떼고 새 명령에 붙인다. 구독은 프레임워크의 약한 이벤트
    /// (<see cref="WeakEvents.CommandCanExecuteChanged"/> — 표준 버튼이 Command 를 들을 때와 같은 길)로 건다. 명령은 대개 컨트롤보다 오래 사는
    /// view-model 이 들고 있어, 보통 구독이면 버려진 컨트롤이 화면 비트맵째 수집되지 않는다. 편집 도형처럼 화면 붙음/떨어짐에 걸지 않는 이유:
    /// 화면에 올라오기 전이나 가려진 동안에도 버튼 상태가 맞아야 하는데, 약한 구독은 누수 없이 그것까지 준다.
    /// 구독자는 속성 자리마다 따로 둔다 — 같은 명령을 두 자리(예: 시작·정지)에 넣었다가 한 자리만 바꿔도 다른 자리의 구독이 남는다.
    /// 약한 이벤트는 구독자를 약하게 쥐므로 이 컨트롤이 필드로 붙잡는다.</summary>
    private void SwapCamCommand(CamCmdSub sub, ICommand? oldCmd, ICommand? newCmd)
    {
        if (oldCmd is not null) WeakEvents.CommandCanExecuteChanged.Unsubscribe(oldCmd, sub);
        if (newCmd is not null) WeakEvents.CommandCanExecuteChanged.Subscribe(newCmd, sub);
        RefreshCamButtons();
    }

    /// <summary>약한 이벤트의 중계는 알림을 올린 스레드에서 이 처리기를 부른다. UI 스레드가 아니면 넘기고 돌아간다 — 기다리지 않는다.
    /// 여기서 하는 일은 다시 묻기뿐이고 그 안의 예외는 <see cref="CvCamCmd.CanRun"/> 이 삼키므로 이 처리기는 던지지 않는다.</summary>
    private void OnCamCommandCanExecuteChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) RefreshCamButtons();
        else Dispatcher.UIThread.Post(RefreshCamButtons);
    }

    /// <summary>약한 이벤트 구독자 — 컨트롤의 공개 형에 구독 인터페이스를 드러내지 않으려고 따로 둔다.</summary>
    private sealed class CamCmdSub(CvDispCtrl owner) : IWeakEventSubscriber<EventArgs>
    {
        public void OnEvent(object? sender, WeakEvent ev, EventArgs e) => owner.OnCamCommandCanExecuteChanged();
    }

    /// <summary>📸·⏯ 의 활성을 지금의 명령으로 다시 정한다(코어 CvCamCmd.IsEnabled — 명령이 없으면 종전대로 살아 있다). ⏯ 는 눌렀을 때
    /// 낼 명령이 정한다(CvCamCmd.PlayGateCmd). 그래서 IsRunning 이 바뀔 때도 다시 정한다(SyncPlayState). 거절된 클릭 뒤에도 다시
    /// 정한다(📸 Click 처리기·⏯ 되돌림). 활성만 바꾸고 색은 넣지 않는다 — 회색은 호스트 테마의 버튼 스타일이 그린다.</summary>
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

    /// <summary>렌더·입력 표면 — 회귀가 좌표로 조작한다.</summary>
    internal CvDispSurface Surface => _surface;
}
