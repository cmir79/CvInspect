// 실카메라 검증 도구 — 벤더 SDK 없이 GigE 카메라를 열어, 어댑터가 실기에서 무엇을 보는지 남긴다.
// 설비 PC 에 폴더째 복사해 돌리고, 나온 로그와 이미지를 그대로 회수해 판정한다.
//
// 이 도구는 카메라 설정을 바꾸지 않는다(노출은 인자로 준 경우에만). 다만 GigE 는 제어권이 하나뿐이라
// 라인 소프트웨어가 카메라를 잡고 있으면 열리지 않는다 — 라인을 세운 뒤에 돌린다.

using System.Diagnostics;
using CvInspect;
using CvInspect.Imaging;
using CvInspect.Imaging.Gev;
using GevSharp;
using GevSharp.GenApi;
using GevSharp.Pfnc;
using OpenCvSharp;

var arg = new Args(args);
if (arg.Help) { Args.PrintUsage(); return 0; }

// 라이브러리 진단을 전부 화면·파일로 끌어낸다 — 붙이지 않으면 조용히 버려진다.
Directory.CreateDirectory(arg.OutDir);
var logPath = Path.Combine(arg.OutDir, "gevprobe.log");
using var logFile = new StreamWriter(logPath, append: true) { AutoFlush = true };
void Log(string line)
{
    var stamped = $"{DateTime.Now:HH:mm:ss.fff} {line}";
    Console.WriteLine(stamped);
    logFile.WriteLine(stamped);
}

CvLog.Sink = (level, src, msg, ex) => Log($"[{level,-7}] {src}: {msg}{(ex is null ? "" : " | " + ex.GetType().Name + ": " + ex.Message)}");
GevLog.MinLevel = arg.Verbose ? GevLogLevel.Debug : GevLogLevel.Info;
GevLog.Sink = (level, src, msg, ex) => Log($"[gev {level,-5}] {src}: {msg}{(ex is null ? "" : " | " + ex.GetType().Name + ": " + ex.Message)}");

Log($"=== gevprobe start === out={arg.OutDir}");

try
{
    var found = await GevDiscovery.DiscoverAsync(new GevDiscoveryOpt { TimeoutMs = arg.DiscoveryMs });
    Log($"discovered {found.Count} device(s)");
    foreach (var d in found)
        Log($"  SN='{d.SerialNumber.Trim()}' {d.Manufacturer} {d.Model} v{d.DeviceVersion} " +
            $"ip={d.Address}/{d.Subnet} mac={d.Mac} nic={d.InterfaceAddress} " +
            $"spec={d.SpecMajor}.{d.SpecMinor} directlyReachable={d.IsReachableDirectly}");

    if (arg.Serial is null)
    {
        Log("no --sn given — listing only. Re-run with --sn <serial> to open a camera.");
        return found.Count > 0 ? 0 : 2;
    }

    var info = found.FirstOrDefault(d => string.Equals(d.SerialNumber.Trim(), arg.Serial, StringComparison.Ordinal));
    if (info is null) { Log($"!! serial '{arg.Serial}' not among the discovered devices"); return 3; }

    await DumpFeaturesAsync(info, arg, Log);
    return await RunAdapterAsync(arg, Log);
}
catch (Exception ex)
{
    Log($"!! FAILED: {ex.GetType().Name}: {ex.Message}");
    Log(ex.ToString());
    return 1;
}

// ── 카메라가 무엇을 선언하는지 그대로 받아 적는다 (어댑터를 거치지 않고) ─────────────────
static async Task DumpFeaturesAsync(GevDeviceInfo info, Args arg, Action<string> log)
{
    log("--- opening directly to read features ---");
    await using var dev = await GevDevice.OpenAsync(info, new GevDeviceOpt { HeartbeatTimeoutMs = 3000 });
    log($"opened {dev.Address} via {dev.LocalAddress} | heartbeat applied={dev.DeviceHeartbeatTimeoutMs}ms period={dev.HeartbeatPeriodMs}ms " +
        $"tickFreq={dev.TimestampTickFrequency} gvcpCap=0x{dev.GvcpCapability:X8}");

    var nodes = await dev.GetNodeMapAsync();
    log($"node map: {nodes.Nodes.Count} nodes");

    // 노출 노드 이름 — 표준 세대차 때문에 현장에 둘 다 있다. 어느 쪽인지가 곧 어댑터 폴백의 실증이다.
    var exposureName = nodes.GetNode("ExposureTime") is not null ? "ExposureTime"
                     : nodes.GetNode("ExposureTimeAbs") is not null ? "ExposureTimeAbs" : "(none)";
    log($"exposure node: {exposureName}");

    foreach (var n in new[] { "PixelFormat", "Width", "Height", "WidthMax", "HeightMax", "OffsetX", "OffsetY",
                              "PayloadSize", "AcquisitionMode", "ChunkModeActive" })
        log($"  {n,-16} = {await ReadAnyAsync(nodes, n)}");
    foreach (var n in new[] { "ReverseX", "ReverseY" })
        log($"  {n,-16} = {await ReadAnyAsync(nodes, n)}");

    // Bayer 위상 — 선언과 기하 계산이 갈리는지. 색이 뒤집히는 사고가 여기서 미리 보인다.
    var codeVal = await ReadIntAsync(nodes, "PixelFormat");
    if (codeVal is { } raw && PixelFormatInfo.IsBayer((uint)raw))
    {
        var code = (uint)raw;
        var declared = PixelFormatInfo.BayerPattern(code);
        var revX = await ReadBoolAsync(nodes, "ReverseX") ?? false;
        var revY = await ReadBoolAsync(nodes, "ReverseY") ?? false;
        var offX = (int)(await ReadIntAsync(nodes, "OffsetX") ?? 0);
        var offY = (int)(await ReadIntAsync(nodes, "OffsetY") ?? 0);
        var maxW = (int)(await ReadIntAsync(nodes, "WidthMax") ?? await ReadIntAsync(nodes, "Width") ?? 0);
        var maxH = (int)(await ReadIntAsync(nodes, "HeightMax") ?? await ReadIntAsync(nodes, "Height") ?? 0);
        var roiW = (int)(await ReadIntAsync(nodes, "Width") ?? 0);
        var roiH = (int)(await ReadIntAsync(nodes, "Height") ?? 0);
        log($"bayer: declared={declared} format={PixelFormatInfo.Name(code)} depth={PixelFormatInfo.Depth(code)}");
        if (maxW > 0 && maxH > 0 && ToCv(declared) is { } dec)
        {
            // 미러의 기준 치수가 최대 치수인지 ROI 치수인지는 장치 구현에 달렸다 — 둘 다 계산해 남긴다.
            var byMax = CvBayerPhase.Effective(dec, maxW, maxH, revX, revY, offX, offY);
            var byRoi = roiW > 0 && roiH > 0 ? CvBayerPhase.Effective(dec, roiW, roiH, revX, revY, offX, offY) : byMax;
            log($"bayer: computed(byMax {maxW}x{maxH})={byMax}  computed(byRoi {roiW}x{roiH})={byRoi}  " +
                $"[revX={revX} revY={revY} off=({offX},{offY})]");
            if (byMax != dec || byRoi != dec)
                log("bayer: !! declared and computed disagree — if colours look wrong, pin GevCamOpt.BayerPatternOverride");
            if (byMax != byRoi)
                log("bayer: !! the two mirror-reference readings disagree — the saved image decides which is right");
        }
    }

    if (arg.RawSeconds > 0) await RawStreamAsync(dev, nodes, arg, log);
}

// ── 어댑터를 건너뛰고 취득 계층이 직접 보는 것을 남긴다 ────────────────────────────────
// 프레임 기하(줄 간격·이미지 크기·청크)와 전송 통계는 어댑터를 거치면 사라진다. 실기에서만
// 확인되는 것들이라(리센드 사본이 어떤 상태로 오는가, 요청이 회수로 이어지는가) 여기서 받아 둔다.
static async Task RawStreamAsync(GevDevice dev, GenApiNodeMap nodes, Args arg, Action<string> log)
{
    log($"--- raw stream for {arg.RawSeconds}s (adapter bypassed) ---");
    var payload = (int?)await ReadIntAsync(nodes, "PayloadSize");

    await using var stream = await dev.OpenStreamAsync(new GevStreamOpt
    {
        BufferCount = 8,
        DeliverIncompleteFrames = false,
        PayloadSize = payload,
    });

    var drops = new Dictionary<GevFrameDropReason, int>();
    stream.FrameDropped += d => { lock (drops) drops[d.Reason] = drops.TryGetValue(d.Reason, out var c) ? c + 1 : 1; };

    await stream.StartAsync();
    log($"stream: negotiated packetSize={stream.PacketSize} localPort={stream.LocalPort} payloadSize={payload?.ToString() ?? "(from leader)"}");
    log("  (the socket receive buffer the OS actually granted is in the [gev ...] lines above — it may be less than requested)");

    if (nodes.GetNode("AcquisitionMode") is IEnumeration mode && mode.GetEntry("Continuous") is not null)
        await mode.SetAsync("Continuous");
    if (nodes.GetNode("AcquisitionStart") is ICommand start) await start.ExecuteAsync();

    var sw = Stopwatch.StartNew();
    var got = 0;
    try
    {
        while (sw.Elapsed.TotalSeconds < arg.RawSeconds)
        {
            // 수신 시한은 반드시 넘긴 토큰으로 — 밖에서 씌우면 버려진 대기자가 다음 프레임을 삼킨다.
            using var cts = new CancellationTokenSource(2000);
            GevFrame frame;
            try { frame = await stream.ReceiveAsync(cts.Token); }
            catch (OperationCanceledException) { log("  (no frame within 2s)"); continue; }
            catch (Exception ex) { log($"  !! receive failed: {ex.GetType().Name}: {ex.Message}"); break; }

            try
            {
                got++;
                if (got <= 3)
                    log($"  frame#{got} id={frame.FrameId} {frame.Width}x{frame.Height} " +
                        $"fmt={PixelFormatInfo.Name(frame.PixelFormatCode)} depth={PixelFormatInfo.Depth(frame.PixelFormatCode)} " +
                        $"stride={frame.Stride} imageSize={frame.ImageSize} payloadSize={frame.PayloadSize} " +
                        $"chunk={frame.HasChunkData} padding=({frame.PaddingX},{frame.PaddingY}) " +
                        $"offset=({frame.OffsetX},{frame.OffsetY}) complete={frame.IsComplete} " +
                        $"packets={frame.ExpectedPackets - frame.MissingPackets}/{frame.ExpectedPackets}");
                if (got == 1)
                {
                    if (frame.Stride == 0)
                        log("  !! Stride == 0 — this geometry genuinely has no line pitch (packed format whose width does not land on a byte boundary)");
                    if (frame.HasChunkData && frame.ImageSize == frame.PayloadSize)
                        log("  !! HasChunkData is true but ImageSize == PayloadSize — the image/chunk split looks wrong");
                }
            }
            finally { frame.Dispose(); }
        }
    }
    finally
    {
        // 정지는 취소 없이 — 중간에 그만두면 카메라가 죽은 소켓으로 계속 쏜다.
        if (nodes.GetNode("AcquisitionStop") is ICommand stop)
            try { await stop.ExecuteAsync(CancellationToken.None); } catch (Exception ex) { log($"  AcquisitionStop failed: {ex.Message}"); }
        await stream.StopAsync(CancellationToken.None);
    }

    var s = stream.Stats.Snapshot();
    log($"raw stream: {got} frames in {sw.Elapsed.TotalSeconds:F1}s = {got / Math.Max(0.001, sw.Elapsed.TotalSeconds):F1} fps");
    log($"stats: {s}");
    log($"stats/resend: PacketsResent={s.PacketsResent} PacketsDuplicated={s.PacketsDuplicated} " +
        $"ResendRequests={s.ResendRequests} ResendRecovered={s.ResendRecovered} PacketsMissing={s.PacketsMissing}");
    if (s.PacketsResent == 0 && s.PacketsDuplicated > 0)
        log("  note: this camera returns resend copies with a normal success status — PacketsResent stays 0 even when resend works, and PacketsDuplicated carries the signal");
    if (s.ResendRequests > 0 && s.ResendRecovered == 0)
        log("  note: resend requests went out but nothing came back — the device may already have retired the block (PacketTimeoutMs too late)");
    if (s.FramesDroppedUnsupported > 0)
        log("  !! frames dropped as Unsupported — the camera sends a payload type we do not assemble (chunk mode on?)");
    lock (drops)
        if (drops.Count > 0)
            log("dropped frames by reason: " + string.Join(", ", drops.Select(kv => $"{kv.Key}={kv.Value}")));
}

// ── 어댑터를 통해 실제로 프레임을 받아 본다 ────────────────────────────────────────────
static async Task<int> RunAdapterAsync(Args arg, Action<string> log)
{
    log("--- opening through the adapter (GevCam) ---");
    var camOpt = new CamOpt
    {
        Name = "probe",
        ComType = "GigE",
        SerialNumber = arg.Serial!,
        IsColor = !arg.Mono,
        ExposureTimeUs = arg.ExposureUs ?? 0,
        UserSettings = arg.UserSet ?? string.Empty,
    };
    var gevOpt = new GevCamOpt { BayerToMono = arg.Mono, BayerPatternOverride = arg.BayerOverride };

    using var cam = new GevCam(camOpt, gevOpt);
    var frames = 0;
    var firstSaved = false;
    CamFrame? keep = null;

    cam.ConnectionChanged += (_, e) => log($"event: connection={e.IsConnected}");
    cam.GrabbingChanged += (_, g) => log($"event: grabbing={g}");
    cam.FrameAcquired += (_, f) =>
    {
        Interlocked.Increment(ref frames);
        if (!firstSaved) { firstSaved = true; keep = f; }
    };

    cam.Open();
    log($"adapter opened: connected={cam.IsConnected}");

    cam.GrabOne();
    if (keep is { } one)
    {
        var path = Path.Combine(arg.OutDir, "grab.png");
        SaveFrame(one, path, log);
        log($"single grab: {one.Width}x{one.Height} {one.Format} stride={one.Stride} -> {path}");
    }
    else log("!! single grab produced no frame");

    if (arg.Seconds > 0)
    {
        log($"--- continuous for {arg.Seconds}s ---");
        Volatile.Write(ref frames, 0);
        var sw = Stopwatch.StartNew();
        cam.StartContinuous();
        var lastReport = 0L;
        while (sw.Elapsed.TotalSeconds < arg.Seconds)
        {
            await Task.Delay(500);
            if (sw.Elapsed.TotalSeconds - lastReport >= 5)
            {
                lastReport = (long)sw.Elapsed.TotalSeconds;
                log($"  t={lastReport,4}s frames={Volatile.Read(ref frames)} ({Volatile.Read(ref frames) / sw.Elapsed.TotalSeconds:F1} fps)");
            }
        }
        cam.StopContinuous();
        sw.Stop();
        var got = Volatile.Read(ref frames);
        log($"continuous done: {got} frames in {sw.Elapsed.TotalSeconds:F1}s = {got / sw.Elapsed.TotalSeconds:F1} fps");

        if (keep is { } last)
        {
            var path = Path.Combine(arg.OutDir, "last.png");
            SaveFrame(last, path, log);
            log($"last frame saved -> {path}");
        }
    }

    cam.Close();
    log("adapter closed — now verify the camera can be reopened by the vendor tool (that proves control was released)");
    return 0;
}

static void SaveFrame(CamFrame f, string path, Action<string> log)
{
    try
    {
        using var mat = f.AsMat();
        if (!Cv2.ImEncode(".png", mat, out var bytes)) { log($"!! encode failed for {path}"); return; }
        File.WriteAllBytes(path, bytes);
    }
    catch (Exception ex) { log($"!! save failed: {ex.Message}"); }
}

static CvBayerPattern? ToCv(BayerPattern p) => p switch
{
    BayerPattern.RG => CvBayerPattern.RG,
    BayerPattern.GR => CvBayerPattern.GR,
    BayerPattern.GB => CvBayerPattern.GB,
    BayerPattern.BG => CvBayerPattern.BG,
    _ => null,
};

static async Task<string> ReadAnyAsync(GenApiNodeMap m, string name)
{
    var node = m.GetNode(name);
    if (node is null) return "(absent)";
    try
    {
        return node switch
        {
            IInteger i => (await i.GetAsync()).ToString(),
            IFloat f => (await f.GetAsync()).ToString("G6"),
            IBoolean b => (await b.GetAsync()).ToString(),
            IEnumeration e => $"{await e.GetAsync()} (0x{await e.GetIntValueAsync():X})",
            IString s => await s.GetAsync(),
            _ => $"({node.Kind})",
        };
    }
    catch (Exception ex) { return $"(unreadable: {ex.GetType().Name})"; }
}

static async Task<long?> ReadIntAsync(GenApiNodeMap m, string name)
{
    try
    {
        return m.GetNode(name) switch
        {
            IInteger i => await i.GetAsync(),
            IEnumeration e => await e.GetIntValueAsync(),
            _ => null,
        };
    }
    catch { return null; }
}

static async Task<bool?> ReadBoolAsync(GenApiNodeMap m, string name)
{
    if (m.GetNode(name) is not IBoolean b) return null;
    try { return await b.GetAsync(); } catch { return null; }
}

/// <summary>명령줄 인자.</summary>
sealed class Args
{
    public string? Serial { get; }
    public int Seconds { get; }
    public int RawSeconds { get; }
    public int DiscoveryMs { get; }
    public string OutDir { get; }
    public bool Mono { get; }
    public bool Verbose { get; }
    public bool Help { get; }
    public double? ExposureUs { get; }
    public string? UserSet { get; }
    public CvBayerPattern? BayerOverride { get; }

    public Args(string[] a)
    {
        string? Val(string key)
        {
            var i = Array.FindIndex(a, x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        }
        bool Has(string key) => Array.Exists(a, x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));

        Help = a.Length == 0 || Has("--help") || Has("-h");
        Serial = Val("--sn")?.Trim();
        Seconds = int.TryParse(Val("--seconds"), out var s) ? s : 0;
        RawSeconds = int.TryParse(Val("--raw-seconds"), out var r) ? r : 0;
        DiscoveryMs = int.TryParse(Val("--discovery-ms"), out var d) ? d : 1500;
        OutDir = Val("--out") ?? Path.Combine(AppContext.BaseDirectory, "probe-out");
        Mono = Has("--mono");
        Verbose = Has("--verbose");
        ExposureUs = double.TryParse(Val("--exposure-us"), out var e) ? e : null;
        UserSet = Val("--user-set");
        BayerOverride = Val("--bayer")?.ToUpperInvariant() switch
        {
            "RG" => CvBayerPattern.RG,
            "GR" => CvBayerPattern.GR,
            "GB" => CvBayerPattern.GB,
            "BG" => CvBayerPattern.BG,
            _ => null,
        };
    }

    public static void PrintUsage() => Console.WriteLine("""
        gevprobe — GigE 카메라 실기 검증 도구 (벤더 SDK 없이)

          gevprobe                                   보이는 카메라 목록만
          gevprobe --sn <시리얼>                      열어서 피처를 받아 적고 한 장 찍는다
          gevprobe --sn <시리얼> --seconds 60         그 뒤 60초 연속 취득

        옵션
          --sn <시리얼>          대상 카메라 (탐색 목록의 SN 과 정확히 일치)
          --seconds <N>         연속 취득 시간 (0=안 함)
          --raw-seconds <N>     어댑터를 건너뛴 취득 — 프레임 기하(줄 간격·이미지 크기·청크)와
                                전송 통계(리센드·중복·유실)를 남긴다. 취득 라이브러리 검증용.
          --discovery-ms <N>    탐색 대기 (기본 1500)
          --out <폴더>          로그·이미지 출력 (기본 실행 폴더의 probe-out)
          --mono                Bayer 를 흑백으로 접는다
          --bayer RG|GR|GB|BG   Bayer 패턴을 못 박는다 (색이 뒤집힐 때)
          --exposure-us <N>     노출 지정 (생략하면 카메라 현재 값을 건드리지 않는다)
          --user-set <이름>     여는 즉시 이 유저셋을 불러온다
          --verbose             취득 라이브러리 디버그 로그까지

        ⚠ GigE 는 제어권이 하나뿐이라 라인 소프트웨어가 카메라를 잡고 있으면 열리지 않는다.
          라인을 세운 뒤 돌리고, 끝나면 벤더 뷰어로 카메라가 다시 열리는지 확인한다(제어권 반납 확인).
        """);
}
