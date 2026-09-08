using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CvInspect.Controls;
using CvInspect.Imaging;
using CvInspect.Vision.Overlay;
using OpenCvSharp;

namespace CvInspect.Demo;

public enum DemoTool { Pattern, Line, Circle, Blob }

/// <summary>
/// 화면 VM — 취득(VirtualCam 이 합성 이미지 폴더를 재생) → 표시(CamFrame 을 Frame 에 그대로) → 검사(AsMat 무복사 래핑으로
/// 툴에 전달) → 오버레이. 프레임은 CamFrame 으로만 들고 다닌다: 수명 계약이 없어 카메라 스레드에서 받아 UI 로 넘겨도,
/// 편집 뒤 다시 검사하려고 붙잡아 둬도 신경 쓸 것이 없다. Mat 은 쓰는 순간에만 잠깐 만든다.
/// </summary>
public sealed partial class MainVm : ObservableObject, IDisposable
{
    public IReadOnlyList<DemoTool> Tools { get; } = [DemoTool.Pattern, DemoTool.Line, DemoTool.Circle, DemoTool.Blob];

    [ObservableProperty] private object? _frame;
    [ObservableProperty] private ViOverlay? _overlay;
    [ObservableProperty] private IReadOnlyList<CvEditShape>? _shapes;
    [ObservableProperty] private object? _toolOpt;
    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private DemoTool _selectedTool;

    private readonly DemoInspector _insp = new();
    private readonly IReadOnlyList<CvEditShape> _patternShapes;
    private readonly IReadOnlyList<CvEditShape> _lineShapes;
    private readonly IReadOnlyList<CvEditShape> _circleShapes;
    private readonly IReadOnlyList<CvEditShape> _blobShapes;
    private readonly ICam _cam;
    private CamFrame? _last;

    public MainVm()
    {
        // 탐색 도형은 툴마다 한 번 만든다 — 드래그가 Opt 에 되쓰이고, 되쓰일 때마다 다시 검사한다.
        _patternShapes = CvShapeBinder.For(_insp.Pattern, Rerun) ?? [];
        _lineShapes = CvShapeBinder.For(_insp.Line, Rerun) ?? [];
        _circleShapes = CvShapeBinder.For(_insp.Circle, Rerun) ?? [];
        _blobShapes = CvShapeBinder.For(_insp.Blob, Rerun) ?? [];

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
        _insp.FrameProvider = () => _last?.AsMat();   // 편집기의 Train 버튼이 현재 프레임으로 학습한다
        _cam.FrameAcquired += (_, f) => Application.Current.Dispatcher.BeginInvoke(() => Show(f));
        _cam.GrabbingChanged += (_, g) => Application.Current.Dispatcher.BeginInvoke(() => IsLive = g);
        _cam.Open();

        // 기본값(Line)은 enum 의 0 이라 대입해도 변경 통지가 나지 않는다 — 초기 배선은 직접 건다.
        ApplyTool(SelectedTool);
        _cam.StartContinuous();   // 첫 프레임(기준 자세)에서 자동 학습되고, 이어지는 프레임에서 부품이 움직인다 — 툴바 ⏯️ 로 멈춘다
    }

    partial void OnSelectedToolChanged(DemoTool value) => ApplyTool(value);

    private void ApplyTool(DemoTool tool)
    {
        (ToolOpt, Shapes) = tool switch
        {
            DemoTool.Pattern => ((object)_insp.Pattern, _patternShapes),
            DemoTool.Line => (_insp.Line, _lineShapes),
            DemoTool.Circle => (_insp.Circle, _circleShapes),
            _ => (_insp.Blob, _blobShapes),
        };
    }

    [RelayCommand] private void Grab() => _cam.GrabOne();
    [RelayCommand] private void Live() => _cam.StartContinuous();
    [RelayCommand] private void Stop() => _cam.StopContinuous();
    [RelayCommand] private void Run() => Rerun();

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

    private void Show(CamFrame frame)
    {
        _last = frame;
        Frame = frame;   // 복사 없음 — 표시 컨트롤이 배열을 참조로 붙잡는다(발행 뒤 불변 계약)
        // 첫 프레임(기준 자세)으로 한 번 자동 학습 — 켜자마자 픽스처가 도는 것을 보이기 위해. 다시 학습하려면 Train 버튼.
        if (!_insp.Pattern.Trained)
        {
            using var m = frame.AsMat();
            _insp.Train(m);
        }
        Rerun();
    }

    private void Rerun()
    {
        if (_last is null) return;
        using var mat = _last.AsMat();   // 무복사 래핑 — dispose 는 핀 해제일 뿐
        var r = _insp.Run(mat);
        Overlay = r.Overlay;
        Status = (r.IsOk ? "OK" : "NG")
            + (r.Pose is { } p ? $"  |  pattern {r.PatternScore:F2} @ {p.ThetaDeg:+0.0;-0.0}°" : _insp.Pattern.Trained ? $"  |  pattern: not found ({r.PatternScore:F2})" : "  |  pattern: not trained")
            + (r.Line is { } l ? $"  |  line {l.AngleDeg:F2}° rms {l.RmsPx:F2}" : "  |  line: not found")
            + (r.Circle is { } c ? $"  |  circle r {c.Radius:F1} @ ({c.CenterX:F1}, {c.CenterY:F1})" : "  |  circle: not found")
            + $"  |  blob n {r.Blobs.Hits.Count} area {r.Blobs.TotalArea:F0}";
    }

    public void Dispose()
    {
        _cam.StopContinuous();
        _cam.Dispose();
    }
}
