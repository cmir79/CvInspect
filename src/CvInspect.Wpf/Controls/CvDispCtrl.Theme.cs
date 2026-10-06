// CvDispCtrl 의 색 표면 — 툴바·상태 줄 색과 ⏯·✋ 강조색을 어디서 가져오는지.
// 먼저 있는 것이 이긴다: 이 인스턴스에 준 속성 값(직접 값·바인딩·스타일 세터) → 호스트 리소스 키(색 사전 한 곳에서 모든
// 인스턴스) → 고정 기본색. 키는 리소스 참조로 붙잡아 두므로 호스트가 실행 중 사전을 갈아 끼우면 다시 띄우지 않아도 따라온다.

using System.Windows;
using System.Windows.Media;

namespace CvInspect.Controls;

public sealed partial class CvDispCtrl
{
    /// <summary>툴바 바탕 리소스 키. 호스트 리소스(앱 색 사전, 창, 이 컨트롤의 <c>Resources</c> 등 위쪽 어디든)에 이 키로
    /// <see cref="Brush"/> 를 두면 그 아래 모든 CvDispCtrl 의 툴바 바탕이 된다(DataTemplate 이 만든 것 포함). 없으면 #FAFAFA.
    /// 사전을 갈아 끼우면 따라온다. XAML 에서는 <c>x:Key="{x:Static cv:CvDispCtrl.ToolbarBackgroundKey}"</c> 로 적기를 권한다 —
    /// 오타가 그 사전을 읽는 순간 멤버 이름을 담은 XamlParseException 으로 드러난다(빌드는 잡지 않는다). 글자로 적은 키는 틀려도
    /// 아무 말 없이 기본색으로 남는다.</summary>
    public const string ToolbarBackgroundKey = "CvDispToolbarBackgroundBrush";

    /// <summary>하단 상태 줄(커서 좌표·픽셀 값·이미지 크기·배율) 바탕 리소스 키. 없으면 #FAFAFA.</summary>
    public const string StatusBarBackgroundKey = "CvDispStatusBarBackgroundBrush";

    /// <summary>상태 줄 글자 리소스 키. 없으면 #555555.</summary>
    public const string StatusBarForegroundKey = "CvDispStatusBarForegroundBrush";

    /// <summary>구분선 리소스 키 — 툴바 버튼 그룹 사이 구분선(도킹 방향에 따라 세로·가로), 툴바가 영상 영역과 맞닿는 가장자리선,
    /// 상태 줄 위 가장자리선 셋을 함께 칠한다. 없으면 #DDDDDD.</summary>
    public const string SeparatorBrushKey = "CvDispSeparatorBrush";

    public static readonly DependencyProperty ToolbarBackgroundProperty = RegisterChrome(nameof(ToolbarBackground));

    /// <summary>이 인스턴스의 툴바 바탕. 주면 <see cref="ToolbarBackgroundKey"/> 를 이긴다. null(기본)이면 키 → 없으면 #FAFAFA.
    /// 직접 값·바인딩·<c>DynamicResource</c>·스타일 세터 어느 것으로 줘도 된다. 직접 준 값을 걷으면(<c>ClearValue</c>) 다음 출처로
    /// 돌아간다 — 스타일 세터가 있으면 그것, 없으면 키. 이 컨트롤은 Control 이 아니라서, 창·UserControl 수준의 암시 스타일은
    /// DataTemplate 이 만든 CvDispCtrl 에 닿지 않는다(WPF 규칙 — 앱 사전의 암시 스타일은 닿는다). 템플릿 안에서는 키를 쓰거나
    /// 템플릿의 요소에 속성을 직접 준다. 나머지 셋(<see cref="StatusBarBackground"/>·<see cref="StatusBarForeground"/>·
    /// <see cref="SeparatorBrush"/>)도 같다.</summary>
    public Brush? ToolbarBackground
    {
        get => (Brush?)GetValue(ToolbarBackgroundProperty);
        set => SetValue(ToolbarBackgroundProperty, value);
    }

    public static readonly DependencyProperty StatusBarBackgroundProperty = RegisterChrome(nameof(StatusBarBackground));

    /// <summary>이 인스턴스의 상태 줄 바탕. 주면 <see cref="StatusBarBackgroundKey"/> 를 이긴다. null(기본)이면 키 → 없으면 #FAFAFA.</summary>
    public Brush? StatusBarBackground
    {
        get => (Brush?)GetValue(StatusBarBackgroundProperty);
        set => SetValue(StatusBarBackgroundProperty, value);
    }

    public static readonly DependencyProperty StatusBarForegroundProperty = RegisterChrome(nameof(StatusBarForeground));

    /// <summary>이 인스턴스의 상태 줄 글자색. 주면 <see cref="StatusBarForegroundKey"/> 를 이긴다. null(기본)이면 키 → 없으면 #555555.</summary>
    public Brush? StatusBarForeground
    {
        get => (Brush?)GetValue(StatusBarForegroundProperty);
        set => SetValue(StatusBarForegroundProperty, value);
    }

    public static readonly DependencyProperty SeparatorBrushProperty = RegisterChrome(nameof(SeparatorBrush));

    /// <summary>이 인스턴스의 구분선 색(그룹 세로선·툴바 가장자리선·상태 줄 가장자리선). 주면 <see cref="SeparatorBrushKey"/> 를 이긴다.
    /// null(기본)이면 키 → 없으면 #DDDDDD.</summary>
    public Brush? SeparatorBrush
    {
        get => (Brush?)GetValue(SeparatorBrushProperty);
        set => SetValue(SeparatorBrushProperty, value);
    }

    // 호스트 키 슬롯 — 생성자 끝에서 SetResourceReference 로 키에 묶는다. 키가 없으면 기본값(null)으로 남고, 호스트가 사전을 바꾸면
    // WPF 가 리소스 참조를 다시 풀어 값이 바뀐다 — 그 변경 콜백이 다시 칠한다. 공개 속성과 따로 두는 이유: 공개 속성에 참조를 걸면
    // 그것이 Local 값이라 호스트의 스타일 세터를 조용히 이기고, 호스트가 값을 줬다가 걷으면 참조까지 지워져 키로 돌아오지 않는다.
    // 형은 object 다 — 같은 이름에 Brush 가 아닌 값(Color 등)이 있어도 기본색으로 남는다. Brush 형 속성에 Brush 가 아닌 값을 가리키는
    // 참조를 걸면 WPF 가 값을 풀 때 InvalidOperationException("'#FF010203' is not a valid value for property …")을 던진다
    // (공개 속성에 참조를 거는 변이로 실측 — 렌더가 그 예외로 끝났다). Brush 를 가리키는 참조는 물론 정상이다.
    private static readonly DependencyProperty ToolbarBackgroundSlotProperty = RegisterSlot("ToolbarBackgroundSlot");
    private static readonly DependencyProperty StatusBarBackgroundSlotProperty = RegisterSlot("StatusBarBackgroundSlot");
    private static readonly DependencyProperty StatusBarForegroundSlotProperty = RegisterSlot("StatusBarForegroundSlot");
    private static readonly DependencyProperty SeparatorBrushSlotProperty = RegisterSlot("SeparatorBrushSlot");

    // 강조색 — 호스트 테마가 정의한 컨트롤 라이브러리 키(PrimaryBrush/SuccessBrush/DangerBrush)를 있으면 그대로 써서 화면 전체
    // 팔레트를 따르고, 없으면(테마 미병합·콘솔 하네스) 고정색으로 떨어진다. 이 컨트롤은 UI 라이브러리를 참조하지 않는다 — 키 이름만 안다.
    // 공개 속성은 없다(호스트가 이미 주는 키를 살아 있게 받는 것뿐이다).
    private static readonly DependencyProperty PrimarySlotProperty = RegisterSlot("PrimarySlot");
    private static readonly DependencyProperty SuccessSlotProperty = RegisterSlot("SuccessSlot");
    private static readonly DependencyProperty DangerSlotProperty = RegisterSlot("DangerSlot");

    private static readonly Brush DefaultBar = Frozen(0xFA, 0xFA, 0xFA);
    private static readonly Brush DefaultStatusFg = Frozen(0x55, 0x55, 0x55);
    private static readonly Brush DefaultSep = Frozen(0xDD, 0xDD, 0xDD);
    private static readonly Brush DefaultPrimary = Frozen(0x15, 0x65, 0xC0);
    private static readonly Brush DefaultSuccess = Frozen(0x2E, 0x7D, 0x32);
    private static readonly Brush DefaultDanger = Frozen(0xC6, 0x28, 0x28);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static DependencyProperty RegisterChrome(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(CvDispCtrl), new PropertyMetadata(null, (d, _) => ((CvDispCtrl)d).ApplyTheme()));

    private static DependencyProperty RegisterSlot(string name) => DependencyProperty.Register(
        name, typeof(object), typeof(CvDispCtrl), new PropertyMetadata(null, (d, e) => ((CvDispCtrl)d).OnSlotChanged(e)));

    /// <summary>슬롯을 키에 묶는다. 생성자 끝에서 한 번 — 묶는 순간 앱 리소스에 키가 있으면 콜백이 바로 돌므로 칠할 요소가 다 만들어진 뒤여야 한다.
    /// 부모 요소에 둔 키는 트리에 붙을 때 WPF 가 참조를 다시 풀어 그때 들어온다.</summary>
    private void BindThemeSlots()
    {
        SetResourceReference(ToolbarBackgroundSlotProperty, ToolbarBackgroundKey);
        SetResourceReference(StatusBarBackgroundSlotProperty, StatusBarBackgroundKey);
        SetResourceReference(StatusBarForegroundSlotProperty, StatusBarForegroundKey);
        SetResourceReference(SeparatorBrushSlotProperty, SeparatorBrushKey);
        SetResourceReference(PrimarySlotProperty, "PrimaryBrush");
        SetResourceReference(SuccessSlotProperty, "SuccessBrush");
        SetResourceReference(DangerSlotProperty, "DangerBrush");
    }

    private void OnSlotChanged(DependencyPropertyChangedEventArgs e)
    {
        // 우리 키에 Brush 가 아닌 값이 있으면 호스트가 이 키를 노리고 잘못 넣은 것이다(흔한 모양: Color 를 넣음) — 말없이 기본색으로
        // 남으면 "키가 안 먹는다" 로만 보이므로 알린다. 강조색 키는 호스트 라이브러리의 이름이라 다른 뜻으로 쓰는 정상 구성일 수 있어 조용히 넘긴다.
        if (e.NewValue is not null and not Brush && OwnKeyOf(e.Property) is { } key)
            CvLog.Publish(CvLogLevel.Warning, nameof(CvDispCtrl),
                $"Resource '{key}' is a {e.NewValue.GetType().Name}, not a Brush; ignored, the default color is used.");
        ApplyTheme();
    }

    private static string? OwnKeyOf(DependencyProperty slot)
        => slot == ToolbarBackgroundSlotProperty ? ToolbarBackgroundKey
         : slot == StatusBarBackgroundSlotProperty ? StatusBarBackgroundKey
         : slot == StatusBarForegroundSlotProperty ? StatusBarForegroundKey
         : slot == SeparatorBrushSlotProperty ? SeparatorBrushKey
         : null;

    /// <summary>현재 출처대로 다시 칠한다 — 속성·슬롯 어느 쪽이 바뀌어도 전부. 몇 개 요소에 브러시를 넣을 뿐이라 나눌 이유가 없다.</summary>
    private void ApplyTheme()
    {
        var sep = Pick(SeparatorBrushProperty, SeparatorBrushSlotProperty, DefaultSep);
        _toolbarBorder.Background = Pick(ToolbarBackgroundProperty, ToolbarBackgroundSlotProperty, DefaultBar);
        _toolbarBorder.BorderBrush = sep;
        foreach (var s in _toolSeps) s.Background = sep;
        _statusBorder.Background = Pick(StatusBarBackgroundProperty, StatusBarBackgroundSlotProperty, DefaultBar);
        _statusBorder.BorderBrush = sep;
        var fg = Pick(StatusBarForegroundProperty, StatusBarForegroundSlotProperty, DefaultStatusFg);
        _statusLeft.Foreground = fg;
        _statusRight.Foreground = fg;
        ApplyAccents();
    }

    private Brush Pick(DependencyProperty own, DependencyProperty slot, Brush fallback)
        => GetValue(own) as Brush ?? GetValue(slot) as Brush ?? fallback;

    /// <summary>⏯ 는 늘(정지 = Danger, 라이브 = Success), ✋ 는 눌렸을 때만 강조색을 넣는다 — 풀린 ✋ 는 버튼 스타일 색이다.</summary>
    private void ApplyAccents()
    {
        _playBtn.Background = _playBtn.IsChecked == true
            ? GetValue(SuccessSlotProperty) as Brush ?? DefaultSuccess
            : GetValue(DangerSlotProperty) as Brush ?? DefaultDanger;
        if (_panBtn.IsChecked == true)
            _panBtn.Background = GetValue(PrimarySlotProperty) as Brush ?? DefaultPrimary;
    }
}
