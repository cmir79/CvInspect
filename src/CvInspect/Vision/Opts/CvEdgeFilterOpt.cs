using System.ComponentModel;

namespace CvInspect.Vision.Opts;

/// <summary>에지 필터 — 이진화 + 연결요소 분석으로 유효 에지 블랍만 남긴다 (값은 에지 크기 유지).</summary>
public sealed class CvEdgeFilterOpt
{
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

    // 영역 좌표 — 디스플레이 도형 드래그로 편집. PG 는 정적 스냅샷이라 드래그를 실시간 반영하지
    // 못해 미노출 (JSON 영속은 유지). AngleDeg 는 중심 기준 회전 (회전 그립).
    [Browsable(false)] public double RegionX { get; set; }
    [Browsable(false)] public double RegionY { get; set; }
    [Browsable(false)] public double RegionW { get; set; } = 100;
    [Browsable(false)] public double RegionH { get; set; } = 100;
    [Browsable(false)] public double RegionAngleDeg { get; set; }
}
