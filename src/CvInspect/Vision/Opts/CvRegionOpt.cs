using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using CvInspect.Vision.Edit;
using CvInspect.Vision.Overlay;

namespace CvInspect.Vision.Opts;

/// <summary>검사 영역의 모양.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CvRegionShape
{
    /// <summary>회전 사각 — 일반 면적 판정.</summary>
    Rect,

    /// <summary>원환(링 밴드) — 고리형 대상(와셔 등). 가운데(볼트 머리 등 닮은 이물)를 안쪽 반경으로 배제.</summary>
    Ring,
}

/// <summary>
/// 공용 검사 영역 — 모양(<see cref="Shape"/>)과 그 기하를 한 덩어리로 갖는다.
/// 툴 파라미터가 이걸 품으면 영역 모양이 툴 설정이 된다 — 모양별로 검사를 따로 만들지 않는다.
/// 마스크/면적 산출은 <see cref="CvRegionMask"/>, 편집 도형은 표시 계층의 도형 바인더가 담당.
///
/// 좌표 공간은 소유 툴의 규약을 따른다 (예: 컬러 세그먼트 = 원본 공간). 기존 툴(블랍 등)의
/// 사각 탐색영역을 이걸로 바꾸려면 티칭 JSON 마이그레이션과 파인더의 마스크 인지가 필요하므로
/// 신설 툴부터 채택한다.
/// </summary>
public sealed class CvRegionOpt : INotifyPropertyChanged, ICvShapeSource
{
    /// <summary>그립이 쓰는 기하(<c>Rect*</c>·<c>Center*</c>·<c>RMinPx</c>·<c>RMaxPx</c>)만 통지한다 — 이 값을 보여 주는 편집기가
    /// 드래그를 따라오게 하려는 것이다. <see cref="Shape"/> 는 편집기에서 고치는 값이라 통지하지 않는다.
    /// 다른 툴 파라미터에 품겨 있으면 편집기는 이 객체를 따로 구독해야 한다 — 품은 쪽이 대신 알리지 않는다.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    private double _rectX = 100;
    private double _rectY = 100;
    private double _rectW = 160;
    private double _rectH = 120;
    private double _rectAngleDeg;
    private double _centerX = 200;
    private double _centerY = 200;
    private double _rMinPx = 80;
    private double _rMaxPx = 120;

    public CvRegionShape Shape { get; set; } = CvRegionShape.Rect;

    // 사각 (Shape=Rect) — 회전 그립 포함 도형 드래그로 편집.
    public double RectX { get => _rectX; set => Notify(Put(ref _rectX, value)); }
    public double RectY { get => _rectY; set => Notify(Put(ref _rectY, value)); }
    public double RectW { get => _rectW; set => Notify(Put(ref _rectW, value)); }
    public double RectH { get => _rectH; set => Notify(Put(ref _rectH, value)); }
    public double RectAngleDeg { get => _rectAngleDeg; set => Notify(Put(ref _rectAngleDeg, value)); }

    // 링 밴드 (Shape=Ring) — 링 도형 드래그로 편집(중심 이동 포함).
    public double CenterX { get => _centerX; set => Notify(Put(ref _centerX, value)); }
    public double CenterY { get => _centerY; set => Notify(Put(ref _centerY, value)); }
    public double RMinPx { get => _rMinPx; set => Notify(Put(ref _rMinPx, value)); }
    public double RMaxPx { get => _rMaxPx; set => Notify(Put(ref _rMaxPx, value)); }

    /// <summary>편집 도형 — 모양(사각/링) 분기를 한곳에 모은다. 어느 툴이든 이 영역을 품으면 이걸로 도형을 얻는다(모양별로 따로 만들지 않는다).
    /// 링은 중심 이동을 허용한다 — 밴드 자리를 티칭에서 직접 잡는다(파생 중심을 쓰는 언랩·충전율과 다른 점).</summary>
    public IReadOnlyList<CvEditShape>? CreateShapes(Action onEdited)
    {
        if (Shape == CvRegionShape.Ring)
        {
            var ring = new CvEditRing { Color = ViOverlayColor.Yellow, Label = "Ring", IsMovable = true };
            ring.Set(CenterX, CenterY, RMinPx, RMaxPx);
            ring.Changed += (_, _) =>
            {
                Notify(Put(ref _centerX, ring.CenterX, nameof(CenterX)), Put(ref _centerY, ring.CenterY, nameof(CenterY)),
                    Put(ref _rMinPx, ring.RMin, nameof(RMinPx)), Put(ref _rMaxPx, ring.RMax, nameof(RMaxPx)));
                onEdited();
            };
            return [ring];
        }

        var rect = new CvEditRect { Color = ViOverlayColor.Yellow, Label = "Region", IsRotatable = true };
        rect.Set(RectX, RectY, RectW, RectH);
        rect.AngleDeg = RectAngleDeg;
        rect.Changed += (_, _) =>
        {
            Notify(Put(ref _rectX, rect.X, nameof(RectX)), Put(ref _rectY, rect.Y, nameof(RectY)),
                Put(ref _rectW, rect.W, nameof(RectW)), Put(ref _rectH, rect.H, nameof(RectH)),
                Put(ref _rectAngleDeg, rect.AngleDeg, nameof(RectAngleDeg)));
            onEdited();
        };
        return [rect];
    }

    /// <summary>값을 쓰고, 바뀌었으면 그 이름을 돌려준다(여기서는 알리지 않는다). 그립 핸들러는 축을 전부 쓴 뒤 <see cref="Notify"/> 로
    /// 한 번에 알린다 — 쓰는 도중에 알리면 구독자가 절반만 바뀐 기하를 읽는다(통지 안에서 도형을 다시 맞추면 아직 안 쓴 축이 옛 값으로 되돌아간다).
    /// 같은 값이어도 쓴다(-0.0 같은 비트 차이를 버리지 않는다) — 알리지만 않는다.</summary>
    private static string? Put(ref double field, double value, [CallerMemberName] string? name = null)
    {
        var same = field.Equals(value);
        field = value;
        return same ? null : name;
    }

    private void Notify(params string?[] names)
    {
        foreach (var n in names)
            if (n is not null) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
