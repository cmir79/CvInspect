using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CvInspect.Vision;
using CvInspect.Vision.Opts;

namespace CvInspect.Demo;

public enum DemoToolKind { Preprocess, Pattern, Line, Circle, Blob }

/// <summary>레시피의 툴 하나 — 키(파일 이름이 된다), 종류, 종류에 맞는 Cv*Opt POCO.</summary>
public sealed class DemoToolEntry
{
    public DemoToolEntry(string key, DemoToolKind kind, object opt)
    {
        Key = key;
        Kind = kind;
        Opt = opt;
    }

    public string Key { get; }
    public DemoToolKind Kind { get; }
    public object Opt { get; }
    public string Title => $"{Kind}  —  {Key}";
}

/// <summary>
/// 레시피 — 순서 있는 툴 목록. 앞의 툴이 뒤의 툴에 영향을 준다: 전처리는 이후 툴의 좌표 공간(단계 이미지)을 바꾸고,
/// 패턴은 이후 툴의 티칭 기하를 발견된 부품 자리로 옮기는 픽스처가 된다. 실행은 <see cref="DemoRecipeRunner"/>.
///
/// 저장 형식은 폴더 하나다 — Recipe.json(툴 순서·키·종류) + 툴마다 {키}.json(Opt 그대로, enum 은 문자열) +
/// 패턴 툴은 {키}.Template.png(학습 템플릿). 사람이 열어 읽고 diff 할 수 있는 형태를 고른 것이다.
/// </summary>
public sealed class DemoRecipe
{
    public const string RecipeFile = "Recipe.json";

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public ObservableCollection<DemoToolEntry> Tools { get; } = [];

    /// <summary>예제 기본 레시피 — 픽스처 + 라인 + 원 + 블랍, 기하는 합성 부품 기준.</summary>
    public static DemoRecipe Default()
    {
        var r = new DemoRecipe();
        r.Add(DemoToolKind.Pattern, "fixture");
        r.Add(DemoToolKind.Line, "top-edge");
        r.Add(DemoToolKind.Circle, "hole");
        r.Add(DemoToolKind.Blob, "hole-area");
        return r;
    }

    /// <summary>종류별 기본 Opt 로 툴을 덧붙인다. 키를 안 주면 종류 이름에 번호를 붙인다.</summary>
    public DemoToolEntry Add(DemoToolKind kind, string? key = null)
    {
        key ??= NextKey(kind);
        var entry = new DemoToolEntry(key, kind, NewOpt(kind));
        Tools.Add(entry);
        return entry;
    }

    private string NextKey(DemoToolKind kind)
    {
        for (var n = 1; ; n++)
        {
            var k = $"{kind.ToString().ToLowerInvariant()}{n}";
            if (Tools.All(t => !string.Equals(t.Key, k, StringComparison.OrdinalIgnoreCase))) return k;
        }
    }

    public static Type OptTypeOf(DemoToolKind kind) => kind switch
    {
        DemoToolKind.Preprocess => typeof(CvImageProcessOpt),
        DemoToolKind.Pattern => typeof(CvPatternOpt),
        DemoToolKind.Line => typeof(CvFindLineOpt),
        DemoToolKind.Circle => typeof(CvFindCircleOpt),
        DemoToolKind.Blob => typeof(CvBlobOpt),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>종류별 기본 Opt — 기하는 합성 부품(원본 공간) 기준. 전처리 뒤에 두면 그 공간에 맞게 사용자가 옮긴다.</summary>
    public static object NewOpt(DemoToolKind kind) => kind switch
    {
        DemoToolKind.Preprocess => new CvImageProcessOpt { SampleX = 1, SampleY = 1, MedianKernel = 0 },
        DemoToolKind.Pattern => new CvPatternOpt
        {
            TrainShape = CvTrainShape.Rect,
            TrainX = DemoImage.PlateX - 24,
            TrainY = DemoImage.PlateY - 24,
            TrainW = 120,
            TrainH = 120,
            UseAngleSearch = true,
            AngleZoneDeg = 20,
            CoarseStepDeg = 4,
            FineStepDeg = 1,
            AcceptScore = 0.6,
        },
        DemoToolKind.Line => new CvFindLineOpt
        {
            StartX = DemoImage.PlateX + 40,
            StartY = DemoImage.PlateY,
            EndX = DemoImage.PlateX + DemoImage.PlateW - 40,
            EndY = DemoImage.PlateY,
        },
        DemoToolKind.Circle => new CvFindCircleOpt
        {
            CenterX = DemoImage.HoleCenterX,
            CenterY = DemoImage.HoleCenterY,
            Radius = DemoImage.HoleRadius,
        },
        DemoToolKind.Blob => new CvBlobOpt
        {
            Polarity = CvBlobPolarity.Dark,
            MinArea = 2000,
            UseSearchRegion = true,
            SearchX = DemoImage.PlateX + 10,
            SearchY = DemoImage.PlateY + 10,
            SearchW = DemoImage.PlateW - 20,
            SearchH = DemoImage.PlateH - 20,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private sealed record RecipeToolRef(string Key, DemoToolKind Kind);
    private sealed record RecipeDoc(List<RecipeToolRef> Tools);

    /// <summary>폴더에 저장 — 있던 툴 파일은 지우고 다시 쓴다(지운 툴의 잔재가 남지 않게).</summary>
    public void Save(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.json").Concat(Directory.GetFiles(dir, "*.Template.png")))
            File.Delete(stale);

        var doc = new RecipeDoc(Tools.Select(t => new RecipeToolRef(t.Key, t.Kind)).ToList());
        File.WriteAllText(Path.Combine(dir, RecipeFile), JsonSerializer.Serialize(doc, Json));
        foreach (var t in Tools)
        {
            File.WriteAllText(Path.Combine(dir, t.Key + ".json"), JsonSerializer.Serialize(t.Opt, OptTypeOf(t.Kind), Json));
            if (t.Opt is CvPatternOpt { TemplatePng: { Length: > 0 } png })
                File.WriteAllBytes(Path.Combine(dir, t.Key + ".Template.png"), png);
        }
    }

    /// <summary>폴더에서 로드. 툴 파일 하나가 깨져도 나머지는 살린다 — 깨진 것은 종류 기본 Opt 로 두고 로그를 남긴다.
    /// 패턴 툴은 템플릿 파일이 있어야 학습 상태가 유지된다(없으면 Trained=false).</summary>
    public static DemoRecipe Load(string dir)
    {
        var doc = JsonSerializer.Deserialize<RecipeDoc>(File.ReadAllText(Path.Combine(dir, RecipeFile)), Json)
                  ?? throw new InvalidDataException("Recipe.json is empty.");
        var r = new DemoRecipe();
        foreach (var t in doc.Tools)
        {
            object opt;
            var path = Path.Combine(dir, t.Key + ".json");
            try
            {
                opt = JsonSerializer.Deserialize(File.ReadAllText(path), OptTypeOf(t.Kind), Json) ?? NewOpt(t.Kind);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                CvLog.Publish(CvLogLevel.Warning, nameof(DemoRecipe), $"Tool file '{path}' could not be read; using defaults for '{t.Key}'. {ex.GetType().Name}: {ex.Message}");
                opt = NewOpt(t.Kind);
            }
            if (opt is CvPatternOpt pat)
            {
                var tpl = Path.Combine(dir, t.Key + ".Template.png");
                if (File.Exists(tpl)) pat.TemplatePng = File.ReadAllBytes(tpl);
                else pat.Trained = false;
            }
            r.Tools.Add(new DemoToolEntry(t.Key, t.Kind, opt));
        }
        return r;
    }
}
