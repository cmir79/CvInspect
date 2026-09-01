using System.ComponentModel;
using System.Text.Json.Serialization;

namespace CvInspect.Vision.Opts;

/// <summary>
/// 원환 충전율 파라미터 — 중심에서 <see cref="RMinPx"/>~<see cref="RMaxPx"/> 반경의 밴드에서
/// "채워진" 화소 비율을 잰다. 중심은 검사가 원 피팅·블랍으로 먼저 잡아 넘긴다.
/// 반경은 대상 이미지(전처리 후) 화소 단위다.
/// </summary>
public sealed class CvRingFillOpt
{
    /// <summary>편집 도형 중심 공급 훅 — 선행 원·블랍 툴의 기대 중심(검사기가 배선). 없으면 (0,0).
    /// 런타임 측정은 검사가 그날 잡은 중심을 따로 넘기므로 이 값은 티칭 표시에만 쓰인다.</summary>
    [Browsable(false)]
    [JsonIgnore]
    public Func<(double X, double Y)>? CenterHook { get; set; }

    [CvCategory("cv:CatRegion", 1)]
    [CvName("cv:RingRMin")]
    [CvDesc("cv:RingRMinDesc")]
    public double RMinPx { get; set; } = 100;

    [CvCategory("cv:CatRegion", 1)]
    [CvName("cv:RingRMax")]
    [CvDesc("cv:RingRMaxDesc")]
    public double RMaxPx { get; set; } = 140;

    [CvCategory("cv:CatAccept", 2)]
    [CvName("cv:RingPolarity")]
    [CvDesc("cv:RingPolarityDesc")]
    public CvBlobPolarity Polarity { get; set; } = CvBlobPolarity.Dark;

    [CvCategory("cv:CatAccept", 2)]
    [CvName("cv:UseOtsu")]
    [CvDesc("cv:UseOtsuDesc")]
    public bool UseOtsu { get; set; }

    [CvCategory("cv:CatAccept", 2)]
    [CvName("cv:RingThreshold")]
    [CvDesc("cv:RingThresholdDesc")]
    public double Threshold { get; set; } = 128;
}
