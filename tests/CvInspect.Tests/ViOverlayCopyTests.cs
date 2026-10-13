using System.Reflection;
using CvInspect.Vision.Overlay;
using Xunit;

namespace CvInspect.Tests;

/// <summary>
/// 오버레이 항목의 복사 생성자 — 소비자가 "한 화면에서만 글자를 키운 라벨" 같은 사본을 만들 공개 길이 없어 속성을 손으로 옮겼다.
/// 그렇게 만든 사본은 이 형에 속성이 늘 때 그것을 조용히 빠뜨린다(예외도 경고도 없이 그 속성만 기본값으로 그려진다).
/// 그래서 단언도 속성을 손으로 나열하지 않고 <b>반사로 전부</b> 훑는다 — 속성이 늘면 이 시험이 그 속성까지 본다.
/// 새 속성의 형에 값을 정해 줄 수 없으면 시험이 그 형을 대라고 실패한다(조용히 건너뛰지 않는다).
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

    [Theory]
    [InlineData(typeof(ViOverlayLabel))]
    [InlineData(typeof(ViOverlaySeg))]
    [InlineData(typeof(ViOverlayRect))]
    [InlineData(typeof(ViOverlayPoly))]
    public void CopyConstructorCarriesEveryProperty(Type type)
    {
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite).ToArray();
        // 살아있음 — 반사가 공통 속성(색·점선)까지 실제로 찾았다. 못 찾으면 아래 비교는 빈 목록이라 늘 초록이다.
        Assert.True(props.Any(p => p.Name == nameof(ViOverlayItem.Color)) && props.Any(p => p.Name == nameof(ViOverlayItem.IsDashed)) && props.Length >= 4,
            $"premise: reflection finds {type.Name}'s settable properties, the shared Color and IsDashed included (found {string.Join(", ", props.Select(p => p.Name))})");

        var fresh = Activator.CreateInstance(type)!;
        var src = Activator.CreateInstance(type)!;
        foreach (var p in props) p.SetValue(src, Different(p.PropertyType, p.GetValue(src), $"{type.Name}.{p.Name}"));
        foreach (var p in props)
            Assert.False(Equals(p.GetValue(src), p.GetValue(fresh)),
                $"premise: {type.Name}.{p.Name} was set away from its default, so a copy that dropped it cannot pass by accident");

        var ctor = type.GetConstructor(new[] { type });
        Assert.True(ctor is not null, $"{type.Name} has a public copy constructor taking a {type.Name}");
        var copy = ctor!.Invoke(new[] { src });
        Assert.NotSame(src, copy);
        foreach (var p in props)
        {
            var a = p.GetValue(src);
            var b = p.GetValue(copy);
            Assert.True(p.PropertyType.IsValueType || a is string ? Equals(a, b) : ReferenceEquals(a, b),
                $"the copy constructor of {type.Name} carries {p.Name} ({a} vs {b}) — a copy that drops a property draws it at its default with no error");
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
