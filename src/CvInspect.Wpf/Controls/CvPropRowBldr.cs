using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;

namespace CvInspect.Controls;

/// <summary>
/// POCO 인스턴스 → 카테고리 그룹/행 VM 빌더. 화면 구성 입력은 런타임 타입의 public 프로퍼티와
/// System.ComponentModel 표준 attribute 5종뿐이다:
/// [Browsable(false)]=숨김, [ReadOnly(true)]=편집 불가, [DisplayName]=라벨, [Description]=설명,
/// [Category]=그룹. 그룹 순번은 카테고리 attribute 가 <see cref="ICvOrderedCategory"/> 면 그 Order 로,
/// 아니면 표시 문자열의 "N. " 접두를 파싱해(번호는 정렬에만 쓰고 헤더에는 남기지 않는다) 정한다.
/// 코어의 CvCategory/CvName/CvDesc 는 이 표준 attribute 의 파생이라 별도 취급이 없다 — 어떤 POCO 든 같은 규칙으로 펼친다.
/// 행 분기는 CLR 타입 기준: bool / int·long·double·float / enum / string / Action(실행 버튼) /
/// 읽기 전용 나열(IEnumerable) / 중첩 객체([TypeConverter(ExpandableObjectConverter)] 를 단 것만 —
/// 값의 런타임 타입으로 다시 펼친다).
/// getter-only 프로퍼티도 지원 타입이면 편집 불가 행으로 표시한다([ReadOnly(true)] 와 동일 렌더).
/// 그 밖의 타입(훅·바이너리 등)은 편집 표면 계약 밖이라 노출하지 않는다.
/// </summary>
public static class CvPropRowBldr
{
    /// <summary>프로퍼티별 attribute 인스턴스 캐시 — GetCustomAttribute 는 호출마다 새 인스턴스를 만드는데,
    /// 번역 attribute 는 해석한 문자열을 인스턴스에 캐시하므로 재빌드마다 새로 만들면 그 캐시가 헛돈다.
    /// 프로퍼티당 1회 생성으로 고정한다.</summary>
    private static readonly ConcurrentDictionary<PropertyInfo, PropMeta> MetaCache = new();

    private sealed record PropMeta(
        CategoryAttribute? Cat, DisplayNameAttribute? Name, DescriptionAttribute? Desc,
        bool Hidden, bool ReadOnly, int? CatOrder, bool Expandable);

    /// <summary>중첩 펼침 한도. 자기 자신을 값으로 들고 있는 객체를 만나면 여기서 멈춘다 —
    /// 순환을 못 끕으면 편집기를 여는 것만으로 스택이 넘친다.</summary>
    private const int MaxNestDepth = 3;

    /// <summary>행 하나를 세우는 데 필요한 호스트 훅 묶음 — 중첩 자식까지 그대로 물려준다.</summary>
    private sealed record Ctx(
        Action<string>? OnCommitted,
        Action<string>? OnActionExecuting,
        Action<string>? OnActionExecuted,
        Action<string, Exception>? OnActionFailed,
        string? ExecuteText,
        bool ShowDesc);

    /// <summary>카테고리 정렬 순번 — attribute 가 <see cref="ICvOrderedCategory"/> 를 구현하면 그 값. 아니면 null 로,
    /// 표시 문자열의 "N. " 접두 파싱으로 폴백한다.</summary>
    private static int? ReadCatOrder(CategoryAttribute? cat)
        => cat is ICvOrderedCategory ordered ? ordered.Order : null;

    public static IReadOnlyList<CvPropGroupVm> Build(
        object src, Action<string>? onCommitted,
        Action<string>? onActionExecuting, Action<string>? onActionExecuted, Action<string, Exception>? onActionFailed,
        string? executeText = null, bool showDesc = true)
    {
        var ctx = new Ctx(onCommitted, onActionExecuting, onActionExecuted, onActionFailed, executeText, showDesc);

        var rows = new List<(int Order, string Header, CvPropRowVm Row)>();
        foreach (var prop in WalkProps(src.GetType()))
        {
            var meta = ReadMeta(prop);
            if (meta.Hidden) continue;

            var row = Configure(prop, meta, src, ctx, 0);
            if (row is null) continue;

            // 순번은 ① attribute 의 Order(접두 없는 표시명 그대로 헤더) → ② "N. " 접두 파싱 폴백.
            var (order, header) = meta.CatOrder is { } catOrder
                ? (catOrder, meta.Cat?.Category ?? string.Empty)
                : ParseCategory(meta.Cat?.Category);
            rows.Add((order, header, row));
        }

        // 그룹은 순번 오름차순(무번호는 뒤), 그룹 안 행은 선언 순서 그대로.
        return rows.GroupBy(r => (r.Order, r.Header))
            .OrderBy(g => g.Key.Order).ThenBy(g => g.Key.Header, StringComparer.Ordinal)
            .Select(g => new CvPropGroupVm(g.Key.Header, g.Select(r => r.Row).ToList()))
            .ToList();
    }

    /// <summary>중첩 객체의 자식 행 — 카테고리로 묶지 않고 선언 순서 그대로 낸다.
    /// 부모 행이 이미 한 그룹 안의 한 줄이라, 그 안에서 다시 그룹을 나누면 머리글이 두 겹이 된다.</summary>
    private static IReadOnlyList<CvPropRowVm> BuildChildren(object src, Ctx ctx, int depth)
    {
        var rows = new List<CvPropRowVm>();
        foreach (var prop in WalkProps(src.GetType()))
        {
            var meta = ReadMeta(prop);
            if (meta.Hidden) continue;

            var row = Configure(prop, meta, src, ctx, depth);
            if (row is not null) rows.Add(row);
        }
        return rows;
    }

    private static PropMeta ReadMeta(PropertyInfo prop) => MetaCache.GetOrAdd(prop, p =>
    {
        var cat = p.GetCustomAttribute<CategoryAttribute>();
        return new PropMeta(
            cat,
            p.GetCustomAttribute<DisplayNameAttribute>(),
            p.GetCustomAttribute<DescriptionAttribute>(),
            p.GetCustomAttribute<BrowsableAttribute>() is { Browsable: false },
            p.GetCustomAttribute<ReadOnlyAttribute>() is { IsReadOnly: true },
            ReadCatOrder(cat),
            IsExpandable(p.GetCustomAttribute<TypeConverterAttribute>()));
    });

    /// <summary>행 하나를 만들고 배선한다 — 만들 수 없는 타입이면 null(그 프로퍼티는 화면에 나오지 않는다).</summary>
    private static CvPropRowVm? Configure(PropertyInfo prop, PropMeta meta, object src, Ctx ctx, int depth)
    {
        var row = MakeRow(prop, meta, ctx.ExecuteText, depth);
        if (row is null) return null;

        var desc = meta.Desc?.Description ?? string.Empty;
        row.Prop = prop;
        row.Source = src;
        row.OnCommitted = ctx.OnCommitted;
        row.Label = meta.Name?.DisplayName ?? prop.Name;
        row.Desc = desc;
        row.InlineDesc = ctx.ShowDesc ? desc : string.Empty;
        // Action 은 getter-only 가 정상 관용구(실행 버튼) — CanWrite 기준 잠금에서 제외한다.
        // 나열과 중첩도 마찬가지다: 값 자체를 대입하는 행이 아니라 보여 주거나 펼치는 행이라
        // setter 유무로 잠그면 멀쩡한 자식 편집까지 같이 잠긴다.
        row.IsReadOnly = meta.ReadOnly
            || (!prop.CanWrite && row is not (CvPropActionRowVm or CvPropListRowVm or CvPropNestedRowVm));
        if (row is CvPropActionRowVm act)
        {
            act.OnExecuting = ctx.OnActionExecuting;
            act.OnExecuted = ctx.OnActionExecuted;
            act.OnActionFailed = ctx.OnActionFailed;
        }
        if (row is CvPropNestedRowVm nested)
            nested.ChildFactory = value => BuildChildren(value, ctx, depth + 1);

        row.Attach();
        row.RefreshFromSource();
        return row;
    }

    /// <summary>base → 파생 순, 타입 안은 선언 순(MetadataToken) — 리플렉션 GetProperties 는 순서 무보장이라 명시한다.
    /// override/new 로 다시 선언된 프로퍼티는 파생 선언이 이기되 자리(선언 순서)는 처음 나온 곳을 지킨다.</summary>
    private static IEnumerable<PropertyInfo> WalkProps(Type type)
    {
        var chain = new List<Type>();
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType) chain.Add(t);
        chain.Reverse();

        var order = new List<string>();
        var byName = new Dictionary<string, PropertyInfo>();
        foreach (var t in chain)
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                         .OrderBy(p => p.MetadataToken))
            {
                if (!byName.ContainsKey(p.Name)) order.Add(p.Name);
                byName[p.Name] = p;
            }
        return order.Select(n => byName[n]);
    }

    private static CvPropRowVm? MakeRow(PropertyInfo prop, PropMeta meta, string? executeText, int depth)
    {
        var t = prop.PropertyType;
        // 실행 버튼 문구 — 지정이 없으면 라이브러리 번역 키. 호스트가 키를 모르면 원문("Execute")으로 폴백된다.
        if (t == typeof(Action)) return new CvPropActionRowVm { ButtonText = executeText ?? CvLoc.T("cv:Execute") };

        if (t == typeof(bool)) return new CvPropBoolRowVm();
        if (t.IsEnum) return new CvPropEnumRowVm { Values = Enum.GetValues(t).Cast<object>().ToList() };
        if (t == typeof(int) || t == typeof(long) || t == typeof(double) || t == typeof(float))
            return new CvPropNumRowVm { NumType = t };
        if (t == typeof(string)) return new CvPropStrRowVm();

        // 중첩은 펼쳐 달라고 명시한 것만 펼친다 — 아무 참조 타입이나 펼치면 도구 객체 그래프를
        // 통째로 걸어 들어간다. 한도를 넘으면 그 자리는 아예 안 그린다(잘린 절반을 남기지 않는다).
        if (meta.Expandable) return depth < MaxNestDepth ? new CvPropNestedRowVm() : null;

        if (typeof(IEnumerable).IsAssignableFrom(t)) return new CvPropListRowVm();

        return null;
    }

    /// <summary>[TypeConverter] 가 <see cref="ExpandableObjectConverter"/> 인가.
    /// attribute 는 타입이 아니라 <b>이름 문자열</b>을 들고 있고, 그 문자열이 어셈블리 한정명일 수도
    /// 짧은 이름일 수도 있다 — 어셈블리 이름은 런타임 판에 따라 달라지므로 앞의 타입 이름만 본다.</summary>
    private static bool IsExpandable(TypeConverterAttribute? tc)
    {
        var name = tc?.ConverterTypeName;
        if (string.IsNullOrEmpty(name)) return false;
        var comma = name!.IndexOf(',');
        var typeName = (comma > 0 ? name.Substring(0, comma) : name).Trim();
        return typeName == typeof(ExpandableObjectConverter).FullName;
    }

    /// <summary>"3. Search" → (3, "Search"). 접두 없으면 무번호(뒤로 정렬), 카테고리 자체가 없으면 헤더 없는 그룹.</summary>
    private static (int Order, string Header) ParseCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return (int.MaxValue, string.Empty);
        var m = Regex.Match(category, @"^(\d+)\.\s*(.*)$");
        return m.Success ? (int.Parse(m.Groups[1].Value), m.Groups[2].Value) : (int.MaxValue, category);
    }
}
