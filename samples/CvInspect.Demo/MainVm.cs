using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CvInspect.Controls;
using CvInspect.Imaging;
using CvInspect.Vision.Opts;
using CvInspect.Vision.Overlay;
using Microsoft.Win32;
using OpenCvSharp;

namespace CvInspect.Demo;

/// <summary>
/// 화면 VM — 취득(VirtualCam 이 합성 부품 여섯 장을 재생) → 표시(CamFrame 을 Frame 에 그대로) → 레시피 실행(AsMat 무복사 래핑) →
/// 선택한 툴의 단계 이미지·도형·오버레이. 프레임은 CamFrame 으로만 들고 다닌다: 수명 계약이 없어 카메라 스레드에서 받아
/// UI 로 넘겨도, 편집 뒤 다시 검사하려고 붙잡아 둬도 신경 쓸 것이 없다. Mat 은 쓰는 순간에만 잠깐 만든다.
/// 레시피는 툴 목록으로 편집하고 폴더에 저장·로드한다 — 바뀐 뒤 저장하지 않았으면 제목에 * 가 붙는다.
/// </summary>
public sealed partial class MainVm : ObservableObject, IDisposable
{
    public IReadOnlyList<DemoToolKind> Kinds { get; } = Enum.GetValues<DemoToolKind>();

    [ObservableProperty] private object? _frame;
    [ObservableProperty] private ViOverlay? _overlay;
    [ObservableProperty] private IReadOnlyList<CvEditShape>? _shapes;
    [ObservableProperty] private object? _toolOpt;
    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private DemoToolEntry? _selectedTool;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RecipeTitle))] private bool _isDirty;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RecipeTitle))] private string? _recipePath;

    public string RecipeTitle => $"Recipe: {(RecipePath is null ? "(unsaved)" : Path.GetFileName(RecipePath))}{(IsDirty ? " *" : "")}";

    private DemoRecipe _recipe;
    public ObservableCollection<DemoToolEntry> Tools => _recipe.Tools;

    private readonly Dictionary<DemoToolEntry, IReadOnlyList<CvEditShape>> _shapeCache = new();
    private readonly ICam _cam;
    private CamFrame? _last;
    private DemoRunResult? _lastRun;

    public MainVm()
    {
        _recipe = DemoRecipe.Default();
        DemoRecipeRunner.WireTrainHooks(_recipe, () => _last?.AsMat());

        // 합성 부품 이미지를 파일로 두고 VirtualCam 이 그 폴더를 재생한다 — 실제 카메라와 같은 ICam 경로를 탄다.
        // 여섯 장은 부품이 조금씩 밀리고 도는 장면이다(첫 장은 기준 자세). 라이브로 돌리면 픽스처가 따라가는 것이 보인다.
        var dir = Path.Combine(Path.GetTempPath(), "CvInspect.Demo");
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(dir, "part*.png")) File.Delete(f);
        var poses = new (double Dx, double Dy, double Deg)[] { (0, 0, 0), (14, -9, 5), (-12, 11, -7), (18, 16, 9), (-9, -15, -4), (6, 4, 12) };
        for (var i = 0; i < poses.Length; i++)
        {
            using var img = DemoImage.Create(poses[i].Dx, poses[i].Dy, poses[i].Deg);
            Cv2.ImEncode(".png", img, out var png);   // 경로 기반 ImWrite 는 비ASCII 경로에서 조용히 실패한다 — 바이트로 쓴다
            File.WriteAllBytes(Path.Combine(dir, $"part_{i:00}.png"), png);
        }
        _cam = CamFactory.Create(new CamOpt { Name = "demo", ComType = "Virtual", VirtualImageDir = dir, IsColor = false, FrameRate = 2 });
        _cam.FrameAcquired += (_, f) => Application.Current.Dispatcher.BeginInvoke(() => Show(f));
        _cam.GrabbingChanged += (_, g) => Application.Current.Dispatcher.BeginInvoke(() => IsLive = g);
        _cam.Open();

        SelectedTool = Tools.FirstOrDefault();
        _cam.StartContinuous();   // 첫 프레임(기준 자세)에서 자동 학습되고, 이어지는 프레임에서 부품이 움직인다 — 툴바 ⏯️ 로 멈춘다
    }

    partial void OnSelectedToolChanged(DemoToolEntry? value)
    {
        ToolOpt = value?.Opt;
        Shapes = value is null ? null : ShapesOf(value);
        Present();
    }

    private IReadOnlyList<CvEditShape> ShapesOf(DemoToolEntry tool)
    {
        if (!_shapeCache.TryGetValue(tool, out var shapes))
        {
            shapes = CvShapeBinder.For(tool.Opt, () => MarkDirty(rerun: true)) ?? [];
            _shapeCache[tool] = shapes;
        }
        return shapes;
    }

    // --- 카메라 ---
    [RelayCommand] private void Grab() => _cam.GrabOne();
    [RelayCommand] private void Live() => _cam.StartContinuous();
    [RelayCommand] private void Stop() => _cam.StopContinuous();

    /// <summary>우클릭 불러오기 — 받은 Mat 은 수신자 소유. CamFrame 으로 실체화한 뒤 바로 놓는다.</summary>
    [RelayCommand]
    private void LoadFrame(Mat mat)
    {
        try
        {
            Show(CamFrame.FromMat(mat));
        }
        catch (NotSupportedException ex)
        {
            Status = "cannot load: " + ex.Message;   // 16비트 TIFF 등 — 표시 계층이 받는 8비트 1/3/4채널이 아니다
        }
        finally
        {
            mat.Dispose();
        }
    }

    // --- 레시피 편집 ---
    [RelayCommand]
    private void AddTool(DemoToolKind kind)
    {
        var entry = _recipe.Add(kind);
        DemoRecipeRunner.WireTrainHooks(_recipe, () => _last?.AsMat());
        SelectedTool = entry;
        MarkDirty(rerun: true);
    }

    [RelayCommand]
    private void RemoveTool()
    {
        if (SelectedTool is not { } t) return;
        var i = Tools.IndexOf(t);
        Tools.Remove(t);
        _shapeCache.Remove(t);
        SelectedTool = Tools.Count == 0 ? null : Tools[Math.Min(i, Tools.Count - 1)];
        MarkDirty(rerun: true);
    }

    [RelayCommand] private void MoveUp() => Move(-1);
    [RelayCommand] private void MoveDown() => Move(+1);

    private void Move(int delta)
    {
        if (SelectedTool is not { } t) return;
        var i = Tools.IndexOf(t);
        var j = i + delta;
        if (j < 0 || j >= Tools.Count) return;
        Tools.Move(i, j);
        SelectedTool = t;
        MarkDirty(rerun: true);
    }

    [RelayCommand] private void Run() => Rerun();

    [RelayCommand]
    private void Save()
    {
        var dlg = new OpenFolderDialog { Title = "Save recipe into folder", InitialDirectory = RecipePath ?? Path.Combine(Path.GetTempPath(), "CvInspect.Demo") };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _recipe.Save(dlg.FolderName);
            RecipePath = dlg.FolderName;
            IsDirty = false;
            Status = $"saved {Tools.Count} tool(s) to {dlg.FolderName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "save failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void Load()
    {
        var dlg = new OpenFolderDialog { Title = "Load recipe from folder", InitialDirectory = RecipePath ?? Path.Combine(Path.GetTempPath(), "CvInspect.Demo") };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var loaded = DemoRecipe.Load(dlg.FolderName);
            _recipe = loaded;
            _shapeCache.Clear();
            DemoRecipeRunner.WireTrainHooks(_recipe, () => _last?.AsMat());
            OnPropertyChanged(nameof(Tools));
            RecipePath = dlg.FolderName;
            IsDirty = false;
            SelectedTool = Tools.FirstOrDefault();
            Rerun();
            Status = $"loaded {Tools.Count} tool(s) from {dlg.FolderName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            Status = "load failed: " + ex.Message;
        }
    }

    /// <summary>편집기 커밋·액션·도형 드래그·목록 변경 — 저장 안 된 변경이 있다고 표시하고 다시 검사한다.</summary>
    public void MarkDirty(bool rerun)
    {
        IsDirty = true;
        if (rerun) Rerun();
    }

    private void Show(CamFrame frame)
    {
        _last = frame;
        // 첫 프레임(기준 자세)으로 학습 안 된 패턴 툴을 한 번 자동 학습 — 켜자마자 픽스처가 도는 것을 보이기 위해.
        foreach (var t in Tools)
            if (t.Opt is CvPatternOpt { Trained: false })
            {
                using var m = frame.AsMat();
                DemoRecipeRunner.Train(_recipe, t, m);
            }
        Rerun();
    }

    private void Rerun()
    {
        if (_last is null) return;
        using var mat = _last.AsMat();   // 무복사 래핑 — dispose 는 핀 해제일 뿐
        var prev = _lastRun;
        _lastRun = DemoRecipeRunner.Run(_recipe, mat);
        prev?.Dispose();
        Status = (_lastRun.IsOk ? "OK" : "NG") + string.Concat(_lastRun.Tools.Select(t => $"  |  {t.Tool.Key}: {(t.Ok ? "ok" : "NG")}"));
        Present();
    }

    /// <summary>선택한 툴의 단계 이미지 위에, 모든 툴의 결과를 그 공간으로 옮겨 얹는다. 단계가 원본이면 CamFrame 을 그대로(복사 없음).</summary>
    private void Present()
    {
        if (_last is null || _lastRun is null) return;
        var index = SelectedTool is { } t ? Tools.IndexOf(t) : -1;
        if (_lastRun.IsOriginalStage(index))
            Frame = _last;
        else
            Frame = CamFrame.FromMat(_lastRun.StageOf(index));   // 전처리 출력 — 그 공간에서 도형을 편집한다
        Overlay = _lastRun.OverlayFor(index);
    }

    public void Dispose()
    {
        _cam.StopContinuous();
        _cam.Dispose();
        _lastRun?.Dispose();
    }
}
