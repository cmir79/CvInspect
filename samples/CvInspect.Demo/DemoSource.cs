using CvInspect.Imaging;

namespace CvInspect.Demo;

/// <summary>
/// 데모가 고를 수 있는 프레임 소스 하나 — 합성 부품(VirtualCam 폴더 재생) 또는 이 PC 의 USB 웹캠.
/// 웹캠은 인덱스가 아니라 VID/PID(같은 모델이 둘이면 인스턴스 ID)를 <see cref="CamOpt.SerialNumber"/> 에 넣어 연다 —
/// OS 가 오늘 어떤 인덱스를 매겼든 같은 카메라가 잡힌다. 실제 카메라와 합성 소스가 같은 <see cref="ICam"/> 경로를 탄다.
/// </summary>
public sealed class DemoSource
{
    private DemoSource(string key, string title, CamOpt opt, bool isSynthetic)
    {
        Key = key;
        Title = title;
        Opt = opt;
        IsSynthetic = isSynthetic;
    }

    /// <summary>명령줄·선택 유지에 쓰는 식별자 — 합성은 "synthetic", 웹캠은 VID/PID 또는 인스턴스 ID.</summary>
    public string Key { get; }
    public string Title { get; }
    public CamOpt Opt { get; }
    public bool IsSynthetic { get; }

    /// <summary>합성 소스 먼저, 그 다음 열거된 웹캠. 열거가 안 되는 OS 에서는 합성만 돌려준다.</summary>
    public static IReadOnlyList<DemoSource> List(string syntheticDir)
    {
        var list = new List<DemoSource>
        {
            new("synthetic", "Synthetic part (VirtualCam)",
                new CamOpt { Name = "demo", ComType = "Virtual", VirtualImageDir = syntheticDir, IsColor = false, FrameRate = 2 }, isSynthetic: true),
        };
        IReadOnlyList<UsbCamInfo> cams;
        try
        {
            cams = UsbCamId.Enumerate();
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException)
        {
            return list;
        }
        foreach (var cam in cams)
        {
            if (cam.VendorId.Length == 0) continue;   // VID/PID 없는 장치는 고정 식별이 안 된다 — 목록에서 뺀다
            var dup = cams.Count(x => x.VendorId == cam.VendorId && x.ProductId == cam.ProductId) > 1;
            var key = dup ? cam.InstanceId : cam.VidPid;
            list.Add(new(key, $"#{cam.Index} {cam.Name} ({cam.VidPid})",
                new CamOpt { Name = cam.Name, ComType = "Webcam", SerialNumber = key, IsColor = true, FrameRate = 30 }, isSynthetic: false));
        }
        return list;
    }
}
