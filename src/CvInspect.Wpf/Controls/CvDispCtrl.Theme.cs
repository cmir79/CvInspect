// CvDispCtrl 의 색 표면 — 툴바·상태 줄·영상 영역 바탕·무이미지 안내 글자 색과 ⏯·✋ 강조색을 어디서 가져오는지.
// 먼저 있는 것이 이긴다: 이 인스턴스에 준 속성 값(직접 값·바인딩·스타일 세터) → 호스트 리소스 키(색 사전 한 곳에서 모든
// 인스턴스) → 고정 기본색(영상 영역 바탕만은 고정색이 아니라 컨트롤의 Background 로 떨어진다). 키는 리소스 참조로 붙잡아 두므로 호스트가 실행 중 사전을 갈아 끼우면 다시 띄우지 않아도 따라온다.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using CvInspect.Display;

namespace CvInspect.Controls;

public sealed partial class CvDispCtrl
{
    /// <summary>툴바 바탕 리소스 키. 호스트 리소스(앱 색 사전, 창, 이 컨트롤의 <c>Resources</c> 등 위쪽 어디든)에 이 키로
    /// <see cref="Brush"/> 를 두면 그 아래 모든 CvDispCtrl 의 툴바 바탕이 된다(DataTemplate 이 만든 것 포함). 없으면 #FAFAFA.
    /// 사전을 갈아 끼우면 따라온다. XAML 에서는 <c>x:Key="{x:Static cv:CvDispCtrl.ToolbarBackgroundKey}"</c> 로 적기를 권한다 —
    /// 오타가 그 사전을 읽는 순간 XamlParseException 으로 드러난다(멤버 이름은 그 안쪽 예외에 있다 — 빌드는 잡지 않는다). 글자로 적은 키는 틀려도
    /// 아무 말 없이 기본색으로 남는다.</summary>
    public const string ToolbarBackgroundKey = CvDispTheme.ToolbarBackgroundKey;

    /// <summary>하단 상태 줄(커서 좌표·픽셀 값·이미지 크기·배율) 바탕 리소스 키. 없으면 #FAFAFA.</summary>
    public const string StatusBarBackgroundKey = CvDispTheme.StatusBarBackgroundKey;

    /// <summary>상태 줄 글자 리소스 키. 없으면 #555555.</summary>
    public const string StatusBarForegroundKey = CvDispTheme.StatusBarForegroundKey;

    /// <summary>구분선 리소스 키 — 툴바 버튼 그룹 사이 구분선(도킹 방향에 따라 세로·가로), 툴바가 영상 영역과 맞닿는 가장자리선,
    /// 상태 줄 위 가장자리선 셋을 함께 칠한다. 없으면 #DDDDDD.</summary>
    public const string SeparatorBrushKey = CvDispTheme.SeparatorBrushKey;

    /// <summary>영상 영역 바탕 리소스 키 — 이미지 바깥 여백(맞춤 여백·축소 시 주변)과 무이미지 화면을 칠한다. 이미지는 그 위에 그린다
    /// (불투명 픽셀은 가리고, Bgra32 프레임의 반투명 픽셀은 이 색과 섞인다). 이 브러시 밑에는 늘 컨트롤 Background(#2A2A2A)가 있어
    /// 반투명 브러시는 호스트가 아니라 그 색과 섞인다. ⚠ 미재현 위험(코드 독해): 배치 반올림이 꺼진 호스트에서 비정수 DPI(125%·150%)면
    /// 툴바·상태 줄과 맞닿는 한 픽셀 줄에 밑의 어두운 Background 가 섞여 밝은 바탕 위에 옅은 선으로 보일 수 있다.
    /// 없으면 컨트롤의 <c>Background</c>(기본 #2A2A2A)가 그대로 보인다 — 밝은 테마 사전에만 두면 다크 쪽은 종전 모습이다.
    /// 무이미지 안내 글자는 #9E9E9E 고정이라 아주 밝은 바탕에서는 옅다. 결과 오버레이의 이름 붙은 색도 어두운 바탕 기준으로 고른 것이라,
    /// 바탕을 밝게 하면 호스트가 오버레이 색을 거기에 맞춰 고른다.</summary>
    public const string ViewBackgroundKey = CvDispTheme.ViewBackgroundKey;

    /// <summary>무이미지 안내 글자(제목·불러오기 힌트) 색 리소스 키. 없으면 #9E9E9E — 어두운 바탕에서도 밝은 바탕에서도 읽히는 중간 회색이지만
    /// 밝은 바탕(#E6E6E6)에서는 대비가 약 2.3:1 로 낮다. <see cref="ViewBackgroundKey"/> 를 밝게 할 때 함께 둔다.
    /// <b>단색 브러시(SolidColorBrush)만 쓴다</b> — 글자는 코어 그리기 목록의 색(ARGB)으로 그려서다. 브러시의 Opacity 는 알파에 곱한다.
    /// 다른 종류(그라데이션 등)는 기본색으로 남고 경고 한 줄을 남긴다.</summary>
    public const string PlaceholderForegroundKey = CvDispTheme.PlaceholderForegroundKey;

    /// <summary>툴바 아이콘 색 리소스 키 — 강조색을 쓰지 않는 버튼(📸·⛶·➕·➖·🧹, 풀린 ✋)의 아이콘 글자를 칠한다.
    /// 없으면 이 컨트롤이 물려받는 글자색(<c>TextElement.Foreground</c> — 창이나 테마가 준 본문 글자색)이다. 버튼 스타일의 글자색을 그대로 두지
    /// 않는 이유: 버튼 글자를 브랜드 색으로 칠하는 테마에서는 아이콘이 전부 강조색이 되어 툴바가 지나치게 튀었다(소비자 실제 창, Avalonia 판).
    /// Avalonia 판과 같은 규칙이라 호스트를 옮겨도 같은 키 한 줄로 따라온다. 비활성 버튼은 버튼 템플릿의 비활성 모양 그대로다.
    /// ⏯ 와 눌린 ✋ 는 강조 바탕 위 흰색이라 이 키와 무관하다. 툴바 바탕(<see cref="ToolbarBackgroundKey"/>, 기본 #FAFAFA)은 테마를 따라가지
    /// 않는다 — 어두운 테마의 밝은 본문 글자색은 기본 바탕 위에서 보이지 않으니, 어두운 테마에서는 바탕 키를 함께 두거나 이 키로 어두운 색을 준다.</summary>
    public const string ToolbarForegroundKey = CvDispTheme.ToolbarForegroundKey;

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

    public static readonly DependencyProperty ViewBackgroundProperty = RegisterChrome(nameof(ViewBackground));

    /// <summary>이 인스턴스의 영상 영역 바탕. 주면 <see cref="ViewBackgroundKey"/> 를 이긴다. null(기본)이면 키 → 없으면 컨트롤의
    /// <c>Background</c>(기본 #2A2A2A — 호스트가 컨트롤에 직접 준 Background 도 여기서 산다). 출처 규칙은 <see cref="ToolbarBackground"/> 와 같다.</summary>
    public Brush? ViewBackground
    {
        get => (Brush?)GetValue(ViewBackgroundProperty);
        set => SetValue(ViewBackgroundProperty, value);
    }

    public static readonly DependencyProperty PlaceholderForegroundProperty = RegisterChrome(nameof(PlaceholderForeground));

    /// <summary>이 인스턴스의 무이미지 안내 글자 색. 주면 <see cref="PlaceholderForegroundKey"/> 를 이긴다. null(기본)이면 키 → 없으면 #9E9E9E.
    /// 단색 브러시만 — 이유와 출처 규칙은 <see cref="PlaceholderForegroundKey"/>·<see cref="ToolbarBackground"/>.</summary>
    public Brush? PlaceholderForeground
    {
        get => (Brush?)GetValue(PlaceholderForegroundProperty);
        set => SetValue(PlaceholderForegroundProperty, value);
    }

    public static readonly DependencyProperty ToolbarForegroundProperty = RegisterChrome(nameof(ToolbarForeground));

    /// <summary>이 인스턴스의 툴바 아이콘 색. 주면 <see cref="ToolbarForegroundKey"/> 를 이긴다. null(기본)이면 키 → 없으면 물려받는 글자색.
    /// 출처 규칙은 <see cref="ToolbarBackground"/> 와 같다.</summary>
    public Brush? ToolbarForeground
    {
        get => (Brush?)GetValue(ToolbarForegroundProperty);
        set => SetValue(ToolbarForegroundProperty, value);
    }

    // 호스트 키 슬롯 — 생성자에서 요소를 다 만든 뒤(행·자식을 붙이기 전) SetResourceReference 로 키에 묶는다. 키가 없으면 기본값(null)으로 남고, 호스트가 사전을 바꾸면
    // WPF 가 리소스 참조를 다시 풀어 값이 바뀐다 — 그 변경 콜백이 다시 칠한다. 공개 속성과 따로 두는 이유: 공개 속성에 참조를 걸면
    // 그것이 Local 값이라 호스트의 스타일 세터를 조용히 이기고, 호스트가 값을 줬다가 걷으면 참조까지 지워져 키로 돌아오지 않는다.
    // 형은 object 다 — 같은 이름에 Brush 가 아닌 값(Color 등)이 있어도 기본색으로 남는다. Brush 형 속성에 Brush 가 아닌 값을 가리키는
    // 참조를 걸면 WPF 가 값을 풀 때 InvalidOperationException("'#FF010203' is not a valid value for property …")을 던진다
    // (공개 속성에 참조를 거는 변이로 실측 — 렌더가 그 예외로 끝났다). Brush 를 가리키는 참조는 물론 정상이다.
    private static readonly DependencyProperty ToolbarBackgroundSlotProperty = RegisterSlot("ToolbarBackgroundSlot");
    private static readonly DependencyProperty StatusBarBackgroundSlotProperty = RegisterSlot("StatusBarBackgroundSlot");
    private static readonly DependencyProperty StatusBarForegroundSlotProperty = RegisterSlot("StatusBarForegroundSlot");
    private static readonly DependencyProperty SeparatorBrushSlotProperty = RegisterSlot("SeparatorBrushSlot");
    private static readonly DependencyProperty ViewBackgroundSlotProperty = RegisterSlot("ViewBackgroundSlot");
    private static readonly DependencyProperty PlaceholderForegroundSlotProperty = RegisterSlot("PlaceholderForegroundSlot");
    private static readonly DependencyProperty ToolbarForegroundSlotProperty = RegisterSlot("ToolbarForegroundSlot");

    // 강조색 — 호스트 테마가 정의한 컨트롤 라이브러리 키(PrimaryBrush/SuccessBrush/DangerBrush)를 있으면 그대로 써서 화면 전체
    // 팔레트를 따르고, 없으면(테마 미병합·콘솔 하네스) 고정색으로 떨어진다. 이 컨트롤은 UI 라이브러리를 참조하지 않는다 — 키 이름만 안다.
    // 공개 속성은 없다(호스트가 이미 주는 키를 살아 있게 받는 것뿐이다).
    private static readonly DependencyProperty PrimarySlotProperty = RegisterSlot("PrimarySlot");
    private static readonly DependencyProperty SuccessSlotProperty = RegisterSlot("SuccessSlot");
    private static readonly DependencyProperty DangerSlotProperty = RegisterSlot("DangerSlot");

    // 고정 기본색의 값은 코어 CvDispTheme 이 정한다 — 여기는 그 색을 얼린 브러시로 만들 뿐이다.
    private static readonly Brush DefaultBar = Frozen(CvDispTheme.DefaultBar);
    private static readonly Brush DefaultStatusFg = Frozen(CvDispTheme.DefaultStatusFg);
    private static readonly Brush DefaultSep = Frozen(CvDispTheme.DefaultSep);
    private static readonly Brush DefaultPrimary = Frozen(CvDispTheme.DefaultPrimary);
    private static readonly Brush DefaultSuccess = Frozen(CvDispTheme.DefaultSuccess);
    private static readonly Brush DefaultDanger = Frozen(CvDispTheme.DefaultDanger);

    private static Color ColorOf(ViPaint p) => Color.FromArgb(p.A, p.R, p.G, p.B);

    private static Brush Frozen(ViPaint p)
    {
        var brush = new SolidColorBrush(ColorOf(p));
        brush.Freeze();
        return brush;
    }

    private static DependencyProperty RegisterChrome(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(CvDispCtrl), new PropertyMetadata(null, (d, _) => ((CvDispCtrl)d).ApplyTheme()));

    private static DependencyProperty RegisterSlot(string name) => DependencyProperty.Register(
        name, typeof(object), typeof(CvDispCtrl), new PropertyMetadata(null, (d, e) => ((CvDispCtrl)d).OnSlotChanged(e)));

    /// <summary>슬롯을 키에 묶는다. 생성자에서 한 번, 요소를 다 만든 뒤 — 묶는 순간 앱 리소스에 키가 있으면 콜백이 바로 돌므로 칠할 요소가 다 만들어진 뒤여야 한다.
    /// 부모 요소에 둔 키는 트리에 붙을 때 WPF 가 참조를 다시 풀어 그때 들어온다.</summary>
    private void BindThemeSlots()
    {
        SetResourceReference(ToolbarBackgroundSlotProperty, ToolbarBackgroundKey);
        SetResourceReference(StatusBarBackgroundSlotProperty, StatusBarBackgroundKey);
        SetResourceReference(StatusBarForegroundSlotProperty, StatusBarForegroundKey);
        SetResourceReference(SeparatorBrushSlotProperty, SeparatorBrushKey);
        SetResourceReference(ViewBackgroundSlotProperty, ViewBackgroundKey);
        SetResourceReference(PlaceholderForegroundSlotProperty, PlaceholderForegroundKey);
        SetResourceReference(ToolbarForegroundSlotProperty, ToolbarForegroundKey);
        SetResourceReference(PrimarySlotProperty, CvDispTheme.PrimaryHostKey);
        SetResourceReference(SuccessSlotProperty, CvDispTheme.SuccessHostKey);
        SetResourceReference(DangerSlotProperty, CvDispTheme.DangerHostKey);
    }

    private void OnSlotChanged(DependencyPropertyChangedEventArgs e)
    {
        // 우리 키에 Brush 가 아닌 값이 있으면 경고한다(강조색 키는 조용히 넘긴다) — 근거와 문구는 코어 CvDispTheme.NotBrushWarning.
        if (e.NewValue is not null and not Brush && OwnKeyOf(e.Property) is { } key)
            CvLog.Publish(CvLogLevel.Warning, CvDispLog.Source, CvDispTheme.NotBrushWarning(key, e.NewValue.GetType().Name));
        ApplyTheme();
    }

    private static string? OwnKeyOf(DependencyProperty slot)
        => slot == ToolbarBackgroundSlotProperty ? ToolbarBackgroundKey
         : slot == StatusBarBackgroundSlotProperty ? StatusBarBackgroundKey
         : slot == StatusBarForegroundSlotProperty ? StatusBarForegroundKey
         : slot == SeparatorBrushSlotProperty ? SeparatorBrushKey
         : slot == ViewBackgroundSlotProperty ? ViewBackgroundKey
         : slot == PlaceholderForegroundSlotProperty ? PlaceholderForegroundKey
         : slot == ToolbarForegroundSlotProperty ? ToolbarForegroundKey
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
        // 영상 영역은 기본색으로 떨어지지 않는다 — 둘 다 없으면 null 을 넘겨 컨트롤의 Background 가 비치게 한다(그것이 종전 기본이다).
        _surface.ViewBackground = CvDispTheme.Pick<Brush>(GetValue(ViewBackgroundProperty), GetValue(ViewBackgroundSlotProperty));
        _surface.PlaceholderPaint = HintPaintOf(CvDispTheme.Pick<Brush>(GetValue(PlaceholderForegroundProperty), GetValue(PlaceholderForegroundSlotProperty)));
        ApplyToolbarForeground();
        ApplyAccents();
    }

    /// <summary>물려받는 글자색이 바뀌면(창·테마가 본문 글자색을 바꿈) 아이콘 기본색이 그것이라 아이콘만 다시 칠한다.
    /// 생성자 도중(버튼 목록을 만들기 전)에 오는 통지는 건너뛴다 — 생성자 끝의 ApplyTheme 가 칠한다.</summary>
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == TextElement.ForegroundProperty && _plainToolBtns is not null) ApplyToolbarForeground();
    }

    /// <summary>강조색을 쓰지 않는 버튼의 아이콘 색 — 속성 → 키 → 물려받는 글자색. 버튼 Foreground 에 지역 값으로 넣어 버튼 스타일의 글자색 세터를
    /// 이긴다. 물려받는 글자색은 늘 값이 있다(아무도 안 주면 시스템 글자색) — 창 글자색을 칠하지 않고 컨트롤 스타일에만 글자색을 두는 어두운 테마에서는
    /// 아이콘이 시스템 글자색(검정)이 되니, 그런 호스트는 이 키나 창 Foreground 를 준다.
    /// <b>비활성 버튼에는 넣지 않는다</b> — 지역 값은 스타일의 IsEnabled 트리거(버튼 자신의 Foreground 를 흐리게 바꾸는 테마도 있다)까지 이겨
    /// 꺼진 📸 가 살아 보인다. 활성이 바뀔 때마다 다시 부른다(IsEnabledChanged). 눌린 ✋ 는 흰색(강조)이 이미 들어가 있어 건너뛴다 — 풀릴 때 여기로 돌아온다.</summary>
    private void ApplyToolbarForeground()
    {
        var fg = CvDispTheme.Pick<Brush>(GetValue(ToolbarForegroundProperty), GetValue(ToolbarForegroundSlotProperty))
                 ?? GetValue(TextElement.ForegroundProperty) as Brush;
        foreach (var b in _plainToolBtns) SetIconForeground(b, fg);
        if (_panBtn.IsChecked != true) SetIconForeground(_panBtn, fg);
    }

    private static void SetIconForeground(Control b, Brush? fg)
    {
        if (fg is null || !b.IsEnabled) b.ClearValue(Control.ForegroundProperty);
        else b.Foreground = fg;
    }

    private Brush? _warnedHintBrush;

    /// <summary>안내 글자 브러시 → 그리기 목록 색. 단색만 옮길 수 있다(글자는 코어 목록의 ARGB 로 그린다). null 이면 코어 기본.
    /// 다른 종류는 기본으로 남기고 같은 브러시에 대해 한 번만 경고한다 — 이 메서드는 색 출처가 바뀔 때마다 불린다.</summary>
    private ViPaint? HintPaintOf(Brush? brush)
    {
        if (brush is null) return null;
        if (brush is SolidColorBrush s)
        {
            var c = s.Color;
            return CvDispTheme.HintPaint(c.A, c.R, c.G, c.B, s.Opacity);
        }
        if (!ReferenceEquals(_warnedHintBrush, brush))
        {
            _warnedHintBrush = brush;
            CvLog.Publish(CvLogLevel.Warning, CvDispLog.Source, CvDispTheme.HintBrushWarning(brush.GetType().Name));
        }
        return null;
    }

    /// <summary>출처 순서(속성 → 키 → 기본색)는 코어 CvDispTheme.Pick.</summary>
    private Brush Pick(DependencyProperty own, DependencyProperty slot, Brush fallback)
        => CvDispTheme.Pick<Brush>(GetValue(own), GetValue(slot)) ?? fallback;

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
