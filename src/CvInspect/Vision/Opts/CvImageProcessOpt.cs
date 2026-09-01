using System.ComponentModel;

namespace CvInspect.Vision.Opts;

// OpenCV 검사 툴 파라미터 POCO — JSON 영속 + PropertyGrid 직결(레벨 구분 없이 전체 노출).
// 좌표 파라미터는 전부 "축소(전처리 출력) 공간" 기준 — 오버레이/표시 시점에만 원본 공간으로 환산한다.
// 표시명/카테고리/설명은 Loc(cv 스코프) — 언어 파일 Assets\lang\cv.{culture}.json.

/// <summary>전처리 — 잘라내기 + 축소(서브샘플) + 미디언 정리. 이후 모든 툴 좌표는 그 출력 공간 기준.</summary>
public sealed class CvImageProcessOpt
{
    /// <summary>
    /// 볼 자리만 남기고 잘라낼지. 대상이 화면의 일부뿐이면 나머지는 탐색 비용이자 오검출 거리다.
    /// 잘라낸 만큼 좌표 원점이 옮겨가므로 오버레이 환산은 <c>CvImageOps.MapOf</c> 를 거쳐야 한다.
    /// </summary>
    [CvCategory("cv:CatCrop", 1)]
    [CvName("cv:UseCrop")]
    [CvDesc("cv:UseCropDesc")]
    public bool UseCrop { get; set; }

    // 잘라낼 자리 — 원본 이미지 공간이다. 다른 툴 좌표가 전부 축소 공간인 것과 다른데,
    // 잘라내기가 축소보다 먼저라 그 시점에는 아직 원본만 있기 때문이다.
    // 표시 위에서 끌어 편집하며, 전처리 툴을 고르면 화면도 원본으로 바뀌어 공간이 맞는다.
    [Browsable(false)] public double CropX { get; set; }
    [Browsable(false)] public double CropY { get; set; }
    [Browsable(false)] public double CropW { get; set; } = 640;
    [Browsable(false)] public double CropH { get; set; } = 480;

    [CvCategory("cv:CatSampling", 2)]
    [CvName("cv:SampleX")]
    [CvDesc("cv:SampleXDesc")]
    public int SampleX { get; set; } = 2;

    [CvCategory("cv:CatSampling", 2)]
    [CvName("cv:SampleY")]
    [CvDesc("cv:SampleYDesc")]
    public int SampleY { get; set; } = 2;

    [CvCategory("cv:CatFilter", 3)]
    [CvName("cv:MedianKernel")]
    [CvDesc("cv:MedianKernelDesc")]
    public int MedianKernel { get; set; } = 3;
}
