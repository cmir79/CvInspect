// 툴바 아이콘 — 7개 버튼(📸⏯✋⛶➕➖🧹)의 그림을 글자 대신 벡터 경로로 그린다.
// 글자로 두면 Skia 가 컬러 이모지 글꼴의 컬러 글리프를 그리고, 그 글꼴의 줄 높이 때문에 버튼 위쪽으로 쏠린다(헤드리스·데스크톱 실측).
// 글꼴이 없는 OS(이모지 글꼴 없는 Linux 등)에서는 상자로 나온다. 경로는 글꼴과 무관하게 같은 모양·같은 자리다.
// 색은 상속되는 글자색(TextElement.Foreground)을 그대로 쓴다 — 버튼 템플릿의 ContentPresenter 가 테마의 평소·비활성 글자색을,
// 강조 버튼(⏯·눌린 ✋)은 PinAccent 가 묶은 버튼 Foreground(흰색)를 내려보낸다. 테마를 강제하지 않는다.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace CvInspect.Avalonia.Controls;

/// <summary>툴바 아이콘 종류.</summary>
internal enum CvToolIconKind { Camera, PlayPause, Hand, Fit, ZoomIn, ZoomOut, Broom }

/// <summary>16×16 DIP 툴바 아이콘. 크기는 WPF 판 툴바(Segoe UI Symbol 16pt 기호)에서 잰 글리프 상자(가로·세로 11~18 px)에 맞췄다.
/// 그림의 경계 상자(선 굵기 포함) 중심을 컨트롤 중심에 맞춰 그린다 — 경로 좌표를 손으로 맞추지 않아도 가운데다.</summary>
internal sealed class CvToolIcon : Control
{
    /// <summary>아이콘 한 변(DIP).</summary>
    internal const double Size = 16;

    static CvToolIcon()
    {
        // 상속 글자색이 바뀌면(비활성·눌림·테마 변형) 다시 그린다.
        AffectsRender<CvToolIcon>(TextElement.ForegroundProperty);
    }

    /// <summary>그림 한 조각 — 면이면 채우고, 선이면 그 굵기로 긋는다. 조각마다 따로 그려 겹치는 면이 감기 방향에 따라 비지 않는다.</summary>
    private readonly record struct Part(Geometry Geo, double Stroke);

    private readonly Part[] _parts;
    private readonly Vector _center;   // 조각들의 경계 상자 중심(경로 좌표)

    public CvToolIcon(CvToolIconKind kind)
    {
        // 경로 객체는 렌더 플랫폼이 선 뒤에 만들어야 하므로 정적으로 두지 않고 인스턴스마다 만든다(버튼 7개라 비용이 없다).
        _parts = PartsOf(kind);
        Rect? box = null;
        foreach (var p in _parts)
        {
            var r = p.Stroke > 0 ? p.Geo.GetRenderBounds(PenOf(Brushes.Black, p.Stroke)) : p.Geo.Bounds;
            box = box is { } b ? b.Union(r) : r;
        }
        _center = box?.Center is { } c ? new Vector(c.X, c.Y) : new Vector(Size / 2, Size / 2);
        Width = Size;
        Height = Size;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public override void Render(DrawingContext context)
    {
        if (GetValue(TextElement.ForegroundProperty) is not { } brush) return;
        var offset = new Vector(Bounds.Width / 2, Bounds.Height / 2) - _center;
        using (context.PushTransform(Matrix.CreateTranslation(offset)))
        {
            foreach (var p in _parts)
            {
                if (p.Stroke > 0) context.DrawGeometry(null, PenOf(brush, p.Stroke), p.Geo);
                else context.DrawGeometry(brush, null, p.Geo);
            }
        }
    }

    // 선 끝은 둥글게 — 모서리 괄호(⛶)·빗자루 자루의 끝이 작은 크기에서 뭉개져 보이지 않게.
    private static Pen PenOf(IBrush brush, double thickness) => new(brush, thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

    private static Part Fill(string data) => new(Geometry.Parse(data), 0);

    private static Part Line(string data, double thickness) => new(Geometry.Parse(data), thickness);

    // 경로는 16×16 좌표. 면은 짝홀 채움(기본) — 카메라 렌즈의 고리처럼 안쪽 도형이 구멍이 된다.
    private static Part[] PartsOf(CvToolIconKind kind) => kind switch
    {
        // 몸체 + 위 돌기, 렌즈 고리(구멍) 안에 렌즈 알
        CvToolIconKind.Camera =>
        [
            Fill("M1,5 H4.5 L6,3 H10 L11.5,5 H15 V13.5 H1 Z M8,6 A3.25,3.25 0 1 1 8,12.5 A3.25,3.25 0 1 1 8,6 Z M8,7.75 A1.5,1.5 0 1 0 8,10.75 A1.5,1.5 0 1 0 8,7.75 Z"),
        ],
        // ▶ + ‖
        CvToolIconKind.PlayPause =>
        [
            Fill("M1.5,3 L8,8 L1.5,13 Z M9.5,3 H11.7 V13 H9.5 Z M12.9,3 H15.1 V13 H12.9 Z"),
        ],
        // 펼친 손 — 손가락 넷·손바닥·엄지
        CvToolIconKind.Hand =>
        [
            Fill("M3.8,9.5 V4 A1,1 0 0 1 5.8,4 V9.5 Z"),
            Fill("M6.3,9 V2.5 A1,1 0 0 1 8.3,2.5 V9 Z"),
            Fill("M8.8,9 V3 A1,1 0 0 1 10.8,3 V9 Z"),
            Fill("M11.3,9.5 V5 A0.9,0.9 0 0 1 13.1,5 V9.5 Z"),
            Fill("M3.8,8.5 H13.1 V11 C13.1,13.4 11.6,15 9.4,15 H8.2 C6.6,15 5.4,14.2 4.5,13 L3.8,12 Z"),
            Fill("M4.4,13 L1.3,9.3 C0.8,8.7 0.9,7.9 1.5,7.5 C2.1,7.1 2.8,7.3 3.2,7.9 L4.6,9.6 Z"),
        ],
        // 모서리 괄호 넷 — 화면 맞춤
        CvToolIconKind.Fit =>
        [
            Line("M3,6.5 V3 H6.5 M9.5,3 H13 V6.5 M13,9.5 V13 H9.5 M6.5,13 H3 V9.5", 2),
        ],
        CvToolIconKind.ZoomIn =>
        [
            Fill("M1.5,6.5 H14.5 V9.5 H1.5 Z"),
            Fill("M6.5,1.5 H9.5 V14.5 H6.5 Z"),
        ],
        CvToolIconKind.ZoomOut =>
        [
            Fill("M1.5,6.5 H14.5 V9.5 H1.5 Z"),
        ],
        // 빗자루 — 자루, 묶음 띠, 솔(띠와 솔 사이를 띄워 경계가 보이게)
        _ =>
        [
            Line("M14.6,1.4 L9.6,6.4", 1.6),
            Fill("M7.4,5.2 L10.8,8.6 L9.7,9.7 L6.3,6.3 Z"),
            Fill("M5.8,7 L9,10.2 C8.3,12.2 7,13.9 5.3,14.9 C3.8,14.7 2.3,14.2 1,13.5 C2.3,11.6 3.6,8.9 5.8,7 Z"),
        ],
    };
}
