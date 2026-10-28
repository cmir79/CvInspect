using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CvInspect.Display;

namespace CvInspect.Controls;

// 통지 인자(CvProp…Evt)·행 VM·빌더는 코어 CvInspect 에 있다(UI 무관) — 이 컨트롤은 그것을 WPF 로 그리기만 한다.

/// <summary>
/// typed POCO 를 리플렉션으로 펼쳐 편집하는 프로퍼티 편집기 — 카테고리 그룹 + 행별 편집기
/// (bool 체크박스 / 수치·문자 텍스트 / enum 콤보 / Action 실행 버튼 / 읽기 전용 나열 /
/// 중첩 객체 펼침), 설명은 행 아래 캡션 상시 표시.
/// 코어의 Cv*Opt 처럼 CvCategory/CvName/CvDesc(System.ComponentModel 파생)를 단 POCO 를 그대로 넣으면 된다.
/// 값 커밋은 행 VM 이 backing 에 SetValue 직행 후 <see cref="Committed"/> 로 통지 — 이 이벤트가 호스트 dirty 마킹의 채널이다.
/// 코어 Opt 의 INotifyPropertyChanged 는 일부 값(그립이 쓰는 기하, 학습·스팬 전환 같은 실행 버튼이 쓰는 값)만 통지하고 호스트 POCO 는
/// 구현하지 않을 수 있어 소스의 통지로 dirty 를 대신할 수 없다. 소스 통지는 표시 갱신에만 쓴다 — 통지된 행은 소스를 다시 읽는다(커밋은 아니다).
/// 다시 읽으면 그 행에 입력 중이던(아직 커밋 안 한) 글자는 소스 값으로 바뀐다 — 입력 중에 같은 값의 그립을 끌면 드래그가 이긴다.
/// 텍스트 커밋 타이밍은 LostFocus + Enter — 입력 중 커밋으로 편집 처리가 끼어들지 않게.
/// 평범한 컨트롤 트리라 호스트 테마의 암시 스타일을 그대로 탄다. UI 라이브러리는 참조하지 않는다 —
/// 캡션·헤더 색은 호스트 테마의 키(SecondaryTextBrush / PrimaryBrush)가 있으면 따르고(실행 중 사전 교체도) 없으면 고정색이다.
/// 소스 INotifyPropertyChanged 구독은 약한 구독이라 파기 시 별도 해제 훅이 없어도 행·컨트롤을 붙잡지 않는다
/// (소스에 남는 작은 다리는 소스의 다음 통지 때 스스로 풀린다 — CvPropWeakSub).
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

    // 호스트 테마 키 슬롯 — 생성자에서 리소스 참조로 키에 묶는다. 트리에 붙을 때와 호스트가 사전을 갈아 끼울 때 WPF 가 참조를
    // 다시 풀고, 변경 콜백이 XAML 쪽 키(CvPropDescBrush / CvPropHeaderBrush)를 그 브러시로 바꿔 끼운다. 키가 없거나 Brush 가 아니면
    // XAML 의 기본 브러시로 돌아간다. (한 번 복사해 두면 실행 중 테마를 바꿔도 처음 색으로 굳는다.)
    // 이 컨트롤은 UI 라이브러리를 참조하지 않는다 — 키 이름만 안다. 형이 object 인 이유: 같은 이름의 다른 형 값으로 깨지지 않게.
    private static readonly DependencyProperty HostDescSlotProperty = DependencyProperty.Register(
        "HostDescSlot", typeof(object), typeof(CvPropEditCtrl),
        new PropertyMetadata(null, (d, e) => { var c = (CvPropEditCtrl)d; c.Resources["CvPropDescBrush"] = e.NewValue as Brush ?? c._defaultDesc; }));

    private static readonly DependencyProperty HostHeaderSlotProperty = DependencyProperty.Register(
        "HostHeaderSlot", typeof(object), typeof(CvPropEditCtrl),
        new PropertyMetadata(null, (d, e) => { var c = (CvPropEditCtrl)d; c.Resources["CvPropHeaderBrush"] = e.NewValue as Brush ?? c._defaultHeader; }));

    private readonly Brush _defaultDesc;
    private readonly Brush _defaultHeader;

    public CvPropEditCtrl()
    {
        InitializeComponent();
        _defaultDesc = (Brush)Resources["CvPropDescBrush"];
        _defaultHeader = (Brush)Resources["CvPropHeaderBrush"];
        SetResourceReference(HostDescSlotProperty, CvPropEditSvc.HostDescKey);
        SetResourceReference(HostHeaderSlotProperty, CvPropEditSvc.HostHeaderKey);
    }

    /// <summary>옛 행 떼기 → 센티널(null·plain object 는 빈 화면) → 짓기 순서는 코어 CvPropEditSvc.Rebuild. 여기는 이벤트를 이 컨트롤을
    /// sender 로 올리는 대리자만 넘긴다.</summary>
    private void RebuildRows()
    {
        _groups = CvPropEditSvc.Rebuild(_groups, Source,
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
