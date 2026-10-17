using System.ComponentModel;
using System.Runtime.CompilerServices;
using CvInspect.Vision.Edit;
using CvInspect.Vision.Overlay;

namespace CvInspect.Vision.Opts;

/// <summary>에지 필터 — 이진화 + 연결요소 분석으로 유효 에지 블랍만 남긴다 (값은 에지 크기 유지).</summary>
public sealed class CvEdgeFilterOpt : INotifyPropertyChanged, ICvShapeSource
{
    /// <summary>그립이 쓰는 영역 좌표(<c>Region*</c>)만 통지한다 — 이 값을 보여 주는 편집기가 드래그를 따라오게
    /// 하려는 것이다. 나머지는 편집기에서 고치는 값이라 편집기가 이미 안다.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    private double _regionX;
    private double _regionY;
    private double _regionW = 100;
    private double _regionH = 100;
    private double _regionAngleDeg;

    [CvCategory("cv:CatThreshold", 1)]
    [CvName("cv:Threshold")]
    [CvDesc("cv:ThresholdDesc")]
    public int Threshold { get; set; } = 40;

    [CvCategory("cv:CatThreshold", 1)]
    [CvName("cv:MinPixels")]
    [CvDesc("cv:MinPixelsDesc")]
    public int MinPixels { get; set; } = 50;

    [CvCategory("cv:CatThreshold", 1)]
    [CvName("cv:FillHoles")]
    [CvDesc("cv:FillHolesEdgeDesc")]
    public bool FillHoles { get; set; }

    [CvCategory("cv:CatThreshold", 1)]
    [CvName("cv:FillHolesAtBorder")]
    [CvDesc("cv:FillHolesAtBorderDesc")]
    public bool FillHolesAtBorder { get; set; }

    [CvCategory("cv:CatRegion", 2)]
    [CvName("cv:UseRegion")]
    [CvDesc("cv:UseRegionDesc")]
    public bool UseRegion { get; set; }

    // 영역 좌표 — 디스플레이 도형 드래그로 편집 (PG 미노출, JSON 영속은 유지). AngleDeg 는 중심 기준 회전 (회전 그립).
    [Browsable(false)] public double RegionX { get => _regionX; set => Notify(Put(ref _regionX, value)); }
    [Browsable(false)] public double RegionY { get => _regionY; set => Notify(Put(ref _regionY, value)); }
    [Browsable(false)] public double RegionW { get => _regionW; set => Notify(Put(ref _regionW, value)); }
    [Browsable(false)] public double RegionH { get => _regionH; set => Notify(Put(ref _regionH, value)); }
    [Browsable(false)] public double RegionAngleDeg { get => _regionAngleDeg; set => Notify(Put(ref _regionAngleDeg, value)); }

    /// <summary>편집 도형 — 회전 가능한 영역 사각(UseRegion 일 때만).</summary>
    public IReadOnlyList<CvEditShape>? CreateShapes(Action onEdited)
    {
        if (!UseRegion) return null;
        var rect = new CvEditRect { Color = ViOverlayColor.Yellow, Label = "Region", IsRotatable = true };
        rect.Set(RegionX, RegionY, RegionW, RegionH);
        rect.AngleDeg = RegionAngleDeg;
        rect.Changed += (_, _) =>
        {
            Notify(Put(ref _regionX, rect.X, nameof(RegionX)), Put(ref _regionY, rect.Y, nameof(RegionY)),
                Put(ref _regionW, rect.W, nameof(RegionW)), Put(ref _regionH, rect.H, nameof(RegionH)),
                Put(ref _regionAngleDeg, rect.AngleDeg, nameof(RegionAngleDeg)));
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
