// USB 웹캠 식별 실기 검증 — 이 도구가 답해야 할 질문은 하나다:
// "UsbCamId 가 열거한 순번으로 OpenCV 를 열면 정말 그 카메라가 열리는가, 그리고 VID/PID 로 다시 열면 같은 카메라인가."
// 카메라가 둘 이상 꽂힌 PC 에서 돌린다. 각 카메라 앞에 서로 다른 글자(A, B)를 두면 저장된 사진만으로 어느 카메라인지 읽힌다.
//
// 산출: <out>/report.txt 와 사진들.
//   enum.txt                    열거 결과(순번·이름·VID/PID·인스턴스 ID)
//   idx_<backend>_<n>.png       백엔드별로 인덱스 n 을 열어 찍은 한 장 — 열거 순번과 실제 카메라의 대응을 눈으로 확인
//   byid_<VID_PID or inst>.png  SerialNumber(VID/PID 또는 인스턴스 ID)로 VideoCaptureCam 을 열어 찍은 한 장
// 판정: byid_* 가 같은 장치의 idx_any_<n>.png 와 같은 카메라를 보여 주면 식별이 맞는 것이다.
using System.Text;
using CvInspect;
using CvInspect.Imaging;
using OpenCvSharp;

var outDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "out");
Directory.CreateDirectory(outDir);
var report = new StringBuilder();
void Log(string line)
{
    Console.WriteLine(line);
    report.AppendLine(line);
}

// 라이브러리 진단도 보고서에 싣는다 — 싱크가 없으면 열기 실패 사유·프레임 유실 같은 줄이 붙잡혀만 있고 여기 안 나온다.
CvLog.Sink = (level, src, msg, ex) => Log($"[{level,-7}] {src}: {msg}{(ex is null ? "" : " | " + ex.GetType().Name + ": " + ex.Message)}");

Log($"=== usbcamprobe {DateTime.Now:yyyy-MM-dd HH:mm:ss}  OS={Environment.OSVersion}  out={outDir}");

// 0) 이 런타임의 OpenCV 가 어떤 Video I/O 백엔드를 갖고 있는가 — DSHOW/MSMF 가 빠진 빌드면 "backend=..." 는 뜻이 없고
//    웹캠은 FFmpeg(dshow 디먹서)로 열린다. 순번이 어느 열거를 따르는지 해석하려면 이것부터 알아야 한다.
Log("--- OpenCV build: Video I/O");
var inVideoIo = false;
foreach (var line in Cv2.GetBuildInformation().Split('\n'))
{
    var l = line.TrimEnd();
    if (l.StartsWith("  Video I/O", StringComparison.Ordinal)) inVideoIo = true;
    else if (inVideoIo && l.Length > 0 && !l.StartsWith("    ", StringComparison.Ordinal)) inVideoIo = false;
    if (inVideoIo) Log("  " + l.Trim());
}

// 1) 열거 — SetupAPI(Windows) / sysfs(Linux). 이 순번이 OpenCV 인덱스라는 것이 검증 대상이다.
IReadOnlyList<UsbCamInfo> devices;
try
{
    devices = UsbCamId.Enumerate();
}
catch (Exception ex)
{
    Log($"enumerate FAILED: {ex.GetType().Name}: {ex.Message}");
    File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString());
    return 2;
}
Log($"enumerated {devices.Count} device(s):");
foreach (var d in devices) Log("  " + d);
File.WriteAllText(Path.Combine(outDir, "enum.txt"), string.Join(Environment.NewLine, devices.Select(d => d.ToString())));

// 2) 백엔드별로 인덱스마다 한 장 — 인덱스 대응이 백엔드에 따라 다른지 본다(원형은 백엔드를 고정하지 않았다).
//    Windows 는 ANY(OpenCV 가 고름)·DSHOW·MSMF 셋, Linux 는 ANY·V4L2 둘.
var backends = OperatingSystem.IsWindows()
    ? new[] { VideoCaptureAPIs.ANY, VideoCaptureAPIs.DSHOW, VideoCaptureAPIs.MSMF }
    : new[] { VideoCaptureAPIs.ANY, VideoCaptureAPIs.V4L2 };
var maxIndex = Math.Max(devices.Count, 2);   // 열거가 비어도 0·1 은 두드려 본다 — 열거와 실제가 다르면 그것도 기록이다
foreach (var api in backends)
{
    Log($"--- backend {api}");
    for (var i = 0; i < maxIndex; i++)
    {
        var name = $"idx_{api.ToString().ToLowerInvariant()}_{i}";
        var r = Snap(() => new VideoCapture(i, api), Path.Combine(outDir, name + ".png"));
        Log($"  index {i}: {r}");
    }
}

// 3) 식별로 다시 열기 — 각 장치를 VID/PID 로, VID/PID 가 겹치면 인스턴스 ID 로 SerialNumber 에 넣어 VideoCaptureCam 을 연다.
Log("--- open by identity (VideoCaptureCam + CamOpt.SerialNumber)");
foreach (var d in devices)
{
    if (d.VendorId.Length == 0) { Log($"  #{d.Index} '{d.Name}': no VID/PID — skipped"); continue; }
    var dup = devices.Count(x => x.VendorId == d.VendorId && x.ProductId == d.ProductId) > 1;
    var key = dup ? d.InstanceId : d.VidPid;
    var file = Path.Combine(outDir, "byid_" + Sanitize(key) + ".png");
    try
    {
        var opt = new CamOpt { Name = "probe", ComType = "VideoCapture", SerialNumber = key, IsColor = true };
        using var cam = CamFactory.Create(opt);
        CamFrame? got = null;
        cam.FrameAcquired += (_, f) => got = f;
        cam.Open();
        cam.GrabOne();
        if (got is null) { Log($"  {key}: opened but no frame"); continue; }
        using var mat = got.AsMat();
        Cv2.ImEncode(".png", mat, out var png);
        File.WriteAllBytes(file, png);
        Log($"  {key}: OK {got.Width}x{got.Height} {got.Format} -> {Path.GetFileName(file)}   (compare with idx_any_{d.Index}.png)");
    }
    catch (Exception ex)
    {
        Log($"  {key}: FAILED {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
    }
}

// 4) 없는 키 — 예외 메시지에 목록이 실리는지(설정에 복사할 수 있는 형태인지).
Log("--- unknown identity (expect a listing in the exception)");
try
{
    UsbCamId.Resolve("VID_DEAD&PID_BEEF");
    Log("  unexpectedly resolved");
}
catch (Exception ex)
{
    Log("  " + ex.Message.Replace("\n", "\n  "));
}

File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString());
Log($"=== done. send back the whole folder: {outDir}");
return 0;

static string Snap(Func<VideoCapture> open, string file)
{
    try
    {
        using var cap = open();
        if (!cap.IsOpened()) return "not opened";
        using var mat = new Mat();
        // 첫 프레임은 자동 노출 전이라 어두울 수 있다 — 몇 장 버리고 찍는다.
        for (var k = 0; k < 5; k++) cap.Read(mat);
        if (mat.Empty()) return "opened, no frame";
        Cv2.ImEncode(".png", mat, out var png);
        File.WriteAllBytes(file, png);
        return $"OK {mat.Width}x{mat.Height} -> {Path.GetFileName(file)}";
    }
    catch (Exception ex)
    {
        return $"FAILED {ex.GetType().Name}: {ex.Message.Split('\n')[0]}";
    }
}

static string Sanitize(string s)
{
    var sb = new StringBuilder(s.Length);
    foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
    return sb.ToString();
}
