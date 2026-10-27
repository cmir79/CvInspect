// CvDispCtrl 의 색 표면 — 툴바·상태 줄·영상 영역 바탕·무이미지 안내 글자 색과 ⏯·✋ 강조색을 어디서 가져오는지.
// 먼저 있는 것이 이긴다: 이 인스턴스에 준 속성 값(직접 값·바인딩·스타일 세터) → 호스트 리소스 키(색 사전 한 곳에서 모든
// 인스턴스) → 고정 기본색(영상 영역 바탕만은 고정색이 아니라 컨트롤의 Background 로 떨어진다). 키는 리소스 관찰로 붙잡아 두므로
// 호스트가 실행 중 사전을 갈아 끼우거나 테마 변형(밝음·어둠)을 바꾸면 다시 띄우지 않아도 따라온다. 키 이름·기본색·출처 순서·경고 문구는 코어 CvDispTheme.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CvInspect.Display;

namespace CvInspect.Avalonia.Controls;

public sealed partial class CvDispCtrl
{
    /// <summary>툴바 바탕 리소스 키. 호스트 리소스(앱 <c>Resources</c>·테마 사전·창·이 컨트롤의 <c>Resources</c> 등 위쪽 어디든)에 이 키로
    /// <see cref="IBrush"/> 를 두면 그 아래 모든 CvDispCtrl 의 툴바 바탕이 된다. 없으면 #FAFAFA. 사전을 갈아 끼우면 따라온다.
    /// 테마 변형별 사전(<c>ThemeDictionaries</c>)에 두면 변형을 바꿀 때도 따라온다. XAML 에서는
    /// <c>x:Key="{x:Static cv:CvDispCtrl.ToolbarBackgroundKey}"</c> 로 적기를 권한다 — 글자로 적은 키는 틀려도 아무 말 없이 기본색으로 남는다.</summary>
    public const string ToolbarBackgroundKey = CvDispTheme.ToolbarBackgroundKey;

    /// <summary>하단 상태 줄(커서 좌표·픽셀 값·이미지 크기·배율) 바탕 리소스 키. 없으면 #FAFAFA.</summary>
    public const string StatusBarBackgroundKey = CvDispTheme.StatusBarBackgroundKey;

    /// <summary>상태 줄 글자 리소스 키. 없으면 #555555.</summary>
    public const string StatusBarForegroundKey = CvDispTheme.StatusBarForegroundKey;

    /// <summary>구분선 리소스 키 — 툴바 버튼 그룹 사이 구분선(도킹 방향에 따라 세로·가로), 툴바가 영상 영역과 맞닿는 가장자리선,
    /// 상태 줄 위 가장자리선 셋을 함께 칠한다. 없으면 #DDDDDD.</summary>
    public const string SeparatorBrushKey = CvDispTheme.SeparatorBrushKey;

    /// <summary>영상 영역 바탕 리소스 키 — 이미지 바깥 여백(맞춤 여백·축소 시 주변)과 무이미지 화면을 칠한다. 이미지는 그 위에 그린다
    /// (불투명 픽셀은 가리고, 4채널 프레임의 반투명 픽셀은 이 색과 섞인다). 이 브러시 밑에는 늘 컨트롤 Background(#2A2A2A)가 있어
    /// 반투명 브러시는 호스트가 아니라 그 색과 섞인다.
    /// 없으면 컨트롤의 <c>Background</c>(기본 #2A2A2A)가 그대로 보인다 — 밝은 테마 사전에만 두면 다크 쪽은 종전 모습이다.
    /// 결과 오버레이의 이름 붙은 색은 어두운 바탕 기준으로 고른 것이라, 바탕을 밝게 하면 호스트가 오버레이 색을 거기에 맞춰 고른다.</summary>
    public const string ViewBackgroundKey = CvDispTheme.ViewBackgroundKey;

    /// <summary>무이미지 안내 글자(제목·불러오기 힌트) 색 리소스 키. 없으면 #9E9E9E — 어두운 바탕에서도 밝은 바탕에서도 읽히는 중간 회색이지만
    /// 밝은 바탕(#E6E6E6)에서는 대비가 약 2.3:1 로 낮다. <see cref="ViewBackgroundKey"/> 를 밝게 할 때 함께 둔다.
    /// <b>단색 브러시(<see cref="ISolidColorBrush"/>)만 쓴다</b> — 글자는 코어 그리기 목록의 색(ARGB)으로 그려서다. 브러시의 Opacity 는 알파에 곱한다.
    /// 다른 종류(그라데이션 등)는 기본색으로 남고 경고 한 줄을 남긴다.</summary>
    public const string PlaceholderForegroundKey = CvDispTheme.PlaceholderForegroundKey;

    public static readonly StyledProperty<IBrush?> ToolbarBackgroundProperty = AvaloniaProperty.Register<CvDispCtrl, IBrush?>(nameof(ToolbarBackground));

    /// <summary>이 인스턴스의 툴바 바탕. 주면 <see cref="ToolbarBackgroundKey"/> 를 이긴다. null(기본)이면 키 → 없으면 #FAFAFA.
    /// 직접 값·바인딩·<c>DynamicResource</c>·스타일 세터 어느 것으로 줘도 된다. 직접 준 값을 걷으면(<c>ClearValue</c>) 다음 출처로
    /// 돌아간다 — 스타일 세터가 있으면 그것, 없으면 키. 나머지(<see cref="StatusBarBackground"/>·<see cref="StatusBarForeground"/>·
    /// <see cref="SeparatorBrush"/>·<see cref="ViewBackground"/>·<see cref="PlaceholderForeground"/>)도 같다.</summary>
    public IBrush? ToolbarBackground
    {
        get => GetValue(ToolbarBackgroundProperty);
        set => SetValue(ToolbarBackgroundProperty, value);
    }

    public static readonly StyledProperty<IBrush?> StatusBarBackgroundProperty = AvaloniaProperty.Register<CvDispCtrl, IBrush?>(nameof(StatusBarBackground));

    /// <summary>이 인스턴스의 상태 줄 바탕. 주면 <see cref="StatusBarBackgroundKey"/> 를 이긴다. null(기본)이면 키 → 없으면 #FAFAFA.</summary>
    public IBrush? StatusBarBackground
    {
        get => GetValue(StatusBarBackgroundProperty);
        set => SetValue(StatusBarBackgroundProperty, value);
    }

    public static readonly StyledProperty<IBrush?> StatusBarForegroundProperty = AvaloniaProperty.Register<CvDispCtrl, IBrush?>(nameof(StatusBarForeground));

    /// <summary>이 인스턴스의 상태 줄 글자색. 주면 <see cref="StatusBarForegroundKey"/> 를 이긴다. null(기본)이면 키 → 없으면 #555555.</summary>
    public IBrush? StatusBarForeground
    {
        get => GetValue(StatusBarForegroundProperty);
        set => SetValue(StatusBarForegroundProperty, value);
    }

    public static readonly StyledProperty<IBrush?> SeparatorBrushProperty = AvaloniaProperty.Register<CvDispCtrl, IBrush?>(nameof(SeparatorBrush));

    /// <summary>이 인스턴스의 구분선 색(그룹 구분선·툴바 가장자리선·상태 줄 가장자리선). 주면 <see cref="SeparatorBrushKey"/> 를 이긴다.
    /// null(기본)이면 키 → 없으면 #DDDDDD.</summary>
    public IBrush? SeparatorBrush
    {
        get => GetValue(SeparatorBrushProperty);
        set => SetValue(SeparatorBrushProperty, value);
    }

    public static readonly StyledProperty<IBrush?> ViewBackgroundProperty = AvaloniaProperty.Register<CvDispCtrl, IBrush?>(nameof(ViewBackground));

    /// <summary>이 인스턴스의 영상 영역 바탕. 주면 <see cref="ViewBackgroundKey"/> 를 이긴다. null(기본)이면 키 → 없으면 컨트롤의
    /// <c>Background</c>(기본 #2A2A2A — 호스트가 컨트롤에 준 Background 도 여기서 산다). 출처 규칙은 <see cref="ToolbarBackground"/> 와 같다.</summary>
    public IBrush? ViewBackground
    {
        get => GetValue(ViewBackgroundProperty);
        set => SetValue(ViewBackgroundProperty, value);
    }

    public static readonly StyledProperty<IBrush?> PlaceholderForegroundProperty = AvaloniaProperty.Register<CvDispCtrl, IBrush?>(nameof(PlaceholderForeground));

    /// <summary>이 인스턴스의 무이미지 안내 글자 색. 주면 <see cref="PlaceholderForegroundKey"/> 를 이긴다. null(기본)이면 키 → 없으면 #9E9E9E.
    /// 단색 브러시만 — 이유와 출처 규칙은 <see cref="PlaceholderForegroundKey"/>·<see cref="ToolbarBackground"/>.</summary>
    public IBrush? PlaceholderForeground
    {
        get => GetValue(PlaceholderForegroundProperty);
        set => SetValue(PlaceholderForegroundProperty, value);
    }

    // 호스트 키 슬롯 — 생성자에서 요소를 다 만든 뒤 리소스 관찰에 묶는다. 관찰은 묶는 순간 지금 값을 내고, 리소스가 바뀌거나 테마 변형이
    // 바뀔 때마다 다시 낸다. 키가 없으면 "값 없음" 을 내 슬롯이 기본값(null)으로 돌아간다 — 그 변경 콜백이 다시 칠한다.
    // 공개 속성과 따로 두는 이유: 공개 속성에 관찰을 묶으면 그것이 지역 값이라 호스트의 스타일 세터를 조용히 이기고, 호스트가 값을 줬다가
    // 걷으면 관찰까지 지워져 키로 돌아오지 않는다. 형은 object 다 — 같은 이름에 브러시가 아닌 값(Color 등)이 있어도 형 오류 없이 받아서
    // 기본색으로 남기고 경고한다.
    private static readonly StyledProperty<object?> ToolbarBackgroundSlotProperty = AvaloniaProperty.Register<CvDispCtrl, object?>("ToolbarBackgroundSlot");
    private static readonly StyledProperty<object?> StatusBarBackgroundSlotProperty = AvaloniaProperty.Register<CvDispCtrl, object?>("StatusBarBackgroundSlot");
    private static readonly StyledProperty<object?> StatusBarForegroundSlotProperty = AvaloniaProperty.Register<CvDispCtrl, object?>("StatusBarForegroundSlot");
    private static readonly StyledProperty<object?> SeparatorBrushSlotProperty = AvaloniaProperty.Register<CvDispCtrl, object?>("SeparatorBrushSlot");
    private static readonly StyledProperty<object?> ViewBackgroundSlotProperty = AvaloniaProperty.Register<CvDispCtrl, object?>("ViewBackgroundSlot");
    private static readonly StyledProperty<object?> PlaceholderForegroundSlotProperty = AvaloniaProperty.Register<CvDispCtrl, object?>("PlaceholderForegroundSlot");

    // 강조색 — 호스트 테마가 정의한 컨트롤 라이브러리 키(PrimaryBrush/SuccessBrush/DangerBrush)를 있으면 그대로 써서 화면 전체
    // 팔레트를 따르고, 없으면 고정색으로 떨어진다. 이 컨트롤은 UI 라이브러리를 참조하지 않는다 — 키 이름만 안다.
    // 다른 라이브러리 키로 자동 대체하지 않는다(공개 동작이 늘어난다) — 그 이름을 정의하지 않는 테마(기본 Fluent·Simple 등)에서는 고정색이다.
    private static readonly StyledProperty<object?> PrimarySlotProperty = AvaloniaProperty.Register<CvDispCtrl, object?>("PrimarySlot");
    private static readonly StyledProperty<object?> SuccessSlotProperty = AvaloniaProperty.Register<CvDispCtrl, object?>("SuccessSlot");
    private static readonly StyledProperty<object?> DangerSlotProperty = AvaloniaProperty.Register<CvDispCtrl, object?>("DangerSlot");

    // 고정 기본색의 값은 코어 CvDispTheme 이 정한다 — 여기는 그 색을 불변 브러시로 만들 뿐이다.
    private static readonly IBrush DefaultBar = BrushOf(CvDispTheme.DefaultBar);
    private static readonly IBrush DefaultStatusFg = BrushOf(CvDispTheme.DefaultStatusFg);
    private static readonly IBrush DefaultSep = BrushOf(CvDispTheme.DefaultSep);
    private static readonly IBrush DefaultPrimary = BrushOf(CvDispTheme.DefaultPrimary);
    private static readonly IBrush DefaultSuccess = BrushOf(CvDispTheme.DefaultSuccess);
    private static readonly IBrush DefaultDanger = BrushOf(CvDispTheme.DefaultDanger);

    private static IImmutableSolidColorBrush BrushOf(ViPaint p) => new ImmutableSolidColorBrush(Color.FromArgb(p.A, p.R, p.G, p.B));

    /// <summary>슬롯을 키에 묶는다. 생성자에서 한 번, 요소를 다 만든 뒤 — 묶는 순간 지금 값이 들어와 콜백이 돌므로 칠할 요소가 다 만들어진 뒤여야 한다.
    /// 부모 쪽에 둔 키는 트리에 붙을 때 관찰이 다시 내서 그때 들어온다. 묶음은 컨트롤과 수명을 같이 한다(풀지 않는다).</summary>
    private void BindThemeSlots()
    {
        Bind(ToolbarBackgroundSlotProperty, this.GetResourceObservable(ToolbarBackgroundKey));
        Bind(StatusBarBackgroundSlotProperty, this.GetResourceObservable(StatusBarBackgroundKey));
        Bind(StatusBarForegroundSlotProperty, this.GetResourceObservable(StatusBarForegroundKey));
        Bind(SeparatorBrushSlotProperty, this.GetResourceObservable(SeparatorBrushKey));
        Bind(ViewBackgroundSlotProperty, this.GetResourceObservable(ViewBackgroundKey));
        Bind(PlaceholderForegroundSlotProperty, this.GetResourceObservable(PlaceholderForegroundKey));
        Bind(PrimarySlotProperty, this.GetResourceObservable(CvDispTheme.PrimaryHostKey));
        Bind(SuccessSlotProperty, this.GetResourceObservable(CvDispTheme.SuccessHostKey));
        Bind(DangerSlotProperty, this.GetResourceObservable(CvDispTheme.DangerHostKey));
    }

    /// <summary>OnPropertyChanged 의 나머지 — 색 속성·슬롯이면 다시 칠한다(그 밖의 속성은 무시).</summary>
    private void OnThemePropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        var p = change.Property;
        if (p == ToolbarBackgroundProperty || p == StatusBarBackgroundProperty || p == StatusBarForegroundProperty
            || p == SeparatorBrushProperty || p == ViewBackgroundProperty || p == PlaceholderForegroundProperty)
        {
            ApplyTheme();
            return;
        }
        if (p != PrimarySlotProperty && p != SuccessSlotProperty && p != DangerSlotProperty && OwnKeyOf(p) is null) return;
        // 우리 키에 브러시가 아닌 값이 있으면 경고한다(강조색 키는 조용히 넘긴다) — 근거와 문구는 코어 CvDispTheme.NotBrushWarning.
        var v = change.NewValue;
        if (v is not null and not IBrush && v != AvaloniaProperty.UnsetValue && OwnKeyOf(p) is { } key)
            CvLog.Publish(CvLogLevel.Warning, CvDispLog.Source, CvDispTheme.NotBrushWarning(key, v.GetType().Name));
        ApplyTheme();
    }

    private static string? OwnKeyOf(AvaloniaProperty slot)
        => slot == ToolbarBackgroundSlotProperty ? ToolbarBackgroundKey
         : slot == StatusBarBackgroundSlotProperty ? StatusBarBackgroundKey
         : slot == StatusBarForegroundSlotProperty ? StatusBarForegroundKey
         : slot == SeparatorBrushSlotProperty ? SeparatorBrushKey
         : slot == ViewBackgroundSlotProperty ? ViewBackgroundKey
         : slot == PlaceholderForegroundSlotProperty ? PlaceholderForegroundKey
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
        // 영상 영역은 기본색으로 떨어지지 않는다 — 둘 다 없으면 null 을 넘겨 컨트롤의 Background 가 비치게 한다(그것이 기본이다).
        _surface.ViewBackground = CvDispTheme.Pick<IBrush>(GetValue(ViewBackgroundProperty), GetValue(ViewBackgroundSlotProperty));
        _surface.PlaceholderPaint = HintPaintOf(CvDispTheme.Pick<IBrush>(GetValue(PlaceholderForegroundProperty), GetValue(PlaceholderForegroundSlotProperty)));
        ApplyAccents();
    }

    private IBrush? _warnedHintBrush;

    /// <summary>안내 글자 브러시 → 그리기 목록 색. 단색만 옮길 수 있다(글자는 코어 목록의 ARGB 로 그린다). null 이면 코어 기본.
    /// 다른 종류는 기본으로 남기고 같은 브러시에 대해 한 번만 경고한다 — 이 메서드는 색 출처가 바뀔 때마다 불린다.</summary>
    private ViPaint? HintPaintOf(IBrush? brush)
    {
        if (brush is null) return null;
        if (brush is ISolidColorBrush s)
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
    private IBrush Pick(StyledProperty<IBrush?> own, StyledProperty<object?> slot, IBrush fallback)
        => CvDispTheme.Pick<IBrush>(GetValue(own), GetValue(slot)) ?? fallback;

    /// <summary>⏯ 는 늘(정지 = Danger, 라이브 = Success), ✋ 는 눌렸을 때만 강조색을 넣는다 — 풀린 ✋ 는 버튼 스타일 색이다.
    /// 버튼의 Background 에 넣는다 — 테마가 눌림·포인터 올림 상태에서 템플릿 안쪽 바탕을 직접 칠해도 PinAccent 가 단 스타일이
    /// 안쪽 바탕을 이 값에 묶어 둔다(CvDispCtrl.cs).</summary>
    private void ApplyAccents()
    {
        _playBtn.Background = _playBtn.IsChecked == true
            ? GetValue(SuccessSlotProperty) as IBrush ?? DefaultSuccess
            : GetValue(DangerSlotProperty) as IBrush ?? DefaultDanger;
        if (_panBtn.IsChecked == true)
            _panBtn.Background = GetValue(PrimarySlotProperty) as IBrush ?? DefaultPrimary;
    }
}
