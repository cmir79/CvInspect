using CvInspect.Vision.Overlay;

namespace CvInspect.Controls;

/// <summary>
/// CvDispCtrl 의 편집 가능 도형 — 좌표는 표시 중인 이미지의 픽셀 공간 (툴 파라미터 공간과 동일 전제).
/// 값 변경 시 Changed 발화 — 바인더가 툴 파라미터(POCO)로 즉시 되write.
/// </summary>
public abstract class CvEditShape
{
    public event EventHandler? Changed;

    public ViOverlayColor Color { get; init; } = ViOverlayColor.Yellow;

    /// <summary>도형 식별 라벨 (예: "Train", "Search") — 도형 근처에 소형 표기. 빈 문자열 = 미표기.</summary>
    public string Label { get; init; } = "";

    protected void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// 사각 영역 — 이동(내부 드래그) + 4 모서리 리사이즈 + 회전 그립.
/// X/Y/W/H 는 미회전 기준(좌상단+크기), AngleDeg 만큼 중심 기준 회전 (툴 파라미터와 동일 규약).
/// </summary>
public sealed class CvEditRect : CvEditShape
{
    private double _x, _y, _w, _h, _angleDeg;

    public double X { get => _x; set { if (_x != value) { _x = value; RaiseChanged(); } } }
    public double Y { get => _y; set { if (_y != value) { _y = value; RaiseChanged(); } } }
    public double W { get => _w; set { if (_w != value) { _w = Math.Max(2, value); RaiseChanged(); } } }
    public double H { get => _h; set { if (_h != value) { _h = Math.Max(2, value); RaiseChanged(); } } }

    /// <summary>중심 기준 회전각(deg, 이미지 atan2 규약). 회전 그립 드래그로 편집.</summary>
    public double AngleDeg { get => _angleDeg; set { if (_angleDeg != value) { _angleDeg = value; RaiseChanged(); } } }

    /// <summary>회전 지원 여부 — false 면 회전 그립 미표시 (탐색 영역 등 축 정렬 전용 영역).</summary>
    public bool IsRotatable { get; init; }

    /// <summary>크기 편집 지원 여부 — false 면 모서리·회전 그립 없이 이동만
    /// (크기가 다른 값과 한 쌍으로 고정된 영역 — AI 크롭 사각처럼 학습 구도와 짝인 경우).</summary>
    public bool IsResizable { get; init; } = true;

    public void Set(double x, double y, double w, double h)
    {
        _x = x; _y = y; _w = Math.Max(2, w); _h = Math.Max(2, h);
        RaiseChanged();
    }
}

/// <summary>선분(탐색 세그먼트) — 이동(선 드래그) + 양 끝점 핸들.</summary>
public sealed class CvEditSeg : CvEditShape
{
    private double _x1, _y1, _x2, _y2;

    public double X1 { get => _x1; set { if (_x1 != value) { _x1 = value; RaiseChanged(); } } }
    public double Y1 { get => _y1; set { if (_y1 != value) { _y1 = value; RaiseChanged(); } } }
    public double X2 { get => _x2; set { if (_x2 != value) { _x2 = value; RaiseChanged(); } } }
    public double Y2 { get => _y2; set { if (_y2 != value) { _y2 = value; RaiseChanged(); } } }

    public void Set(double x1, double y1, double x2, double y2)
    {
        _x1 = x1; _y1 = y1; _x2 = x2; _y2 = y2;
        RaiseChanged();
    }
}

/// <summary>
/// 동심원 링(언랩 반경 밴드) — 안/바깥 반경 그립 (min/max 쌍 원 시각 표시).
/// 안 그립은 좌측(180°), 바깥 그립은 우측(0°) 원주에 배치 — 반경이 가까워도 그립이 겹치지 않게.
/// 중심은 기본 이동 불가(IsMovable=false) — 언랩 밴드 중심은 서클 툴(검출 중심)을 따르는 파생 값.
/// </summary>
public sealed class CvEditRing : CvEditShape
{
    private double _cx, _cy, _rMin = 40, _rMax = 120;

    /// <summary>중심 이동 허용 — 기본 false (중심이 다른 툴에서 파생되는 밴드 용도).</summary>
    public bool IsMovable { get; init; }

    public double CenterX { get => _cx; set { if (_cx != value) { _cx = value; RaiseChanged(); } } }
    public double CenterY { get => _cy; set { if (_cy != value) { _cy = value; RaiseChanged(); } } }

    /// <summary>안쪽 반경 — 4 이상, 바깥 반경 − 4 이하로 클램프.</summary>
    public double RMin { get => _rMin; set { var v = Math.Clamp(value, 4, _rMax - 4); if (_rMin != v) { _rMin = v; RaiseChanged(); } } }

    /// <summary>바깥 반경 — 안쪽 반경 + 4 이상으로 클램프.</summary>
    public double RMax { get => _rMax; set { var v = Math.Max(value, _rMin + 4); if (_rMax != v) { _rMax = v; RaiseChanged(); } } }

    public void Set(double cx, double cy, double rMin, double rMax)
    {
        _cx = cx;
        _cy = cy;
        _rMax = Math.Max(rMax, 8);
        _rMin = Math.Clamp(rMin, 4, _rMax - 4);
        RaiseChanged();
    }
}

/// <summary>원(꽉 찬 원 영역) — 이동(내부/중심 드래그) + 반경 그립(우측 0°). 원형 학습영역 등.</summary>
public sealed class CvEditCircle : CvEditShape
{
    private double _cx, _cy, _r = 60;

    public double CenterX { get => _cx; set { if (_cx != value) { _cx = value; RaiseChanged(); } } }
    public double CenterY { get => _cy; set { if (_cy != value) { _cy = value; RaiseChanged(); } } }

    /// <summary>반경 — 4 이상 클램프 (그보다 작으면 학습 표본이 못 된다).</summary>
    public double Radius { get => _r; set { var v = Math.Max(4, value); if (_r != v) { _r = v; RaiseChanged(); } } }

    public void Set(double cx, double cy, double r)
    {
        _cx = cx;
        _cy = cy;
        _r = Math.Max(4, r);
        RaiseChanged();
    }
}

/// <summary>
/// 원호(서클 파인더 기대 원) — 이동(호/중심 드래그) + 시작각·스팬 끝·반경 그립.
/// 각도는 이미지 atan2 규약(deg, +X축 기준·y-아래 증가), 스팬은 시작각에서 + 방향 (5~360°).
/// 스팬 끝 그립에는 진행 방향 화살촉 표시 (캘리퍼 배치 순서/각도 부호 시인).
/// </summary>
public sealed class CvEditArc : CvEditShape
{
    private double _cx, _cy, _r, _startDeg, _spanDeg = 360;

    public double CenterX { get => _cx; set { if (_cx != value) { _cx = value; RaiseChanged(); } } }
    public double CenterY { get => _cy; set { if (_cy != value) { _cy = value; RaiseChanged(); } } }
    public double Radius { get => _r; set { if (_r != value) { _r = Math.Max(3, value); RaiseChanged(); } } }
    public double StartDeg { get => _startDeg; set { if (_startDeg != value) { _startDeg = value; RaiseChanged(); } } }

    /// <summary>스팬(deg) — 5~360 클램프 (0 스팬은 검출 불능이라 도형에서 차단).</summary>
    public double SpanDeg { get => _spanDeg; set { var v = Math.Clamp(value, 5, 360); if (_spanDeg != v) { _spanDeg = v; RaiseChanged(); } } }

    public void Set(double cx, double cy, double r, double startDeg, double spanDeg)
    {
        _cx = cx;
        _cy = cy;
        _r = Math.Max(3, r);
        _startDeg = startDeg;
        _spanDeg = Math.Clamp(spanDeg, 5, 360);
        RaiseChanged();
    }
}
