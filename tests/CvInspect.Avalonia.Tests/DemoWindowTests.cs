// Avalonia 데모 창 연기 시험 — 창을 헤드리스로 띄워 합성 소스(VirtualCam)의 첫 프레임이 표시 컨트롤까지 오고, 레시피가 돌아
// 결과 오버레이가 생기는지 본다. 카메라·웹캠은 열지 않는다(시작 소스는 합성이 첫 항목이다).
// 화면 VM 은 WPF 데모와 같은 소스(samples/CvInspect.Demo/MainVm.cs)라, 이 시험이 두 데모가 함께 쓰는 VM 의 취득→표시→검사 사슬을 잰다.
// 시간 의존은 하나 — VirtualCam 이 2 fps 로 첫 장을 내는 것. 고정 대기 대신 조건이 설 때까지 디스패처를 돌리고 상한만 둔다.

using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CvInspect.Avalonia.Controls;
using CvInspect.Demo;
using CvInspect.Imaging;
using CvInspect.Vision.Opts;
using CvInspect.Vision.Overlay;
using static CvInspect.Avalonia.Tests.AvaTest;

namespace CvInspect.Avalonia.Tests;

public class DemoWindowTests
{
    [AvaloniaFact]
    public void Demo_window_shows_the_first_synthetic_frame_and_runs_the_recipe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CvInspect.Avalonia.Tests.Demo." + Guid.NewGuid().ToString("N"));
        var win = new MainWindow([], dir) { Width = 1180, Height = 760 };
        try
        {
            win.Show();
            var vm = win.Vm;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(20) && !(vm.Frame is CamFrame && vm.Overlay is { Items.Count: > 0 }))
            {
                Pump();
                Thread.Sleep(20);
            }
            Check(vm.Frame is CamFrame { Width: DemoImage.Width, Height: DemoImage.Height },
                $"the synthetic source delivers a {DemoImage.Width}x{DemoImage.Height} CamFrame within 20 s (got {vm.Frame?.GetType().Name ?? "null"} after {sw.ElapsedMilliseconds} ms, status '{vm.Status}')");
            Check(vm.Overlay is { Items.Count: > 0 }, $"the recipe ran on the frame and produced overlay items (status '{vm.Status}')");
            Check(vm.Tools.Any(t => t.Opt is CvPatternOpt { Trained: true }),
                "the synthetic source auto-trains the pattern tool on its first frame, so the fixture runs without a user Train");

            Settle(win);
            var disp = win.FindControl<CvDispCtrl>("Disp")!;
            Check(ReferenceEquals(disp.Frame, vm.Frame) && ReferenceEquals(disp.Overlay, vm.Overlay),
                "the window binds the view-model frame and overlay to the display control by reference (CamFrame is not copied)");
            var editor = win.FindControl<CvPropEditCtrl>("Editor")!;
            Check(ReferenceEquals(editor.Source, vm.ToolOpt) && vm.ToolOpt is not null && editor.Groups.Count > 0,
                $"the property editor shows the selected tool's options ({editor.Groups.Count} group(s))");

            // 그린 화면에 판(밝기 200 의 회색)이 보이는가 — 무이미지 화면은 어두운 단색이라 이것이 없다.
            var shot = Capture(win);
            int plate = 0;
            for (int y = 0; y < shot.H; y++)
                for (int x = 0; x < shot.W * 2 / 3; x++)
                {
                    var c = shot.At(x, y);
                    if (c.R is >= 185 and <= 215 && c.G is >= 185 and <= 215 && c.B is >= 185 and <= 215 && Math.Abs(c.R - c.B) < 6) plate++;
                }
            Check(plate > 5000, $"the drawn display shows the synthetic plate (gray-200 pixels in the image area: {plate})");
            // 닫은 뒤 "라이브 아님" 단언의 대조군 — 닫기 전에 라이브였어야 그 단언이 정지를 잰다.
            Check(vm.IsLive, "the synthetic source is live before closing (the grabbing notice reached the UI thread through the port)");
        }
        finally
        {
            win.Close();
            Pump();
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Check(!win.Vm.IsLive, "closing the window stops the camera (Closing disposes the view-model)");
    }
}
