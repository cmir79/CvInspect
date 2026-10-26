// 그리기 목록의 프리미티브 — 오버레이·편집 도형을 화면 좌표(DIP)의 선·경로·채움·글자로 펼친 것. UI 어댑터는 목록 순서대로 그리기만 한다.
// 글자 크기만은 UI 마다 재는 법이 달라 ITextMetricPort 로 받는다(정렬·배경 상자·줄 간격이 그 값에 기댄다).

using CvInspect.Vision.Overlay;

namespace CvInspect.Display;

/// <summary>칠할 색 — 오버레이 이름 색이거나 ARGB. 이름 색의 실제 값은 어댑터가 정한다(어두운 Green/Teal 등 UI 쪽 선택).</summary>
internal readonly struct ViPaint
{
    internal ViOverlayColor? Named { get; }
    internal byte A { get; }
    internal byte R { get; }
    internal byte G { get; }
    internal byte B { get; }

    private ViPaint(ViOverlayColor? named, byte a, byte r, byte g, byte b)
    {
        Named = named;
        A = a;
        R = r;
        G = g;
        B = b;
    }

    internal static ViPaint Of(ViOverlayColor color) => new(color, 0, 0, 0, 0);

    internal static ViPaint Argb(byte a, byte r, byte g, byte b) => new(null, a, r, g, b);
}

/// <summary>선 — 색·굵기(DIP)·점선 여부.</summary>
internal readonly record struct ViStroke(ViPaint Paint, double Thick, bool IsDashed);

/// <summary>글꼴 역할 — 글꼴 이름은 어댑터가 정한다. Data = 오버레이·편집 도형 라벨(고정폭 굵게), Ui = 무이미지 안내 문구.</summary>
internal enum ViFontRole { Data, Ui }

/// <summary>잰 글자 블록 — 폭·높이(DIP, 높이는 줄 높이 × 줄 수). <see cref="Tag"/> 는 어댑터가 잴 때 만든 자기 객체(같은 글자를 그릴 때 다시 쓴다).</summary>
internal readonly record struct ViTextMetric(double Width, double Height, object? Tag);

/// <summary>상자(DIP) — 왼쪽 위 + 폭·높이.</summary>
internal readonly record struct ViBox(double X, double Y, double W, double H);

/// <summary>글자 측정 — UI 어댑터가 구현한다. 크기는 pt 그대로 넘기고 DIP 환산·범위 자르기는 구현이 한다(역할마다 규칙이 다르다).</summary>
internal interface ITextMetricPort
{
    /// <summary>글자 블록 크기. <paramref name="paint"/> 는 그릴 색 — 구현이 <see cref="ViTextMetric.Tag"/> 를 그대로 그릴 수 있게 넘긴다.</summary>
    ViTextMetric Measure(string text, float sizePt, ViFontRole role, ViPaint paint);

    /// <summary>원점 (x, y) 에 놓인 글자의 잉크 경계 — 글자가 실제로 칠하는 영역(줄 높이보다 좁다). 잉크가 없으면 null.
    /// 원점을 함께 넘기는 것은 원점 0 에서 재고 나중에 더하면 끝자리가 갈려 배경 상자 가장자리가 달라질 수 있어서다.</summary>
    ViBox? MeasureInk(in ViTextMetric metric, string text, float sizePt, ViFontRole role, double x, double y);
}

/// <summary>프리미티브 — 목록 순서가 곧 그리는 순서다.</summary>
internal abstract class ViPrim
{
}

/// <summary>선분 하나.</summary>
internal sealed class ViPrimLine(ViStroke stroke, (double X, double Y) p1, (double X, double Y) p2) : ViPrim
{
    internal ViStroke Stroke { get; } = stroke;
    internal (double X, double Y) P1 { get; } = p1;
    internal (double X, double Y) P2 { get; } = p2;
}

/// <summary>꺾은선 경로 하나 — 선분을 따로 긋지 않고 한 지오메트리로(모서리 이음·명령 수). 닫으면 마지막 점이 첫 점으로 이어진다.</summary>
internal sealed class ViPrimPath(ViStroke stroke, (double X, double Y)[] points, bool isClosed) : ViPrim
{
    internal ViStroke Stroke { get; } = stroke;
    internal (double X, double Y)[] Points { get; } = points;
    internal bool IsClosed { get; } = isClosed;
}

/// <summary>채운 다각형(외곽선 없음) — 화살촉.</summary>
internal sealed class ViPrimFill(ViPaint paint, (double X, double Y)[] points) : ViPrim
{
    internal ViPaint Paint { get; } = paint;
    internal (double X, double Y)[] Points { get; } = points;
}

/// <summary>채운 축 정렬 사각(외곽선 없음) — 핸들, 라벨 배경.</summary>
internal sealed class ViPrimBox(ViPaint paint, ViBox box) : ViPrim
{
    internal ViPaint Paint { get; } = paint;
    internal ViBox Box { get; } = box;
}

/// <summary>채운 원(외곽선 없음) — 회전·반경 그립.</summary>
internal sealed class ViPrimDot(ViPaint paint, (double X, double Y) center, double radius) : ViPrim
{
    internal ViPaint Paint { get; } = paint;
    internal (double X, double Y) Center { get; } = center;
    internal double Radius { get; } = radius;
}

/// <summary>글자 — 원점은 블록 왼쪽 위. <see cref="Metric"/> 이 있으면 그 Tag 로 그린다(잰 것과 같은 객체).</summary>
internal sealed class ViPrimText(string text, float sizePt, ViFontRole role, ViPaint paint, (double X, double Y) origin, ViTextMetric? metric) : ViPrim
{
    internal string Text { get; } = text;
    internal float SizePt { get; } = sizePt;
    internal ViFontRole Role { get; } = role;
    internal ViPaint Paint { get; } = paint;
    internal (double X, double Y) Origin { get; } = origin;
    internal ViTextMetric? Metric { get; } = metric;
}

/// <summary>여기서부터 이 상자 안만 그린다 — <see cref="ViPrimClipPop"/> 까지.</summary>
internal sealed class ViPrimClipPush(ViBox box) : ViPrim
{
    internal ViBox Box { get; } = box;
}

/// <summary>앞의 <see cref="ViPrimClipPush"/> 를 푼다.</summary>
internal sealed class ViPrimClipPop : ViPrim
{
    internal static readonly ViPrimClipPop Instance = new();

    private ViPrimClipPop()
    {
    }
}
