using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using CvInspect.Vision.Edit;
using CvInspect.Vision.Overlay;

namespace CvInspect.Vision.Opts;

/// <summary>
/// 원환 충전율 파라미터 — 중심에서 <see cref="RMinPx"/>~<see cref="RMaxPx"/> 반경의 밴드에서
/// "채워진" 화소 비율을 잰다. 중심은 검사가 원 피팅·블랍으로 먼저 잡아 넘긴다.
/// 반경은 대상 이미지(전처리 후) 화소 단위다.
/// </summary>
public sealed class CvRingFillOpt : INotifyPropertyChanged, ICvShapeSource
{
    /// <summary>그립이 쓰는 반경 밴드(<see cref="RMinPx"/>·<see cref="RMaxPx"/>)만 통지한다 — 속성 편집기의 숫자 칸이
    /// 드래그를 따라오게 하려는 것이다. 나머지는 편집기에서 고치는 값이라 편집기가 이미 안다.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    private double _rMinPx = 100;
    private double _rMaxPx = 140;

    /// <summary>편집 도형 중심 공급 훅 — 선행 원·블랍 툴의 기대 중심(검사기가 배선). 없으면 (0,0).
    /// 런타임 측정은 검사가 그날 잡은 중심을 따로 넘기므로 이 값은 티칭 표시에만 쓰인다.</summary>
    [Browsable(false)]
    [JsonIgnore]
    public Func<(double X, double Y)>? CenterHook { get; set; }

    [CvCategory("cv:CatRegion", 1)]
    [CvName("cv:RingRMin")]
    [CvDesc("cv:RingRMinDesc")]
    public double RMinPx { get => _rMinPx; set => SetField(ref _rMinPx, value); }

    [CvCategory("cv:CatRegion", 1)]
    [CvName("cv:RingRMax")]
    [CvDesc("cv:RingRMaxDesc")]
    public double RMaxPx { get => _rMaxPx; set => SetField(ref _rMaxPx, value); }

    [CvCategory("cv:CatAccept", 2)]
    [CvName("cv:RingPolarity")]
    [CvDesc("cv:RingPolarityDesc")]
    public CvBlobPolarity Polarity { get; set; } = CvBlobPolarity.Dark;

    [CvCategory("cv:CatAccept", 2)]
    [CvName("cv:UseOtsu")]
    [CvDesc("cv:RingUseOtsuDesc")]
    public bool UseOtsu { get; set; }

    [CvCategory("cv:CatAccept", 2)]
    [CvName("cv:RingThreshold")]
    [CvDesc("cv:RingThresholdDesc")]
    public double Threshold { get; set; } = 128;

    /// <summary>편집 도형 — 충전율 밴드(min/max)만 그립 편집. 중심은 선행 원·블랍 툴 파생(이동 불가)이라 <see cref="CenterHook"/> 이 준다.</summary>
    public IReadOnlyList<CvEditShape>? CreateShapes(Action onEdited)
    {
        var (cx, cy) = CenterHook?.Invoke() ?? (0, 0);
        var ring = new CvEditRing { Color = ViOverlayColor.Teal, Label = "Ring" };
        ring.Set(cx, cy, RMinPx, RMaxPx);
        ring.Changed += (_, _) =>
        {
            RMinPx = ring.RMin;
            RMaxPx = ring.RMax;
            onEdited();
        };
        return [ring];
    }

    private void SetField(ref double field, double value, [CallerMemberName] string? name = null)
    {
        if (field.Equals(value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
