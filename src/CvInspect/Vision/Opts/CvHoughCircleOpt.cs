using System.ComponentModel;

namespace CvInspect.Vision.Opts;

/// <summary>
/// 허프서클 검출 파라미터 — 에지 그라디언트 투표 원 검출. 밝기 문턱 없이 동작해
/// 제품-배경 밝기가 모호해도 원 에지만 있으면 잡는다 (원형 대상 전제).
/// 기대 원은 자체 티칭 도형 — 탐색 반경 범위 = 기대 반경 ±허용% (다른 툴 의존 없이 단독 성립).
/// 좌표는 대상 이미지 픽셀 공간.
/// </summary>
public sealed class CvHoughCircleOpt
{
    // 기대 원 — 디스플레이 도형(중심/반경 그립) 드래그로 편집 (PG 미노출 — 정적 스냅샷이라 실시간 미반영).
    // 검출 자체는 영역 전체 탐색이라 중심은 시각 기준일 뿐, 반경이 탐색 범위(±허용%)의 기준.
    [Browsable(false)] public double CenterX { get; set; } = 100;
    [Browsable(false)] public double CenterY { get; set; } = 100;
    [Browsable(false)] public double Radius { get; set; } = 100;

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

    // 탐색 영역 좌표 — 디스플레이 도형 드래그로 편집 (PG 미노출 — 정적 스냅샷이라 실시간 미반영).
    [Browsable(false)] public double SearchX { get; set; }
    [Browsable(false)] public double SearchY { get; set; }
    [Browsable(false)] public double SearchW { get; set; } = 200;
    [Browsable(false)] public double SearchH { get; set; } = 200;
}
