using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using CvInspect;
using CvInspect.Vision;
using CvInspect.Vision.Opts;
using OpenCvSharp;
using Xunit;
using CvBayerPattern = CvInspect.Imaging.Gev.CvBayerPattern;

namespace CvInspect.Tests;

/// <summary>
/// 회귀 묶음. 각 절은 실제로 잡힌 결함을 못 박은 것이라 이름보다 <b>메시지</b>가 근거다 —
/// 실패했을 때 무엇이 왜 틀렸는지 그 문장 하나로 읽히도록 쓴다.
/// </summary>
public class SmokeTests
{
    /// <summary>단언 하나 — 실패 메시지가 곧 그 회귀가 존재하는 이유다.</summary>
    private static void Check(bool cond, string name) => Assert.True(cond, name);

    /// <summary>번역 조회 — 리졸버 우선, 내장 표, 느슨한 파일의 키 단위 병합, 문화권 정규화.
    /// 이 절은 <b>순서를 지켜야 한다</b> — CvLoc 은 프로세스 전역이고 누락 이력과 적재 상태가
    /// 앞 단계에서 만들어진다. 그래서 하나로 묶어 둔다(쪼개면 서로의 전제를 지운다).</summary>
    [Fact]
    public void Localization()
    {
    // === 1) CvLoc — 내장 리소스 전용 배포 (느슨한 파일 자동 복사 없음) ===
    var langDir = Path.Combine(AppContext.BaseDirectory, "Assets", "lang");
    Check(!File.Exists(Path.Combine(langDir, "cv.ko.json")), "no auto-copied lang files (embedded-only packaging)");

    CvLoc.Culture = "ko";
    var ko = CvLoc.T("cv:AcceptScore");
    Check(ko == "수락 스코어", $"ko lookup from embedded: '{ko}'");

    CvLoc.Culture = "en";
    var en = CvLoc.T("cv:AcceptScore");
    Check(en != "AcceptScore" && en != ko && en.Length > 0, $"en lookup: '{en}'");

    // === 2) Resolver 우선 + null 폴백 ===
    CvLoc.Resolver = k => k == "cv:AcceptScore" ? "OVERRIDE" : null;
    Check(CvLoc.T("cv:AcceptScore") == "OVERRIDE", "resolver wins over builtin");
    Check(CvLoc.T("cv:BlobThreshold") != "BlobThreshold", "resolver null → builtin fallback");
    CvLoc.Resolver = null;

    // === 3) 미스: 키 원문 + MissingKey 1회 ===
    var missingCount = 0;
    string? missingMsg = null;
    CvLoc.MissingKey += m => { missingCount++; missingMsg = m; };
    var miss1 = CvLoc.T("cv:__NoSuchKey");
    var miss2 = CvLoc.T("cv:__NoSuchKey");
    Check(miss1 == "__NoSuchKey" && miss2 == "__NoSuchKey", $"missing key returns bare key: '{miss1}'");
    Check(missingCount == 1, $"MissingKey fired exactly once (got {missingCount}): {missingMsg}");

    // === 3b) Resolver 교체 시 누락 이력 초기화 — 같은 키가 다시 1회 경고 ===
    CvLoc.Resolver = k => null;
    var missBefore = missingCount;
    _ = CvLoc.T("cv:__NoSuchKey");
    Check(missingCount == missBefore + 1, "resolver swap clears warn-once history");
    CvLoc.Resolver = null;

    // === 4) 부분 느슨한 파일 = 키 단위 병합 (내장 표를 통째로 가리지 않는다) ===
    Directory.CreateDirectory(langDir);
    File.WriteAllText(Path.Combine(langDir, "cv.ko.json"), "{ \"AcceptScore\": \"LOOSE\" }");
    CvLoc.Reload();
    CvLoc.Culture = "ko";
    Check(CvLoc.T("cv:AcceptScore") == "LOOSE", "loose file overrides per key");
    var merged = CvLoc.T("cv:BlobThreshold");
    Check(merged == "고정 문턱", $"keys absent from loose file still resolve from embedded ko: '{merged}'");

    // === 4b) 문화권 정규화 — 지역 태그는 상위 언어로 접는다 ===
    CvLoc.Culture = "ko-KR";
    CvLoc.Reload();
    var koKr = CvLoc.T("cv:BlobThreshold");
    Check(koKr == "고정 문턱", $"ko-KR folds to ko: '{koKr}'");

    // === 4c) 내장 리소스 폴백 — 느슨한 파일 삭제 후 재적재 ===
    Directory.Delete(Path.Combine(AppContext.BaseDirectory, "Assets"), true);
    CvLoc.Reload();
    CvLoc.Culture = "ko";
    var koEmb = CvLoc.T("cv:AcceptScore");
    Check(koEmb == "수락 스코어", $"embedded fallback after deleting loose files: '{koEmb}'");

    }

    /// <summary>표준 System.ComponentModel 베이스로 조회되는지 — 프로퍼티 그리드가 이 라이브러리를 몰라도 되어야 한다.</summary>
    [Fact]
    public void PropertyMetadata()
    {
    // === 5) attribute — 표준 베이스로 조회, 번역·Order 판독 ===
    var prop = typeof(CvFindCircleOpt).GetProperty("NumCalipers")!;
    var cat = prop.GetCustomAttribute<CategoryAttribute>();
    Check(cat is CvCategoryAttribute, $"CategoryAttribute base query returns CvCategoryAttribute ({cat?.GetType().Name})");
    Check(cat is CvCategoryAttribute { Order: 2 }, "Order=2 readable");
    Check(((ICvOrderedCategory)cat!).Order == 2, "ICvOrderedCategory seam");
    var catText = cat!.Category;
    Check(catText != "cv:CatCaliper" && catText != "CatCaliper" && !catText.StartsWith("1.") && !catText.StartsWith("2."), $"category translated, no numeric prefix: '{catText}'");

    var dn = prop.GetCustomAttribute<DisplayNameAttribute>();
    Check(dn is CvNameAttribute && dn.DisplayName != "cv:NumCalipers" && dn.DisplayName != "NumCalipers", $"display name translated: '{dn?.DisplayName}'");
    var de = prop.GetCustomAttribute<DescriptionAttribute>();
    Check(de is CvDescAttribute && de.Description.Length > 0 && de.Description != "NumCalipersDesc" && !de.Description.StartsWith("cv:"), $"description translated: '{de?.Description}'");

    // 무번호 카테고리 = 맨 뒤 센티넬 + 0 이하 가드
    var shapeCat = typeof(CvColorSegmentOpt).GetProperty("Shape")!.GetCustomAttribute<CategoryAttribute>();
    Check(shapeCat is CvCategoryAttribute { Order: int.MaxValue }, "unnumbered category → Order=int.MaxValue");
    Check(new CvCategoryAttribute("cv:CatX", 0).Order == int.MaxValue && new CvCategoryAttribute("cv:CatX", -3).Order == int.MaxValue,
        "order<=0 folds to Unordered");

    }

    /// <summary>옵션 POCO 직렬화 — 델리게이트 프로퍼티 제외, enum 은 식별자 문자열.</summary>
    [Fact]
    public void OptionSerialization()
    {
    // === 6) 직렬화 — Action 프로퍼티 [JsonIgnore], enum 문자열 ===
    var patJson = JsonSerializer.Serialize(new CvPatternOpt());
    Check(!patJson.Contains("Train\":") || !patJson.Contains("System.Action"), "CvPatternOpt serializes (Action ignored)");
    Check(!patJson.Contains("TemplatePng"), "TemplatePng [JsonIgnore]");
    var segJson = JsonSerializer.Serialize(new CvColorSegmentOpt());
    Check(!segJson.Contains("\"Train\""), "CvColorSegmentOpt Action ignored");
    var round = JsonSerializer.Deserialize<CvPatternOpt>(patJson);
    Check(round is not null && Math.Abs(round.CoarseScale - 0.5) < 1e-12, "round-trip deserialize");

    // enum 은 식별자 문자열로 직렬화, 구판 정수 파일도 읽힌다
    Check(patJson.Contains("\"TrainShape\":\"Rect\""), $"enum serializes as string (TrainShape)");
    var blobJson = JsonSerializer.Serialize(new CvBlobOpt());
    Check(blobJson.Contains("\"Polarity\":\"Bright\""), "enum serializes as string (BlobPolarity)");
    var legacy = JsonSerializer.Deserialize<CvPatternOpt>("{\"TrainShape\":1}");
    Check(legacy is { TrainShape: CvTrainShape.Circle }, "legacy integer enum value still deserializes");

    }

    /// <summary>로그는 라이브러리가 쓰지 않고 호스트 싱크로만 나간다.</summary>
    [Fact]
    public void LogSink()
    {
    // === 7) CvLog sink ===
    var logHits = new List<string>();
    CvLog.Sink = (level, src, msg, ex) => logHits.Add($"{level}|{src}|{msg}");
    CvLog.Publish(CvLogLevel.Warning, "smoke", "hello");
    Check(logHits.Count == 1 && logHits[0].StartsWith("Warning|smoke"), "CvLog sink receives");

    }

    /// <summary>알고리즘 스모크 — 합성 원 이미지에서 서클 파인더(네이티브 경유).</summary>
    [Fact]
    public void CircleFinderOnSyntheticImage()
    {
        // 이 절은 크롭 경고가 CvLog 로 나가는지도 본다 — 싱크를 자기가 걸어 다른 절의 설정에 기대지 않는다.
        var logHits = new List<string>();
        CvLog.Sink = (level, src, msg, ex) => logHits.Add($"{level}|{src}|{msg}");

    // === 8) 알고리즘 스모크 — 합성 원 이미지에서 서클 파인더 (네이티브 경유) ===
    using (var img = new Mat(400, 400, MatType.CV_8UC1, Scalar.Black))
    {
        Cv2.Circle(img, new OpenCvSharp.Point(200, 200), 100, new Scalar(255), -1);
        Cv2.GaussianBlur(img, img, new OpenCvSharp.Size(5, 5), 0);

        var opt = new CvFindCircleOpt
        {
            CenterX = 192, CenterY = 207, Radius = 95,
            NumCalipers = 24, SearchLength = 60, ProjectionLength = 5,
            Polarity = CvEdgePolarity.Either, ContrastThreshold = 10,
        };
        var fit = CvCircleFinder.FindWithRefit(img, opt);
        Check(fit is { } f
              && Math.Abs(f.CenterX - 200) < 2 && Math.Abs(f.CenterY - 200) < 2
              && Math.Abs(f.Radius - 100) < 2,
            $"circle finder on synthetic image: {fit}");

        var pre = CvImageOps.Preprocess(img, new CvImageProcessOpt());
        Check(pre.Cols > 0 && pre.Rows > 0, $"preprocess: {pre.Cols}x{pre.Rows}");
        pre.Dispose();

        // 크롭 영역 이상 → 경고 로그 seam 경유 확인
        logHits.Clear();
        var badCrop = new CvImageProcessOpt { UseCrop = true, CropX = -999, CropY = -999, CropW = 1, CropH = 1 };
        var rect = CvImageOps.CropRectOf(img, badCrop);
        Check(rect.Width == 400 && logHits.Count == 1 && logHits[0].Contains("Crop region"), $"crop warn via CvLog: {logHits.FirstOrDefault()}");
    }
    }

    /// <summary>취득 계약과 프레임 소스 — CamFrame 수명, 변환 순서, 팩토리 선택.</summary>
    [Fact]
    public void AcquisitionContract()
    {
    // === 9) CvInspect.Imaging — 취득 계약·가상/비디오 소스 ===
    {
        // 9-1) VirtualCam 테스트 패턴 — GrabOne 이벤트 스코프에서 치수/타입 확인
        var vopt = new CvInspect.Imaging.CamOpt { ComType = "Virtual", IsColor = false };
        using (var cam = CvInspect.Imaging.CamFactory.Create(vopt))
        {
            Check(cam is CvInspect.Imaging.VirtualCam, $"factory: Virtual → VirtualCam ({cam.GetType().Name})");
            CvInspect.Imaging.CamFrame? held = null;
            var conn = new List<bool>();
            cam.ConnectionChanged += (_, e) => conn.Add(e.IsConnected);
            cam.FrameAcquired += (_, f) => held = f;
            cam.Open();
            cam.GrabOne();
            Check(held is { Width: 640, Height: 480, Format: CvInspect.Imaging.CamPixelFormat.Mono8 },
                $"VirtualCam test pattern 640x480 Mono8: {held?.Width}x{held?.Height} {held?.Format}");
            Check(conn.Count == 1 && conn[0], "ConnectionChanged(true) on Open");

            // 이벤트 밖 보관 안전(GC 소유) + AsMat 무복사 래핑
            cam.GrabOne();   // 다음 프레임이 발행돼도 held 는 유효해야 한다
            Check(held!.Pixels.Length == 640 * 480, "held frame stays valid after next grab (GC-owned)");
            using (var view = CvInspect.Imaging.CamFrameMatExt.AsMat(held))
                Check(view.Width == 640 && view.Height == 480 && view.Type() == MatType.CV_8UC1
                      && view.At<byte>(3, 5) == held.Pixels[3 * 640 + 5], "AsMat zero-copy view matches pixels");

            // 9-2) 연속 그랩 — 프레임 수집 + GrabbingChanged 쌍
            var grabEvents = new List<bool>();
            var frames = 0;
            cam.GrabbingChanged += (_, g) => grabEvents.Add(g);
            cam.FrameAcquired += (_, _) => Interlocked.Increment(ref frames);
            cam.StartContinuous();
            var t0 = Environment.TickCount;
            while (Volatile.Read(ref frames) < 3 && Environment.TickCount - t0 < 3000) Thread.Sleep(10);
            cam.StopContinuous();
            Check(Volatile.Read(ref frames) >= 3, $"continuous grab produced {frames} frames");
            Check(grabEvents.Count == 2 && grabEvents[0] && !grabEvents[1], "GrabbingChanged true→false");
        }

        // 9-3) 폴더 순환 공급 — 이름순 a→b→a
        var imgDir = Path.Combine(AppContext.BaseDirectory, "vcam-imgs");
        Directory.CreateDirectory(imgDir);
        using (var a = new Mat(50, 100, MatType.CV_8UC1, new Scalar(10)))
        using (var b = new Mat(100, 50, MatType.CV_8UC1, new Scalar(20)))
        {
            Cv2.ImWrite(Path.Combine(imgDir, "a.png"), a);
            Cv2.ImWrite(Path.Combine(imgDir, "b.png"), b);
        }
        using (var cam = new CvInspect.Imaging.VirtualCam(new CvInspect.Imaging.CamOpt { VirtualImageDir = imgDir }))
        {
            var sizes = new List<(int W, int H)>();
            cam.FrameAcquired += (_, f) => sizes.Add((f.Width, f.Height));
            cam.Open();
            cam.GrabOne(); cam.GrabOne(); cam.GrabOne();
            Check(sizes.Count == 3 && sizes[0] == (100, 50) && sizes[1] == (50, 100) && sizes[2] == (100, 50),
                $"folder replay name-ordered cycle: {string.Join(" ", sizes)}");
        }

        // 9-4) CamXform — Rotate90 치수 스왑 + Flip 원본 유지
        using (var cam = new CvInspect.Imaging.VirtualCam(new CvInspect.Imaging.CamOpt
            { VirtualImageDir = imgDir, Rotation = CvInspect.Imaging.RotateMode.Rotate90 }))
        {
            (int W, int H) size = default;
            cam.FrameAcquired += (_, f) => size = (f.Width, f.Height);
            cam.Open();
            cam.GrabOne();
            Check(size == (50, 100), $"Rotate90 swaps dims (100x50→50x100): {size}");
        }

        // 9-4b) 변환 순서 계약 — 회전 먼저 → 플립 (비가환이라 순서가 곧 계약)
        using (var srcA = new Mat(2, 3, MatType.CV_8UC1))
        {
            srcA.SetArray(new byte[] { 1, 2, 3, 4, 5, 6 });
            using var expectR = new Mat();
            Cv2.Rotate(srcA, expectR, RotateFlags.Rotate90Clockwise);
            Cv2.Flip(expectR, expectR, OpenCvSharp.FlipMode.Y);
            var got2 = CvInspect.Imaging.CamXform.Apply(srcA, CvInspect.Imaging.FlipMode.Horizontal, CvInspect.Imaging.RotateMode.Rotate90);
            try
            {
                Check(got2.Rows == expectR.Rows && got2.Cols == expectR.Cols
                      && Cv2.Norm(got2, expectR, NormTypes.L1) == 0,
                    "CamXform order contract: rotate first, then flip");
            }
            finally { if (!ReferenceEquals(got2, srcA)) got2.Dispose(); }
        }

        // 9-4c) 비ASCII(한글) 폴더/파일 경로 — ImDecode 경유라 동작해야 한다
        var koDir = Path.Combine(AppContext.BaseDirectory, "한글 이미지");
        Directory.CreateDirectory(koDir);
        using (var m = new Mat(30, 40, MatType.CV_8UC1, new Scalar(77)))
        {
            Cv2.ImEncode(".png", m, out var png);
            File.WriteAllBytes(Path.Combine(koDir, "샘플.png"), png);
        }
        using (var cam = new CvInspect.Imaging.VirtualCam(new CvInspect.Imaging.CamOpt { VirtualImageDir = koDir }))
        {
            CvInspect.Imaging.CamFrame? kf = null;
            cam.FrameAcquired += (_, f) => kf = f;
            cam.Open();
            cam.GrabOne();
            Check(kf is { Width: 40, Height: 30 } && kf.Pixels[0] == 77,
                $"Korean-path folder decode: {kf?.Width}x{kf?.Height} v={kf?.Pixels[0]}");
        }

        // CamOpt 기본값 (No/Enabled — 다중 카메라 라우팅용)
        Check(new CvInspect.Imaging.CamOpt() is { No: 1, Enabled: true }, "CamOpt defaults No=1 Enabled=true");

        // Imaging enum 도 문자열 직렬화 + 구판 정수 역직렬화 (코어와 동일 계약)
        var camJson = JsonSerializer.Serialize(new CvInspect.Imaging.CamOpt { Rotation = CvInspect.Imaging.RotateMode.Rotate90 });
        Check(camJson.Contains("\"Rotation\":\"Rotate90\"") && camJson.Contains("\"Flip\":\"None\""),
            "Imaging enums serialize as strings");
        var camLegacy = JsonSerializer.Deserialize<CvInspect.Imaging.CamOpt>("{\"Rotation\":180,\"Flip\":1}");
        Check(camLegacy is { Rotation: CvInspect.Imaging.RotateMode.Rotate180, Flip: CvInspect.Imaging.FlipMode.Horizontal },
            "Imaging legacy integer enum values still deserialize");

        // 9-5) 팩토리 — extern 등록 우선 + 미지정 폴백
        CvInspect.Imaging.CamFactory.Register("SmokeVendor", o => new CvInspect.Imaging.VirtualCam(o));
        using (var ext = CvInspect.Imaging.CamFactory.Create(new CvInspect.Imaging.CamOpt { ComType = "SmokeVendor" }))
            Check(ext is CvInspect.Imaging.VirtualCam, "factory: extern-registered ComType resolves");
        // 모르는 ComType 은 가상 카메라가 아니라 죽은 자리로 — 합성 프레임이 검사로 흘러드는 것이 멈추는 것보다 나쁘다
        using (var fb = CvInspect.Imaging.CamFactory.Create(new CvInspect.Imaging.CamOpt { ComType = "NoSuchVendor" }))
        {
            Check(fb is CvInspect.Imaging.DeadCam, $"factory: unknown ComType → DeadCam, not a synthetic source ({fb.GetType().Name})");
            Check(fb is CvInspect.Imaging.DeadCam d2 && d2.Reason.Contains("NoSuchVendor") && d2.Reason.Contains("SmokeVendor"),
                $"the reason names the bad ComType and lists what is registered: {(fb as CvInspect.Imaging.DeadCam)?.Reason}");
            fb.Open();   // 던지지 않는다 — 나머지 카메라가 떠야 한다
            Check(!fb.IsConnected, "the dead slot stays disconnected");
        }

        // 9-6) VideoCaptureCam — MJPG avi 순환 재생 (VideoWriter 가용 시)
        var aviPath = Path.Combine(AppContext.BaseDirectory, "smoke.avi");
        var wrote = false;
        using (var vw = new VideoWriter(aviPath, FourCC.MJPG, 10, new OpenCvSharp.Size(160, 120), true))
        {
            if (vw.IsOpened())
            {
                using var f = new Mat(120, 160, MatType.CV_8UC3);
                for (var i = 0; i < 5; i++) { f.SetTo(new Scalar(i * 40, 0, 0)); vw.Write(f); }
                wrote = true;
            }
        }
        if (wrote)
        {
            using var cam = CvInspect.Imaging.CamFactory.Create(new CvInspect.Imaging.CamOpt
                { ComType = "VideoCapture", VideoSource = aviPath, IsColor = false, FrameRate = 200 });
            Check(cam is CvInspect.Imaging.VideoCaptureCam, "factory: VideoCapture → VideoCaptureCam");
            var vFrames = 0; CvInspect.Imaging.CamPixelFormat? vfmt = null;
            cam.FrameAcquired += (_, f) => { vFrames++; vfmt = f.Format; };
            cam.Open();
            for (var i = 0; i < 8; i++) cam.GrabOne();   // 5프레임 파일 → 8회 = 되감기 순환 검증
            Check(vFrames == 8 && vfmt == CvInspect.Imaging.CamPixelFormat.Mono8,
                $"VideoCaptureCam file loop + gray fold: frames={vFrames} fmt={vfmt}");
            cam.Close();
        }
        else
        {
            Console.WriteLine("SKIP VideoCaptureCam — VideoWriter(MJPG) unavailable in this environment");
        }
    }
    }

    /// <summary>재연결 데코레이터의 불변식 — 의도 보존, 루프 단일성, 갭 중 조작, 청산 후 미부활.</summary>
    [Fact]
    public void ReconnectingCam()
    {
    // === 10) ReconnectingCam — 재연결 데코레이터 ===
    {
        static bool Wait(Func<bool> cond, int ms = 2000)
        {
            var t0 = Environment.TickCount;
            while (!cond() && Environment.TickCount - t0 < ms) Thread.Sleep(5);
            return cond();
        }
        var fastOpt = new CvInspect.Imaging.CamReconnectOpt { BackoffMs = new[] { 20 }, ShutdownWaitMs = 1000 };

        // 10-1) 행복 경로 — 상실 → 재연결 → 연속취득 자동 재개, 이벤트는 전이당 1회씩만
        {
            var made = new List<FakeCam>();
            using var cam = new CvInspect.Imaging.ReconnectingCam(() => { var c = new FakeCam(); made.Add(c); return c; }, fastOpt);
            var conn = new List<bool>(); var grab = new List<bool>();
            cam.ConnectionChanged += (_, e) => { lock (conn) conn.Add(e.IsConnected); };
            cam.GrabbingChanged += (_, g) => { lock (grab) grab.Add(g); };
            cam.Open();
            cam.StartContinuous();
            made[0].LoseConnection();
            Check(Wait(() => made.Count == 2 && cam.IsConnected), $"reconnect creates a fresh instance (made={made.Count} connected={cam.IsConnected})");
            Check(Wait(() => cam.IsGrabbing), "continuous grab resumed after reconnect");
            Check(Wait(() => conn.Count == 3), $"ConnectionChanged edges true,false,true: [{string.Join(",", conn)}]");
            Check(conn.Count == 3 && conn[0] && !conn[1] && conn[2], $"connection edge order: [{string.Join(",", conn)}]");
            Check(grab.Count == 3 && grab[0] && !grab[1] && grab[2], $"grabbing edge order true,false,true: [{string.Join(",", grab)}]");
            Check(made[0].Disposed, "old instance disposed on swap");
        }

        // 10-2) 연속취득 의도가 없으면 재연결 후에도 시작되지 않는다
        {
            var made = new List<FakeCam>();
            using var cam = new CvInspect.Imaging.ReconnectingCam(() => { var c = new FakeCam(); made.Add(c); return c; }, fastOpt);
            var grab = 0;
            cam.GrabbingChanged += (_, _) => Interlocked.Increment(ref grab);
            cam.Open();
            made[0].LoseConnection();
            Check(Wait(() => made.Count == 2), "reconnect without continuous intent");
            Thread.Sleep(60);
            Check(grab == 0 && !cam.IsGrabbing, $"no grabbing events when intent absent (events={grab})");
        }

        // 10-3) StopContinuous 로 청산한 의도는 재연결이 되살리지 않는다
        {
            var made = new List<FakeCam>();
            using var cam = new CvInspect.Imaging.ReconnectingCam(() => { var c = new FakeCam(); made.Add(c); return c; }, fastOpt);
            cam.Open(); cam.StartContinuous(); cam.StopContinuous();
            made[0].LoseConnection();
            Check(Wait(() => made.Count == 2), "reconnect after explicit stop");
            Thread.Sleep(60);
            Check(!cam.IsGrabbing && !made[1].IsGrabbing, "stopped intent is not revived by reconnect");
        }

        // 10-4) 명시적 Close 이후 부활 금지 — 백오프 대기 중 Close 하면 새 인스턴스가 안 생긴다
        {
            var made = new List<FakeCam>();
            var slow = new CvInspect.Imaging.CamReconnectOpt { BackoffMs = new[] { 400 }, ShutdownWaitMs = 1000 };
            using var cam = new CvInspect.Imaging.ReconnectingCam(() => { var c = new FakeCam(); made.Add(c); return c; }, slow);
            cam.Open();
            made[0].LoseConnection();
            Thread.Sleep(30);
            cam.Close();
            var atClose = made.Count;
            Thread.Sleep(600);
            Check(made.Count == atClose && !cam.IsConnected, $"Close gate blocks revival (made {atClose}→{made.Count})");
        }

        // 10-5) 중복 기동 억제 — 상실 통지를 연속으로 퍼부어도 재연결 루프는 하나
        {
            var made = new List<FakeCam>();
            using var cam = new CvInspect.Imaging.ReconnectingCam(
                () => { var c = new FakeCam { FailOnOpen = true }; made.Add(c); return c; },
                new CvInspect.Imaging.CamReconnectOpt { BackoffMs = new[] { 50 }, ShutdownWaitMs = 500 });
            var first = new FakeCam();
            // 최초 Open 은 성공해야 하므로 실패 팩토리 앞에 정상 인스턴스를 하나 세운다
            using var cam2 = new CvInspect.Imaging.ReconnectingCam(
                () => { var c = new FakeCam { FailOnOpen = made.Count > 0 }; made.Add(c); return c; },
                new CvInspect.Imaging.CamReconnectOpt { BackoffMs = new[] { 50 }, ShutdownWaitMs = 500 });
            cam2.Open();
            for (var i = 0; i < 10; i++) made[0].LoseConnection();
            Thread.Sleep(300);
            Check(made.Count < 15, $"loss storm does not multiply reconnect loops (attempts={made.Count}, 10 loops would be ~40+)");
        }

        // 10-6) 런타임 노출값이 새 인스턴스에 재적용된다
        {
            var made = new List<FakeCam>();
            using var cam = new CvInspect.Imaging.ReconnectingCam(() => { var c = new FakeCam(); made.Add(c); return c; }, fastOpt);
            cam.Open();
            cam.SetExposureTimeUs(4321);
            made[0].LoseConnection();
            Check(Wait(() => made.Count == 2 && made[1].LastExposure == 4321),
                $"runtime exposure survives reconnect (new instance exposure={made.ElementAtOrDefault(1)?.LastExposure})");
        }

        // 10-7) 폐기된 인스턴스의 유령 이벤트는 밖으로 나가지 않는다
        {
            var made = new List<FakeCam>();
            using var cam = new CvInspect.Imaging.ReconnectingCam(() => { var c = new FakeCam(); made.Add(c); return c; }, fastOpt);
            var frames = 0;
            cam.FrameAcquired += (_, _) => Interlocked.Increment(ref frames);
            cam.Open();
            var old = made[0];
            old.LoseConnection();
            Check(Wait(() => made.Count == 2), "swap happened before ghost test");
            old.EmitFrame(new CvInspect.Imaging.CamFrame(new byte[4], 2, 2, 2, CvInspect.Imaging.CamPixelFormat.Mono8));
            old.LoseConnection();
            Thread.Sleep(60);
            Check(frames == 0, $"frames from a retired instance are blocked (leaked={frames})");
            Check(made.Count == 2, $"ghost loss does not trigger another reconnect (made={made.Count})");
            made[1].EmitFrame(new CvInspect.Imaging.CamFrame(new byte[4], 2, 2, 2, CvInspect.Imaging.CamPixelFormat.Mono8));
            Check(Wait(() => frames == 1), "current instance frames still relayed");
        }

        // 10-8) 미연결 구간의 GrabOne 은 조용히 무시되지 않고 명확히 실패한다
        {
            var made = new List<FakeCam>();
            var slow = new CvInspect.Imaging.CamReconnectOpt { BackoffMs = new[] { 400 }, ShutdownWaitMs = 1000 };
            using var cam = new CvInspect.Imaging.ReconnectingCam(() => { var c = new FakeCam(); made.Add(c); return c; }, slow);
            cam.Open();
            made[0].LoseConnection();
            Thread.Sleep(40);
            var threw = false;
            try { cam.GrabOne(); } catch (InvalidOperationException) { threw = true; }
            Check(threw, "GrabOne during the reconnect gap fails explicitly");
            cam.SetExposureTimeUs(999);   // 상태 계열은 예외 없이 의도로 기록
            Check(Wait(() => made.Count == 2 && made[1].LastExposure == 999, 2000), "state calls during the gap are recorded and applied on reconnect");
        }

        // 10-8b) 기동 시점에 없던 카메라 — RetryInitialOpen 이면 Open 이 안 던지고 붙을 때 합류한다
        {
            var made = new List<FakeCam>();
            var absent = true;
            using var cam = new CvInspect.Imaging.ReconnectingCam(
                () => { var c = new FakeCam { FailOnOpen = Volatile.Read(ref absent) }; made.Add(c); return c; },
                new CvInspect.Imaging.CamReconnectOpt { BackoffMs = new[] { 20 }, RetryInitialOpen = true, ShutdownWaitMs = 1000 });
            var conn = new List<bool>();
            cam.ConnectionChanged += (_, e) => { lock (conn) conn.Add(e.IsConnected); };
            cam.Open();                                   // 카메라가 아직 없다
            Check(!cam.IsConnected, "initial open does not throw when RetryInitialOpen is set");
            cam.StartContinuous();                        // 의도만 기록된다
            Volatile.Write(ref absent, false);            // 카메라가 붙었다
            Check(Wait(() => cam.IsConnected), "the camera joins once it appears");
            Check(Wait(() => cam.IsGrabbing), "the recorded continuous intent is honoured on join");
            Check(conn.Count == 1 && conn[0], $"only one connection edge, and it is 'connected': [{string.Join(",", conn)}]");
        }

        // 10-8c) 기본값에서는 첫 Open 실패가 그대로 올라온다(조용히 죽은 자리를 만들지 않는다)
        {
            using var cam = new CvInspect.Imaging.ReconnectingCam(
                () => new FakeCam { FailOnOpen = true },
                new CvInspect.Imaging.CamReconnectOpt { BackoffMs = new[] { 20 }, ShutdownWaitMs = 500 });
            var threw = false;
            try { cam.Open(); } catch (InvalidOperationException) { threw = true; }
            Check(threw, "initial open still throws by default");
        }

        // 10-9) 백오프 대기 중 Dispose 가 유한 시간에 끝난다(좀비 루프 없음)
        {
            var made = new List<FakeCam>();
            var slow = new CvInspect.Imaging.CamReconnectOpt { BackoffMs = new[] { 5000 }, ShutdownWaitMs = 1000 };
            var cam = new CvInspect.Imaging.ReconnectingCam(() => { var c = new FakeCam(); made.Add(c); return c; }, slow);
            cam.Open();
            made[0].LoseConnection();
            Thread.Sleep(40);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            cam.Dispose();
            sw.Stop();
            Check(sw.ElapsedMilliseconds < 1500, $"Dispose drains a waiting reconnect loop promptly ({sw.ElapsedMilliseconds}ms)");
            var disposedThrew = false;
            try { cam.Open(); } catch (ObjectDisposedException) { disposedThrew = true; }
            Check(disposedThrew, "disposed decorator rejects further use");
        }
    }
    }

    /// <summary>쓸 수 없는 자리를 메우는 널 오브젝트 — 던지지 않고, 발화하지 않고, 이유를 남긴다.</summary>
    [Fact]
    public void DeadCam()
    {
    // === 10-D) DeadCam — 쓸 수 없는 자리를 메운다 ===
    {
        using var dead = new CvInspect.Imaging.DeadCam(new CvInspect.Imaging.CamOpt { Name = "cam3" }, "serial not configured");
        var frames = 0;
        dead.FrameAcquired += (_, _) => frames++;
        dead.Open();                       // 던지지 않는다 — 나머지 카메라가 뜨는 것이 목적이다
        dead.StartContinuous();
        Check(!dead.IsConnected && !dead.IsGrabbing && frames == 0, "DeadCam never connects and never emits");
        // 조작에 예외를 던지지 않는다 — 프레임은 이벤트로 오므로 호출부는 "안 오는 경우"를 이미 다뤄야 하고,
        // 예외로 대신하면 타임아웃을 실패 회신으로 바꾸는 호스트에서 그 회신 경로를 건너뛴다.
        var logged = new List<string>();
        var prevSink = CvLog.Sink;
        CvLog.Sink = (lvl, src, msg, _) => { if (src == "DeadCam") logged.Add(msg); };
        dead.GrabOne();
        dead.SetExposureTimeUs(1000);
        CvLog.Sink = prevSink;
        Check(frames == 0, "DeadCam still emits nothing after GrabOne");
        Check(logged.Any(m => m.Contains("GrabOne ignored") && m.Contains("serial not configured")),
            "GrabOne leaves a reason in the log instead of throwing");
        Check(logged.Any(m => m.Contains("SetExposureTimeUs ignored")),
            "operations that lose user intent are logged, not swallowed");

        // Dispose 뒤에는 던진다 — 그건 호출부의 수명 관리 버그라 숨기면 안 된다
        var dead2 = new CvInspect.Imaging.DeadCam(null, "gone");
        dead2.Dispose();
        var threw = false;
        try { dead2.GrabOne(); } catch (ObjectDisposedException) { threw = true; }
        Check(threw, "DeadCam still rejects use after Dispose");
    }
    }

    /// <summary>PFNC 화소 변환과 Bayer 위상 대수.</summary>
    [Fact]
    public void GevPixelAndBayerPhase()
    {
    // === 11) CvInspect.Imaging.Gev — PFNC 변환·Bayer 위상 ===
    {
        static byte[] Mosaic(int w, int h, CvBayerPattern pat, byte b, byte g, byte r)
        {
            var tile = pat switch
            {
                CvBayerPattern.RG => new[,] { { 'R', 'G' }, { 'G', 'B' } },
                CvBayerPattern.GR => new[,] { { 'G', 'R' }, { 'B', 'G' } },
                CvBayerPattern.GB => new[,] { { 'G', 'B' }, { 'R', 'G' } },
                _ => new[,] { { 'B', 'G' }, { 'G', 'R' } },
            };
            var buf = new byte[w * h];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    buf[y * w + x] = tile[y & 1, x & 1] switch { 'R' => r, 'G' => g, _ => b };
            return buf;
        }

        // 11-1) 4개 패턴 전부 — 순색 빨강 모자이크가 빨강으로 복원되는가 (매핑 실측의 회귀 고정)
        foreach (var pat in new[] { CvBayerPattern.RG, CvBayerPattern.GR, CvBayerPattern.GB, CvBayerPattern.BG })
        {
            var frame = CvInspect.Imaging.Gev.GevFrameConv.ToCamFrame(
                Mosaic(64, 64, pat, 0, 0, 255), 64, 64, 64,
                CvInspect.Imaging.Gev.GevPixelLayout.Bayer, 8, pat);
            var o = 32 * frame.Stride + 32 * 3;
            Check(frame.Format == CvInspect.Imaging.CamPixelFormat.Bgr24
                  && frame.Pixels[o] < 8 && frame.Pixels[o + 1] < 8 && frame.Pixels[o + 2] > 247,
                $"Bayer{pat}8 pure red restores as red (BGR {frame.Pixels[o]},{frame.Pixels[o + 1]},{frame.Pixels[o + 2]})");
        }

        // 11-2) 파랑도 확인 — R/B 뒤바뀜은 빨강만으로도 잡히지만 대칭 확인
        {
            var frame = CvInspect.Imaging.Gev.GevFrameConv.ToCamFrame(
                Mosaic(32, 32, CvBayerPattern.RG, 255, 0, 0), 32, 32, 32,
                CvInspect.Imaging.Gev.GevPixelLayout.Bayer, 8, CvBayerPattern.RG);
            var o = 16 * frame.Stride + 16 * 3;
            Check(frame.Pixels[o] > 247 && frame.Pixels[o + 2] < 8,
                $"BayerRG8 pure blue restores as blue (BGR {frame.Pixels[o]},{frame.Pixels[o + 1]},{frame.Pixels[o + 2]})");
        }

        // 11-3) PFNC 코드 해석
        {
            Check(CvInspect.Imaging.Gev.GevPfnc.BitsPerPixel(CvInspect.Imaging.Gev.GevPfnc.Mono16) == 16
                  && CvInspect.Imaging.Gev.GevPfnc.BitsPerPixel(CvInspect.Imaging.Gev.GevPfnc.Bgr8) == 24,
                "PFNC bits-per-pixel decoded from the code");
            var ok = CvInspect.Imaging.Gev.GevPfnc.TryDescribe(CvInspect.Imaging.Gev.GevPfnc.BayerGB8, out var lay, out var bpp, out var bay);
            Check(ok && lay == CvInspect.Imaging.Gev.GevPixelLayout.Bayer && bpp == 8 && bay == CvBayerPattern.GB, "PFNC BayerGB8 described");
            // ⚠ 점유 비트(16)가 아니라 유효 비트(10/12)를 내야 한다 — 점유 비트를 넘기면 화면이 4배·16배 어두워진다
            CvInspect.Imaging.Gev.GevPfnc.TryDescribe(CvInspect.Imaging.Gev.GevPfnc.Mono10, out _, out var d10, out _);
            CvInspect.Imaging.Gev.GevPfnc.TryDescribe(CvInspect.Imaging.Gev.GevPfnc.Mono12, out _, out var d12, out _);
            Check(d10 == 10 && d12 == 12 && CvInspect.Imaging.Gev.GevPfnc.BitsPerPixel(CvInspect.Imaging.Gev.GevPfnc.Mono10) == 16,
                $"TryDescribe yields significant bits, not occupancy (Mono10 depth={d10} occupancy=16, Mono12 depth={d12})");
            Check(!CvInspect.Imaging.Gev.GevPfnc.TryDescribe(0x010C0004, out _, out _, out _), "packed format reported as not directly describable");
        }

        // 11-4) Mono16 → Mono8 다운시프트, RGB8 → Bgr24 채널 스왑
        {
            var px = new byte[4 * 2 * 2];
            for (var i = 0; i < 4; i++) { px[i * 2] = 0x00; px[i * 2 + 1] = 0x80; }   // 0x8000 (LE)
            var f = CvInspect.Imaging.Gev.GevFrameConv.ToCamFrame(px, 2, 2, 4, CvInspect.Imaging.Gev.GevPixelLayout.Mono, 16);
            Check(f.Format == CvInspect.Imaging.CamPixelFormat.Mono8 && f.Pixels[0] == 0x80, $"Mono16 → Mono8 keeps the high byte (got {f.Pixels[0]:X2})");

            // Mono10/12 는 16비트 그릇에 우측 정렬로 담긴다 — 포화값이 255 로 와야 한다(점유 비트로 접으면 63·15).
            static byte Depth(int max, int bits)
            {
                var b = new byte[2 * 2 * 2];
                for (var i = 0; i < 4; i++) { b[i * 2] = (byte)(max & 0xFF); b[i * 2 + 1] = (byte)(max >> 8); }
                return CvInspect.Imaging.Gev.GevFrameConv.ToCamFrame(b, 2, 2, 4, CvInspect.Imaging.Gev.GevPixelLayout.Mono, bits).Pixels[0];
            }
            var v10 = Depth(1023, 10);
            var v12 = Depth(4095, 12);
            Check(v10 == 255 && v12 == 255, $"Mono10/12 saturate to 255, not to occupancy-folded values (10bit→{v10}, 12bit→{v12})");

            // 줄 간격 없음(0) → 빈틈없는 행으로 접는다. 취득 계층은 압축 포맷에서 줄이 바이트 경계에 안
            // 떨어질 때 0 을 알리는데, 푼 뒤에는 행이 정수 바이트라 CamFrame.Stride 가 양수여야 한다.
            var flat = new byte[] { 0, 10, 20, 30, 40, 50 };
            var f0 = CvInspect.Imaging.Gev.GevFrameConv.ToCamFrame(flat, 3, 2, 0, CvInspect.Imaging.Gev.GevPixelLayout.Mono, 8);
            Check(f0.Stride == 3 && f0.Pixels[3] == 30, $"stride 0 folds to a tight row (stride={f0.Stride}, row1[0]={f0.Pixels[3]})");
            var f0c = CvInspect.Imaging.Gev.GevFrameConv.ToCamFrame(new byte[2 * 3 * 4], 2, 4, 0, CvInspect.Imaging.Gev.GevPixelLayout.Bgr, 24);
            Check(f0c.Stride == 6, $"stride 0 folds for 3-channel too (stride={f0c.Stride})");

            // 짧은 버퍼는 관리 힙 밖을 읽기 전에 막는다 (풀지 않은 압축 화소를 넘기는 실수도 여기 걸린다)
            var shortThrew = false;
            try
            {
                CvInspect.Imaging.Gev.GevFrameConv.ToCamFrame(new byte[10], 8, 4, 0, CvInspect.Imaging.Gev.GevPixelLayout.Mono, 8);
            }
            catch (ArgumentException) { shortThrew = true; }
            Check(shortThrew, "undersized pixel buffer is rejected before it reaches native code");
            // 마지막 행에 패딩이 없는 정상 버퍼는 거절하지 않는다 (하한을 정확히 재는지 확인)
            var tightLast = CvInspect.Imaging.Gev.GevFrameConv.ToCamFrame(
                new byte[5 * 2 + 3], 3, 3, 5, CvInspect.Imaging.Gev.GevPixelLayout.Mono, 8);
            Check(tightLast.Width == 3 && tightLast.Height == 3, "a buffer whose last row omits padding is accepted");

            var rgb = new byte[] { 10, 20, 30, 10, 20, 30 };   // R,G,B
            var f2 = CvInspect.Imaging.Gev.GevFrameConv.ToCamFrame(rgb, 2, 1, 6, CvInspect.Imaging.Gev.GevPixelLayout.Rgb, 24);
            Check(f2.Pixels[0] == 30 && f2.Pixels[1] == 20 && f2.Pixels[2] == 10, $"RGB8 → Bgr24 swaps channels (got {f2.Pixels[0]},{f2.Pixels[1]},{f2.Pixels[2]})");
        }

        // 11-0) 취득 라이브러리 진단이 CvLog 로 흐르는가 — 안 흐르면 설비에서 볼 수 있는 게 예외 문구뿐이다
        {
            var seen = new List<string>();
            var prevSink = CvLog.Sink;
            CvLog.Sink = (lv, src, msg, _) => seen.Add($"{lv}|{src}|{msg}");
            try
            {
                GevSharp.GevLog.Sink = null;
                // 호스트가 창구를 안 꽂았으면 GevCam 생성이 자동으로 연결한다
                using (var _ = new CvInspect.Imaging.Gev.GevCam(new CvInspect.Imaging.CamOpt { SerialNumber = "x" }))
                    Check(GevSharp.GevLog.Sink is not null, "GevCam attaches the acquisition-library log sink when none is set");

                GevSharp.GevLog.Sink!(GevSharp.GevLogLevel.Warn, "GevStream", "socket buffer granted 1MB of 32MB", null);
                Check(seen.Any(s => s.StartsWith("Warning|GevStream") && s.Contains("socket buffer")),
                    $"library warnings reach CvLog with the level mapped (got: {seen.LastOrDefault()})");

                // 호스트가 이미 꽂아 둔 창구는 덮지 않는다
                var mine = 0;
                GevSharp.GevLog.Sink = (_, _, _, _) => mine++;
                using (var _ = new CvInspect.Imaging.Gev.GevCam(new CvInspect.Imaging.CamOpt { SerialNumber = "x" })) { }
                GevSharp.GevLog.Sink!(GevSharp.GevLogLevel.Info, "s", "m", null);
                Check(mine == 1, "an existing host sink is left alone");
            }
            finally { CvLog.Sink = prevSink; GevSharp.GevLog.Sink = null; }
        }

        // 11-5) 위상 대수 — 시프트 표와 미러의 짝수/홀수 폭 의존
        {
            Check(CvInspect.Imaging.Gev.CvBayerPhase.Shift(CvBayerPattern.RG, 1, 0) == CvBayerPattern.GR
                  && CvInspect.Imaging.Gev.CvBayerPhase.Shift(CvBayerPattern.RG, 0, 1) == CvBayerPattern.GB
                  && CvInspect.Imaging.Gev.CvBayerPhase.Shift(CvBayerPattern.RG, 1, 1) == CvBayerPattern.BG,
                "phase shift table for RG");
            Check(CvInspect.Imaging.Gev.CvBayerPhase.Effective(CvBayerPattern.RG, 1920, 1080, true, false, 0, 0) == CvBayerPattern.GR,
                "ReverseX on even width shifts RG→GR");
            Check(CvInspect.Imaging.Gev.CvBayerPhase.Effective(CvBayerPattern.RG, 1921, 1080, true, false, 0, 0) == CvBayerPattern.RG,
                "ReverseX on odd width leaves the pattern alone");
            Check(CvInspect.Imaging.Gev.CvBayerPhase.Effective(CvBayerPattern.RG, 1920, 1080, true, false, 1, 0) == CvBayerPattern.RG,
                "ReverseX and an odd OffsetX cancel out");
            Check(CvInspect.Imaging.Gev.CvBayerPhase.Effective(CvBayerPattern.RG, 1920, 1080, false, false, 1, 1) == CvBayerPattern.BG,
                "odd ROI offset alone shifts RG→BG");
            // 기준 치수를 잘못 넘기면 결과가 뒤집힌다 — API 를 "ROI 폭만 받게" 단순화하지 못하도록 고정한다.
            // (두 해석의 차이는 2·Offset + 폭 − Max 이고, 2·Offset 만 소거되므로 폭과 Max 의 홀짝이 갈리면 어긋난다.)
            Check(CvInspect.Imaging.Gev.CvBayerPhase.Effective(CvBayerPattern.RG, 1920, 1080, true, false, 0, 0)
                  != CvInspect.Imaging.Gev.CvBayerPhase.Effective(CvBayerPattern.RG, 1919, 1080, true, false, 0, 0),
                "mirror reference dimension matters — parity mismatch flips the answer");
        }
    }
    }

    /// <summary>패킷 간 지연의 틱 환산 — 같은 지연이라도 장치마다 숫자가 다르다.</summary>
    [Fact]
    public void ScpdTicks()
    {
    // === 11-B) SCPD — 같은 지연이라도 장치마다 틱 숫자가 다르다 ===
    {
        // 실측 대조값: 150us 에서 125 MHz 장치는 18,750, 66.67 MHz 장치는 10,000 이었다.
        Check(CvInspect.Imaging.Gev.GevScpd.TicksFor(150, 125_000_000) == 18_750,
            "150us at 125 MHz is 18,750 ticks (matches the measured value)");
        Check(CvInspect.Imaging.Gev.GevScpd.TicksFor(150, 66_666_666) == 10_000,
            "150us at 66.67 MHz is 10,000 ticks (matches the measured value)");
        Check(CvInspect.Imaging.Gev.GevScpd.TicksFor(150, 125_000_000)
              != CvInspect.Imaging.Gev.GevScpd.TicksFor(150, 66_666_666),
            "the same delay is a different number on a different device — that is why we take time, not ticks");

        // 환산할 수 없으면 0 이 아니라 null — 0 은 "건드리지 않음" 이라 조용히 미적용이 된다
        Check(CvInspect.Imaging.Gev.GevScpd.TicksFor(150, 0) is null,
            "an unknown tick frequency yields null, so the caller can say it could not apply the delay");
        Check(CvInspect.Imaging.Gev.GevScpd.TicksFor(0, 125_000_000) is null
              && CvInspect.Imaging.Gev.GevScpd.TicksFor(-1, 125_000_000) is null,
            "no delay asked for means no delay applied");

        // 아주 짧은 지연이 0 으로 접히면 요청이 조용히 사라진다 — 1 틱으로 올린다
        Check(CvInspect.Imaging.Gev.GevScpd.TicksFor(0.001, 1000) == 1,
            "a delay too small for one tick becomes one tick, never a silent zero");
        Check(CvInspect.Imaging.Gev.GevScpd.TicksFor(1e9, 4_000_000_000) == int.MaxValue,
            "an absurd delay clamps instead of overflowing");
    }
    }

    /// <summary>노출 격자 맞춤 — 사람이 넣은 값이 카메라 간격에 맞을 이유가 없다.</summary>
    [Fact]
    public void ExposureGrid()
    {
    // === 11-C) 격자 맞춤 — 사람이 넣은 값이 카메라 간격에 맞을 이유가 없다 ===
    {
        // 실측 대조: 노출 격자가 기준 35 · 간격 35 인 장치에서 60000 은 거절되고 59990 이 들어갔다.
        Check(CvInspect.Imaging.Gev.GevGrid.Snap(60000, 35, 35) == 59990,
            "60000 snaps to 59990 on a 35/35 grid (matches the measured camera)");
        Check((59990 - 35) % 35 == 0, "and 59990 really is on that grid");

        Check(CvInspect.Imaging.Gev.GevGrid.Snap(59990, 35, 35) == 59990,
            "a value already on the grid is left alone");
        Check(CvInspect.Imaging.Gev.GevGrid.Snap(10, 35, 35) == 35,
            "below the anchor snaps up to the anchor, never below what the camera accepts");

        // 간격이 없으면 맞출 것이 없다 — 억지로 값을 바꾸면 안 된다
        Check(CvInspect.Imaging.Gev.GevGrid.Snap(60000, 35, 1) is null
              && CvInspect.Imaging.Gev.GevGrid.Snap(60000, 35, 0) is null,
            "no increment means nothing to snap to");
        Check(CvInspect.Imaging.Gev.GevGrid.Snap(double.NaN, 35, 35) is null,
            "a nonsense request yields nothing rather than a nonsense value");

        // 중간값은 위로 — 노출을 요청보다 짧게 깎으면 어두워지는 쪽으로 조용히 틀어진다
        Check(CvInspect.Imaging.Gev.GevGrid.Snap(50, 0, 100) == 100,
            "a midpoint rounds away from zero, not down into a darker frame");
    }
    }
}
