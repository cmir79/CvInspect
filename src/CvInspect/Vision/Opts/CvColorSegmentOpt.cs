using System.ComponentModel;
using System.Text.Json.Serialization;
using CvInspect.Vision.Edit;
using CvInspect.Vision.Overlay;

namespace CvInspect.Vision.Opts;

/// <summary>
/// 컬러 세그먼트 — 등록한 HSV 색 대역에 드는 픽셀을 뽑는다 (등록 색 면적 비율 판정용).
/// 색 등록은 '색 학습' 버튼 — 영역 안 픽셀의 HSV 분포에서 대역을 자동 산출하고, 세부는 PG 로 손질한다.
///
/// 판정/표본 영역은 공용 영역(<see cref="CvRegionOpt"/>) — 모양(사각/링)은 <see cref="Shape"/> 로
/// 고른다. 링은 와셔류처럼 고리형 대상용이다: 구멍 가운데의 볼트 머리(대상과 같은 밝은 금속)를 세면
/// 대상이 없어도 비율이 부풀어 오판하므로 안쪽 반경으로 명시적으로 배제한다.
/// 검사를 모양별로 따로 만들지 않는다.
///
/// ⚠ 좌표 공간 주의: 이 툴의 영역은 <b>원본(컬러) 이미지 공간</b>이다 — 컬러 픽셀은 전처리(축소)를
/// 거치지 않고 원본에서 직접 읽기 때문에, 축소 공간을 쓰는 다른 툴들과 다르다
/// (검사 구현이 이 툴의 프레임 슬롯에 원본을 넣어 화면·도형 공간을 맞춘다).
///
/// H 는 OpenCV 규약(0~179, 1칸=2°). 무채색(금속·회색) 대상은 H 가 무의미하므로 색 학습이
/// 채도 상한이 낮으면 HueTol 을 전 범위로 열어 S/V 대역만으로 가른다.
/// </summary>
public sealed class CvColorSegmentOpt : INotifyPropertyChanged, ICvShapeSource
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>판정/표본 영역 — 기하는 도형 드래그로 편집 (PG 미노출), 모양만 아래 Shape 로 노출.</summary>
    [Browsable(false)]
    public CvRegionOpt Region { get; set; } = new();

    [CvCategory("cv:CatRegion")]
    [CvName("cv:RegionShape")]
    [CvDesc("cv:RegionShapeDesc")]
    [JsonIgnore]   // Region.Shape 로 영속 — 이 프로퍼티는 PG 노출용 전달자
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CvRegionShape Shape
    {
        get => Region.Shape;
        set => Region.Shape = value;
    }

    [CvCategory("cv:CatTrain", 1)]
    [CvName("cv:TrainColor")]
    [CvDesc("cv:TrainColorDesc")]
    [JsonIgnore]
    public Action Train
    {
        get => () =>
        {
            // 이 예외 문구는 로그가 아니라 화면으로 간다 — 실행 편집기가 잡아 호스트에 넘기고
            // 호스트가 제 방식으로 알린다. 그래서 영어 고정이 아니라 현재 언어로 번역해 담는다.
            // 패턴용 TrainFailed 를 재사용하지 않는다 — "패턴 학습 실패" 안내는 색 학습 실패의 원인
            // (영역이 이미지 밖·컬러 아님 등) 진단을 오도한다.
            if (TrainHook is null || !TrainHook())
                throw new InvalidOperationException(CvLoc.T("cv:TrainColorFailed"));
        };
    }

    private bool _trained;

    [CvCategory("cv:CatTrain", 1)]
    [CvName("cv:ColorTrained")]
    [CvDesc("cv:ColorTrainedDesc")]
    [ReadOnly(true)]
    public bool Trained
    {
        get => _trained;
        set
        {
            if (_trained == value) return;
            _trained = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Trained)));
        }
    }

    // === 색 대역 (HSV) — 색 학습이 채우고, 손질은 PG 로. 코드 경로 갱신이라 INPC 통지 대상 ===
    private double _hueCenter;
    private double _hueTol = 15;
    private int _satMin = 60;
    private int _satMax = 255;
    private int _valMin = 60;
    private int _valMax = 255;

    [CvCategory("cv:CatColorBand", 2)]
    [CvName("cv:HueCenter")]
    [CvDesc("cv:HueCenterDesc")]
    public double HueCenter { get => _hueCenter; set => SetField(ref _hueCenter, value, nameof(HueCenter)); }

    [CvCategory("cv:CatColorBand", 2)]
    [CvName("cv:HueTol")]
    [CvDesc("cv:HueTolDesc")]
    public double HueTol { get => _hueTol; set => SetField(ref _hueTol, value, nameof(HueTol)); }

    [CvCategory("cv:CatColorBand", 2)]
    [CvName("cv:SatMin")]
    [CvDesc("cv:SatMinDesc")]
    public int SatMin { get => _satMin; set => SetField(ref _satMin, value, nameof(SatMin)); }

    [CvCategory("cv:CatColorBand", 2)]
    [CvName("cv:SatMax")]
    [CvDesc("cv:SatMaxDesc")]
    public int SatMax { get => _satMax; set => SetField(ref _satMax, value, nameof(SatMax)); }

    [CvCategory("cv:CatColorBand", 2)]
    [CvName("cv:ValMin")]
    [CvDesc("cv:ValMinDesc")]
    public int ValMin { get => _valMin; set => SetField(ref _valMin, value, nameof(ValMin)); }

    [CvCategory("cv:CatColorBand", 2)]
    [CvName("cv:ValMax")]
    [CvDesc("cv:ValMaxDesc")]
    public int ValMax { get => _valMax; set => SetField(ref _valMax, value, nameof(ValMax)); }

    [CvCategory("cv:CatCleanup", 3)]
    [CvName("cv:MorphOpen")]
    [CvDesc("cv:MorphOpenDesc")]
    public int MorphOpenKernel { get; set; } = 3;

    /// <summary>Train 버튼 → 검사 구현의 색 학습 루틴 연결 훅 (검사 ctor 가 배선).</summary>
    [Browsable(false)]
    [JsonIgnore]
    public Func<bool>? TrainHook { get; set; }

    private void SetField<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>편집 도형 — 판정/표본 겸용 영역(<see cref="Region"/>) 그대로. 이 툴만 원본(컬러) 이미지 공간이다 — 컬러는 전처리 축소를 안 거친다.</summary>
    public IReadOnlyList<CvEditShape>? CreateShapes(Action onEdited) => Region.CreateShapes(onEdited);
}
