// 표시 표면의 뷰 변환 — 이미지 좌표 ↔ 화면 좌표. 등방 배율 + 이동만 쓴다(회전·비등방 없음).
// UI 프레임워크 무관: 지금은 WPF 어댑터(CvDispSurface)가 이 값으로 그리고 잡고, 다른 UI 어댑터도 같은 값을 쓰게 하려는 자리다.
//
// 식은 WPF Matrix 의 배율+이동 경로와 같은 순서로 계산한다 — 한 연산이라도 순서를 바꾸면 끝자리가 갈리고,
// 히트 판정 경계(±8 DIP, 변 길이 21 DIP)에서 결과가 뒤집힐 수 있다. Windows 회귀(CvViewXformMatrixTests)가 Matrix 와 비트 단위로 대조한다.
//
// ⚠ default(CvViewXform) 는 배율 0 이라 ToImage 가 무한대를 낸다(Matrix 의 default 는 단위행렬처럼 동작했다).
// 필드는 반드시 Identity 로 초기화한다.

namespace CvInspect.Display;

/// <summary>이미지 → 화면 변환(등방 배율 <see cref="Scale"/> + 이동). 값 타입이라 조작은 새 값을 돌려준다.</summary>
internal readonly struct CvViewXform
{
    /// <summary>줌 하한·상한 — 이 밖으로는 휠·버튼 줌과 맞춤이 자른다.</summary>
    internal const double MinScale = 0.02;
    internal const double MaxScale = 64;

    /// <summary>휠 한 칸·줌 버튼 한 번의 배율.</summary>
    internal const double ZoomStep = 1.25;

    /// <summary>맞춤 여백 — 표면에 꽉 채우지 않고 이만큼 줄인다(가장자리 핸들·라벨이 표면 경계에 붙지 않게).</summary>
    private const double FitMargin = 0.97;

    /// <summary>맞춤을 하지 않는 표면 크기 하한(DIP) — 이보다 작으면 배율이 0 에 가까워져 뜻이 없다.</summary>
    private const double FitMinViewPx = 8;

    internal static readonly CvViewXform Identity = new(1, 0, 0);

    internal double Scale { get; }
    internal double OffsetX { get; }
    internal double OffsetY { get; }

    internal CvViewXform(double scale, double offsetX, double offsetY)
    {
        Scale = scale;
        OffsetX = offsetX;
        OffsetY = offsetY;
    }

    /// <summary>이미지 좌표 → 화면 좌표.</summary>
    internal (double X, double Y) ToScreen(double x, double y) => (x * Scale + OffsetX, y * Scale + OffsetY);

    /// <summary>화면 좌표 → 이미지 좌표. 역배율을 먼저 구해 곱한다(WPF Matrix.Invert 의 배율+이동 경로와 같은 순서).</summary>
    internal (double X, double Y) ToImage(double sx, double sy)
    {
        var inv = 1.0 / Scale;
        return (sx * inv + -OffsetX * inv, sy * inv + -OffsetY * inv);
    }

    /// <summary>화면 점 (cx, cy) 를 고정한 채 factor 배 — 배율에 곱하고 이동은 그 점 기준으로 다시 잡는다.</summary>
    internal CvViewXform ScaleAt(double factor, double cx, double cy)
        => new(Scale * factor, factor * OffsetX + (cx - factor * cx), factor * OffsetY + (cy - factor * cy));

    /// <summary>화면에서 (dx, dy) 만큼 이동.</summary>
    internal CvViewXform Translate(double dx, double dy) => new(Scale, OffsetX + dx, OffsetY + dy);

    /// <summary>줌 한 번 — 배율을 [<see cref="MinScale"/>, <see cref="MaxScale"/>] 로 자른 뒤 남는 배수로 (cx, cy) 기준 확대·축소.
    /// 끝값에 닿아 배수가 사실상 1 이면 false(아무것도 안 바뀌었으니 다시 그릴 것도 없다).</summary>
    internal bool TryZoomAt(double factor, double cx, double cy, out CvViewXform next)
    {
        var target = Math.Clamp(Scale * factor, MinScale, MaxScale);
        factor = target / Scale;
        if (Math.Abs(factor - 1) < 1e-9)
        {
            next = this;
            return false;
        }
        next = ScaleAt(factor, cx, cy);
        return true;
    }

    /// <summary>이미지를 표면 가운데에 맞춘 변환. 표면이 너무 작거나 이미지가 없으면 false — 호출자는 지금 뷰를 그대로 둔다.</summary>
    internal static bool TryFit(double viewW, double viewH, int imgW, int imgH, out CvViewXform fit)
    {
        if (viewW < FitMinViewPx || viewH < FitMinViewPx || imgW == 0)
        {
            fit = Identity;
            return false;
        }
        var scale = Math.Min(viewW / imgW, viewH / imgH) * FitMargin;
        scale = Math.Clamp(scale, MinScale, MaxScale);
        fit = new CvViewXform(scale, (viewW - imgW * scale) / 2, (viewH - imgH * scale) / 2);
        return true;
    }
}
