// CvDispCtrl 본체 — 컨트롤 조립(툴바·상태바·우클릭 메뉴)과 이미지 파일 불러오기/저장.
// 렌더·입력은 내부 서피스(CvDispSurface)가 맡고, 바인딩 표면은 CvDispCtrl.Props.cs 에 있다.

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using OpenCvSharp;

namespace CvInspect.Controls;

/// <summary>
/// OpenCV 검사용 WPF 디스플레이 — Mat 표시 + 중립 오버레이(ViOverlay) 렌더 + 편집 도형(CvEditShape) 인터랙션.
/// 오버레이/도형 좌표는 "표시 중인 이미지"의 픽셀 공간 — 원본이든 단계(축소) 이미지든,
/// 호출측이 이미지와 같은 공간의 좌표만 짝지어 바인딩한다.
/// 조작 체계: 상단 툴바(좌: 📸그랩·⏯️라이브 / 우: ⛶Fit·➕➖줌) +
/// 우클릭 이미지 불러오기/저장 + 휠 줌·좌드래그 팬/도형 편집.
/// </summary>
public sealed partial class CvDispCtrl : Grid
{
    private readonly CvDispSurface _surface = new();
    private readonly StackPanel _camPanel;
    private readonly StackPanel _dispPanel;
    private readonly Border _toolbarBorder;
    private readonly Border[] _toolSeps;
    private readonly MenuItem _loadItem;
    private readonly FrameworkElement[] _toolMenuItems;
    private readonly ToggleButton _playBtn;
    private readonly TextBlock _statusLeft;
    private readonly TextBlock _statusRight;
    private bool _syncingPlay;

    public CvDispCtrl()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // === 툴바 — 좌: 카메라 조작 / 우: 디스플레이 조작. ToolbarDock 으로 4방향 도킹 (기본 상단) ===
        var toolbar = new DockPanel { LastChildFill = false };

        _camPanel = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(_camPanel, Dock.Left);

        var grabBtn = MakeToolButton("📸", "Grab");
        grabBtn.Click += (_, _) => GrabCommand?.Execute(null);
        _camPanel.Children.Add(grabBtn);

        _playBtn = new ToggleButton
        {
            Content = "⏯️",
            ToolTip = "Play / Stop",
            FontSize = 16,
            Padding = new Thickness(10, 4, 10, 4),
            MinWidth = 44,
            MinHeight = 40,
            Margin = new Thickness(2, 0, 2, 0),
            Foreground = Brushes.White,
            Background = ThemeBrush("DangerBrush", 0xC6, 0x28, 0x28),
        };
        _playBtn.Checked += OnPlayToggle;
        _playBtn.Unchecked += OnPlayToggle;
        _camPanel.Children.Add(_playBtn);
        toolbar.Children.Add(_camPanel);

        _dispPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var dispPanel = _dispPanel;
        DockPanel.SetDock(dispPanel, Dock.Right);
        var fitBtn = MakeToolButton("⛶", "Fit");
        fitBtn.Click += (_, _) => _surface.FitToView();
        var zoomInBtn = MakeToolButton("➕", "Zoom In");
        zoomInBtn.Click += (_, _) => _surface.ZoomAtCenter(1.25);
        var zoomOutBtn = MakeToolButton("➖", "Zoom Out");
        zoomOutBtn.Click += (_, _) => _surface.ZoomAtCenter(1 / 1.25);

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
        panBtn.Checked += (_, _) =>
        {
            _surface.IsPanMode = true;
            _surface.Cursor = Cursors.Hand;
            panBtn.Background = ThemeBrush("PrimaryBrush", 0x15, 0x65, 0xC0);
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

        _toolbarBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
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
        Children.Add(dockHost);

        // === 하단 상태바 — 마우스 이미지 좌표·픽셀 값(좌) + 이미지 크기·줌 배율(우) ===
        var statusFont = new System.Windows.Media.FontFamily("Consolas");
        _statusLeft = new TextBlock { FontFamily = statusFont, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), VerticalAlignment = VerticalAlignment.Center };
        _statusRight = new TextBlock { FontFamily = statusFont, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_statusRight, Dock.Right);
        var statusPanel = new DockPanel { LastChildFill = false };
        statusPanel.Children.Add(_statusRight);
        statusPanel.Children.Add(_statusLeft);
        var statusBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(8, 2, 8, 2),
            Child = statusPanel,
        };
        SetRow(statusBorder, 1);
        Children.Add(statusBorder);

        _surface.MouseMove += (_, e) => UpdateStatus(e.GetPosition(_surface));
        _surface.MouseLeave += (_, _) => UpdateStatus(null);
        // 뷰/프레임 변경(줌·핏·새 프레임) 통지 — 마우스 정지 상태에서도 크기/배율 라벨 최신 유지
        _surface.StatusChanged = () => Dispatcher.BeginInvoke(
            new Action(() => UpdateStatus(_surface.IsMouseOver ? Mouse.GetPosition(_surface) : null)),
            System.Windows.Threading.DispatcherPriority.Background);
        UpdateStatus(null);

        // === 우클릭 메뉴 — 디스플레이 조작(버튼 순서와 동일) + 구분선 + 이미지 파일 불러오기/저장 ===
        var panItem = new MenuItem { IsCheckable = true };
        panItem.Click += (_, _) => panBtn.IsChecked = panItem.IsChecked;
        var zoomInItem = new MenuItem();
        zoomInItem.Click += (_, _) => _surface.ZoomAtCenter(1.25);
        var zoomOutItem = new MenuItem();
        zoomOutItem.Click += (_, _) => _surface.ZoomAtCenter(1 / 1.25);
        var fitItem = new MenuItem();
        fitItem.Click += (_, _) => _surface.FitToView();
        var clearItem = new MenuItem();
        clearItem.Click += (_, _) => _surface.SetOverlay(null);
        var loadItem = new MenuItem();
        loadItem.Click += (_, _) => LoadImageFromFile();
        var saveItem = new MenuItem();
        saveItem.Click += (_, _) => SaveImageToFile();
        var menu = new ContextMenu();
        _loadItem = loadItem;
        // 감출 때는 그 자리에 딸린 구분선까지 한 묶음으로 — 항목만 빼면 구분선이 겹쳐 남는다.
        //  - 불러오기: 받을 곳(LoadFrameCommand)이 없으면 감춘다
        //  - 그래픽 지우기: 툴바 버튼과 짝이라 툴바를 감추면 같이 사라진다
        _toolMenuItems = [new Separator(), clearItem];

        // 항목 구성은 여기서 한 번만 끝내고, 이후엔 Visibility 로만 여닫는다. 메뉴가 열리는 도중에
        // Items 를 갈아 끼우면 팝업이 직전 구성의 크기로 열려, 늘어난 항목이 배경 장식 밖으로
        // 삐져나온 채 그려진다(장식 없는 맨 배경 위에 한 줄만 떠 보인다).
        menu.Items.Add(loadItem);
        menu.Items.Add(saveItem);

        // 팬·맞춤·확대·축소는 툴바와 무관하게 늘 남긴다. 툴바를 감춘 화면에서 무슨 이유로든 배율이
        // 어긋나면 되돌릴 수단이 이 메뉴밖에 없다 — 버튼이 없다고 메뉴에서도 빼면 손쓸 방법이 없어진다.
        menu.Items.Add(new Separator());
        menu.Items.Add(panItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(fitItem);
        menu.Items.Add(zoomInItem);
        menu.Items.Add(zoomOutItem);

        foreach (var item in _toolMenuItems) menu.Items.Add(item);

        void RefreshMenu()
        {
            // 라벨은 매번 다시 읽는다 — 언어 전환 대응.
            panItem.Header = CvLoc.T("cv:Pan");
            zoomInItem.Header = CvLoc.T("cv:ZoomIn");
            zoomOutItem.Header = CvLoc.T("cv:ZoomOut");
            fitItem.Header = CvLoc.T("cv:FitView");
            clearItem.Header = CvLoc.T("cv:ClearGraphics");
            loadItem.Header = CvLoc.T("cv:LoadImage");
            saveItem.Header = CvLoc.T("cv:SaveImage");
            panItem.IsChecked = panBtn.IsChecked == true;
            // 저장은 "지금 그릴 게 없다"는 일시 상태라 회색으로 남긴다 — 불러오기와 달리 화면의 성격 문제가 아니다.
            saveItem.IsEnabled = Frame is not null;
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
    }

    /// <summary>상태바 갱신 — pos 는 surface 기준 마우스 위치 (null = 이미지 밖/마우스 이탈).</summary>
    private void UpdateStatus(System.Windows.Point? pos)
    {
        var size = _surface.FrameSize;
        _statusRight.Text = size is null
            ? ""
            : $"{size.Value.W}×{size.Value.H}  {Math.Round(_surface.ViewScale * 100)}%";

        var probe = pos is null ? null : _surface.ProbeImage(pos.Value);
        _statusLeft.Text = probe is null
            ? ""
            : probe.Value.Val is { } v
                ? $"X {probe.Value.X}  Y {probe.Value.Y}  {v}"
                : $"X {probe.Value.X}  Y {probe.Value.Y}";
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

    /// <summary>툴바 강조색 — 호스트 테마가 정의한 컨트롤 라이브러리 키(PrimaryBrush/SuccessBrush/DangerBrush)를
    /// 있으면 그대로 써서 화면 전체 팔레트를 따르고, 없으면(테마 미병합·콘솔 하네스) 고정색으로 떨어진다.
    /// 이 컨트롤은 UI 라이브러리를 참조하지 않는다 — 키 이름만 안다.</summary>
    private Brush ThemeBrush(string key, byte r, byte g, byte b)
        => TryFindResource(key) as Brush ?? new SolidColorBrush(Color.FromRgb(r, g, b));

    /// <summary>툴바 그룹 구분 세로선 — 우클릭 메뉴의 구분선과 동일 그룹 경계.</summary>
    private static Border MakeToolSeparator() => new()
    {
        Width = 1,
        Background = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
        Margin = new Thickness(4, 6, 4, 6),
    };

    private void OnPlayToggle(object sender, RoutedEventArgs e)
    {
        _playBtn.Background = _playBtn.IsChecked == true
            ? ThemeBrush("SuccessBrush", 0x2E, 0x7D, 0x32)
            : ThemeBrush("DangerBrush", 0xC6, 0x28, 0x28);
        if (_syncingPlay) return;
        if (_playBtn.IsChecked == true) ContinuousCommand?.Execute(null);
        else StopCommand?.Execute(null);
    }

    private void LoadImageFromFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Image|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff|All|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var mat = Cv2.ImRead(dlg.FileName, ImreadModes.Unchanged);
            if (mat.Empty())
            {
                mat.Dispose();
                throw new InvalidOperationException("Unsupported image format");
            }
            // Mat 소유권은 명령 수신자(VM)로 넘어간다 — 받을 곳이 없으면 여기서 해제.
            if (LoadFrameCommand is { } cmd) cmd.Execute(mat);
            else mat.Dispose();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, CvLoc.T("cv:LoadImage"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveImageToFile()
    {
        if (Frame is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG|*.png|BMP|*.bmp|JPEG|*.jpg",
            FileName = $"image_{DateTime.Now:yyyyMMdd_HHmmss}.png",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            // 저장 원본은 서피스의 픽셀 스냅샷 — Frame DP 의 Mat 은 할당 후 호스트가 dispose 했을 수 있어 건드리지 않는다.
            using var mat = _surface.SnapMat()
                ?? throw new InvalidOperationException("Unsupported frame format");
            var isJpeg = Path.GetExtension(dlg.FileName).ToLowerInvariant() is ".jpg" or ".jpeg";
            if (isJpeg && mat.Channels() == 4)
            {
                // JPEG 는 알파 미지원 — BGRA 는 BGR 로 접어 저장
                using var bgr = new Mat();
                Cv2.CvtColor(mat, bgr, ColorConversionCodes.BGRA2BGR);
                if (!Cv2.ImWrite(dlg.FileName, bgr)) throw new IOException("Failed to write image file");
            }
            else if (!Cv2.ImWrite(dlg.FileName, mat))
            {
                throw new IOException("Failed to write image file");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, CvLoc.T("cv:SaveImage"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
