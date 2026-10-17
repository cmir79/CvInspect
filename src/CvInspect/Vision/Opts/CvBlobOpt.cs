using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using CvInspect.Vision.Edit;
using CvInspect.Vision.Overlay;

namespace CvInspect.Vision.Opts;

/// <summary>
/// 블랍 검출 파라미터 — 극성 이진화(Otsu/고정 문턱) → 연결요소 최대 면적 블랍.
/// 위치 선탐지·존재 확인 등 범용 (검사가 용도를 조립). 좌표는 대상 이미지 픽셀 공간.
/// ⚠ 존재 확인·면적 판정에는 자동 문턱을 끈다 — <see cref="UseOtsu"/> 참조(제품이 없는 영역에서 큰 블랍을 만든다).
/// </summary>
public sealed class CvBlobOpt : INotifyPropertyChanged, ICvShapeSource
{
    /// <summary>그립이 쓰는 탐색 영역 좌표(<c>Search*</c>)만 통지한다 — 이 값을 보여 주는 편집기가 드래그를 따라오게
    /// 하려는 것이다. 나머지는 편집기에서 고치는 값이라 편집기가 이미 안다.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    private double _searchX;
    private double _searchY;
    private double _searchW = 200;
    private double _searchH = 200;

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
    /// 실측(<see cref="CvBlobFinder"/> 요약): 제품 없는 영역에서 영역의 42~54% 블랍, 영역을 덮은 제품은 면적이 절반 안팎(42~54%).
    /// 그래서 <b>있음·없음이나 면적 하한으로 판정하는 검사는 끄고 고정 문턱(<see cref="Threshold"/>)을 쓴다</b> —
    /// 자동이 틀리는 방향이 "없는 것을 있다고" 쪽이라 그 검사의 fail-safe 와 반대다.
    /// <b>기본값은 꺼짐이다</b>(0.29.0 부터 — 그 전에는 켜짐). 블랍의 주된 쓰임이 존재 확인이라 기본값이 안전한 쪽이어야 한다.
    /// 위치만 잡는 용도라면 켜는 것이 조명 변동에 유연하다 — 그때는 명시적으로 켠다.
    /// ⚠ <b>기본값은 저장된 레시피를 바꾸지 않는다.</b> 모든 속성을 적는 저장(System.Text.Json 기본 설정 등)이면 이 값도 적히므로, 0.29.0 전에 저장한 레시피는
    /// 적힌 값(대개 켜짐)을 그대로 쓴다 — 존재·면적 판정 레시피는 직접 끄고 <see cref="Threshold"/> 를 잡아야 한다.
    /// 기본값이 닿는 것은 저장 파일 없이 코드로 만든 옵션과, 이 키가 빠진 파일(꺼짐으로 읽힘)뿐이다.</summary>
    [CvCategory("cv:CatThreshold", 2)]
    [CvName("cv:UseOtsu")]
    [CvDesc("cv:BlobUseOtsuDesc")]
    public bool UseOtsu { get; set; }

    /// <summary>고정 문턱(0~255) — <see cref="UseOtsu"/> 가 꺼졌을 때 쓴다. Bright 는 이 값보다 큰 화소, Dark 는 이 값 이하인
    /// 화소가 제품이다(<c>Cv2.Threshold</c> 의 Binary/BinaryInv). <b>128 은 자리값이지 맞춘 값이 아니다</b> — 제품과 배경
    /// 밝기 사이로 잡고, 제품이 없는 샘플에서 블랍이 안 나오는지 확인한다.</summary>
    [CvCategory("cv:CatThreshold", 2)]
    [CvName("cv:BlobThreshold")]
    [CvDesc("cv:BlobThresholdDesc")]
    public double Threshold { get; set; } = 128;

    [CvCategory("cv:CatSearch", 3)]
    [CvName("cv:UseSearchRegion")]
    [CvDesc("cv:UseSearchRegionDesc")]
    public bool UseSearchRegion { get; set; }

    // 탐색 영역 좌표 — 디스플레이 도형 드래그로 편집 (PG 미노출).
    [Browsable(false)] public double SearchX { get => _searchX; set => Notify(Put(ref _searchX, value)); }
    [Browsable(false)] public double SearchY { get => _searchY; set => Notify(Put(ref _searchY, value)); }
    [Browsable(false)] public double SearchW { get => _searchW; set => Notify(Put(ref _searchW, value)); }
    [Browsable(false)] public double SearchH { get => _searchH; set => Notify(Put(ref _searchH, value)); }

    /// <summary>편집 도형 — 탐색 영역 사각(UseSearchRegion 일 때만). 티칭 도형은 Teal — 검사 결과 기하(Cyan)와 색으로 구분.</summary>
    public IReadOnlyList<CvEditShape>? CreateShapes(Action onEdited)
    {
        if (!UseSearchRegion) return null;
        var search = new CvEditRect { Color = ViOverlayColor.Teal, Label = "Search" };
        search.Set(SearchX, SearchY, SearchW, SearchH);
        search.Changed += (_, _) =>
        {
            Notify(Put(ref _searchX, search.X, nameof(SearchX)), Put(ref _searchY, search.Y, nameof(SearchY)),
                Put(ref _searchW, search.W, nameof(SearchW)), Put(ref _searchH, search.H, nameof(SearchH)));
            onEdited();
        };
        return [search];
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

/// <summary>블랍 극성 — 블랍이 배경 대비 밝은지(Bright) 어두운지(Dark).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CvBlobPolarity
{
    Bright,
    Dark,
}
