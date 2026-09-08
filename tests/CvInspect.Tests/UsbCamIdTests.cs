// USB 웹캠 고정 식별 회귀 — 키 해석, 매칭 규칙(단일/없음/중복/인스턴스), Linux sysfs 해석(합성 트리), 현재 OS 열거.
// Windows 열거 순번이 OpenCV 인덱스와 맞는지는 여기서 못 잰다(카메라가 있는 PC 가 필요) — 그 사실은 문서에 적혀 있다.
using System.Runtime.InteropServices;
using CvInspect.Imaging;
using Xunit;

namespace CvInspect.Tests;

public class UsbCamIdTests
{
    private static void Check(bool cond, string name) => Assert.True(cond, name);

    private static UsbCamInfo Dev(int i, string name, string vid, string pid, string inst, string? path = null)
        => new(i, name, vid, pid, inst, path);

    [Fact]
    public void KeyFormats()
    {
        Check(UsbCamId.TryParse("VID_046D&PID_082D", out var v, out var p, out var i) && v == "046D" && p == "082D" && i is null,
            "VID_/PID_ form parses to upper-case hex, no instance");
        Check(UsbCamId.TryParse("vid_046d&pid_082d", out v, out p, out i) && v == "046D" && p == "082D" && i is null,
            "case-insensitive");
        Check(UsbCamId.TryParse("046d:082d", out v, out p, out i) && v == "046D" && p == "082D" && i is null,
            "vid:pid form");
        Check(UsbCamId.TryParse(@"USB\VID_046D&PID_082D\5&2C1F7A8&0&0001", out v, out p, out i) && v == "046D" && i == @"USB\VID_046D&PID_082D\5&2C1F7A8&0&0001",
            "a full Windows instance id keeps VID/PID and becomes an instance key");
        Check(UsbCamId.TryParse("usb-046d_HD_Pro_Webcam_C920_ABCDEF12-video-index0", out v, out p, out i) && v == "" && i is not null,
            "a Linux by-id name (no VID_/PID_) is an instance key");
        Check(!UsbCamId.TryParse("   ", out _, out _, out _), "blank key is rejected");
    }

    [Fact]
    public void MatchRules()
    {
        var devs = new[]
        {
            Dev(0, "Integrated Camera", "04F2", "B6D9", @"USB\VID_04F2&PID_B6D9\0001"),
            Dev(1, "HD Pro Webcam C920", "046D", "082D", @"USB\VID_046D&PID_082D\5&2C1F7A8&0&0001"),
            Dev(2, "HD Pro Webcam C920", "046D", "082D", @"USB\VID_046D&PID_082D\5&2C1F7A8&0&0003"),
            Dev(3, "Capture Card", "", "", @"PCI\VEN_1234&DEV_5678\0"),
        };

        Check(UsbCamId.Match("VID_04F2&PID_B6D9", devs).Index == 0, "a unique VID/PID picks its device");

        var ex = Assert.Throws<InvalidOperationException>(() => UsbCamId.Match("VID_046D&PID_082D", devs));
        Check(ex.Message.Contains("Ambiguous") && ex.Message.Contains("0&0001") && ex.Message.Contains("0&0003"),
            $"two identical models are not silently disambiguated — the message lists both instance ids: {ex.Message}");

        Check(UsbCamId.Match(@"USB\VID_046D&PID_082D\5&2C1F7A8&0&0003", devs).Index == 2, "an instance id picks the exact device");

        ex = Assert.Throws<InvalidOperationException>(() => UsbCamId.Match(@"USB\VID_046D&PID_082D\5&2C1F7A8&0&0009", devs));
        Check(ex.Message.Contains("not found") && ex.Message.Contains("HD Pro Webcam C920") && ex.Message.Contains("Capture Card"),
            $"an unknown instance id does not fall back to VID/PID; the message lists every device: {ex.Message}");

        ex = Assert.Throws<InvalidOperationException>(() => UsbCamId.Match("VID_DEAD&PID_BEEF", devs));
        Check(ex.Message.Contains("Discovered 4 video device(s)"), $"not-found message counts the devices: {ex.Message}");

        ex = Assert.Throws<InvalidOperationException>(() => UsbCamId.Match("VID_DEAD&PID_BEEF", Array.Empty<UsbCamInfo>()));
        Check(ex.Message.Contains("No video devices"), $"empty enumeration is said plainly: {ex.Message}");
    }

    [Fact]
    public void LinuxSysfsTree()
    {
        // 합성 sysfs: video0 = 캡처 노드(USB 046D:082D), video1 = 같은 카메라의 메타데이터 노드(index 1 → 제외),
        // video2 = 다른 카메라(1234:5678). by-id 폴더는 없다 → 인스턴스 식별자는 노드 이름.
        var root = Path.Combine(Path.GetTempPath(), "cvinspect-sysfs-" + Guid.NewGuid().ToString("N"));
        try
        {
            void Node(string n, string index, string name, string modalias)
            {
                var d = Path.Combine(root, "sys", n);
                Directory.CreateDirectory(Path.Combine(d, "device"));
                File.WriteAllText(Path.Combine(d, "index"), index + "\n");
                File.WriteAllText(Path.Combine(d, "name"), name + "\n");
                File.WriteAllText(Path.Combine(d, "device", "modalias"), modalias + "\n");
            }
            Node("video0", "0", "HD Pro Webcam C920: HD Pro Webcam", "usb:v046Dp082Dd0016dcEFdsc02dp01ic0Eisc01ip00in00");
            Node("video1", "1", "HD Pro Webcam C920: HD Pro Webcam", "usb:v046Dp082Dd0016dcEFdsc02dp01ic0Eisc01ip02in02");
            Node("video2", "0", "USB2.0 Camera", "usb:v1234p5678d0100dcEFdsc02dp01ic0Eisc01ip00in00");

            var list = UsbCamId.EnumerateLinux(Path.Combine(root, "sys"), Path.Combine(root, "by-id-missing"), "/dev");
            Check(list.Count == 2 && list[0].Index == 0 && list[1].Index == 2, $"capture nodes only, in node order: {string.Join(" | ", list)}");
            Check(list[0].VendorId == "046D" && list[0].ProductId == "082D" && list[1].VendorId == "1234",
                "VID/PID come from device/modalias");
            Check(list[0].DevicePath == "/dev/video0" && list[1].DevicePath == "/dev/video2", "OpenCV opens Linux devices by path, not by index");
            Check(list[0].InstanceId == "video0", "without udev by-id the node name is the instance id");
            Check(UsbCamId.Match("1234:5678", list).DevicePath == "/dev/video2", "a key resolves to the device path");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void EnumerateOnThisOs()
    {
        // 카메라 유무와 무관하게 — 지원 OS 는 목록(빈 목록 포함)을 돌려주고, 그 밖은 미지원을 예외로 말한다.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var list = UsbCamId.Enumerate();
            Assert.NotNull(list);   // 던지지 않고 목록(빈 목록 포함)을 돌려주는 것이 계약이다
            Console.WriteLine($"UsbCamId.Enumerate(): {list.Count} device(s)" + (list.Count > 0 ? "\n  " + string.Join("\n  ", list) : ""));
        }
        else
        {
            Assert.Throws<PlatformNotSupportedException>(() => UsbCamId.Enumerate());
        }
    }
}
