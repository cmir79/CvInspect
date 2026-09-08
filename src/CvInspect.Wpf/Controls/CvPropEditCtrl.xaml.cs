using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CvInspect.Controls;

/// <summary>편집 커밋 통지 — 어떤 프로퍼티가 커밋됐는지 (호스트 dirty 마킹 채널). CLR 프로퍼티명이다.</summary>
public sealed record CvPropCommittedEvt(string PropertyName);

/// <summary>실행 버튼 실행 직전 통지 — 호스트가 사전 준비(낡은 학습 체인 재구성 등)를 거는 자리.
/// PropertyName 은 CLR 프로퍼티명.</summary>
public sealed record CvPropActionExecutingEvt(string PropertyName);

/// <summary>실행 버튼 정상 완료 통지 — 파라미터를 바꾸는 액션(스팬 전환 등)의 편집 추적용.
/// 창만 여는 액션까지 dirty 로 오염시키지 않도록, 어느 편집기에 배선할지는 호스트가 정한다.</summary>
public sealed record CvPropActionExecutedEvt(string PropertyName);

/// <summary>실행 버튼 실패 통지 — 표시 방법은 호스트가 정한다. Label 은 행의 번역된 표시명이다
/// (사용자에게 보여줄 제목용 — 코드 분기 키로 쓰지 말 것).</summary>
public sealed record CvPropActionFailedEvt(string Label, Exception Error);

/// <summary>
/// typed POCO 를 리플렉션으로 펼쳐 편집하는 프로퍼티 편집기 — 카테고리 그룹 + 행별 편집기
/// (bool 체크박스 / 수치·문자 텍스트 / enum 콤보 / Action 실행 버튼), 설명은 행 아래 캡션 상시 표시.
/// 코어의 Cv*Opt 처럼 CvCategory/CvName/CvDesc(System.ComponentModel 파생)를 단 POCO 를 그대로 넣으면 된다.
/// 값 커밋은 행 VM 이 backing 에 SetValue 직행 후 <see cref="Committed"/> 로 통지 — 대상 POCO
/// 대부분이 INotifyPropertyChanged 미구현이라 이 이벤트가 호스트 dirty 마킹의 유일한 채널이다.
/// 텍스트 커밋 타이밍은 LostFocus + Enter — 입력 중 커밋으로 편집 처리가 끼어들지 않게.
/// 평범한 컨트롤 트리라 호스트 테마의 암시 스타일을 그대로 탄다. UI 라이브러리는 참조하지 않는다 —
/// 캡션·헤더 색은 호스트 테마의 키(SecondaryTextBrush / PrimaryBrush)가 있으면 따르고 없으면 고정색이다.
/// 소스 INotifyPropertyChanged 구독은 약한 구독이라 파기 시 별도 해제 훅이 없어도 새지 않는다.
/// </summary>
public partial class CvPropEditCtrl : UserControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(object), typeof(CvPropEditCtrl),
        new PropertyMetadata(null, (d, _) => ((CvPropEditCtrl)d).RebuildRows()));

    /// <summary>편집 대상 POCO — 교체 시 행 전면 재구성. null 이면 빈 화면.</summary>
    public object? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public static readonly DependencyProperty ExecuteTextProperty = DependencyProperty.Register(
        nameof(ExecuteText), typeof(string), typeof(CvPropEditCtrl),
        new PropertyMetadata(null, (d, _) => ((CvPropEditCtrl)d).RebuildRows()));

    /// <summary>Action 행 실행 버튼 라벨 — 미지정(null)이면 번역 키(cv:Execute)로 해석한다.</summary>
    public string? ExecuteText
    {
        get => (string?)GetValue(ExecuteTextProperty);
        set => SetValue(ExecuteTextProperty, value);
    }

    public static readonly DependencyProperty ShowDescProperty = DependencyProperty.Register(
        nameof(ShowDesc), typeof(bool), typeof(CvPropEditCtrl),
        new PropertyMetadata(true, (d, _) => ((CvPropEditCtrl)d).RebuildRows()));

    /// <summary>행 아래 설명 캡션 표시 여부 — 좁은 패널에서 목록을 짧게 가져가려면 끈다.</summary>
    public bool ShowDesc
    {
        get => (bool)GetValue(ShowDescProperty);
        set => SetValue(ShowDescProperty, value);
    }

    /// <summary>행 편집 커밋 — 값은 이미 backing POCO 에 반영된 뒤다.</summary>
    public event EventHandler<CvPropCommittedEvt>? Committed;

    /// <summary>Action 실행 직전 — 델리게이트 호출 전에 동기로 발화한다.</summary>
    public event EventHandler<CvPropActionExecutingEvt>? ActionExecuting;

    /// <summary>Action 정상 완료 — 예외 없이 끝난 실행 뒤에 발화한다.</summary>
    public event EventHandler<CvPropActionExecutedEvt>? ActionExecuted;

    /// <summary>Action 실행 실패 — 로그는 행 VM 이 남기고, 표시는 호스트가 정한다.</summary>
    public event EventHandler<CvPropActionFailedEvt>? ActionFailed;

    private IReadOnlyList<CvPropGroupVm> _groups = [];

    /// <summary>지금 펼쳐진 그룹들 — 호스트가 행 수를 세거나 검사할 때 쓴다(읽기 전용 스냅샷).</summary>
    public IReadOnlyList<CvPropGroupVm> Groups => _groups;

    public CvPropEditCtrl()
    {
        InitializeComponent();
        // 테마 키는 트리에 붙은 뒤에야 보인다 — 생성자 시점의 TryFindResource 는 호스트 리소스에 닿지 않는다.
        Loaded += (_, _) => AdoptThemeBrushes();
    }

    /// <summary>호스트 테마가 정의한 텍스트/강조 키를 있으면 채택 — 없으면 XAML 의 기본 브러시가 그대로 남는다.
    /// 이 컨트롤은 UI 라이브러리를 참조하지 않는다 — 키 이름만 안다.</summary>
    private void AdoptThemeBrushes()
    {
        if (TryFindResource("SecondaryTextBrush") is Brush desc) Resources["CvPropDescBrush"] = desc;
        if (TryFindResource("PrimaryBrush") is Brush header) Resources["CvPropHeaderBrush"] = header;
    }

    private void RebuildRows()
    {
        foreach (var g in _groups)
            foreach (var r in g.Rows)
                r.Detach();

        // plain object 는 "typed 설정 없음" sentinel — 빈 화면 (GetType() != typeof(object) 규약).
        var src = Source;
        _groups = src is null || src.GetType() == typeof(object)
            ? []
            : CvPropRowBldr.Build(src,
                name => Committed?.Invoke(this, new CvPropCommittedEvt(name)),
                name => ActionExecuting?.Invoke(this, new CvPropActionExecutingEvt(name)),
                name => ActionExecuted?.Invoke(this, new CvPropActionExecutedEvt(name)),
                (label, ex) => ActionFailed?.Invoke(this, new CvPropActionFailedEvt(label, ex)),
                ExecuteText, ShowDesc);
        GroupsHost.ItemsSource = _groups;
    }

    // 텍스트 편집 Enter = 즉시 커밋 — 기본 LostFocus 커밋에 더해, 편집 직후 저장/검사에 stale 값이 쓰이지 않게.
    private void OnEditTextKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox tb) return;
        tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        tb.SelectAll();
        e.Handled = true;
    }
}
