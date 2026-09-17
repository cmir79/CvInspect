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

    /// <summary>진단 추적을 받아 두는 청취기 — 싱크가 없을 때 남는 그 한 줄이 실제로 나오는지 본다.
    /// 그 호출은 TRACE 상수가 있어야 컴파일에 남으므로, 여기서 잡힌다는 것이 곧 배포 빌드에 들어 있다는 증거다.</summary>
    private sealed class TraceCapture : System.Diagnostics.TraceListener
    {
        public readonly List<string> Lines = new();
        public override void Write(string? message) { if (message is not null) Lines.Add(message); }
        public override void WriteLine(string? message) { if (message is not null) Lines.Add(message); }
        public override void TraceEvent(System.Diagnostics.TraceEventCache? eventCache, string source,
            System.Diagnostics.TraceEventType eventType, int id, string? message)
            => Lines.Add($"{eventType}|{message}");
    }

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
    // 매 Run 대입되는 런타임 값이 레시피에 박히면 한 프레임의 값이 다음 런의 파라미터로 되먹여진다 —
    // 실측으로 같은 장면의 판정이 뒤집혔다(score 1.0000 → 0.0893, 멀쩡한 부품이 미검출).
    // README 가 소비자에게 "volatile runtime values are [JsonIgnore]" 라고 약속한 계약의 나머지 절반이다.
    Check(!patJson.Contains("WrapPeriodX"), "WrapPeriodX is a per-Run runtime value and must not persist into a recipe");
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
    // 싱크가 붙는 순간 그동안 붙잡혀 있던 줄이 먼저 흘러든다 — 이 절이 세는 것은 자기 줄뿐이다.
    var logHits = new List<string>();
    CvLog.Sink = (level, src, msg, ex) => { if (src == "smoke") logHits.Add($"{level}|{src}|{msg}"); };
    CvLog.Publish(CvLogLevel.Warning, "smoke", "hello");
    Check(logHits.Count == 1 && logHits[0].StartsWith("Warning|smoke"), "CvLog sink receives");

    }

    /// <summary>싱크가 없는 동안의 진단은 사라지지 않는다 — 붙는 순간 순서대로 흘러가고, 밀려난 것은 세어지고,
    /// 첫 줄은 진단 추적에 한 번 남는다. 한 소비자가 배선 없이 이틀을 돌며 잃은 것이 이 줄들이다.</summary>
    [Fact]
    public void LogSinkHoldsUntilAttached()
    {
    // === 7-B) CvLog — 배선이 늦어도 잃지 않고, 없으면 세어서 드러낸다 ===
    {
        var prev = CvLog.Sink;
        var trace = new TraceCapture();
        System.Diagnostics.Trace.Listeners.Add(trace);
        try
        {
            CvLog.Sink = (_, _, _, _) => { };   // 붙였다 뗀다 — 다음 미연결 구간이 새 구간으로 세어진다
            CvLog.Sink = null;
            var before = CvLog.DroppedCount;
            Check(!CvLog.IsAttached, "no sink attached");

            CvLog.Publish(CvLogLevel.Info, "t", "one");
            CvLog.Publish(CvLogLevel.Warning, "t", "two");
            Check(trace.Lines.Count(l => l.Contains("CvLog.Sink is not set")) == 1,
                $"the first held line raises exactly one Trace warning: [{string.Join(" | ", trace.Lines)}]");

            var seen = new List<string>();
            CvLog.Sink = (lv, src, msg, _) => seen.Add($"{lv}|{src}|{msg}");
            Check(CvLog.IsAttached, "sink attached");
            Check(seen.Count == 3 && seen[0] == "Info|CvLog|replaying 2 lines held before a sink was attached."
                  && seen[1] == "Info|t|one" && seen[2] == "Warning|t|two",
                $"held lines replay in order behind one line that says so: [{string.Join(" | ", seen)}]");
            CvLog.Publish(CvLogLevel.Error, "t", "three");
            Check(seen.Count == 4 && seen[3] == "Error|t|three", "after attach lines go straight through");
            Check(CvLog.DroppedCount == before, "nothing is dropped while under the hold limit");

            // 한도를 넘긴 것은 오래된 순으로 밀려나고 세어진다 — 0 이 아닌 계수가 "배선이 없었다" 의 증거다
            CvLog.Sink = null;
            for (var i = 0; i < CvLog.HoldCapacity + 6; i++) CvLog.Publish(CvLogLevel.Debug, "t", $"m{i}");
            Check(CvLog.DroppedCount == before + 6, $"lines pushed out of the hold are counted (dropped={CvLog.DroppedCount - before})");
            Check(trace.Lines.Count(l => l.Contains("CvLog.Sink is not set")) == 2,
                "a new unattached stretch warns once more, not once per line");
            seen.Clear();
            CvLog.Sink = (_, _, msg, _) => seen.Add(msg);
            Check(seen.Count == CvLog.HoldCapacity + 1
                  && seen[0] == $"replaying {CvLog.HoldCapacity} lines held before a sink was attached; 6 older lines were dropped."
                  && seen[1] == "m6" && seen[^1] == $"m{CvLog.HoldCapacity + 5}",
                $"the newest {CvLog.HoldCapacity} replay and the notice names the drop (first={seen.FirstOrDefault()} second={seen.Skip(1).FirstOrDefault()} last={seen.LastOrDefault()})");

            // 호스트 로거는 남의 코드다 — 배선도 발행도 그 자리에서 싱크를 부른다. 새면 배선은 평범한 프로퍼티
            // 대입문에서, 발행은 취득 스레드·해제 경로 한복판에서 깨진다(종료 중 이미 정리된 로거가 흔한 경우다).
            // 아래 대입이나 발행이 던지면 이 절은 예외로 끝난다 — 그 자체가 회귀 신호다.
            CvLog.Sink = null;
            var beforeThrow = CvLog.DroppedCount;
            CvLog.Publish(CvLogLevel.Info, "t", "held-a");
            CvLog.Publish(CvLogLevel.Info, "t", "held-b");
            CvLog.Sink = (_, _, msg, _) => { if (msg == "held-a") throw new InvalidOperationException("logger not ready"); };
            Check(trace.Lines.Count(l => l.Contains("line not delivered: Info t held-a")) == 1,
                $"a throwing replay is reported on the diagnostic trace instead of escaping the assignment: [{string.Join(" | ", trace.Lines)}]");

            CvLog.Publish(CvLogLevel.Info, "t", "held-a");
            Check(trace.Lines.Count(l => l.Contains("line not delivered: Info t held-a")) == 2,
                "a throwing sink is reported on publish too — it does not escape into the thread that logged");

            var recovered = new List<string>();
            CvLog.Sink = (_, _, msg, _) => recovered.Add(msg);
            Check(recovered.Count == 3 && recovered[0] == "replaying 2 lines held before a sink was attached."
                  && recovered[1] == "held-a" && recovered[2] == "held-b",
                $"lines the throwing sink never received are put back and reach the next sink: [{string.Join(" | ", recovered)}]");
            Check(CvLog.DroppedCount == beforeThrow, $"putting lines back does not count them as dropped (dropped={CvLog.DroppedCount - beforeThrow})");
        }
        finally
        {
            System.Diagnostics.Trace.Listeners.Remove(trace);
            CvLog.Sink = prev;
        }

        // 문화권을 골랐다는 사실이 보인다 — 기본 en 과 명시된 en 을 가르는 유일한 표지
        CvLoc.Culture = CvLoc.Culture;
        Check(CvLoc.IsCultureSet, "an explicit Culture assignment is visible as such");
    }
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

            // 표시 계약 — CamFrame 은 ICvPixelSource 이고 채널 수·스트라이드·길이가 그 계약을 만족한다.
            // 표시 계층은 이 배열을 복사 없이 참조로 붙잡는다(발행 뒤 불변). 화면은 대입 시점에 백버퍼로 옮겨져 안전하지만,
            // 계약이 깨지면 그 배열을 읽는 픽셀 조회·저장이 화면과 어긋난다.
            ICvPixelSource ps = held;
            Check(ps.Channels == 1 && ps.Stride >= ps.Width * ps.Channels && ps.Pixels.Length >= ps.Stride * ps.Height,
                $"ICvPixelSource contract: ch={ps.Channels} stride={ps.Stride} len={ps.Pixels.Length}");
            Check(ReferenceEquals(ps.Pixels, held.Pixels), "ICvPixelSource.Pixels is the frame's own array (reference-holdable, no copy)");
            foreach (var (fmt, ch) in new[] { (CvInspect.Imaging.CamPixelFormat.Mono8, 1), (CvInspect.Imaging.CamPixelFormat.Bgr24, 3), (CvInspect.Imaging.CamPixelFormat.Bgra32, 4) })
                Check(new CvInspect.Imaging.CamFrame(new byte[2 * 2 * ch], 2, 2, 2 * ch, fmt).Channels == ch, $"Channels({fmt}) == {ch}");

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

        // 10-3b) 안쪽 시작이 실패하면 의도가 남지 않는다 — 남으면 다음 재연결 때 아무도 청하지 않은 연속 취득이
        //        되살아난다. 부른 쪽은 예외를 받았지 시작을 받은 것이 아니다.
        {
            var made = new List<FakeCam>();
            using var cam = new CvInspect.Imaging.ReconnectingCam(() => { var c = new FakeCam(); made.Add(c); return c; }, fastOpt);
            cam.Open();
            made[0].FailNextStart = true;
            var threw = false;
            try { cam.StartContinuous(); } catch (InvalidOperationException) { threw = true; }
            Check(threw, "an inner start failure reaches the caller");
            made[0].LoseConnection();
            Check(Wait(() => made.Count == 2), "reconnect after a failed start");
            Thread.Sleep(60);
            Check(!cam.IsGrabbing && !made[1].IsGrabbing,
                "a start that failed is not revived by reconnect — the caller saw an exception, not a start");
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

        // 10-10) 연결은 멀쩡한데 취득만 죽으면 세션을 갈아 끼워 되살린다.
        //        안쪽 구현은 수신 스트림이 접히면 연결을 잃지 않은 채 취득만 접고 GrabbingChanged(false) 만 낸다.
        //        그 신호를 안 들으면 의도만 true 로 남아 아무도 다시 켜지 않는다 — 연결은 정상이라고 답하는데
        //        프레임이 영영 안 온다. 상위가 기다리는 것 말고 할 수 있는 일이 없는 상태다.
        {
            var made = new List<FakeCam>();
            using var cam = new CvInspect.Imaging.ReconnectingCam(() => { var c = new FakeCam(); made.Add(c); return c; }, fastOpt);
            var grab = new List<bool>();
            cam.GrabbingChanged += (_, g) => { lock (grab) grab.Add(g); };
            cam.Open();
            cam.StartContinuous();
            Check(cam.IsGrabbing && made.Count == 1, "grabbing before the stream dies");

            made[0].StopGrabbingOnItsOwn();                       // 연결 상실은 없다 — 취득만 접혔다
            Check(Wait(() => made.Count == 2 && made[1].IsGrabbing),
                $"acquisition that dies on its own is resumed on a fresh session (instances={made.Count}, grabbing={made.ElementAtOrDefault(1)?.IsGrabbing})");
            Check(Wait(() => cam.IsGrabbing), "the decorator reports grabbing again after the rebuild");
            lock (grab) Check(grab.Count >= 3 && grab[0] && !grab[1] && grab[^1],
                $"subscribers see the stop before the resume: [{string.Join(",", grab)}]");
        }

        // 10-10c) 제어 상실은 한 번만 되살린다 — 두 통지가 오지만 세션 교체는 한 번이다.
        //         구현은 제어를 잃으면 GrabbingChanged(false) 와 ConnectionChanged(false) 를 잇달아 낸다.
        //         둘을 각각 "되살려야 할 사건" 으로 읽으면 교체가 두 번 걸려, 방금 살아난 멀쩡한 세션을
        //         다시 뜯는다(상위에는 false 없이 connected/grabbing 이 두 번 오고 그 사이 프레임이 끊긴다).
        //         그리고 그 줄은 제어 상실이므로 "청하지도 않았는데 멈췄다" 로 남아서도 안 된다 — 원인을
        //         엉뚱한 데로 보낸다.
        {
            // 사다리를 간격보다 길게 잡는 것이 이 항의 조건이다 — 둘째 통지가 첫 라운드의 백오프 대기 중에
            // 닿아야 아직 교체 전이라 유령 통지로 걸러지지 않고, 그래야 '요청 두 건' 이 실제로 겹친다.
            var made = new List<FakeCam>();
            var slowLadder = new CvInspect.Imaging.CamReconnectOpt { BackoffMs = new[] { 200 }, ShutdownWaitMs = 1000 };
            using var cam = new CvInspect.Imaging.ReconnectingCam(() => { var c = new FakeCam(); made.Add(c); return c; }, slowLadder);
            cam.Open();
            cam.StartContinuous();

            var lines = new List<string>();
            var prevSink = CvLog.Sink;
            try
            {
                CvLog.Sink = (_, src, msg, _) => { if (src == "ReconnectingCam") lock (lines) lines.Add(msg); };
                made[0].LoseConnection(gapMs: 20);          // 실제 순서 그대로 — 취득 통지가 먼저, 연결 통지가 뒤
                Check(Wait(() => made.Count == 2 && made[1].IsGrabbing), "control loss is recovered");
                Thread.Sleep(600);                          // 두 번째 라운드가 걸렸다면 이 안에 돈다(백오프 200ms)
                Check(made.Count == 2, $"one control loss rebuilds the session once (instances={made.Count})");
                lock (lines) Check(!lines.Any(l => l.Contains("without being asked")),
                    $"a control loss is not reported as an unrequested stop: [{string.Join(" / ", lines)}]");
            }
            finally { CvLog.Sink = prevSink; }
        }

        // 10-10d) 통지 순서가 달라도 교체는 한 번이다. 상태를 나중에 내리는 구현에서는 취득 통지 시점에
        //         아직 연결이 살아 있어 보이므로 10-10c 의 가드가 듣지 않는다. 그때도 한 죽음은 한 사건이다 —
        //         세는 기준을 통지 순서가 아니라 죽은 인스턴스로 두면 구현마다 갈리지 않는다.
        {
            var made = new List<FakeCam>();
            var slowLadder = new CvInspect.Imaging.CamReconnectOpt { BackoffMs = new[] { 200 }, ShutdownWaitMs = 1000 };
            using var cam = new CvInspect.Imaging.ReconnectingCam(() => { var c = new FakeCam(); made.Add(c); return c; }, slowLadder);
            cam.Open();
            cam.StartContinuous();
            made[0].LoseConnectionAnnouncingGrabFirst(gapMs: 20);
            Check(Wait(() => made.Count == 2 && made[1].IsGrabbing), "recovered regardless of notification order");
            Thread.Sleep(600);
            Check(made.Count == 2, $"one death is one rebuild whatever the order (instances={made.Count})");
        }

        // 10-10e) 구독자가 던져도 카메라가 하는 일은 달라지지 않는다.
        //         재연결 성공 뒤의 연결 통지는 재연결 루프에서 나간다. 감싸지 않으면 구독자 예외가 루프
        //         바깥 catch 까지 올라가 "재연결이 실패했다" 로 읽히고(실제로는 성공했는데), 그 길에
        //         연속취득 재개가 통째로 건너뛰어진다 — 연결은 살아났는데 취득은 안 돈다. 그리고 로그에는
        //         호스트 핸들러가 원인이라는 흔적이 없다.
        {
            var made = new List<FakeCam>();
            using var cam = new CvInspect.Imaging.ReconnectingCam(() => { var c = new FakeCam(); made.Add(c); return c; }, fastOpt);
            cam.Open();
            cam.StartContinuous();
            cam.ConnectionChanged += (_, e) => { if (e.IsConnected) throw new InvalidOperationException("subscriber blew up"); };

            var lines = new List<string>();
            var prevSink = CvLog.Sink;
            try
            {
                CvLog.Sink = (lvl, src, msg, _) => { if (src == "ReconnectingCam") lock (lines) lines.Add($"{lvl}|{msg}"); };
                made[0].LoseConnection();
                Check(Wait(() => made.Count == 2 && made[1].IsGrabbing),
                    $"continuous grab still resumes when a ConnectionChanged subscriber throws (grabbing={made.ElementAtOrDefault(1)?.IsGrabbing})");
                lock (lines) Check(lines.Any(l => l.Contains("subscriber threw")),
                    $"the throwing subscriber is named in the log: [{string.Join(" / ", lines)}]");
                lock (lines) Check(!lines.Any(l => l.Contains("reconnect loop failed")),
                    $"a subscriber fault is not reported as our own reconnect failure: [{string.Join(" / ", lines)}]");
            }
            finally { CvLog.Sink = prevSink; }
        }

        // 10-10b) 대조군 — 사용자가 끈 취득은 되살아나지 않는다. 같은 통지가 오지만 의도가 내려가 있다.
        //         이것이 없으면 위 항은 "무슨 일이 있어도 다시 켠다" 와 구분되지 않는다.
        {
            var made = new List<FakeCam>();
            using var cam = new CvInspect.Imaging.ReconnectingCam(() => { var c = new FakeCam(); made.Add(c); return c; }, fastOpt);
            cam.Open();
            cam.StartContinuous();
            cam.StopContinuous();                                 // 의도를 내린다 — 안쪽도 멈추며 같은 통지를 낸다
            Thread.Sleep(80);
            Check(made.Count == 1 && !cam.IsGrabbing,
                $"a stop the user asked for is not undone (instances={made.Count}, grabbing={cam.IsGrabbing})");
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

    /// <summary>연속 취득 중의 단발 그랩은 답할 수 없는 호출이다 — 조용히 무동작이면 자유 실행 프레임이
    /// 그 자리에 들어와 다른 대상을 판정한다.</summary>
    [Fact]
    public void GrabOneDuringContinuousThrows()
    {
    // === 9-C) 취득 계약 — 연속 중 GrabOne 은 던진다 ===
    {
        var opt = new CvInspect.Imaging.CamOpt { ComType = "Virtual", IsColor = false };
        using var cam = CvInspect.Imaging.CamFactory.Create(opt);
        cam.Open();

        // 정지 상태에서는 답할 수 있다 — 던지지 않는다.
        cam.GrabOne();

        cam.StartContinuous();
        Check(cam.IsGrabbing, "IsGrabbing is true while continuous acquisition runs");

        // 이 저장소의 결함 하나가 여기 있었다: 조용히 돌아가면 부른 쪽은 자기 호출의 답 대신
        // 흘러가던 프레임을 받고, 티칭 조명으로 찍힌 다른 대상을 판정하고도 아무 신호가 남지 않는다.
        Assert.Throws<InvalidOperationException>(() => cam.GrabOne());

        cam.StopContinuous();
        Check(!cam.IsGrabbing, "IsGrabbing is false after StopContinuous");

        // 멈추면 다시 답할 수 있다 — 규정은 "연속 중" 에만 걸린다.
        cam.GrabOne();
    }
    }

    /// <summary>프레임 번호는 되돌이를 돈다 — 크기 비교로는 되돌이 뒤 프레임이 전부 옛것이 된다.</summary>
    [Fact]
    public void FrameIdWrapAround()
    {
    // === 11-C) 프레임 번호 판정 — 크기가 아니라 16비트 안의 거리 ===
    {
        Check(CvInspect.Imaging.Gev.GevCam.IsNewerFrameId(101, 100, false), "the next number is newer");
        Check(!CvInspect.Imaging.Gev.GevCam.IsNewerFrameId(100, 100, false), "the same number is not newer");
        Check(!CvInspect.Imaging.Gev.GevCam.IsNewerFrameId(99, 100, false), "the previous number is not newer");

        // 이 저장소의 결함 하나가 여기 있었다: 되돌이를 지나면 새 프레임의 번호가 작아지는데, 크기로
        // 비교하면 그 뒤 오는 프레임이 전부 옛것으로 기각되어 단발 그랩이 영영 시한을 넘긴다.
        // 14fps 면 78분마다 한 바퀴 돈다 — 하루를 도는 설비에서는 이론이 아니다.
        Check(CvInspect.Imaging.Gev.GevCam.IsNewerFrameId(3, 65530, false), "a number past the wrap is newer");
        Check(!CvInspect.Imaging.Gev.GevCam.IsNewerFrameId(65530, 3, false), "and the one before the wrap is older");
        Check(3 < 65530, "size comparison would have said the opposite — that is the defect this pins");

        // 확장 번호는 되돌지 않는다 — 거리 식을 그대로 쓰면 간격이 반 바퀴를 넘는 순간 뒤집히므로
        // 그쪽은 크기로 본다. 같은 두 값이 폭에 따라 반대로 판정되는 것이 이 갈림의 요점이다.
        Check(!CvInspect.Imaging.Gev.GevCam.IsNewerFrameId(3, 65530, true), "with extended ids 3 is simply older");
        Check(CvInspect.Imaging.Gev.GevCam.IsNewerFrameId(40000, 3, true), "with extended ids 40000 is plainly newer than 3");
        Check(!CvInspect.Imaging.Gev.GevCam.IsNewerFrameId(40000, 3, false), "the 16-bit reading of that pair is the opposite — it reads as a wrap");

        // 반 바퀴가 경계다. 16비트 쪽은 그보다 멀리 벌어지면 방향을 알 수 없어 뒤집힌다 —
        // 이 판정이 보는 간격은 대기열 깊이 수준이라 닿지 않는다는 전제 위에 서 있다.
        Check(CvInspect.Imaging.Gev.GevCam.IsNewerFrameId(40000 + 0x7FFF, 40000, false), "half a lap ahead is still newer");
        Check(!CvInspect.Imaging.Gev.GevCam.IsNewerFrameId(40000 - 0x7FFF, 40000, false), "half a lap behind is older");
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

    /// <summary>
    /// 펌프 계측 — 밀림의 원인을 가르는 숫자들이 실제로 그 값을 내는지.
    /// 이 절이 있는 이유: 확인하지 않은 진단을 넣었다가 정상 시스템에서 항상 경고가 뜨게 만든 적이 있고,
    /// 그 뒤에는 몇 시간짜리 "지연" 을 태연히 찍는 판까지 냈다.
    /// </summary>
    [Fact]
    public void PumpStats()
    {
        var freq = System.Diagnostics.Stopwatch.Frequency;
        long Ms(double ms) => (long)(ms / 1000.0 * freq);

        Check(!new CvInspect.Imaging.Gev.GevPumpStats(0).Enabled, "interval 0 turns the diagnostic off");

        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var s = new CvInspect.Imaging.Gev.GevPumpStats(100);
        Check(s.Enabled, "a positive interval turns it on");
        Check(s.Add(Ms(1), Ms(60), start + Ms(10), TimeSpan.FromSeconds(1.00)) is null,
            "nothing is emitted before the window fills");

        var line = s.Add(Ms(1), Ms(60), start + Ms(300), TimeSpan.FromSeconds(1.10));
        Check(line is not null, "the window fires once the interval has elapsed");
        var excess = System.Text.RegularExpressions.Regex.Match(line!, @"([\d.]+)ms max above the best seen");
        Check(excess.Success, $"the line reports queueing above the best offset: {line}");
        var ms = double.Parse(excess.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Check(ms is > 150 and < 250, $"a frame that sat 200ms longer than the best is reported as such (got {ms:F0}ms)");

        // 수신 대기 — 대기열이 서 있는지를 가르는 숫자
        var wait = System.Text.RegularExpressions.Regex.Match(line!, @"receive wait ([\d.]+)ms avg");
        Check(wait.Success && double.Parse(wait.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) > 50,
            $"time spent waiting for the next frame is reported: {line}");

        var start1b = System.Diagnostics.Stopwatch.GetTimestamp();
        var sQ = new CvInspect.Imaging.Gev.GevPumpStats(50);
        sQ.Add(Ms(1), 0, start1b + Ms(10), null);              // 기다림 없이 곧바로 받았다 = 줄이 서 있다
        var lineQ = sQ.Add(Ms(1), 0, start1b + Ms(80), null);
        var waitMin = System.Text.RegularExpressions.Regex.Match(lineQ!, @"([\d.]+)ms min");
        Check(waitMin.Success && double.Parse(waitMin.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) < 1,
            $"a receive that never waits means completed frames were already queued: {lineQ}");

        // 장치가 시각을 안 주면 잴 수 없다고 말한다 — 0 을 대신 쓰지 않는다
        var start2 = System.Diagnostics.Stopwatch.GetTimestamp();
        var s2 = new CvInspect.Imaging.Gev.GevPumpStats(50);
        var line2 = s2.Add(Ms(1), Ms(60), start2 + Ms(80), null);
        Check(line2 is not null && line2.Contains("no timestamp"),
            $"without a device timestamp it says latency cannot be measured: {line2}");

        // 핸들러가 펌프 시간을 다 먹으면 그 비율이 드러난다
        var start3 = System.Diagnostics.Stopwatch.GetTimestamp();
        var s3 = new CvInspect.Imaging.Gev.GevPumpStats(50);
        var line3 = s3.Add(Ms(95), 0, start3 + Ms(100), null);
        var duty = System.Text.RegularExpressions.Regex.Match(line3!, @"(\d+)% of the pump's time");
        Check(duty.Success && int.Parse(duty.Groups[1].Value) > 80,
            $"handlers eating the pump's time show up as a high share: {line3}");

        // 결번 — "늦게 온다" 와 "아예 안 온다" 를 가른다
        var start4 = System.Diagnostics.Stopwatch.GetTimestamp();
        var s4 = new CvInspect.Imaging.Gev.GevPumpStats(50);
        s4.Add(Ms(1), 0, start4 + Ms(10), null, 100);
        s4.Add(Ms(1), 0, start4 + Ms(20), null, 101);
        s4.Add(Ms(1), 0, start4 + Ms(30), null, 105);   // 102·103·104 가 안 왔다
        var line4 = s4.Add(Ms(1), 0, start4 + Ms(80), null, 106);
        Check(line4 is not null && line4.Contains("3 frame(s) never arrived in 1 gap(s)"),
            $"a jump in the device frame number is reported as frames that never arrived: {line4}");

        var start5 = System.Diagnostics.Stopwatch.GetTimestamp();
        var s5 = new CvInspect.Imaging.Gev.GevPumpStats(50);
        s5.Add(Ms(1), 0, start5 + Ms(10), null, 7);
        var line5 = s5.Add(Ms(1), 0, start5 + Ms(80), null, 8);
        Check(line5 is not null && !line5.Contains("never arrived"),
            $"a contiguous sequence says nothing about losses: {line5}");

        // ⚠ 장치 시계가 되감기면 몇 시간짜리 "지연" 이 찍힌다 — 실기에서 실제로 그렇게 나왔다.
        // 기준을 다시 잡고 튀었다고 말해야 한다. 틀린 숫자는 없는 것보다 나쁘다.
        var start8 = System.Diagnostics.Stopwatch.GetTimestamp();
        var s8 = new CvInspect.Imaging.Gev.GevPumpStats(50);
        s8.Add(Ms(1), 0, start8 + Ms(10), TimeSpan.FromHours(4));          // 가동 4시간째
        var line8 = s8.Add(Ms(1), 0, start8 + Ms(80), TimeSpan.FromSeconds(0.01));  // 시계가 0 으로 되감겼다
        Check(line8 is not null && line8.Contains("the device clock jumped"),
            $"a device clock that winds back is called out, not reported as latency: {line8}");
        var bad = System.Text.RegularExpressions.Regex.Match(line8!, @"([\d.]+)ms max above the best seen");
        Check(bad.Success && double.Parse(bad.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) < 1000,
            $"and the latency numbers stay sane instead of showing hours: {line8}");
    }

    /// <summary>취득 계약 — "발행되는 프레임에는 CamOpt.Flip/Rotation 이 이미 적용돼 있다"(Imaging README).
    /// 그 계약을 GigE 백엔드만 안 지키고 있었다 — USB·파일·가상 카메라는 CamXform 을 부르는데 여기만 안 불러,
    /// 같은 레시피로 백엔드를 갈아 끼우면 화면 방향이 말없이 달라졌다.</summary>
    [Fact]
    public void GigEFramesCarryTheMountTransform()
    {
        // 비대칭 패턴이어야 뒤집힘이 실제로 드러난다 — 대칭이면 뒤집어도 같은 그림이라 아무것도 검증 못 한다.
        using var src = new Mat(40, 60, MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(src, new Rect(2, 2, 14, 8), new Scalar(255), -1);   // 왼쪽 위에만 표식
        var input = CvInspect.Imaging.CamFrame.FromMat(src, TimeSpan.FromSeconds(1.25));

        static (int X, int Y) Mark(CvInspect.Imaging.CamFrame f)
        {
            using var m = CvInspect.Imaging.CamFrameMatExt.AsMat(f);
            using var nz = new Mat();
            Cv2.FindNonZero(m, nz);
            var p = nz.At<Point>(0);
            return (p.X, p.Y);
        }
        Check(Mark(input) is (2, 2), $"the marker starts at the top-left ({Mark(input)})");

        // 끔 — 같은 인스턴스를 그대로 돌려준다. 안 쓰는 설비에 비용이 0 이라는 주장이 이것이다.
        using (var cam = new CvInspect.Imaging.Gev.GevCam(new CvInspect.Imaging.CamOpt { SerialNumber = "x" }))
            Check(ReferenceEquals(cam.ApplyMountXform(input), input),
                "with no flip or rotation the frame passes through untouched — the default costs nothing");

        // 좌우 반전 — 표식이 오른쪽으로 간다.
        using (var cam = new CvInspect.Imaging.Gev.GevCam(new CvInspect.Imaging.CamOpt
        {
            SerialNumber = "x", Flip = CvInspect.Imaging.FlipMode.Horizontal,
        }))
        {
            var got = cam.ApplyMountXform(input);
            Check(!ReferenceEquals(got, input) && Mark(got).X > 40,
                $"a horizontal flip actually reaches the published frame on the GigE backend too ({Mark(got)})");
            Check(got.DeviceTimestamp == TimeSpan.FromSeconds(1.25),
                $"and the device timestamp survives the transform — the pump statistics read it ({got.DeviceTimestamp})");
        }

        // 90° 회전 — 치수가 바뀐다(변환 경로를 실제로 탔다는 증거이기도 하다).
        using (var cam = new CvInspect.Imaging.Gev.GevCam(new CvInspect.Imaging.CamOpt
        {
            SerialNumber = "x", Rotation = CvInspect.Imaging.RotateMode.Rotate90,
        }))
        {
            var got = cam.ApplyMountXform(input);
            Check(got.Width == input.Height && got.Height == input.Width,
                $"a 90 degree rotation swaps the dimensions ({got.Width}x{got.Height} from {input.Width}x{input.Height})");
        }
    }

    /// <summary>포즈 프리미티브 — 학습각 보정 포즈와 정규화 창.
    /// <b>중립값(학습각 0·스케일 1)에서는 틀린 판과 맞는 판이 같은 값을 낸다</b> — 그래서 이 절은
    /// 학습각 25°·스케일 1.2·가산각 90° 처럼 전부 중립이 아닌 값으로만 잰다. 중립값에서만 돌린 검증은
    /// 그 파라미터를 검증하지 않는다.</summary>
    [Fact]
    public void PosePrimitivesCarryTheTrainedAngleAndScale()
    {
    // === 10-A) FixturePose 는 XformByPose 와 같은 답을 낸다 — 발견 포즈에 바로 Apply 하면 틀린다 ===
    {
        var pat = new CvPatternOpt { TrainedOriginX = 100, TrainedOriginY = 80, TrainedAngleDeg = 25 };
        var found = new CvPose(40, pat.TrainedOriginX, pat.TrainedOriginY, 300, 220, 0.91, 1.2);

        var (ex, ey) = CvInspGeom.XformByPose(pat, found, 160, 130);
        var fx = CvInspGeom.FixturePose(pat, found).Apply(160, 130);
        Check(Math.Abs(fx.X - ex) < 1e-9 && Math.Abs(fx.Y - ey) < 1e-9,
            $"FixturePose(...).Apply == XformByPose — 학습각 보정이 포즈 안으로 들어갔다 ({fx} vs ({ex},{ey}))");

        // 대조군: 보정 없이 발견 포즈에 바로 Apply 하면 학습각만큼 더 돈다. 이 차이가 0 이면 이 절은 아무것도 재지 않은 것이다.
        var raw = found.Apply(160, 130);
        Check(Math.Sqrt((raw.X - ex) * (raw.X - ex) + (raw.Y - ey) * (raw.Y - ey)) > 20,
            $"the uncorrected pose is visibly wrong, which is why the corrected one needs a name (raw={raw} correct=({ex},{ey}))");

        // 학습각 0 이면 둘이 같다 — 그래서 중립값으로만 검증하면 위 결함이 안 드러난다.
        var flat = new CvPatternOpt { TrainedOriginX = 100, TrainedOriginY = 80 };
        var flatFound = new CvPose(40, 100, 80, 300, 220, 0.91, 1.2);
        var (fex, fey) = CvInspGeom.XformByPose(flat, flatFound, 160, 130);
        var rawFlat = flatFound.Apply(160, 130);
        Check(Math.Abs(rawFlat.X - fex) < 1e-9 && Math.Abs(rawFlat.Y - fey) < 1e-9,
            "with a zero trained angle the wrong call and the right call agree — a regression that only runs here proves nothing");
    }

    // === 10-B) NormalizedWindow — 티칭 자리의 특징이 창 중앙에 서고, 좌표가 한 번에 되돌아온다 ===
    using (var scene = new Mat(400, 500, MatType.CV_8UC1, Scalar.Black))
    {
        // 티칭: 앵커 원점 (150,120), 랜드마크는 거기서 (+90,-40) 자리. 학습각 25°.
        var pat = new CvPatternOpt { TrainedOriginX = 150, TrainedOriginY = 120, TrainedAngleDeg = 25 };
        const double lmX = 240, lmY = 80;

        // 런: 앵커가 (300,230) 에서 각도 70°·스케일 1.2 로 발견됐다고 하자. 랜드마크의 실제 자리는 계약상 XformByPose 다.
        var found = new CvPose(70, pat.TrainedOriginX, pat.TrainedOriginY, 300, 230, 0.95, 1.2);
        var (trueX, trueY) = CvInspGeom.XformByPose(pat, found, lmX, lmY);
        Cv2.Circle(scene, new OpenCvSharp.Point((int)Math.Round(trueX), (int)Math.Round(trueY)), 9, new Scalar(255), -1);

        var got = CvInspGeom.NormalizedWindow(scene, pat, found, lmX, lmY, 60, 60);
        Check(got is not null, "a window comes back");
        using var win = got!.Value.Window;
        Check(win.Width == 60 && win.Height == 60, $"the window is the requested size in taught pixels ({win.Width}x{win.Height})");

        // 창 안에서 특징의 무게중심 — 티칭 자리를 요청했으니 창 중앙에 서야 한다.
        var mo = Cv2.Moments(win, binaryImage: false);
        Check(mo.M00 > 0, "the feature is inside the window at all");
        var wcx = mo.M10 / mo.M00;
        var wcy = mo.M01 / mo.M00;
        Check(Math.Abs(wcx - 30) < 1.5 && Math.Abs(wcy - 30) < 1.5,
            $"the taught point lands at the window centre even at 25 deg trained angle and 1.2 scale (found at {wcx:F2},{wcy:F2})");

        // 되돌아오는 길은 포즈 하나 — 창 좌표를 Apply 하면 입력 이미지 좌표다.
        var back = got.Value.WindowToImage.Apply(wcx, wcy);
        Check(Math.Abs(back.X - trueX) < 1.5 && Math.Abs(back.Y - trueY) < 1.5,
            $"WindowToImage.Apply maps straight back to the input image ({back.X:F2},{back.Y:F2} vs {trueX:F2},{trueY:F2})");

        // 가산각을 주면 다른 자리를 본다 — 대칭 스윕이 호출자 쪽에서 도는 방식. 여기서 같은 자리가 나오면 extraDeg 가 안 먹는 것이다.
        var turned = CvInspGeom.NormalizedWindow(scene, pat, found, lmX, lmY, 60, 60, extraDeg: 90);
        using var turnedWin = turned!.Value.Window;
        var mo2 = Cv2.Moments(turnedWin, binaryImage: false);
        Check(mo2.M00 < mo.M00 * 0.2,
            $"a candidate angle looks somewhere else — the sweep stays with the caller (mass {mo2.M00:F0} vs {mo.M00:F0})");

        // 창이 이미지를 벗어나도 크기·좌표계는 그대로다 — 잘린 창이 다른 공간 값을 내놓던 것이 앞 판의 결함이었다.
        var edge = CvInspGeom.NormalizedWindow(scene, pat, found, lmX, lmY, 60, 60, extraDeg: 180);
        using var edgeWin = edge!.Value.Window;
        Check(edgeWin.Width == 60 && edgeWin.Height == 60, "a window that falls outside the image keeps its size instead of shrinking");
    }

    // === 10-B2) 기울여 티칭한 대상 — extraDeg 에 그 학습각을 더하면 창 안에서 템플릿 자세로 선다 ===
    using (var scene = new Mat(400, 500, MatType.CV_8UC1, Scalar.Black))
    {
        // 창을 돌려 세우는 부호가 이 절의 전부다. 손으로 정하면 뒤집히는 자리라 실측으로 못 박는다 —
        // 뒤집혀도 "점수가 좀 낮다" 로만 보여서 진짜 미검출과 구분되지 않는다.
        var pat = new CvPatternOpt { TrainedOriginX = 150, TrainedOriginY = 120, TrainedAngleDeg = 25 };
        var found = new CvPose(70, pat.TrainedOriginX, pat.TrainedOriginY, 300, 230, 0.95, 1.0);
        const double lmTrainedAngle = 20;   // 랜드마크를 20° 기울여 티칭했다

        // 티칭 공간에서 lmTrainedAngle 로 누운 막대 → 이미지에서는 그만큼 더 돌아 있다.
        var barAngleInImage = (found.ThetaDeg - pat.TrainedAngleDeg) + lmTrainedAngle;
        var (bx, by) = CvInspGeom.XformByPose(pat, found, 240, 80);
        using (var bar = new Mat(400, 500, MatType.CV_8UC1, Scalar.Black))
        {
            Cv2.Rectangle(bar, new Rect(250 - 18, 200 - 4, 36, 8), new Scalar(255), -1);
            using var rot = Cv2.GetRotationMatrix2D(new Point2f(250, 200), -barAngleInImage, 1.0);
            using var spun = new Mat();
            Cv2.WarpAffine(bar, spun, rot, bar.Size());
            using var moved = new Mat();
            using var shift = Cv2.GetRotationMatrix2D(new Point2f(250, 200), 0, 1.0);
            shift.Set(0, 2, shift.At<double>(0, 2) + (bx - 250));
            shift.Set(1, 2, shift.At<double>(1, 2) + (by - 200));
            Cv2.WarpAffine(spun, moved, shift, bar.Size());
            moved.CopyTo(scene);
        }

        static double OrientDeg(Mat m)
        {
            var mo = Cv2.Moments(m, binaryImage: false);
            return 0.5 * Math.Atan2(2 * mo.Mu11, mo.Mu20 - mo.Mu02) * 180.0 / Math.PI;
        }

        var plain = CvInspGeom.NormalizedWindow(scene, pat, found, 240, 80, 70, 70);
        using var plainWin = plain!.Value.Window;
        Check(Math.Abs(OrientDeg(plainWin) - lmTrainedAngle) < 3.0,
            $"in taught space the target still lies at its own trained angle ({OrientDeg(plainWin):F2} deg, expected {lmTrainedAngle})");

        var aligned = CvInspGeom.NormalizedWindow(scene, pat, found, 240, 80, 70, 70, extraDeg: lmTrainedAngle);
        using var alignedWin = aligned!.Value.Window;
        Check(Math.Abs(OrientDeg(alignedWin)) < 3.0,
            $"adding the target's trained angle to extraDeg stands it up in template orientation ({OrientDeg(alignedWin):F2} deg) — the sign is pinned here");

        // 부호를 반대로 주면 두 배로 기운다 — 대조군이 없으면 위 단언이 우연히 통과할 수 있다.
        var wrong = CvInspGeom.NormalizedWindow(scene, pat, found, 240, 80, 70, 70, extraDeg: -lmTrainedAngle);
        using var wrongWin = wrong!.Value.Window;
        Check(Math.Abs(OrientDeg(wrongWin)) > 30.0,
            $"the opposite sign leans it twice as far, which is why this is measured and not reasoned ({OrientDeg(wrongWin):F2} deg)");

        // 대상 옵트를 받는 오버로드는 그 덧셈을 대신한다 — 호출부가 학습각을 손으로 더할 일이 없다.
        // 그 덧셈은 조작자가 학습 사각을 기울이는 순간에만 0 이 아니게 되므로, 손에 맡기면 평소 티칭에서는
        // 드러나지 않다가 기울여 잡은 첫 티칭에서만 점수가 깎인다.
        using (var templ = new Mat(30, 40, MatType.CV_8UC1, Scalar.Gray))
        {
            Cv2.ImEncode(".png", templ, out var png);
            var target = new CvPatternOpt
            {
                TrainedOriginX = 240, TrainedOriginY = 80,
                TrainedAngleDeg = lmTrainedAngle, TemplatePng = png,
            };
            var auto = CvInspGeom.NormalizedWindow(scene, pat, found, target, marginPx: 15);
            using var autoWin = auto!.Value.Window;
            Check(autoWin.Width == 40 + 30 && autoWin.Height == 30 + 30,
                $"the window is sized from the target's own template plus the margin ({autoWin.Width}x{autoWin.Height})");
            Check(Math.Abs(OrientDeg(autoWin)) < 3.0,
                $"and it stands the target up without the caller adding the trained angle ({OrientDeg(autoWin):F2} deg)");

            // 창이 세워 준 자세를 옵트가 모르면 파인더는 어긋난 한 자세만 본다 — 점수가 낮은 게 아니라 미검출이다.
            var run = auto.Value.MatchOpt;
            Check(run.TrainedAngleDeg == 0 && run.UseSearchRegion
                  && Math.Abs(run.SearchW - 30) < 1e-9 && Math.Abs(run.SearchX - (35 - 15)) < 1e-9,
                $"the window hands back the opt that matches in it: angle normalised, centre held to +-margin ({run.TrainedAngleDeg}, {run.SearchX},{run.SearchW})");
            Check(Math.Abs(target.TrainedAngleDeg - lmTrainedAngle) < 1e-9 && !target.UseSearchRegion,
                "and the caller's own opt is untouched");

            // 대조군: 같은 창을 낮은 오버로드로 부르면서 학습각을 빠뜨리면 기울어진다 — 그게 옮길 때 밟는 자리다.
            var byHand = CvInspGeom.NormalizedWindow(scene, pat, found,
                target.TrainedOriginX, target.TrainedOriginY, 70, 60, extraDeg: 0);
            using var byHandWin = byHand!.Value.Window;
            Check(Math.Abs(OrientDeg(byHandWin) - lmTrainedAngle) < 3.0,
                $"forgetting it leaves the target leaning by exactly its trained angle ({OrientDeg(byHandWin):F2} deg)");

            Check(CvInspGeom.NormalizedWindow(scene, pat, found, new CvPatternOpt(), marginPx: 15) is null,
                "a target with no template trained yet gives null instead of guessing a window size");
        }
    }

    // === 10-B3) 끝까지 — 기울여 티칭한 대상을 창에서 실제로 찾는다. 준비 안 된 옵트는 못 찾는다 ===
    using (var scene = new Mat(400, 500, MatType.CV_8UC1, Scalar.Black))
    {
        var pat = new CvPatternOpt { TrainedOriginX = 150, TrainedOriginY = 120, TrainedAngleDeg = 25 };
        var found = new CvPose(70, pat.TrainedOriginX, pat.TrainedOriginY, 300, 230, 0.95, 1.0);
        const double lmAngle = 20;

        // 비대칭 템플릿(자세가 실제로 구별되게) — 학습각을 편 상태로 저장되는 규약 그대로.
        using var templ = new Mat(34, 46, MatType.CV_8UC1, Scalar.All(40));
        Cv2.Rectangle(templ, new Rect(4, 4, 30, 10), new Scalar(230), -1);
        Cv2.Circle(templ, new OpenCvSharp.Point(38, 26), 5, new Scalar(230), -1);
        Cv2.ImEncode(".png", templ, out var lmPng);

        var lm = new CvPatternOpt
        {
            TrainedOriginX = 240, TrainedOriginY = 80, TrainedAngleDeg = lmAngle, TemplatePng = lmPng,
            AcceptScore = 0.45,
            // 각도 탐색을 끈 상태가 이 결함이 드러나는 조건이다 — 켜 두면 존이 어긋남을 덮어 가려진다.
            UseAngleSearch = false,
        };

        // 런 장면에 그 템플릿을 실제 자세로 심는다: 티칭 공간 회전(발견각−앵커 학습각) + 대상 자신의 학습각.
        var placeDeg = (found.ThetaDeg - pat.TrainedAngleDeg) + lmAngle;
        var (tx, ty) = CvInspGeom.XformByPose(pat, found, lm.TrainedOriginX, lm.TrainedOriginY);
        using (var stamp = new Mat(400, 500, MatType.CV_8UC1, Scalar.Black))
        {
            using var roi = new Mat(stamp, new Rect((int)tx - templ.Cols / 2, (int)ty - templ.Rows / 2, templ.Cols, templ.Rows));
            templ.CopyTo(roi);
            using var rot = Cv2.GetRotationMatrix2D(new Point2f((float)tx, (float)ty), -placeDeg, 1.0);
            Cv2.WarpAffine(stamp, scene, rot, stamp.Size());
        }

        var got = CvInspGeom.NormalizedWindow(scene, pat, found, lm, marginPx: 12);
        using var win = got!.Value.Window;

        var ok = CvInspGeom.MatchPattern(win, got.Value.MatchOpt);

        // 대조군 — 창은 그대로 두고 옵트만 준비 안 된 것(학습각이 그대로)으로 바꾼다.
        var raw = lm.Clone();
        raw.UseSearchRegion = false;
        var missed = CvInspGeom.MatchPattern(win, raw);

        // 단언은 **평가된 자세**로 한다. 점수 낙차로 재면 대상 나름이라 합성 장면에서는 작게 나오는데,
        // 틀어진 사실 자체는 각도에 정확히 드러난다 — 창은 대상을 0° 로 세웠고, 준비 안 된 옵트는
        // 학습각(20°) 한 자세만 평가한다(각도 탐색이 꺼져 존이 0 이라 그 하나뿐이다).
        Check(ok.Pose is { } a && Math.Abs(a.ThetaDeg) < 1e-6,
            $"the prepared opt evaluates the pose the window actually stood up (theta={ok.Pose?.ThetaDeg})");
        Check(missed.Pose is { } b && Math.Abs(b.ThetaDeg - lmAngle) < 1e-6,
            $"the unprepared one is pinned to the trained angle — a pose the window does not contain (theta={missed.Pose?.ThetaDeg})");
        Check(ok.Score > missed.Score,
            $"so it scores worse, and how much worse is the target's business (prepared={ok.Score:F4} unprepared={missed.Score:F4})");

        // 탐색 사각이 실제로 걸렸는가 — 발견 중심이 창 중앙 ±마진 안에 있어야 한다. 안 걸면 파인더는
        // 창 전체를 허용 범위로 잡아 실효 반경이 "마진 + 템플릿 절반" 으로 넓어진다(옆 후보가 미끄러져 들어온다).
        // (창↔이미지 좌표 왕복 자체는 앞 절이 무게중심으로 못 박았다 — 여기서 다시 재지 않는다.)
        Check(ok.Pose is { } c
              && Math.Abs(c.FoundX - win.Width / 2.0) <= 12 + 1e-6
              && Math.Abs(c.FoundY - win.Height / 2.0) <= 12 + 1e-6,
            $"the centre stays inside the +-margin box the caller asked for ({ok.Pose?.FoundX:F1},{ok.Pose?.FoundY:F1} in a {win.Width}x{win.Height} window)");
    }

    // === 10-C) Clone — 프로퍼티를 손으로 베끼지 않으므로 빠지는 것이 없다 ===
    {
        var src = new CvPatternOpt
        {
            TrainShape = CvTrainShape.Circle, TrainedShape = CvTrainShape.Circle,
            TrainedAngleDeg = 25, AcceptScore = 0.77, MaxCount = 3, TemplatePng = [1, 2, 3],
        };
        var notified = 0;
        src.PropertyChanged += (_, _) => notified++;

        var copy = src.Clone();
        Check(copy.TrainedShape == CvTrainShape.Circle && Math.Abs(copy.AcceptScore - 0.77) < 1e-12
              && copy.MaxCount == 3 && ReferenceEquals(copy.TemplatePng, src.TemplatePng),
            "every value comes across, including the ones a hand-written copy forgets (TrainedShape drives the circular mask)");

        copy.MaxCount = 9;
        copy.AcceptScore = 0.1;
        Check(src.MaxCount == 3 && Math.Abs(src.AcceptScore - 0.77) < 1e-12, "editing the copy leaves the original alone");
        Check(notified == 0, "the copy does not report its edits to whoever is watching the original (a stale editor would react)");

        // 템플릿 크기는 디코드 없이 머리글에서 — 창을 "템플릿 + 여유" 로 잡으려면 매칭 전에 알아야 한다.
        using (var templ = new Mat(37, 52, MatType.CV_8UC1, Scalar.Gray))
        {
            Cv2.ImEncode(".png", templ, out var png);
            var sized = new CvPatternOpt { TemplatePng = png };
            Check(sized.TemplateSize() is { } sz && sz.W == 52 && sz.H == 37,
                $"TemplateSize reads a real encoded PNG header without decoding it ({sized.TemplateSize()})");
        }
        Check(new CvPatternOpt().TemplateSize() is null, "no template trained yet — no size, and no exception");
        Check(new CvPatternOpt { TemplatePng = [1, 2, 3] }.TemplateSize() is null, "bytes that are not a PNG give null instead of a wrong number");

        // 재학습은 배열을 갈아 끼운다 — 사본이 들고 있던 템플릿은 그대로다(공유해도 안전한 이유).
        var before = src.TemplatePng;
        var held = src.Clone();
        src.TemplatePng = [9, 9];
        Check(ReferenceEquals(held.TemplatePng, before) && !ReferenceEquals(src.TemplatePng, before),
            "re-training the original swaps its reference; the copy keeps the template it was cloned with");
    }
    }

    /// <summary>측정값이 "그럴싸하게 틀리는" 세 자리 — 못 쓰는 포즈, 증거 부족, 못 본 면적.
    /// 셋 다 조용히 답을 내놓던 것을 드러내게 고친 자리다.</summary>
    [Fact]
    public void PlausibleButWrongMeasurements()
    {
    // === 9-A) default(CvPose) 는 null 이 못 되고 Scale 이 0 이다 ===
    {
        // 빈 결과에 FirstOrDefault() 를 쓰면 "멀쩡해 보이는" 포즈가 손에 들어온다. 그것으로 티칭 기하를
        // 옮기면 좌표가 전부 (0,0) 으로 접히는데, 화면에는 도형이 그려지고 검사도 돌아 틀렸다는 신호가 없다.
        var empty = new List<CvPose>();
        var fallback = empty.FirstOrDefault();
        Check(fallback.Scale == 0 && !fallback.IsValid,
            $"default(CvPose) is not an identity — Scale is 0, not the constructor's 1.0 (scale={fallback.Scale})");

        var real = new CvPose(12, 100, 100, 250, 180, 0.93);
        Check(real.IsValid && real.Apply(300, 200) is { } p && Math.Abs(p.X - 250) > 1,
            "a real pose still transforms");
        Check(Assert.Throws<InvalidOperationException>(() => fallback.Apply(300, 200)).Message.Contains("FirstOrDefault"),
            "an unusable pose refuses to fold coordinates onto (0,0) and says where it came from");
        Check(Assert.Throws<InvalidOperationException>(() => fallback.Inverse()) is not null,
            "inverting it would give an infinite scale, which kills the process in native code — it throws instead");
    }

    // === 9-B) 증거가 줄면 잔차는 좋아진다 — 점 수로만 걸린다 ===
    using (var img = new Mat(200, 400, MatType.CV_8UC1, Scalar.Black))
    {
        // 티칭 세그먼트는 가로 360px 인데 에지는 왼쪽 100px 에만 있다.
        Cv2.Rectangle(img, new Rect(20, 100, 100, 100), new Scalar(255), -1);
        Cv2.GaussianBlur(img, img, new OpenCvSharp.Size(3, 3), 0);

        var opt = new CvFindLineOpt
        {
            StartX = 20, StartY = 100, EndX = 380, EndY = 100,
            NumCalipers = 12, SearchLength = 40, ProjectionLength = 3,
            Polarity = CvEdgePolarity.Either, ContrastThreshold = 10,
            UseRmsGate = true, MaxRmsPx = 2.0,
        };
        var partial = CvLineFinder.Find(img, opt.StartX, opt.StartY, opt.EndX, opt.EndY, opt);
        Check(partial is { } f && f.PointCount < 6 && f.RmsPx < 0.5,
            $"a line seen over a fraction of the taught segment still passes the residual gate — the residual gets BETTER as evidence disappears: {partial}");

        opt.MinPoints = 8;
        Check(CvLineFinder.Find(img, opt.StartX, opt.StartY, opt.EndX, opt.EndY, opt) is null,
            "MinPoints is the only gate that catches it — the residual never will");

        opt.MinPoints = 0;
        Check(CvLineFinder.Find(img, opt.StartX, opt.StartY, opt.EndX, opt.EndY, opt) is not null, "0 keeps the previous behaviour (nothing changes for a recipe that does not set it)");
    }

    // === 9-C) 못 본 면적을 분모에서 빼면 충전율이 올라간다 (거짓 OK) ===
    using (var full = new Mat(300, 300, MatType.CV_8UC1, Scalar.Black))
    {
        Cv2.Circle(full, new OpenCvSharp.Point(150, 150), 90, new Scalar(255), -1);
        var opt = new CvRingFillOpt { RMinPx = 60, RMaxPx = 80, Threshold = 128, Polarity = CvBlobPolarity.Bright };

        var inside = CvRingFill.Measure(full, 150, 150, opt);
        Check(inside is { } a && a.OutsidePx == 0 && a.RatePct > 99.5,
            $"a band fully inside the image is unchanged: {inside}");

        // 같은 밴드를 화면 가장자리로 옮긴다 — 보이는 부분은 여전히 꽉 차 있다.
        var clipped = CvRingFill.Measure(full, 20, 150, opt);
        Check(clipped is { } b && b.OutsidePx > 0 && b.RatePct < 80,
            $"the part that was never seen counts as unfilled instead of vanishing from the denominator: {clipped}");
        Check(clipped is { } c && Math.Abs(c.TotalPx - inside!.Value.TotalPx) < inside.Value.TotalPx * 0.02,
            $"the denominator is the whole band either way — that is what makes two runs comparable (in={inside!.Value.TotalPx} clipped={clipped!.Value.TotalPx})");
    }
    }
}
