using System.ComponentModel;
using System.Text.Json.Serialization;
using CvInspect.Vision.Edit;
using CvInspect.Vision.Overlay;

namespace CvInspect.Vision.Opts;

/// <summary>패턴 학습영역의 모양.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CvTrainShape
{
    /// <summary>회전 사각 — 기본.</summary>
    Rect,

    /// <summary>원 — 원형 대상(볼트 머리·구멍 주변 등)용. 모서리에 배경이 섞이지 않고,
    /// 마스크가 회전 불변이라 회전 탐색과 궁합이 좋다.</summary>
    Circle,
}

/// <summary>
/// 패턴 — 각도 스텝 회전 템플릿 매칭 (탐색 영역 추종 전용, 회전은 측정각에 미합산).
/// 학습: 학습 이미지 체인의 해당 슬롯에서 학습영역(사각 또는 원)을 잘라 템플릿으로 저장.
/// 탐색: 코스(축소·굵은 스텝) → 파인(원본·가는 스텝) 2 단계 + 파라볼라 각도 보간.
/// 매칭 코어가 마스크 기반이라 원형 학습영역은 원 마스크로 그대로 지원된다.
/// INotifyPropertyChanged 는 PropertyGrid 표시 갱신용 — Trained 처럼 코드 경로(학습 버튼/로드)로
/// 바뀌는 표시 값만 통지한다 (드래그 편집 좌표는 PG 미노출이라 통지 불요).
/// </summary>
public sealed class CvPatternOpt : INotifyPropertyChanged, ICvShapeSource
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>학습영역 모양 — 바꾸면 편집 도형이 바뀌고, 다음 '학습'부터 그 모양으로 템플릿을 뜬다.
    /// 이미 학습된 템플릿의 해석은 학습 시점 모양(TrainedShape)을 따르므로 토글만으로는 안 바뀐다.</summary>
    [CvCategory("cv:CatTrain", 1)]
    [CvName("cv:TrainShape")]
    [CvDesc("cv:TrainShapeDesc")]
    public CvTrainShape TrainShape { get; set; } = CvTrainShape.Rect;

    // 학습/탐색 영역 좌표 — 디스플레이 도형 드래그로 편집 (PG 미노출 — 정적 스냅샷이라 실시간 미반영).
    // TrainAngleDeg 는 학습 영역 회전 (대각 피처를 딱 맞게 감싸는 용도 — 회전 그립).
    [Browsable(false)] public double TrainX { get; set; }
    [Browsable(false)] public double TrainY { get; set; }
    [Browsable(false)] public double TrainW { get; set; } = 100;
    [Browsable(false)] public double TrainH { get; set; } = 100;
    [Browsable(false)] public double TrainAngleDeg { get; set; }

    // 원형 학습영역 (TrainShape=Circle) — 중심/반경, 도형 드래그로 편집.
    [Browsable(false)] public double TrainCircleX { get; set; } = 200;
    [Browsable(false)] public double TrainCircleY { get; set; } = 200;
    [Browsable(false)] public double TrainCircleR { get; set; } = 60;

    [CvCategory("cv:CatTrain", 1)]
    [CvName("cv:Train")]
    [CvDesc("cv:TrainDesc")]
    [JsonIgnore]
    public Action Train
    {
        get => () =>
        {
            // 이 예외 문구는 로그가 아니라 화면으로 간다 — 실행 편집기가 잡아 호스트에 넘기고
            // 호스트가 제 방식으로 알린다. 그래서 영어 고정이 아니라 현재 언어로 번역해 담는다.
            if (TrainHook is null || !TrainHook())
                throw new InvalidOperationException(CvLoc.T("cv:TrainFailed"));
        };
    }

    private bool _trained;

    [CvCategory("cv:CatTrain", 1)]
    [CvName("cv:Trained")]
    [CvDesc("cv:TrainedDesc")]
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

    [CvCategory("cv:CatSearch", 2)]
    [CvName("cv:UseSearchRegion")]
    [CvDesc("cv:UseSearchRegionDesc")]
    public bool UseSearchRegion { get; set; }

    [Browsable(false)] public double SearchX { get; set; }
    [Browsable(false)] public double SearchY { get; set; }
    [Browsable(false)] public double SearchW { get; set; } = 200;
    [Browsable(false)] public double SearchH { get; set; } = 200;

    // === 존 설정 (탐색 공통) — 코스 축소 배율 + 앵글/스케일 탐색 유무 ===
    [CvCategory("cv:CatZone", 3)]
    [CvName("cv:CoarseScale")]
    [CvDesc("cv:CoarseScaleDesc")]
    public double CoarseScale { get; set; } = 0.5;

    [CvCategory("cv:CatZone", 3)]
    [CvName("cv:UseAngleSearch")]
    [CvDesc("cv:UseAngleSearchDesc")]
    public bool UseAngleSearch { get; set; } = true;

    [CvCategory("cv:CatZone", 3)]
    [CvName("cv:UseScaleSearch")]
    [CvDesc("cv:UseScaleSearchDesc")]
    public bool UseScaleSearch { get; set; }

    // === 앵글 — 회전 탐색 존/스텝 (UseAngleSearch 시 유효) ===
    [CvCategory("cv:CatAngle", 4)]
    [CvName("cv:AngleZoneDeg")]
    [CvDesc("cv:AngleZoneDegDesc")]
    public double AngleZoneDeg { get; set; } = 45;

    [CvCategory("cv:CatAngle", 4)]
    [CvName("cv:CoarseStepDeg")]
    [CvDesc("cv:CoarseStepDegDesc")]
    public double CoarseStepDeg { get; set; } = 4.0;

    [CvCategory("cv:CatAngle", 4)]
    [CvName("cv:FineStepDeg")]
    [CvDesc("cv:FineStepDegDesc")]
    public double FineStepDeg { get; set; } = 1.0;

    // === 스케일 — 크기 탐색 존/스텝 (UseScaleSearch 시 유효) ===
    [CvCategory("cv:CatScale", 5)]
    [CvName("cv:ScaleZonePct")]
    [CvDesc("cv:ScaleZonePctDesc")]
    public double ScaleZonePct { get; set; } = 10;

    [CvCategory("cv:CatScale", 5)]
    [CvName("cv:ScaleStepPct")]
    [CvDesc("cv:ScaleStepPctDesc")]
    public double ScaleStepPct { get; set; } = 2.5;

    [CvCategory("cv:CatAccept", 6)]
    [CvName("cv:AcceptScore")]
    [CvDesc("cv:AcceptScoreDesc")]
    public double AcceptScore { get; set; } = 0.8;

    // === 다중 검출 — 겹치지 않는 후보를 여러 개 뽑아 호출측이 고르게 하는 설정 ===
    [CvCategory("cv:CatMulti", 7)]
    [CvName("cv:MaxCount")]
    [CvDesc("cv:MaxCountDesc")]
    public int MaxCount { get; set; } = 1;

    [CvCategory("cv:CatMulti", 7)]
    [CvName("cv:MinSepPct")]
    [CvDesc("cv:MinSepPctDesc")]
    public double MinSepPct { get; set; } = 50;

    /// <summary>학습 시점 원점(TrainRect 중심, 축소 공간) — 포즈 산출 기준. 학습 후 TrainRect 를 옮겨도 유지.</summary>
    [Browsable(false)] public double TrainedOriginX { get; set; }
    [Browsable(false)] public double TrainedOriginY { get; set; }

    /// <summary>학습 시점 영역 회전각 — 템플릿이 이 각도로 추출되므로 포즈 각도에서 차감(학습각 보정).</summary>
    [Browsable(false)] public double TrainedAngleDeg { get; set; }

    /// <summary>학습 시점 영역 모양 — 매칭 마스크(사각=전면/원=내접원)는 이걸 따른다.
    /// 학습 후 TrainShape 토글이 기존 템플릿의 해석을 바꾸지 않게 분리해 둔다.</summary>
    [Browsable(false)] public CvTrainShape TrainedShape { get; set; }

    /// <summary>가로 순환 주기(픽셀). 0 = 끔. <b>티칭 값이 아니라 호스트가 매 Run 대입하는 런타임 값</b>이라
    /// PG 에 노출하지 않는다 — 그 프레임의 원 반경에 따라 달라진다.
    ///
    /// 링을 극좌표로 편 <b>언랩 띠</b>는 가로가 360° 주기다. 캔버스가 띠보다 넓어 남는 자리를 가장자리
    /// 복제로 채우면 <b>없는 내용</b>이 들어가고, 복제 영역은 x 로 상수라 행 프로파일이 지배적인 띠 템플릿과
    /// 높은 상관을 받아 <b>망가진 진짜 대상을 이긴다</b>(발견 여부가 곧 판정인 툴에서는 NG 미검). 감아 채우면
    /// 그 자리에 <b>진짜 반대편 내용</b>이 들어와 그 병리가 사라지고, 360° 전체를 담은 템플릿은 순환 비교가
    /// 되어 회전 추종도 제대로 성립한다(복제로는 가짜로 성립했다).
    ///
    /// <b>띠 폭이 아니라 주기</b>를 넣어야 한다 — 언랩은 보통 <c>w360 + overlap</c> 으로 저장되므로 둘이 다르다
    /// (실례: w360 694 · overlap 58 · 띠 폭 752 — 752 로 감으면 58px 틀린다). 주기가 0 이하거나 소스 폭보다
    /// 크면 끔으로 친다. 켜면 발견 X 좌표를 <c>[0, 주기)</c> 로 접어 돌려준다(캔버스가 여러 바퀴를 담을 수 있다).
    ///
    /// <b>순환을 켜도 못 잡는 축이 하나 남는다</b> — 템플릿이 대상의 <b>전 주기</b>를 덮고 탐색이 전 범위면
    /// (<see cref="UseSearchRegion"/> 끔), <b>주기적 결함은 회전과 구분되지 않는다.</b> 반 바퀴가 들린 것과
    /// 원판을 그만큼 돌린 것이 상관값으로 같기 때문이다. 순환이 그 구분을 없앤 것이 아니라, 종전에는
    /// 가장자리 복제와 창 에너지 게이트가 회전 추종을 사실상 막고 있어서 <b>우연히</b> 잡히던 것이다
    /// (실기 실측: 순환을 켜면 다른 결함 장면들은 복제 미접촉 통제군 값으로 정확히 내려오는데,
    /// 반 바퀴 결함만 안 내려온다 — 그 자리의 고득점은 가짜 내용이 아니라 정당한 회전 추종에서 온다).
    /// 판별식은 <b>템플릿 폭 ≈ 주기</b> 이고 영역 미사용. 그 조합이면 탐색 범위를 실제 회전 허용치만큼만
    /// 열거나, 전 주기가 아닌 부분 구간 템플릿으로 학습하는 것이 답이다 — 레시피 쪽 축이다.</summary>
    [Browsable(false)] public double WrapPeriodX { get; set; }

    /// <summary>학습된 템플릿 PNG — Save/Load 가 Template.png 로 별도 영속 (JSON 제외).</summary>
    [Browsable(false)]
    [JsonIgnore]
    public byte[]? TemplatePng { get; set; }

    /// <summary>Train 버튼 → 검사 구현의 학습 루틴 연결 훅 (검사 ctor 가 배선).</summary>
    [Browsable(false)]
    [JsonIgnore]
    public Func<bool>? TrainHook { get; set; }

    /// <summary>편집 도형 — 학습 영역(사각은 회전 그립, 원은 반경 그립) + 탐색 영역(UseSearchRegion 일 때).
    /// 탐색 영역은 학습 영역의 회전을 따라간다 — 특징과 그 주변은 같은 국소 좌표계에 있어 각도를 따로 둘 이유가 없고,
    /// 따로 두면 되돌리는 변환이 한 겹 더 는다. 그래서 회전 그립은 학습 영역에만 있고 탐색 영역은 그 각을 물려받는다.
    /// 원형 학습영역은 각도 개념이 없어 탐색 영역도 축 정렬(0°)이다.</summary>
    public IReadOnlyList<CvEditShape>? CreateShapes(Action onEdited)
    {
        var shapes = new List<CvEditShape>();
        CvEditRect? search = null;

        if (TrainShape == CvTrainShape.Circle)
        {
            var circle = new CvEditCircle { Color = ViOverlayColor.Yellow, Label = "Train" };
            circle.Set(TrainCircleX, TrainCircleY, TrainCircleR);
            circle.Changed += (_, _) =>
            {
                TrainCircleX = circle.CenterX;
                TrainCircleY = circle.CenterY;
                TrainCircleR = circle.Radius;
                onEdited();
            };
            shapes.Add(circle);
        }
        else
        {
            var train = new CvEditRect { Color = ViOverlayColor.Yellow, Label = "Train", IsRotatable = true };
            train.Set(TrainX, TrainY, TrainW, TrainH);
            train.AngleDeg = TrainAngleDeg;
            train.Changed += (_, _) =>
            {
                TrainX = train.X;
                TrainY = train.Y;
                TrainW = train.W;
                TrainH = train.H;
                TrainAngleDeg = train.AngleDeg;
                if (search is not null) search.AngleDeg = train.AngleDeg;
                onEdited();
            };
            shapes.Add(train);
        }

        if (UseSearchRegion)
        {
            // 티칭 도형은 Teal(어두운 청록) — 검사 결과 측정 기하(순색 Cyan)와 색으로 구분.
            search = new CvEditRect { Color = ViOverlayColor.Teal, Label = "Search" };
            search.Set(SearchX, SearchY, SearchW, SearchH);
            search.AngleDeg = TrainShape == CvTrainShape.Circle ? 0 : TrainAngleDeg;
            search.Changed += (_, _) =>
            {
                SearchX = search.X;
                SearchY = search.Y;
                SearchW = search.W;
                SearchH = search.H;
                onEdited();
            };
            shapes.Add(search);
        }
        return shapes;
    }
}
