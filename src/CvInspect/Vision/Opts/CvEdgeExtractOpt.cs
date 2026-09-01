namespace CvInspect.Vision.Opts;

/// <summary>에지 강조 — Sobel 크기(magnitude) 이미지 생성.</summary>
public sealed class CvEdgeExtractOpt
{
    [CvCategory("cv:CatEdge", 1)]
    [CvName("cv:MagnitudeScale")]
    [CvDesc("cv:MagnitudeScaleDesc")]
    public double MagnitudeScale { get; set; } = 0.25;
}
