using System.ComponentModel;
using System.Text.Json.Serialization;

namespace CvInspect.Vision.Opts;

// OpenCV 검사 툴 파라미터 POCO — JSON 영속 + PropertyGrid 직결(레벨 구분 없이 전체 노출).
// 좌표 파라미터는 전부 "축소(전처리 출력) 공간" 기준 — 오버레이/표시 시점에만 원본 공간으로 환산한다.
// 표시명/카테고리/설명은 Loc(cv 스코프) — 언어 파일 Assets\lang\cv.{culture}.json.

/// <summary>
/// 전처리 — 축소(서브샘플) + 미디언 정리. 이후 모든 툴 좌표는 그 출력 공간 기준.
/// 자르기는 하지 않는다 — 0.27 부터 <see cref="CvCropOpt"/> 가 따로 맡는다(툴이 하나 늘더라도 자르기와
/// 화질 정리는 다른 일이라 나눴다). 자른 이미지를 입력으로 넘기면 된다.
/// </summary>
public sealed class CvImageProcessOpt
{
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

    // ── 옛 자르기 필드 — 0.27 에서 CvCropOpt 로 옮겼다 ──
    // 지우지 않는 이유: 0.26 까지의 저장본에 이 키들이 있다. 지우면 역직렬화가 모르는 키로 건너뛰어, 잘라서 티칭한
    // 레시피가 예외도 경고도 없이 전체 이미지에서 돈다 — 이후 툴 좌표가 전부 잘린 공간 기준이라 자르기 원점만큼
    // 어긋난 자리에서 판정하고, 그 판정은 정상처럼 나간다. 그래서 읽기는 받고, 켜진 채로 Preprocess 에 오면 던지고,
    // 옮기는 길은 CvCropOpt.FromLegacy 하나로 둔다.
    // 저장: 켜져 있으면 그대로 실린다(이관 전에 값을 잃지 않게). 꺼져 있으면 게터가 기본값을 내 새 저장에서 빠진다 —
    // 쓰지 않던 옛 자리를 파일마다 끌고 다니지 않는다.
    private const string LegacyCropNote = "Cropping moved to CvCropOpt in 0.27 — move a saved crop with CvCropOpt.FromLegacy.";

    private bool _useCrop;
    private double _cropX, _cropY, _cropW, _cropH;

    [Obsolete(LegacyCropNote)]
    [Browsable(false)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UseCrop { get => _useCrop; set => _useCrop = value; }

    [Obsolete(LegacyCropNote)]
    [Browsable(false)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double CropX { get => _useCrop ? _cropX : 0; set => _cropX = value; }

    [Obsolete(LegacyCropNote)]
    [Browsable(false)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double CropY { get => _useCrop ? _cropY : 0; set => _cropY = value; }

    [Obsolete(LegacyCropNote)]
    [Browsable(false)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double CropW { get => _useCrop ? _cropW : 0; set => _cropW = value; }

    [Obsolete(LegacyCropNote)]
    [Browsable(false)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double CropH { get => _useCrop ? _cropH : 0; set => _cropH = value; }

    /// <summary>0.26 까지의 저장본에서 켜진 크롭을 읽어 왔고 아직 옮기지 않았는지 — 그렇다면 <c>Preprocess</c> 가 던진다.
    /// 옮기는 것은 <see cref="CvCropOpt.FromLegacy"/>.</summary>
    [JsonIgnore]
    [Browsable(false)]
    public bool HasLegacyCrop => _useCrop;

    internal (double X, double Y, double W, double H)? LegacyCrop
        => _useCrop ? (_cropX, _cropY, _cropW, _cropH) : null;

    internal void ClearLegacyCrop()
    {
        _useCrop = false;
        _cropX = _cropY = _cropW = _cropH = 0;
    }
}
