using System.Reflection;
using CvInspect.Vision.Overlay;
using Xunit;

namespace CvInspect.Tests;

/// <summary>
/// 오버레이 항목의 복사 생성자 — 소비자가 "한 화면에서만 글자를 키운 라벨" 같은 사본을 만들 공개 길이 없어 속성을 손으로 옮겼다.
/// 그렇게 만든 사본은 이 형에 속성이 늘 때 그것을 조용히 빠뜨린다(예외도 경고도 없이 그 속성만 기본값으로 그려진다).
/// 그래서 단언도 속성을 손으로 나열하지 않고 <b>반사로 전부</b> 훑는다 — 속성이 늘면 이 시험이 그 속성까지 본다.
/// 새 속성의 형에 값을 정해 줄 수 없으면 시험이 그 형을 대라고 실패한다(조용히 건너뛰지 않는다). 형 목록도 어셈블리에서 찾는다 —
/// 항목 형이 늘면 그 형도 본다.
/// </summary>
public class ViOverlayCopyTests
{
    /// <summary>지금 값과 다른 값 — 복사가 기본값을 기본값으로 옮겨 "같다" 로 통과하지 않게 한다.</summary>
    private static object Different(Type t, object? current, string where)
    {
        if (t == typeof(string)) return (string?)current + "-copied";
        if (t == typeof(double)) return (double)current! + 7.25;
        if (t == typeof(float)) return (float)current! + 3.5f;
        if (t == typeof(bool)) return !(bool)current!;
        if (t.IsEnum) return Enum.GetValues(t).Cast<object>().First(v => !v.Equals(current));
        if (t == typeof(IReadOnlyList<ViOverlayColor?>)) return new ViOverlayColor?[] { ViOverlayColor.Red, null };
        if (t == typeof(IReadOnlyList<(double X, double Y)>)) return new (double X, double Y)[] { (1, 2), (3, 4) };
        if (t == typeof(ViHudSummary)) return new ViHudSummary(false, "t", new[] { ("l", (ViOverlayColor?)null) }, ViHudPos.BottomRight);
        throw new Xunit.Sdk.XunitException(
            $"{where} has type {t.Name}, which this test cannot give a distinct value — add one here so the new property is checked too");
    }

    /// <summary>라이브러리의 구체 항목 형 전부.</summary>
    private static List<Type> ItemTypeList()
        => typeof(ViOverlayItem).Assembly.GetTypes().Where(t => t.IsPublic && !t.IsAbstract && typeof(ViOverlayItem).IsAssignableFrom(t)).ToList();

    public static TheoryData<Type> ItemTypes()
    {
        var data = new TheoryData<Type>();
        foreach (var t in ItemTypeList()) data.Add(t);
        return data;
    }

    /// <summary>살아있음 — 형 목록을 어셈블리에서 찾으므로, 그 탐색이 죽으면 아래 이론은 행이 없어 조용히 통과한다.</summary>
    [Fact]
    public void TheItemTypeListFindsTheKnownTypes()
    {
        var found = ItemTypeList();
        Assert.True(new[] { typeof(ViOverlayLabel), typeof(ViOverlaySeg), typeof(ViOverlayRect), typeof(ViOverlayPoly) }.All(found.Contains),
            $"premise: the type scan finds every known overlay item type (found {string.Join(", ", found.Select(t => t.Name))})");
    }

    /// <summary>속성을 <b>하나씩</b> 기본값에서 떼어 놓고 복사해, 사본의 <b>모든</b> 속성이 원본과 같은지 본다. 전부 한꺼번에 떼면 같은 형의
    /// 형제 속성이 같은 값이 되어(실수는 전부 0 에서 같은 만큼, 불리언은 전부 true) 엉뚱한 속성에서 읽은 사본(<c>X1 = source.X2</c>,
    /// <c>IsHud = source.IsDashed</c>)도 통과했다 — 선분의 끝점이 뒤바뀌어 화살표가 반대로 그려져도 스위트 전체가 초록이었다(검토의 변이로 확인).</summary>
    [Theory]
    [MemberData(nameof(ItemTypes))]
    public void CopyConstructorCarriesEveryProperty(Type type)
    {
        var all = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        // 읽기 전용 속성은 생성자에서만 정해지므로 사본도 옮겨야 한다 — 생기면 이 시험이 그것을 다루게 고친다(건너뛰지 않는다)
        Assert.True(all.All(p => p.CanWrite),
            $"{type.Name} has get-only properties ({string.Join(", ", all.Where(p => !p.CanWrite).Select(p => p.Name))}) — a copy must carry them too; teach this test to check them");
        var props = all;
        // 살아있음 — 반사가 공통 속성(색·점선)까지 실제로 찾았다. 못 찾으면 아래 비교는 빈 목록이라 늘 초록이다.
        Assert.True(props.Any(p => p.Name == nameof(ViOverlayItem.Color)) && props.Any(p => p.Name == nameof(ViOverlayItem.IsDashed)) && props.Length >= 3,
            $"premise: reflection finds {type.Name}'s settable properties, the shared Color and IsDashed included (found {string.Join(", ", props.Select(p => p.Name))})");

        var ctor = type.GetConstructor(new[] { type });
        Assert.True(ctor is not null, $"{type.Name} has a public copy constructor taking a {type.Name}");
        var fresh = Activator.CreateInstance(type)!;
        foreach (var changed in props)
        {
            var src = Activator.CreateInstance(type)!;
            changed.SetValue(src, Different(changed.PropertyType, changed.GetValue(src), $"{type.Name}.{changed.Name}"));
            Assert.False(Equals(changed.GetValue(src), changed.GetValue(fresh)),
                $"premise: {type.Name}.{changed.Name} was set away from its default, so a copy that dropped it cannot pass by accident");
            var copy = ctor!.Invoke(new[] { src });
            Assert.NotSame(src, copy);
            foreach (var p in props)
            {
                var a = p.GetValue(src);
                var b = p.GetValue(copy);
                Assert.True(p.PropertyType.IsValueType || a is string ? Equals(a, b) : ReferenceEquals(a, b),
                    $"with only {changed.Name} changed, the copy constructor of {type.Name} carries {p.Name} ({a} vs {b}) — a copy that drops a property, " +
                    "or reads it from a sibling, draws it wrong with no error");
            }
        }
    }

    /// <summary>쓰는 모양 그대로 — 바꾼 것만 바뀌고 원본은 그대로다(항목은 이력·다른 화면이 같은 참조를 쥐고 있다).</summary>
    [Fact]
    public void ACopyWithOneChangeLeavesTheSourceAlone()
    {
        var hud = new ViHudSummary(true, "title", new[] { ("line", (ViOverlayColor?)ViOverlayColor.Cyan) }, ViHudPos.TopLeft);
        var src = new ViOverlayLabel
        {
            Text = "[OK] A", X = 10, Y = 20, FontSize = 16f, Align = ViOverlayAlign.BottomLeft, HasBackground = false,
            LineColors = new ViOverlayColor?[] { ViOverlayColor.Orange }, IsHud = true, Hud = hud, Color = ViOverlayColor.Green, IsDashed = true,
        };
        var big = new ViOverlayLabel(src) { FontSize = src.FontSize * 1.5f };
        Assert.True(big.FontSize == 24f && src.FontSize == 16f, $"only the font size changes on the copy and the source keeps its own ({big.FontSize} / {src.FontSize})");
        Assert.True((big.Text, big.X, big.Y, big.Align, big.HasBackground, big.IsHud, big.Color, big.IsDashed)
                    == (src.Text, src.X, src.Y, src.Align, src.HasBackground, src.IsHud, src.Color, src.IsDashed)
                    && ReferenceEquals(big.LineColors, src.LineColors) && ReferenceEquals(big.Hud, src.Hud),
            "everything else is carried over");
        Assert.Throws<ArgumentNullException>(() => new ViOverlayLabel(null!));
    }
}
