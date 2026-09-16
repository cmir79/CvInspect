// 프로퍼티 편집기 행 VM 계층 — typed POCO 를 리플렉션으로 펼쳐 카테고리 그룹/행으로 만든다(빌드는 CvPropRowBldr).
// 편집은 backing 인스턴스의 PropertyInfo.SetValue 직행 — 커밋마다 OnCommitted(프로퍼티명) 통지가 호스트 dirty
// 마킹의 유일한 채널이다(대상 POCO 대부분이 INotifyPropertyChanged 미구현).
// 이 계층은 MVVM 라이브러리를 참조하지 않는다 — 통지와 커맨드는 이 파일 안에서 자족한다.

using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace CvInspect.Controls;

/// <summary>카테고리 한 그룹 — 헤더와 소속 행들 (정렬은 빌더가 끝내고 넘긴다).</summary>
public sealed class CvPropGroupVm(string header, IReadOnlyList<CvPropRowVm> rows)
{
    public string Header { get; } = header;
    public bool HasHeader => Header.Length > 0;
    public IReadOnlyList<CvPropRowVm> Rows { get; } = rows;
}

/// <summary>행 공통 — 라벨/설명/읽기전용 + backing 프로퍼티 쓰기와 커밋 통지.
/// 소스가 INotifyPropertyChanged 를 구현하면 코드 경로(학습 버튼 등)의 값 변경을 표시에 동기화한다.</summary>
public abstract class CvPropRowVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    internal PropertyInfo Prop { get; set; } = null!;
    internal object Source { get; set; } = null!;
    internal Action<string>? OnCommitted { get; set; }

    public string Label { get; internal set; } = string.Empty;

    /// <summary>설명 원문 — 표시 여부와 무관하게 항상 담는다 (툴팁이 이 값을 쓴다).</summary>
    public string Desc { get; internal set; } = string.Empty;

    /// <summary>행 아래 상시 캡션에 보일 설명 — ShowDesc 가 꺼지면 비어 있다.</summary>
    public string InlineDesc { get; internal set; } = string.Empty;

    /// <summary>라벨 툴팁 원문 — 라벨이 첫 줄, 설명이 있으면 그 아래. 라벨은 좁은 판에서 말줄임으로 잘리는 쪽이라
    /// 사용자가 툴팁을 띄우는 이유의 절반은 라벨 전문이고(설명은 캡션으로 이미 보이는 경우가 많다), 캡션을 끈 호스트에서는
    /// 설명의 유일한 통로다. XAML 은 두 줄을 따로 꾸미고, 이 문자열은 텍스트로 쓰는 호스트 몫이다.</summary>
    public string Tip => Desc.Length > 0 ? Label + "\n" + Desc : Label;

    public bool IsReadOnly { get; internal set; }
    public bool IsEditable => !IsReadOnly;

    private INotifyPropertyChanged? _inpc;
    private bool _detached;

    // 약한 구독(PropertyChangedEventManager) — 화면 이탈로 Detach 가 안 돌아도 오래 사는 소스
    // (검사 인스턴스 소유 POCO)가 죽은 행/컨트롤을 붙잡지 못하게 한다.
    internal void Attach()
    {
        if (Source is INotifyPropertyChanged inpc)
        {
            _inpc = inpc;
            PropertyChangedEventManager.AddHandler(inpc, OnSourceChanged, string.Empty);
        }
    }

    internal virtual void Detach()
    {
        _detached = true;
        OnCommitted = null;
        if (_inpc is null) return;
        PropertyChangedEventManager.RemoveHandler(_inpc, OnSourceChanged, string.Empty);
        _inpc = null;
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName) && e.PropertyName != Prop.Name) return;
        // 통지 경로에서 새면 소스가 PropertyChanged 를 낸 자리에서 터진다 — 편집기가 호스트 코드를 깨뜨리는 꼴이다.
        // 여기서는 행을 뺄 수 없으니 마지막으로 읽은 값을 그대로 남긴다.
        TryRefreshFromSource();
    }

    /// <summary>소스 현재 값 → 표시 필드 직접 대입(setter 미경유 — 커밋을 되쏘지 않는다) + 통지.</summary>
    internal abstract void RefreshFromSource();

    /// <summary>
    /// 읽기를 감싼다 — <b>프로퍼티 getter 는 남의 코드다.</b> 장치를 그때 읽는 지연 평가, 다른 스레드가 채우는
    /// 컬렉션(열거 중 수정), 아직 배선되지 않은 참조 — 무엇이든 던질 수 있다. 새면 편집기를 세우는 대입
    /// (<c>Source = opt</c>)이나 소스가 <c>PropertyChanged</c> 를 낸 자리에서 터지고, WPF 라 대개 잡는 사람이 없어
    /// 앱이 그대로 종료된다. 잡되 삼키지 않는다 — 어느 프로퍼티가 무엇으로 실패했는지 경고로 남긴다
    /// (실행 버튼 행과 같은 규율).
    /// </summary>
    internal bool TryRefreshFromSource()
    {
        try
        {
            RefreshFromSource();
            return true;
        }
        catch (Exception ex)
        {
            CvLog.Publish(CvLogLevel.Warning, nameof(CvPropRowVm), $"Property '{Prop.Name}' read failed: {ex.GetType().Name}", ex);
            return false;
        }
    }

    /// <summary>backing 에 쓰고 커밋 통지 — 사용자 편집 setter 전용 경로.
    /// 떼어진 행(재구성 후 잔존 시각 트리)의 늦은 커밋은 폐기된 POCO 오염이라 하드 no-op.</summary>
    protected void WriteAndCommit(object? value)
    {
        if (_detached) return;
        Prop.SetValue(Source, value);
        OnCommitted?.Invoke(Prop.Name);
    }
}

public sealed class CvPropBoolRowVm : CvPropRowVm
{
    private bool _value;
    public bool Value
    {
        get => _value;
        set { if (SetProperty(ref _value, value)) WriteAndCommit(value); }
    }

    internal override void RefreshFromSource()
    {
        _value = Prop.GetValue(Source) is true;
        OnPropertyChanged(nameof(Value));
    }
}

/// <summary>정수/실수 모두 텍스트로 편집. 커밋 후에는 성공·실패 모두 backing 기준 표기로 재렌더한다 —
/// 실패는 마지막 유효값으로 되돌리고(잘못된 텍스트 잔류 = 안 바뀐 걸 바뀐 줄 아는 오해),
/// 성공은 정규 표기("1." → "1")로 맞춘다. "화면에 보이는 값 = 저장될 값" 불변식.
/// double 을 컨트롤에 직결하면 소수점 입력이 깨지는 함정의 우회이기도 하다.</summary>
public sealed class CvPropNumRowVm : CvPropRowVm
{
    internal Type NumType { get; set; } = typeof(int);

    private string _text = string.Empty;
    public string Text
    {
        get => _text;
        set
        {
            if (!SetProperty(ref _text, value ?? string.Empty)) return;
            if (TryParse(_text, NumType, out var parsed)) WriteAndCommit(parsed);
            RefreshFromSource();
        }
    }

    internal override void RefreshFromSource()
    {
        _text = Convert.ToString(Prop.GetValue(Source), CultureInfo.InvariantCulture) ?? string.Empty;
        OnPropertyChanged(nameof(Text));
    }

    private static bool TryParse(string s, Type t, out object value)
    {
        value = 0;
        if (t == typeof(int)) { if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) { value = v; return true; } return false; }
        if (t == typeof(long)) { if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) { value = v; return true; } return false; }
        if (t == typeof(double)) { if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) { value = v; return true; } return false; }
        if (t == typeof(float)) { if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) { value = v; return true; } return false; }
        return false;
    }
}

public sealed class CvPropStrRowVm : CvPropRowVm
{
    private string _value = string.Empty;
    public string Value
    {
        get => _value;
        set { if (SetProperty(ref _value, value ?? string.Empty)) WriteAndCommit(_value); }
    }

    internal override void RefreshFromSource()
    {
        _value = Prop.GetValue(Source) as string ?? string.Empty;
        OnPropertyChanged(nameof(Value));
    }
}

/// <summary>enum — 콤보 표기는 식별자 원문(ToString) 그대로. 설명문들이 그 원문을 인용해 해설하는 체계라,
/// 표기를 바꾸면 설명과 화면이 어긋난다.</summary>
public sealed class CvPropEnumRowVm : CvPropRowVm
{
    public IReadOnlyList<object> Values { get; internal init; } = [];

    private object? _value;
    public object? Value
    {
        get => _value;
        set { if (SetProperty(ref _value, value) && value is not null) WriteAndCommit(value); }
    }

    internal override void RefreshFromSource()
    {
        _value = Prop.GetValue(Source);
        OnPropertyChanged(nameof(Value));
    }
}

/// <summary>Action 실행 버튼 — 값을 고르는 게 아니라 한 번 눌러 실행하는 파라미터(학습·창 열기 등).</summary>
public sealed class CvPropActionRowVm : CvPropRowVm
{
    internal Action<string>? OnExecuting { get; set; }
    internal Action<string>? OnExecuted { get; set; }
    internal Action<string, Exception>? OnActionFailed { get; set; }

    public string ButtonText { get; internal init; } = string.Empty;

    public ICommand ExecuteCommand { get; }

    public CvPropActionRowVm() => ExecuteCommand = new ExecuteCmd(this);

    internal override void Detach()
    {
        OnExecuting = null;
        OnExecuted = null;
        OnActionFailed = null;
        base.Detach();
    }

    private void Execute()
    {
        try
        {
            // 실행 직전 훅(사전 준비 — 낡은 학습 체인 재구성 등)도 try 안이다: 호스트 훅이 검사
            // 파이프라인을 돌리므로 델리게이트 본체만큼 던질 수 있고, 새면 앱이 그대로 종료된다.
            OnExecuting?.Invoke(Prop.Name);

            // getter 가 호출마다 새 델리게이트를 만드는 관용구라 캐시하지 않고 실행 시점에 읽는다.
            (Prop.GetValue(Source) as Action)?.Invoke();

            // 실행 후 통지 — 파라미터를 바꾸는 액션(스팬 전환 등)의 편집 추적은 호스트가 이 훅을
            // 어느 편집기에 배선할지로 정한다(창만 여는 액션까지 dirty 로 오염시키지 않게).
            OnExecuted?.Invoke(Prop.Name);
        }
        catch (Exception ex)
        {
            // 여기서 잡지 않으면 dispatcher 까지 올라가 앱이 그대로 종료된다. 잡되 삼키지는 않는다 —
            // 아무도 구독하지 않아도 흔적은 남긴다: 눌렀는데 아무 일도 안 일어나는 것이 가장 나쁘다.
            CvLog.Publish(CvLogLevel.Warning, nameof(CvPropActionRowVm), $"Action '{Prop.Name}' failed: {ex.GetType().Name}", ex);
            OnActionFailed?.Invoke(Label, ex);
        }
    }

    internal override void RefreshFromSource() { }

    /// <summary>항상 실행 가능한 매개변수 없는 커맨드 — 버튼 하나를 위해 MVVM 라이브러리를 끌어오지 않는다.</summary>
    private sealed class ExecuteCmd(CvPropActionRowVm owner) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => owner.Execute();
    }
}

/// <summary>
/// 읽기 전용 나열 — 항목을 줄바꿈으로 이어 붙여 그대로 보여 준다(문자열 배열 등).
/// 편집하지 않는다: 이 자리에 오는 것은 도구가 지금 들고 있는 목록(등록된 연산자 등)이라
/// 화면에서 고칠 값이 아니라 확인할 값이다. 목록이 비면 <c>(empty)</c> 로 적어 "빈 목록"과
/// "아직 못 읽음"을 가른다.
/// </summary>
public sealed class CvPropListRowVm : CvPropRowVm
{
    private string _text = string.Empty;
    public string Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    /// <summary>한 번에 적는 항목 수 상한. 이 행은 확인용이라 전부 적을 값어치가 없고, 상한이 없으면
    /// <c>[Browsable(false)]</c> 가 빠진 큰 배열(학습 템플릿 바이트 등)이 여기 닿는 순간 항목마다 한 줄을 만들어
    /// <b>편집기를 여는 것만으로 화면이 굳는다.</b> 넘치면 남은 개수만 뒤에 덧붙인다 — 숫자라 번역이 필요 없다.</summary>
    private const int MaxItems = 20;

    internal override void RefreshFromSource() => Text = Format(Prop.GetValue(Source));

    private static string Format(object? value)
    {
        if (value is not IEnumerable items || value is string) return value?.ToString() ?? string.Empty;

        var parts = new List<string>();
        var truncated = false;
        foreach (var item in items)
        {
            // 상한에서 열거를 멈춘다 — 끝까지 세면 지연 열거(무한일 수도 있다)에서 그대로 갇힌다.
            if (parts.Count == MaxItems) { truncated = true; break; }
            parts.Add(item?.ToString() ?? string.Empty);
        }

        if (parts.Count == 0) return "(empty)";
        // 남은 개수는 세지 않고 아는 경우(ICollection)에만 적는다.
        if (truncated) parts.Add(items is ICollection col ? $"… (+{col.Count - MaxItems})" : "…");
        return string.Join(Environment.NewLine, parts);
    }
}

/// <summary>
/// 중첩 객체 — 값이 들고 있는 인스턴스를 다시 펼쳐 자식 행으로 그린다.
/// <see cref="System.ComponentModel.TypeConverterAttribute"/> 가
/// <see cref="System.ComponentModel.ExpandableObjectConverter"/> 인 프로퍼티만 이 행이 된다.
/// 선언 타입이 <c>object</c> 이고 실제 타입이 private 중첩 클래스인 관용구가 이 자리의 주 용도라,
/// <b>선언 타입이 아니라 값의 런타임 타입으로 펼친다.</b>
///
/// 값이 <b>다른 인스턴스로 바뀌면 자식을 통째로 다시 세운다</b> — 모드 전환으로 어댑터가 교체되는
/// 자리라(세그먼트 방식·연산자 선택 등) 자식의 구성 자체가 달라진다. 값만 갱신하면 옛 타입의
/// 행이 새 인스턴스를 붙잡고 남아, 화면은 그대로인데 쓰기가 엉뚱한 곳으로 간다.
/// 그래서 소스는 교체 시점에 그 프로퍼티로 변경 통지를 내야 한다.
/// </summary>
public sealed class CvPropNestedRowVm : CvPropRowVm
{
    private object? _current;
    private IReadOnlyList<CvPropRowVm> _children = [];

    /// <summary>자식 행을 세우는 방법 — 빌더가 주입한다(재귀 진입점). 순환 참조를 막기 위해 깊이는 빌더가 센다.</summary>
    internal Func<object, IReadOnlyList<CvPropRowVm>>? ChildFactory { get; set; }

    public IReadOnlyList<CvPropRowVm> Children
    {
        get => _children;
        private set => SetProperty(ref _children, value);
    }

    /// <summary>펼칠 것이 없을 때(값이 null) 자리를 비워 두지 않도록 — 헤더만 남는 빈 블록을 감춘다.</summary>
    public bool HasChildren => _children.Count > 0;

    internal override void Detach()
    {
        foreach (var c in _children) c.Detach();
        ChildFactory = null;
        base.Detach();
    }

    internal override void RefreshFromSource()
    {
        var value = Prop.GetValue(Source);
        if (ReferenceEquals(value, _current))
        {
            // 같은 인스턴스면 구성은 그대로다 — 자식들이 각자 자기 값만 다시 읽는다.
            // 한 자식이 던져도 나머지는 갱신한다(감싼 호출) — 여기서 새면 소스의 통지 지점에서 터진다.
            foreach (var c in _children) c.TryRefreshFromSource();
            return;
        }

        foreach (var c in _children) c.Detach();
        _current = value;
        Children = value is null || ChildFactory is null ? [] : ChildFactory(value);
        OnPropertyChanged(nameof(HasChildren));
    }
}
