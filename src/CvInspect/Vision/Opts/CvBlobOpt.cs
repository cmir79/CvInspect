using System.ComponentModel;
using System.Text.Json.Serialization;

namespace CvInspect.Vision.Opts;

/// <summary>
/// 블랍 검출 파라미터 — 극성 이진화(Otsu/고정 문턱) → 연결요소 최대 면적 블랍.
/// 위치 선탐지·존재 확인 등 범용 (검사가 용도를 조립). 좌표는 대상 이미지 픽셀 공간.
/// </summary>
public sealed class CvBlobOpt
{
    [CvCategory("cv:CatBlob", 1)]
    [CvName("cv:BlobPolarity")]
    [CvDesc("cv:BlobPolarityDesc")]
    public CvBlobPolarity Polarity { get; set; } = CvBlobPolarity.Bright;

    [CvCategory("cv:CatBlob", 1)]
    [CvName("cv:MinPixels")]
    [CvDesc("cv:MinPixelsDesc")]
    public int MinArea { get; set; } = 200;

    /// <summary>
    /// 안에 갇힌 구멍을 블랍 면적에 포함할지. 사방이 막힌 구멍만 메워지고, 한 곳이라도 바깥으로
    /// 트여 있으면 그대로 배경이다. 부품 가운데가 뚫려 있어도 외형 면적을 재고 싶을 때 켠다.
    /// </summary>
    [CvCategory("cv:CatBlob", 1)]
    [CvName("cv:FillHoles")]
    [CvDesc("cv:FillHolesDesc")]
    public bool FillHoles { get; set; }

    [CvCategory("cv:CatThreshold", 2)]
    [CvName("cv:UseOtsu")]
    [CvDesc("cv:UseOtsuDesc")]
    public bool UseOtsu { get; set; } = true;

    [CvCategory("cv:CatThreshold", 2)]
    [CvName("cv:BlobThreshold")]
    [CvDesc("cv:BlobThresholdDesc")]
    public double Threshold { get; set; } = 128;

    [CvCategory("cv:CatSearch", 3)]
    [CvName("cv:UseSearchRegion")]
    [CvDesc("cv:UseSearchRegionDesc")]
    public bool UseSearchRegion { get; set; }

    // 탐색 영역 좌표 — 디스플레이 도형 드래그로 편집 (PG 미노출 — 정적 스냅샷이라 실시간 미반영).
    [Browsable(false)] public double SearchX { get; set; }
    [Browsable(false)] public double SearchY { get; set; }
    [Browsable(false)] public double SearchW { get; set; } = 200;
    [Browsable(false)] public double SearchH { get; set; } = 200;
}

/// <summary>블랍 극성 — 블랍이 배경 대비 밝은지(Bright) 어두운지(Dark).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CvBlobPolarity
{
    Bright,
    Dark,
}
