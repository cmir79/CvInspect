namespace CvInspect.Vision.Overlay;

/// <summary>
/// 라이브러리 중립 결과 오버레이 — 검사 구현이 기하 프리미티브로 적재하고
/// 렌더러(비트맵 합성 렌더 / 화면 벡터 렌더 등)가 각자 해석한다. 좌표는 전부 원본 이미지 픽셀 공간.
/// Run 마다 새 인스턴스로 교체 — 소비 측(디스플레이 DP·이력 렌더)이 참조 변경으로 갱신을 감지하고,
/// 다음 사이클과의 열거 경합 없이 안전하게 소비한다.
/// </summary>
public sealed class ViOverlay
{
    public List<ViOverlayItem> Items { get; } = [];

    public void Add(ViOverlayItem item) => Items.Add(item);

    /// <summary>
    /// 잘라 낸 이미지 위에 얹을 사본 — (x, y, width, height) 는 원본 좌표의 자른 자리(<c>CvImageOps.Crop</c> 이 돌려준
    /// used 를 그대로 넘긴다. 같은 사각으로 <c>CamFrame.Crop</c> 을 하면 프레임과 그림이 맞는다).
    ///
    /// 항목은 전부 (−x, −y) 만큼 옮긴다. 요약 HUD(<see cref="ViOverlayLabel.IsHud"/> 이고 모서리 정렬)만은 옮기지 않고
    /// 잘린 이미지의 <b>같은 모서리</b>에 다시 붙인다 — 원본 모서리에 붙은 라벨을 그대로 옮기면 잘린 이미지 밖으로 나간다
    /// (왼쪽·위 모서리는 음수 좌표로, 오른쪽·아래 모서리는 잘린 폭·높이 너머로).
    /// 붙이는 규칙은 <see cref="ViHud"/> 가 처음 붙일 때와 같다(왼쪽·위는 가장자리까지의 거리 보존, 오른쪽·아래는 HUD 여백).
    /// HUD 표식이 있어도 모서리 정렬이 아니면 붙일 모서리가 없으므로 다른 라벨처럼 옮긴다.
    ///
    /// 원본은 건드리지 않는다 — 이력 저장·다른 화면이 같은 참조를 쥐고 있다(항목도 init 전용이라 새로 만든다).
    /// 영역 밖 항목도 거르지 않는다: 경계에 걸친 도형은 반만이라도 보여야 하고, 보이는 범위로 자르는 것은 렌더러 몫이다.
    /// 이 파일이 모르는 항목 종류(호스트가 파생한 것)는 옮길 방법이 없어 그대로 싣는다 — 그런 항목은 호스트가 옮긴다.
    /// </summary>
    public ViOverlay CropTo(double x, double y, double width, double height)
    {
        var cut = new ViOverlay();
        foreach (var item in Items)
            cut.Add(item switch
            {
                ViOverlayLabel
                {
                    IsHud: true,
                    Align: ViOverlayAlign.TopLeft or ViOverlayAlign.TopRight or ViOverlayAlign.BottomLeft or ViOverlayAlign.BottomRight,
                } hud => ViHud.Reanchor(hud, width, height),
                ViOverlayLabel l => l.MovedTo(l.X - x, l.Y - y),
                ViOverlaySeg s => new ViOverlaySeg
                {
                    X1 = s.X1 - x, Y1 = s.Y1 - y, X2 = s.X2 - x, Y2 = s.Y2 - y, HasEndArrow = s.HasEndArrow,
                    Color = s.Color, IsDashed = s.IsDashed,
                },
                ViOverlayRect r => new ViOverlayRect
                {
                    CenterX = r.CenterX - x, CenterY = r.CenterY - y, Width = r.Width, Height = r.Height, AngleDeg = r.AngleDeg,
                    Color = r.Color, IsDashed = r.IsDashed,
                },
                ViOverlayPoly p => new ViOverlayPoly
                {
                    Points = p.Points.Select(q => (q.X - x, q.Y - y)).ToList(), IsClosed = p.IsClosed,
                    Color = p.Color, IsDashed = p.IsDashed,
                },
                _ => item,
            });
        return cut;
    }
}

public enum ViOverlayColor { Green, Red, Cyan, Orange, Yellow, White, Teal }

public enum ViOverlayAlign
{
    /// <summary>텍스트 상단 중앙이 앵커 — 앵커 아래로 그려짐.</summary>
    TopCenter,

    /// <summary>텍스트 하단 중앙이 앵커 — 앵커 위로 그려짐.</summary>
    BottomCenter,

    /// <summary>텍스트 좌상단이 앵커 — 좌측 정렬 블록(요약 HUD 등).</summary>
    TopLeft,

    /// <summary>텍스트 좌하단이 앵커 — 검출 박스 좌상단 위에 라벨을 얹는 용도 (박스 왼쪽에서 시작).</summary>
    BottomLeft,

    /// <summary>텍스트 우상단이 앵커 — 앵커에서 왼쪽·아래로 펼쳐짐 (이미지 우상단에 붙이는 블록).
    /// 여러 줄이면 블록의 오른쪽 변이 앵커에 닿고 줄 안 정렬은 왼쪽이다.</summary>
    TopRight,

    /// <summary>텍스트 우하단이 앵커 — 앵커에서 왼쪽·위로 펼쳐짐 (이미지 우하단에 붙이는 블록).</summary>
    BottomRight,
}

public abstract class ViOverlayItem
{
    public ViOverlayColor Color { get; init; }

    /// <summary>점선 렌더 — 보조 기하(검색 창 등)를 실선 결과 기하와 구분.</summary>
    public bool IsDashed { get; init; }
}

/// <summary>선분 — HasEndArrow 면 끝점에 채운 화살촉(라인 방향/각도 부호 혼동 방지).</summary>
public sealed class ViOverlaySeg : ViOverlayItem
{
    public double X1 { get; init; }
    public double Y1 { get; init; }
    public double X2 { get; init; }
    public double Y2 { get; init; }
    public bool HasEndArrow { get; init; }
}

/// <summary>텍스트 라벨 — FontSize 는 포인트 단위. 픽셀이 아니라서 이미지 줌과 무관한 화면 크기다.</summary>
public sealed class ViOverlayLabel : ViOverlayItem
{
    public string Text { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
    public float FontSize { get; init; } = 20f;
    public ViOverlayAlign Align { get; init; } = ViOverlayAlign.TopCenter;

    /// <summary>글자 크기에 타이트한 검은 배경 박스 — 이미지 위 가독성. 기본 켜짐(HUD 는 항상 켬).</summary>
    public bool HasBackground { get; init; } = true;

    /// <summary>줄별 색 — <see cref="Text"/> 의 줄 순서와 1:1 이고, 항목이 null 이면
    /// <see cref="ViOverlayItem.Color"/> 를 쓴다. 이 속성 자체가 null 이면(기본) 블록 전체가 한 색이다.
    /// 여러 라벨을 쌓지 않고 한 블록 안에서 색을 나누는 이유: 라벨 위치는 이미지 좌표인데 글자 크기는
    /// 별도 스케일이라, 줄 높이만큼 좌표를 내려 쌓으면 배율에 따라 겹치거나 벌어진다.</summary>
    public IReadOnlyList<ViOverlayColor?>? LineColors { get; init; }

    /// <summary>요약 HUD 표식 — <see cref="ViHud"/> 가 켠다. 이 라벨은 이미지의 한 <b>모서리</b>에 붙어 있고
    /// (<see cref="Align"/> 이 그 모서리) 검사 기하와 무관하다 — 그래서 잘라 보일 때(<see cref="ViOverlay.CropTo"/>)
    /// 옮기지 않고 같은 모서리에 다시 붙이며, 화면이 HUD 를 따로 떼어 적을 때 이것으로 가린다.
    /// Align 만으로는 못 가린다 — 모서리 Align 은 검출 박스 위 라벨(BottomLeft 등)에도 쓰인다. 렌더러는 읽지 않는다.</summary>
    public bool IsHud { get; init; }

    /// <summary>앵커만 옮긴 사본 — 나머지 속성을 빠짐없이 잇는다(속성이 늘면 여기 한 곳만 고친다).</summary>
    internal ViOverlayLabel MovedTo(double x, double y) => new()
    {
        Text = Text,
        X = x,
        Y = y,
        FontSize = FontSize,
        Align = Align,
        HasBackground = HasBackground,
        LineColors = LineColors,
        IsHud = IsHud,
        Color = Color,
        IsDashed = IsDashed,
    };
}

/// <summary>회전 사각 외곽선 — 패턴 발견 위치 표시 등.</summary>
public sealed class ViOverlayRect : ViOverlayItem
{
    public double CenterX { get; init; }
    public double CenterY { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public double AngleDeg { get; init; }
}

/// <summary>폴리라인 — 블랍 경계 등. IsClosed 면 시작점으로 닫아 그림.</summary>
public sealed class ViOverlayPoly : ViOverlayItem
{
    public IReadOnlyList<(double X, double Y)> Points { get; init; } = [];
    public bool IsClosed { get; init; } = true;
}
