using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CvInspect.Imaging;

/// <summary>USB 웹캠 한 대 — 열거 순번(OpenCV 가 여는 인덱스), 표시명, VID/PID, 인스턴스 식별자, (Linux) 장치 경로.</summary>
public sealed record UsbCamInfo(int Index, string Name, string VendorId, string ProductId, string InstanceId, string? DevicePath)
{
    /// <summary>"VID_046D&amp;PID_082D" — 설정에 적는 기본 형태.</summary>
    public string VidPid => VendorId.Length == 0 ? "(no VID/PID)" : $"VID_{VendorId}&PID_{ProductId}";

    public override string ToString()
        => $"#{Index} '{Name}' {VidPid} instance='{InstanceId}'" + (DevicePath is null ? "" : $" path={DevicePath}");
}

/// <summary>
/// USB 웹캠의 고정 식별. OpenCV 의 장치 인덱스는 OS 가 그때그때 매기는 순서라 재열거·재부팅·허브 순서 변경에 뒤바뀐다 —
/// 두 대짜리 설비에서 0번과 1번이 바뀌어도 아무 경고 없이 다른 카메라가 열린다. <see cref="CamOpt.SerialNumber"/> 에
/// VID/PID(또는 인스턴스 식별자)를 적으면 이 클래스가 현재 열거에서 그 장치를 찾아 여는 방법(인덱스 또는 장치 경로)을
/// 돌려주고, 못 찾으면 예외에 현재 목록을 실어 설정에 복사하게 한다.
///
/// 키 형식: <c>VID_046D&amp;PID_082D</c> · <c>046D:082D</c> · Windows 인스턴스 ID 전체
/// (<c>USB\VID_046D&amp;PID_082D\5&amp;2C1F7A8&amp;0&amp;0001</c>) · Linux by-id 이름. 같은 모델 두 대는 VID/PID 로 못 가르므로
/// 그때는 인스턴스 식별자를 적는다 — 후보가 둘 이상이면 조용히 첫 것을 열지 않고 예외로 목록을 보여 준다.
///
/// <b>검증 상태.</b> Windows: 서로 다른 모델 두 대(내장 + 외장)를 꽂은 Windows 11 PC 에서 이 이식본으로 실측 — SetupAPI 열거
/// 순번이 ANY·DSHOW·MSMF 세 백엔드 모두에서 OpenCV 인덱스와 일치했고 VID/PID 로 연 것이 그 카메라였다(원형도 웹캠 두 대짜리
/// 설비에서 실증됐고, WMI 의 순서는 맞지 않았다). 같은 모델 두 대(인스턴스 ID 경로)와 핫 재연결은 아직 안 쟀다.
/// Linux: 하드웨어에서 돌려 보지 않았다 — 합성 sysfs 트리 회귀만 있다. macOS: 미지원(예외).
/// </summary>
public static class UsbCamId
{
    private static readonly Regex VidPidRx = new(@"VID_([0-9A-Fa-f]{4}).*?PID_([0-9A-Fa-f]{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ColonRx = new(@"^([0-9A-Fa-f]{4}):([0-9A-Fa-f]{4})$", RegexOptions.Compiled);
    private static readonly Regex ModaliasRx = new(@"^usb:v([0-9A-Fa-f]{4})p([0-9A-Fa-f]{4})", RegexOptions.Compiled);

    /// <summary>키 해석 — VID/PID 가 있으면 그것을, 백슬래시가 둘 이상인 Windows 인스턴스 ID 나 VID/PID 없는 문자열은
    /// 인스턴스 식별자로 본다. 빈 키는 false.</summary>
    public static bool TryParse(string key, out string vendorId, out string productId, out string? instanceId)
    {
        vendorId = productId = string.Empty;
        instanceId = null;
        var k = (key ?? string.Empty).Trim();
        if (k.Length == 0) return false;

        var m = VidPidRx.Match(k);
        if (m.Success)
        {
            vendorId = m.Groups[1].Value.ToUpperInvariant();
            productId = m.Groups[2].Value.ToUpperInvariant();
            if (k.Count(c => c == '\\') >= 2) instanceId = k;   // "USB\VID_..&PID_..\<port path>" — 포트까지 지정한 것
            return true;
        }
        m = ColonRx.Match(k);
        if (m.Success)
        {
            vendorId = m.Groups[1].Value.ToUpperInvariant();
            productId = m.Groups[2].Value.ToUpperInvariant();
            return true;
        }
        instanceId = k;
        return true;
    }

    /// <summary>현재 OS 의 비디오 장치 열거. Windows·Linux 만 — 그 밖은 <see cref="PlatformNotSupportedException"/>.</summary>
    public static IReadOnlyList<UsbCamInfo> Enumerate()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return EnumerateWindows();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return EnumerateLinux("/sys/class/video4linux", "/dev/v4l/by-id", "/dev");
        throw new PlatformNotSupportedException(
            "USB camera identification is implemented for Windows (SetupAPI) and Linux (sysfs/udev) only. " +
            "On this OS set CamOpt.VideoSource to a device index and leave SerialNumber empty.");
    }

    /// <summary>키로 장치 하나를 고른다 — 없거나 여럿이면 예외(메시지에 현재 목록).</summary>
    public static UsbCamInfo Resolve(string key) => Match(key, Enumerate());

    internal static UsbCamInfo Match(string key, IReadOnlyList<UsbCamInfo> devices)
    {
        if (!TryParse(key, out var vid, out var pid, out var inst))
            throw new ArgumentException("Camera identity key is empty.", nameof(key));

        if (inst is not null)
        {
            var hit = devices.FirstOrDefault(d => string.Equals(d.InstanceId, inst, StringComparison.OrdinalIgnoreCase));
            // 인스턴스가 안 맞아도 VID/PID 로 다시 찾지 않는다 — 다른 포트의 같은 모델을 여는 것이 곧 사고다.
            return hit ?? throw NotFound(key, devices);
        }

        var hits = devices.Where(d => d.VendorId == vid && d.ProductId == pid).ToList();
        if (hits.Count == 1) return hits[0];
        if (hits.Count == 0) throw NotFound(key, devices);
        throw new InvalidOperationException(Describe(
            $"Ambiguous camera identity '{key}': {hits.Count} devices share {hits[0].VidPid}. Put one device's instance id into SerialNumber instead:", hits));
    }

    private static InvalidOperationException NotFound(string key, IReadOnlyList<UsbCamInfo> devices)
        => new(devices.Count == 0
            ? $"USB camera not found: SerialNumber='{key.Trim()}'. No video devices were enumerated on this machine."
            : Describe($"USB camera not found: SerialNumber='{key.Trim()}'. Discovered {devices.Count} video device(s) — copy the one you want into the camera settings:", devices));

    private static string Describe(string head, IReadOnlyList<UsbCamInfo> devices)
    {
        var sb = new StringBuilder(head);
        foreach (var d in devices) sb.Append('\n').Append("  ").Append(d);
        return sb.ToString();
    }

    // ---- Windows: SetupAPI. 열거 순번을 그대로 OpenCV 인덱스로 쓴다 — VID/PID 가 없는 장치도 자리를 차지한다(OpenCV 가 세는 방식과 같다).
    //      WMI(Win32_PnPEntity)는 논리/물리 순서를 주지 않아 인덱스와 맞지 않았다 — 원형이 그 이유로 SetupAPI 로 바꾼 것이다.

    private const string KsCategoryVideo = "{E5323777-F976-4F5B-9B55-B94699C46E44}";
    private const int DIGCF_PRESENT = 0x00000002;
    private const int DIGCF_DEVICEINTERFACE = 0x00000010;
    private const int SPDRP_HARDWAREID = 0x00000001;
    private const int SPDRP_FRIENDLYNAME = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, int memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, int property,
        out int propertyRegDataType, StringBuilder propertyBuffer, int propertyBufferSize, out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, StringBuilder deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    private static IReadOnlyList<UsbCamInfo> EnumerateWindows()
    {
        var list = new List<UsbCamInfo>();
        var guid = new Guid(KsCategoryVideo);
        var set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
            throw new InvalidOperationException($"SetupDiGetClassDevs failed (Win32 error {Marshal.GetLastWin32Error()}).");
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (var i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                var name = new StringBuilder(512);
                var hw = new StringBuilder(512);
                var inst = new StringBuilder(512);
                // 등록 속성 버퍼 크기는 바이트(유니코드 2바이트/문자), 인스턴스 ID 는 문자 수.
                var hasName = SetupDiGetDeviceRegistryProperty(set, ref data, SPDRP_FRIENDLYNAME, out _, name, name.Capacity * 2, out _);
                var hasHw = SetupDiGetDeviceRegistryProperty(set, ref data, SPDRP_HARDWAREID, out _, hw, hw.Capacity * 2, out _);
                SetupDiGetDeviceInstanceId(set, ref data, inst, inst.Capacity, out _);
                var m = hasHw ? VidPidRx.Match(hw.ToString()) : System.Text.RegularExpressions.Match.Empty;
                list.Add(new UsbCamInfo(
                    i,
                    hasName ? name.ToString() : string.Empty,
                    m.Success ? m.Groups[1].Value.ToUpperInvariant() : string.Empty,
                    m.Success ? m.Groups[2].Value.ToUpperInvariant() : string.Empty,
                    inst.ToString(),
                    null));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return list;
    }

    // ---- Linux: sysfs + udev. 순번 문제가 애초에 없다 — OpenCV 를 장치 경로(/dev/videoN)로 연다.
    //      VID/PID 는 device/modalias("usb:v046Dp082D…")에서, 인스턴스 식별자는 /dev/v4l/by-id 이름(시리얼 포함)에서.

#if NET6_0_OR_GREATER
    internal static IReadOnlyList<UsbCamInfo> EnumerateLinux(string sysRoot, string byIdRoot, string devRoot)
    {
        var list = new List<UsbCamInfo>();
        if (!Directory.Exists(sysRoot)) return list;

        // by-id 심링크 → 노드 이름. udev 가 없으면 이 표가 비고, 인스턴스 식별자는 노드 이름으로 떨어진다.
        var byId = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Directory.Exists(byIdRoot))
        {
            foreach (var link in Directory.GetFileSystemEntries(byIdRoot))
            {
                var fn = Path.GetFileName(link);
                if (!fn.EndsWith("-video-index0", StringComparison.Ordinal)) continue;
                string? target = null;
                try { target = new FileInfo(link).ResolveLinkTarget(true)?.FullName; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                if (target is not null) byId[Path.GetFileName(target)] = fn;
            }
        }

        foreach (var dir in Directory.GetDirectories(sysRoot, "video*").OrderBy(NodeNumber))
        {
            var node = Path.GetFileName(dir);
            var n = NodeNumber(dir);
            if (n < 0) continue;
            var idx = ReadTrim(Path.Combine(dir, "index"));
            if (idx is not null && idx != "0") continue;   // 메타데이터 노드(video-index1) 등 — 캡처 노드만
            var name = ReadTrim(Path.Combine(dir, "name")) ?? node;
            var m = ModaliasRx.Match(ReadTrim(Path.Combine(dir, "device", "modalias")) ?? string.Empty);
            list.Add(new UsbCamInfo(
                n, name,
                m.Success ? m.Groups[1].Value.ToUpperInvariant() : string.Empty,
                m.Success ? m.Groups[2].Value.ToUpperInvariant() : string.Empty,
                byId.TryGetValue(node, out var id) ? id : node,
                devRoot.TrimEnd('/') + "/" + node));   // Linux 경로 — Path.Combine 은 호스트 OS 구분자를 쓴다(테스트가 Windows 에서도 돈다)
        }
        return list;
    }
#else
    internal static IReadOnlyList<UsbCamInfo> EnumerateLinux(string sysRoot, string byIdRoot, string devRoot)
        => throw new PlatformNotSupportedException(
            "Linux camera identification needs the net6.0-or-later asset (symlink resolution); the netstandard2.1 build cannot enumerate /dev/v4l/by-id.");
#endif

    private static int NodeNumber(string dir)
        => int.TryParse(Path.GetFileName(dir).Substring("video".Length), out var n) ? n : -1;

    private static string? ReadTrim(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
