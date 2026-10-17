using System.ComponentModel;
using System.Runtime.CompilerServices;
using CvInspect.Vision.Edit;
using CvInspect.Vision.Overlay;

namespace CvInspect.Vision.Opts;

/// <summary>
/// 허프서클 검출 파라미터 — 에지 그라디언트 투표 원 검출. 밝기 문턱 없이 동작해
/// 제품-배경 밝기가 모호해도 원 에지만 있으면 잡는다 (원형 대상 전제).
/// 기대 원은 자체 티칭 도형 — 탐색 반경 범위 = 기대 반경 ±허용% (다른 툴 의존 없이 단독 성립).
/// 좌표는 대상 이미지 픽셀 공간.
/// </summary>
public sealed class CvHoughCircleOpt : INotifyPropertyChanged, ICvShapeSource
{
    /// <summary>그립이 쓰는 기대 원(<c>Center*</c>·<see cref="Radius"/>)과 탐색 영역 좌표(<c>Search*</c>)만 통지한다 —
    /// 이 값을 보여 주는 편집기가 드래그를 따라오게 하려는 것이다. 나머지는 편집기에서 고치는 값이라 편집기가 이미 안다.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    private double _centerX = 100;
    private double _centerY = 100;
    private double _radius = 100;
    private double _searchX;
    private double _searchY;
    private double _searchW = 200;
    private double _searchH = 200;

    // 기대 원 — 디스플레이 도형(중심/반경 그립) 드래그로 편집 (PG 미노출).
    // 검출 자체는 영역 전체 탐색이라 중심은 시각 기준일 뿐, 반경이 탐색 범위(±허용%)의 기준.
    [Browsable(false)] public double CenterX { get => _centerX; set => Notify(Put(ref _centerX, value)); }
    [Browsable(false)] public double CenterY { get => _centerY; set => Notify(Put(ref _centerY, value)); }
    [Browsable(false)] public double Radius { get => _radius; set => Notify(Put(ref _radius, value)); }

    [CvCategory("cv:CatHough", 1)]
    [CvName("cv:CannyThreshold")]
    [CvDesc("cv:CannyThresholdDesc")]
    public double CannyThreshold { get; set; } = 100;

    [CvCategory("cv:CatHough", 1)]
    [CvName("cv:AccumThreshold")]
    [CvDesc("cv:AccumThresholdDesc")]
    public double AccumThreshold { get; set; } = 30;

    [CvCategory("cv:CatHough", 1)]
    [CvName("cv:RadiusTolPct")]
    [CvDesc("cv:RadiusTolPctDesc")]
    public double RadiusTolPct { get; set; } = 20;

    [CvCategory("cv:CatSearch", 2)]
    [CvName("cv:UseSearchRegion")]
    [CvDesc("cv:UseSearchRegionDesc")]
    public bool UseSearchRegion { get; set; }

    // 탐색 영역 좌표 — 디스플레이 도형 드래그로 편집 (PG 미노출).
    [Browsable(false)] public double SearchX { get => _searchX; set => Notify(Put(ref _searchX, value)); }
    [Browsable(false)] public double SearchY { get => _searchY; set => Notify(Put(ref _searchY, value)); }
    [Browsable(false)] public double SearchW { get => _searchW; set => Notify(Put(ref _searchW, value)); }
    [Browsable(false)] public double SearchH { get => _searchH; set => Notify(Put(ref _searchH, value)); }

    /// <summary>편집 도형 — 기대 원(탐색 반경 범위 ±허용% 의 기준; 검출은 영역 전체 탐색이라 중심은 시각 기준일 뿐)
    /// + 탐색 영역 사각(UseSearchRegion 일 때).</summary>
    public IReadOnlyList<CvEditShape>? CreateShapes(Action onEdited)
    {
        var shapes = new List<CvEditShape>();
        var expect = new CvEditArc { Color = ViOverlayColor.Yellow, Label = "Expect" };
        expect.Set(CenterX, CenterY, Radius, 0, 360);
        expect.Changed += (_, _) =>
        {
            Notify(Put(ref _centerX, expect.CenterX, nameof(CenterX)), Put(ref _centerY, expect.CenterY, nameof(CenterY)),
                Put(ref _radius, expect.Radius, nameof(Radius)));
            onEdited();
        };
        shapes.Add(expect);

        if (UseSearchRegion)
        {
            var search = new CvEditRect { Color = ViOverlayColor.Teal, Label = "Search" };
            search.Set(SearchX, SearchY, SearchW, SearchH);
            search.Changed += (_, _) =>
            {
                Notify(Put(ref _searchX, search.X, nameof(SearchX)), Put(ref _searchY, search.Y, nameof(SearchY)),
                    Put(ref _searchW, search.W, nameof(SearchW)), Put(ref _searchH, search.H, nameof(SearchH)));
                onEdited();
            };
            shapes.Add(search);
        }
        return shapes;
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
