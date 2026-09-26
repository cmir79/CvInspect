using System.ComponentModel;
using System.Text.Json.Serialization;
using CvInspect.Vision.Edit;
using CvInspect.Vision.Overlay;

namespace CvInspect.Vision.Opts;

/// <summary>
/// 블랍 검출 파라미터 — 극성 이진화(Otsu/고정 문턱) → 연결요소 최대 면적 블랍.
/// 위치 선탐지·존재 확인 등 범용 (검사가 용도를 조립). 좌표는 대상 이미지 픽셀 공간.
/// ⚠ 존재 확인·면적 판정에는 자동 문턱을 끈다 — <see cref="UseOtsu"/> 참조(제품이 없는 영역에서 큰 블랍을 만든다).
/// </summary>
public sealed class CvBlobOpt : ICvShapeSource
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

    /// <summary>자동 문턱(Otsu). <b>영역에 제품과 배경 두 무리가 다 있을 때만 맞다.</b>
    ///
    /// Otsu 는 히스토그램을 언제나 둘로 가른다. 제품이 없어 배경뿐이거나 제품이 영역을 가득 채우면 한 무리의
    /// 잡음·조명 기울기를 반으로 갈라, 8-연결로 이어진 큰 블랍 하나를 만든다 — MinArea 로 걸러지지 않는다.
    /// 실측(<see cref="CvBlobFinder"/> 요약): 제품 없는 영역에서 영역의 42~54% 블랍, 영역을 덮은 제품은 면적 반 이하.
    /// 그래서 <b>있음·없음이나 면적 하한으로 판정하는 검사는 끄고 고정 문턱(<see cref="Threshold"/>)을 쓴다</b> —
    /// 자동이 틀리는 방향이 "없는 것을 있다고" 쪽이라 그 검사의 fail-safe 와 반대다.
    /// 기본값은 켜짐이다(종전 판 그대로). 위치만 잡는 용도라면 조명 변동에 유연하다.</summary>
    [CvCategory("cv:CatThreshold", 2)]
    [CvName("cv:UseOtsu")]
    [CvDesc("cv:BlobUseOtsuDesc")]
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

    /// <summary>편집 도형 — 탐색 영역 사각(UseSearchRegion 일 때만). 티칭 도형은 Teal — 검사 결과 기하(Cyan)와 색으로 구분.</summary>
    public IReadOnlyList<CvEditShape>? CreateShapes(Action onEdited)
    {
        if (!UseSearchRegion) return null;
        var search = new CvEditRect { Color = ViOverlayColor.Teal, Label = "Search" };
        search.Set(SearchX, SearchY, SearchW, SearchH);
        search.Changed += (_, _) =>
        {
            SearchX = search.X;
            SearchY = search.Y;
            SearchW = search.W;
            SearchH = search.H;
            onEdited();
        };
        return [search];
    }
}

/// <summary>블랍 극성 — 블랍이 배경 대비 밝은지(Bright) 어두운지(Dark).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CvBlobPolarity
{
    Bright,
    Dark,
}
