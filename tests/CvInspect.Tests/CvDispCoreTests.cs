// 디스플레이 컨트롤의 UI 무관 규칙(CvInspect.Display — UI 패키지가 소스로 링크)의 코어 회귀. WPF·Avalonia 판이 같은 규칙을 쓰므로
// 여기서 한 번 못 박으면 두 판이 함께 지켜진다. Linux CI 에서도 돈다.
// 못 박는 것: 상태 줄 글자 형식 · 프레임 버퍼(형 분기·Mat 스냅샷·공급자 계약 검사와 경고 문구·픽셀 값 라벨·맞춤 다시 할 차례·저장용 복원) ·
// 카메라 명령 판정(던지는 CanExecute 는 실행 가능 + 인스턴스당 한 번 경고, 클릭 결과, ⏯ 가 고르는 명령) · 이미지 파일 규칙
// (대화상자 필터·기본 이름·JPEG 알파 접기·확장자 없으면 PNG·비ASCII 경로) · 색 규칙(키 글자·기본색·출처 순서·경고 문구) ·
// 메뉴 구성 · 도형 구독 걸기 판단 · 휠 배수 · 편집기 Source 규칙.
// 화면 자체(버튼 회색·메뉴 표시·비트맵)는 Windows 스위트(CvInspect.Wpf.Tests) 몫이다.
using System.Windows.Input;
using CvInspect.Controls;
using CvInspect.Display;
using CvInspect.Vision.Edit;
using OpenCvSharp;
using Xunit;

namespace CvInspect.Tests;

public class CvDispCoreTests
{
    /// <summary>단언 하나 — 실패 메시지가 곧 그 회귀가 존재하는 이유다.</summary>
    private static void Check(bool cond, string why) => Assert.True(cond, why);

    /// <summary>디스플레이 출처("CvDispCtrl")의 로그 줄을 모은다. 먼저 빈 싱크를 붙여 앞서 붙잡혀 있던 줄을 흘려보낸다 — 안 그러면 그 줄이
    /// 이 시험의 줄로 섞인다.</summary>
    private sealed class DispLog : IDisposable
    {
        private readonly Action<CvLogLevel, string, string, Exception?>? _prev = CvLog.Sink;
        internal readonly List<string> Lines = new();

        internal DispLog()
        {
            CvLog.Sink = static (_, _, _, _) => { };
            CvLog.Sink = (lv, src, msg, _) => { if (src == "CvDispCtrl") Lines.Add($"{lv}|{msg}"); };
        }

        public void Dispose() => CvLog.Sink = _prev;
    }

    private sealed class PixSrc(byte[] pixels, int w, int h, int stride, int ch) : ICvPixelSource
    {
        public byte[] Pixels { get; } = pixels;
        public int Width { get; } = w;
        public int Height { get; } = h;
        public int Stride { get; } = stride;
        public int Channels { get; } = ch;
    }

    private sealed class Cmd : ICommand
    {
        internal bool Can = true;
        internal bool Throws;
        internal int Asked;
        internal int Runs;
        internal readonly List<object?> Params = new();

        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter)
        {
            Asked++;
            if (Throws) throw new InvalidOperationException("boom");
            return Can;
        }

        public void Execute(object? parameter)
        {
            Runs++;
            Params.Add(parameter);
        }
    }

    // ───────────── 상태 줄 ─────────────

    /// <summary>상태 줄 글자는 화면 계약이다 — 두 UI 패키지가 같은 글자를 내야 하고, 칸 사이 공백 둘이 고정폭 글꼴에서 칸을 가른다.</summary>
    [Fact]
    public void StatusTextKeepsTheDocumentedShapes()
    {
        Check(CvDispStatus.SizeText((640, 480), 1.0) == "640×480  100%",
            $"right cell is \"W×H  pct%\" with the multiplication sign and two spaces (got '{CvDispStatus.SizeText((640, 480), 1.0)}')");
        Check(CvDispStatus.SizeText((640, 480), 0.4567) == "640×480  46%", "the zoom percent is rounded to a whole number");
        Check(CvDispStatus.SizeText((8, 8), 0.125) == "8×8  12%",
            $"the percent uses Math.Round's default (half to even: 12.5 -> 12) — changing the rounding mode changes what operators read (got '{CvDispStatus.SizeText((8, 8), 0.125)}')");
        Check(CvDispStatus.SizeText(null, 3.0) == "", "no frame -> the right cell is empty, not a stale size");

        Check(CvDispStatus.ProbeText((3, 4, "V 7")) == "X 3  Y 4  V 7", $"left cell is \"X x  Y y  value\" (got '{CvDispStatus.ProbeText((3, 4, "V 7"))}')");
        Check(CvDispStatus.ProbeText((3, 4, null)) == "X 3  Y 4", "without a value label the left cell stops after Y — no trailing spaces");
        Check(CvDispStatus.ProbeText(null) == "", "cursor off the image -> the left cell is empty");
    }

    // ───────────── 프레임 버퍼 ─────────────

    /// <summary>픽셀 값 라벨 — 1채널은 "V n", 컬러는 "RGB r,g,b" 로 R 부터 읽는다(저장 순서는 BGR(A)). 순서를 뒤집으면 빨강과 파랑이 바뀐 값을 보인다.</summary>
    [Fact]
    public void ValueLabelsReadGrayAndColorInRgbOrder()
    {
        var buf = new CvFrameBuf();
        using (var gray = new Mat(2, 3, MatType.CV_8UC1, Scalar.All(0)))
        {
            gray.Set(1, 2, (byte)77);
            buf.Set(gray);
        }
        Check(buf.ValueAt(2, 1) == "V 77", $"gray pixel reads as \"V n\" (got '{buf.ValueAt(2, 1)}')");
        Check(buf.ValueAt(3, 1) is null && buf.ValueAt(-1, 0) is null && buf.ValueAt(0, 2) is null, "outside the image there is no value");

        using (var bgr = new Mat(2, 2, MatType.CV_8UC3, Scalar.All(0)))
        {
            bgr.Set(1, 1, new Vec3b(10, 20, 30));
            buf.Set(bgr);
        }
        Check(buf.ValueAt(1, 1) == "RGB 30,20,10", $"BGR storage reads back as RGB (got '{buf.ValueAt(1, 1)}')");

        using (var bgra = new Mat(1, 2, MatType.CV_8UC4, Scalar.All(0)))
        {
            bgra.Set(0, 1, new Vec4b(1, 2, 3, 200));
            buf.Set(bgra);
        }
        Check(buf.ValueAt(1, 0) == "RGB 3,2,1", $"BGRA reads RGB and leaves alpha out (got '{buf.ValueAt(1, 0)}')");
    }

    /// <summary>Mat 은 대입 순간 자체 버퍼로 복사된다 — 호스트는 대입 직후 Mat 을 dispose 한다. 서브뷰(ROI)는 행 사이 패딩이 있어 연속 복사로
    /// 읽으면 옆 열을 읽는다.</summary>
    [Fact]
    public void MatFramesAreSnapshottedIncludingSubViews()
    {
        var buf = new CvFrameBuf();
        using (var big = new Mat(4, 5, MatType.CV_8UC1, Scalar.All(0)))
        {
            big.Set(2, 3, (byte)99);
            using var roi = new Mat(big, new Rect(1, 1, 3, 2));
            Check(!roi.IsContinuous(), "control: the ROI really is non-continuous, so this test exercises the Clone path");
            buf.Set(roi);
            big.Set(2, 3, (byte)5);   // 대입 뒤 원본을 바꿔도 화면 픽셀은 그대로여야 한다
        }
        Check(buf.Width == 3 && buf.Height == 2 && buf.Stride == 3 && buf.Channels == 1,
            $"a Mat snapshot is tight (stride = width x channels) (w={buf.Width} h={buf.Height} stride={buf.Stride} ch={buf.Channels})");
        Check(buf.ValueAt(2, 1) == "V 99", $"the snapshot holds the ROI's own pixels as of assignment, after the source was changed and disposed (got '{buf.ValueAt(2, 1)}')");

        using (var f16 = new Mat(2, 2, MatType.CV_16UC1, Scalar.All(1)))
            buf.Set(f16);
        Check(!buf.HasFrame && buf.Width == 0, "only 8-bit 1/3/4-channel Mats are shown; others give a blank frame");
    }

    /// <summary>공급자 배열은 복사하지 않고 참조로 쥔다("발행 뒤 불변" 계약) — 행 끝 패딩은 그쪽 Stride 로 건넌다.</summary>
    [Fact]
    public void PixelSourcesAreHeldByReferenceAndHonourRowPadding()
    {
        // 2x2 Gray8, 행 보폭 4(패딩 2바이트) — 패딩 자리에 표식을 둬 보폭을 무시하면 그 값을 읽게 한다.
        var px = new byte[] { 1, 2, 0xEE, 0xEE, 3, 4, 0xEE, 0xEE };
        var buf = new CvFrameBuf();
        buf.Set(new PixSrc(px, 2, 2, 4, 1));
        Check(ReferenceEquals(buf.Pixels, px), "the provider's array is kept by reference — copying it would double the per-frame cost");
        Check(buf.ValueAt(0, 1) == "V 3" && buf.ValueAt(1, 1) == "V 4",
            $"rows are addressed with the provider's stride, not width x channels (got '{buf.ValueAt(0, 1)}', '{buf.ValueAt(1, 1)}')");

        using var snap = buf.SnapMat()!;
        Check(snap.IsContinuous() && snap.Width == 2 && snap.Height == 2 && snap.At<byte>(1, 0) == 3,
            "the save snapshot folds the row padding into a continuous Mat with the same pixels");
    }

    /// <summary>계약을 어긴 공급자 값은 빈 화면 + 경고 한 줄 — 그대로 넘기면 비트맵 쓰기가 던져 화면이 죽는다. 경고 문구는 현장 로그를
    /// 거르는 글자라 그대로 못 박는다. 채널 수가 표시 밖인 것은 계약 위반이 아니라 "표시 불가" 라 경고하지 않는다.</summary>
    [Fact]
    public void ContractViolationsGiveABlankFrameAndOneWarningEach()
    {
        using var log = new DispLog();
        var buf = new CvFrameBuf();

        buf.Set(new PixSrc(new byte[6], 3, 2, 2, 1));   // stride < width x channels
        buf.Set(new PixSrc(new byte[5], 3, 2, 3, 1));   // 길이 < stride x height
        buf.Set(new PixSrc(null!, 3, 2, 3, 1));          // 배열 없음
        Check(!buf.HasFrame, "a violating provider leaves the frame blank");
        Check(log.Lines.Count == 3, $"each violation logs one warning (lines={log.Lines.Count})");
        Check(log.Lines[0] == "Warning|Frame ignored: ICvPixelSource buffer violates its contract (w=3 h=2 ch=1 stride=2 len=6).",
            $"stride violation text (got '{log.Lines[0]}')");
        Check(log.Lines[1] == "Warning|Frame ignored: ICvPixelSource buffer violates its contract (w=3 h=2 ch=1 stride=3 len=5).",
            $"length violation text (got '{log.Lines[1]}')");
        Check(log.Lines[2] == "Warning|Frame ignored: ICvPixelSource buffer violates its contract (w=3 h=2 ch=1 stride=3 len=-1).",
            $"a missing array reports len=-1 (got '{log.Lines[2]}')");

        log.Lines.Clear();
        buf.Set(new PixSrc(new byte[8], 2, 2, 4, 2));    // 2채널 — 표시 불가
        buf.Set(new PixSrc(new byte[8], 0, 2, 4, 1));    // 폭 0
        Check(!buf.HasFrame && log.Lines.Count == 0, $"unshowable formats and empty sizes are blank without a warning (lines={log.Lines.Count})");

        buf.Set("not a frame");
        Check(!buf.HasFrame && log.Lines.Count == 1
              && log.Lines[0] == "Warning|Frame ignored: expected Mat or ICvPixelSource, got System.String.",
            $"a value of another type is blank with the type named in the warning (got '{string.Join(" / ", log.Lines)}')");
    }

    /// <summary>맞춤을 다시 할 차례 — 치수가 바뀌거나 프레임이 없어졌을 때만. 같은 치수의 새 프레임마다 맞추면 사람이 준 줌·이동이 프레임마다 날아간다.</summary>
    [Fact]
    public void RefitIsDueOnlyWhenTheSizeChangesOrTheFrameGoes()
    {
        var buf = new CvFrameBuf();
        Check(buf.Set(new PixSrc(new byte[6], 3, 2, 3, 1)), "the first frame is due a fit");
        Check(!buf.Set(new PixSrc(new byte[6], 3, 2, 3, 1)), "a same-size frame keeps the user's zoom");
        Check(buf.Set(new PixSrc(new byte[8], 4, 2, 4, 1)), "a size change refits");
        Check(buf.Set(null), "clearing the frame is due a fit, so the next frame comes up fitted");
        Check(buf.Pixels is null && buf.SnapMat() is null, "a cleared buffer has nothing to save");
    }

    // ───────────── 카메라 명령 ─────────────

    /// <summary>CanExecute 가 던지면 실행 가능으로 본다(회색으로 바꾸면 요란하던 실패가 조용히 굳은 버튼이 된다). 경고는 명령 인스턴스마다 한 번 —
    /// 버튼 상태는 대입·알림·클릭마다 다시 물어 매번 남기면 같은 줄이 로그를 덮는다(실측 8줄).</summary>
    [Fact]
    public void AThrowingCanExecuteCountsAsRunnableAndWarnsOncePerInstance()
    {
        using var log = new DispLog();
        var a = new Cmd { Throws = true };
        var b = new Cmd { Throws = true };
        for (var i = 0; i < 5; i++) Check(CvCamCmd.CanRun(a), "a throwing CanExecute is treated as runnable");
        Check(a.Asked == 5, "it is still asked every time — only the warning is deduplicated");
        Check(log.Lines.Count == 1, $"five throws from one instance log once (lines={log.Lines.Count})");
        Check(log.Lines[0] == "Warning|Cmd.CanExecute threw; treating the command as runnable, so the throw does not grey its button "
                              + "and clicks still reach Execute. Logged once per command instance; later throws from it are not logged.",
            $"the warning says what happens on both the refresh and the click path (got '{log.Lines[0]}')");

        CvCamCmd.CanRun(b);
        Check(log.Lines.Count == 2, "a second instance of the same command type warns on its own — the table is keyed by instance, not by type");

        Check(CvCamCmd.Click(a) == CvCmdRun.Ran && a.Runs == 1, "a click on a throwing command still reaches Execute (the pre-CanExecute behaviour)");
        Check(log.Lines.Count == 2, "the click path does not log again for an instance already warned");
    }

    /// <summary>버튼 활성과 클릭 — 명령이 없으면 버튼은 살아 있고 아무 일도 없다. 거절은 실행하지 않고 Refused 를 돌려준다(어댑터가 그때
    /// 버튼 상태를 다시 정한다 — 알림 없이 false 가 된 명령의 유일한 신호). 실행에는 늘 null 을 넘긴다.</summary>
    [Fact]
    public void ClicksRunOnlyWhatCanExecuteAllowsAndReportRefusals()
    {
        Check(CvCamCmd.IsEnabled(null), "no command -> the button stays enabled (the old behaviour)");
        Check(CvCamCmd.Click(null) == CvCmdRun.NoCommand, "no command -> a click does nothing and asks for no refresh");

        var cmd = new Cmd { Can = false };
        Check(!CvCamCmd.IsEnabled(cmd), "CanExecute false -> the button is grey");
        Check(CvCamCmd.Click(cmd) == CvCmdRun.Refused && cmd.Runs == 0, "a click that arrives after CanExecute went false runs nothing and reports the refusal");

        cmd.Can = true;
        Check(CvCamCmd.Click(cmd) == CvCmdRun.Ran && cmd.Runs == 1 && cmd.Params.Single() is null,
            "an allowed click runs once, with a null parameter like a standard command button");
    }

    /// <summary>⏯ 의 활성은 눌렀을 때 낼 명령이 정한다 — 시작 명령 하나로 정하면 시작이 막힌 사이에 라이브를 끌 수 없다.
    /// 토글 직후에는 눌림이면 시작, 풀림이면 정지를 낸다.</summary>
    [Fact]
    public void PlayPicksStopWhileRunningAndStartWhileStopped()
    {
        var start = new Cmd();
        var stop = new Cmd();
        Check(ReferenceEquals(CvCamCmd.PlayGateCmd(true, start, stop), stop), "while running, the stop command decides whether ⏯ is enabled");
        Check(ReferenceEquals(CvCamCmd.PlayGateCmd(false, start, stop), start), "while stopped, the start command decides");
        Check(ReferenceEquals(CvCamCmd.PlayClickCmd(true, start, stop), start), "a toggle that just went checked sends start");
        Check(ReferenceEquals(CvCamCmd.PlayClickCmd(false, start, stop), stop), "a toggle that just went unchecked sends stop");
    }

    // ───────────── 이미지 파일 ─────────────

    /// <summary>대화상자 필터와 기본 이름은 사람이 보는 글자다 — 0.33 까지의 리터럴과 글자 단위로 같다.</summary>
    [Fact]
    public void DialogFiltersAndDefaultNameMatchTheShippedStrings()
    {
        var open = CvImgFileIO.WinFilter(CvImgFileIO.OpenTypes);
        var save = CvImgFileIO.WinFilter(CvImgFileIO.SaveTypes);
        Check(open == "Image|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff|All|*.*", $"open filter (got '{open}')");
        Check(save == "PNG|*.png|BMP|*.bmp|JPEG|*.jpg", $"save filter — PNG first, matching the default name (got '{save}')");
        var name = CvImgFileIO.DefaultSaveName(new DateTime(2026, 10, 27, 9, 5, 3));
        Check(name == "image_20261027_090503.png", $"default save name is image_yyyyMMdd_HHmmss.png with 24-hour, zero-padded fields (got '{name}')");
    }

    /// <summary>인코드 규칙 — JPEG 는 알파를 못 담아 BGRA 를 BGR 로 접는다(안 접으면 인코더가 실패하거나 채널이 어긋난다). 다른 형식은 알파를 지킨다.
    /// 확장자가 없으면 PNG 다(대화상자에서 이름만 친 경우).</summary>
    [Fact]
    public void EncodeFoldsAlphaForJpegAndDefaultsToPng()
    {
        using var bgra = new Mat(4, 4, MatType.CV_8UC4, new Scalar(10, 20, 30, 128));

        using (var jpg = CvImgFileIO.Decode(CvImgFileIO.Encode("frame.jpg", bgra)))
            Check(jpg.Channels() == 3, $"a 4-channel frame saved as .jpg comes back as 3-channel BGR (channels={jpg.Channels()})");
        using (var jpeg = CvImgFileIO.Decode(CvImgFileIO.Encode("frame.jpeg", bgra)))
            Check(jpeg.Channels() == 3, "the .jpeg spelling folds alpha too");
        using (var png = CvImgFileIO.Decode(CvImgFileIO.Encode("frame.png", bgra)))
            Check(png.Channels() == 4 && png.At<Vec4b>(0, 0).Item3 == 128, "PNG keeps the alpha channel and its value");

        var noExt = CvImgFileIO.Encode("frame", bgra);
        Check(noExt.Length > 8 && noExt[0] == 0x89 && noExt[1] == (byte)'P' && noExt[2] == (byte)'N' && noExt[3] == (byte)'G',
            "a name without an extension is encoded as PNG (PNG signature)");
    }

    /// <summary>읽을 수 없는 바이트는 던진다(빈 Mat 을 명령에 넘기면 받는 쪽이 빈 화면을 원본으로 쓴다). 문구는 오류 창에 그대로 뜬다.</summary>
    [Fact]
    public void DecodeRejectsBytesThatAreNotAnImage()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CvImgFileIO.Decode("not an image at all"u8.ToArray()));
        Check(ex.Message == "Unsupported image format", $"message shown in the error box (got '{ex.Message}')");

        var empty = Assert.Throws<InvalidOperationException>(() => CvImgFileIO.WriteFrame("unused.png", new CvFrameBuf()));
        Check(empty.Message == "Unsupported frame format", $"saving with no frame names the cause (got '{empty.Message}')");
    }

    /// <summary>파일은 바이트로 읽고 쓴다 — 경로 기반 ImRead/ImWrite 는 Windows 비ASCII(한글) 경로에서 조용히 실패한다. 불러온 Mat 의 소유권은
    /// 불러오기 명령으로 넘어간다(받을 곳이 없으면 해제 — 던지지 않는다). 경로 기반으로 되돌리는 변이는 Windows CI 에서만 빨개진다 —
    /// Linux 는 UTF-8 경로가 어느 쪽으로도 열린다.</summary>
    [Fact]
    public void SaveAndLoadGoThroughBytesSoNonAsciiPathsWork()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cvinspect-디스플레이-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "검사 영상.png");
            var px = new byte[] { 1, 2, 3, 4, 5, 6 };
            var buf = new CvFrameBuf();
            buf.Set(new PixSrc(px, 3, 2, 3, 1));
            CvImgFileIO.WriteFrame(path, buf);
            Check(File.Exists(path), "the frame is written under a non-ASCII path");

            var cmd = new Cmd();
            CvImgFileIO.LoadInto(path, cmd);
            Check(cmd.Runs == 1 && cmd.Params.Single() is Mat, "the decoded Mat is handed to the load command");
            using (var mat = (Mat)cmd.Params.Single()!)
                Check(mat.Width == 3 && mat.Height == 2 && mat.At<byte>(1, 2) == 6, "the file round-trips the frame's pixels");
            Check(cmd.Asked == 0, "the load command's CanExecute is not consulted — the parameter only exists after the dialog closes");

            CvImgFileIO.LoadInto(path, null);   // 받을 곳이 없으면 해제하고 조용히 끝난다
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ───────────── 색 규칙 ─────────────

    /// <summary>키 글자는 호스트 색 사전이 적는 공개 계약이다 — 틀리면 그 사전이 아무 말 없이 안 먹는다.</summary>
    [Fact]
    public void ThemeKeysAndDefaultColorsAreTheDocumentedOnes()
    {
        Check(CvDispTheme.ToolbarBackgroundKey == "CvDispToolbarBackgroundBrush"
              && CvDispTheme.StatusBarBackgroundKey == "CvDispStatusBarBackgroundBrush"
              && CvDispTheme.StatusBarForegroundKey == "CvDispStatusBarForegroundBrush"
              && CvDispTheme.SeparatorBrushKey == "CvDispSeparatorBrush"
              && CvDispTheme.ViewBackgroundKey == "CvDispViewBackgroundBrush"
              && CvDispTheme.PlaceholderForegroundKey == "CvDispPlaceholderForegroundBrush",
            "own resource keys spell the documented strings");
        Check(CvDispTheme.PrimaryHostKey == "PrimaryBrush" && CvDispTheme.SuccessHostKey == "SuccessBrush" && CvDispTheme.DangerHostKey == "DangerBrush",
            "accent keys are the host control library's names — only the names are known, the library is not referenced");
        Check(CvPropEditSvc.HostDescKey == "SecondaryTextBrush" && CvPropEditSvc.HostHeaderKey == "PrimaryBrush",
            "editor caption/header follow the same host library keys");

        static string Hex(ViPaint p) => $"{p.A:X2}{p.R:X2}{p.G:X2}{p.B:X2}";
        Check(Hex(CvDispTheme.DefaultBar) == "FFFAFAFA" && Hex(CvDispTheme.DefaultStatusFg) == "FF555555" && Hex(CvDispTheme.DefaultSep) == "FFDDDDDD",
            "chrome defaults #FAFAFA / #555555 / #DDDDDD (documented on the public properties)");
        Check(Hex(CvDispTheme.DefaultPrimary) == "FF1565C0" && Hex(CvDispTheme.DefaultSuccess) == "FF2E7D32" && Hex(CvDispTheme.DefaultDanger) == "FFC62828",
            "accent defaults #1565C0 / #2E7D32 / #C62828");
        Check(Hex(CvDispTheme.DefaultControlBack) == "FF2A2A2A", "control background default #2A2A2A — what the view shows without a view key");
    }

    /// <summary>출처 순서 — 인스턴스 값이 키를 이기고, 형이 다른 값은 없는 것으로 본다. 경고 문구는 호스트가 "키가 안 먹는다" 를 로그로 찾는 글자다.</summary>
    [Fact]
    public void ThemePickOrderAndWarningTexts()
    {
        object own = "own", slot = "slot";
        Check(ReferenceEquals(CvDispTheme.Pick<string>(own, slot), own), "the instance value beats the key");
        Check(ReferenceEquals(CvDispTheme.Pick<string>(null, slot), slot), "no instance value -> the key");
        Check(ReferenceEquals(CvDispTheme.Pick<string>(42, slot), slot), "an instance value of the wrong type is skipped, not used");
        Check(CvDispTheme.Pick<string>(null, 42) is null, "neither -> null, so the caller falls back to its default");

        Check(CvDispTheme.NotBrushWarning(CvDispTheme.ToolbarBackgroundKey, "Color")
              == "Resource 'CvDispToolbarBackgroundBrush' is a Color, not a Brush; ignored, the default color is used.",
            $"non-brush warning for a chrome key (got '{CvDispTheme.NotBrushWarning(CvDispTheme.ToolbarBackgroundKey, "Color")}')");
        Check(CvDispTheme.NotBrushWarning(CvDispTheme.ViewBackgroundKey, "Color")
              == "Resource 'CvDispViewBackgroundBrush' is a Color, not a Brush; ignored, the control's Background shows instead.",
            "the view key falls back to the control's Background, and says so");
        Check(CvDispTheme.HintBrushWarning("LinearGradientBrush")
              == "Placeholder text brush is a LinearGradientBrush; only SolidColorBrush is supported, the default color is used.",
            "placeholder brush warning text");

        Check(CvDispTheme.HintPaint(255, 1, 2, 3, 0.5).A == 128, "brush opacity multiplies alpha and rounds (127.5 -> 128)");
        Check(CvDispTheme.HintPaint(200, 1, 2, 3, 2.0).A == 200 && CvDispTheme.HintPaint(200, 1, 2, 3, -1).A == 0,
            "opacity is clamped to [0, 1] before multiplying — out-of-range opacity cannot wrap the byte");
        var p = CvDispTheme.HintPaint(255, 1, 2, 3, 1);
        Check(p.R == 1 && p.G == 2 && p.B == 3 && p.Named is null, "colour channels pass through as ARGB");
    }

    // ───────────── 메뉴 ─────────────

    /// <summary>메뉴 순서·묶음 — 불러오기가 맨 위(회귀가 첫 항목으로 찾는다), 지우기가 맨 아래. 팬·맞춤·확대·축소는 툴바를 감춰도 남는다
    /// (되돌릴 유일한 수단). 감출 때는 딸린 구분선까지 한 묶음이다.</summary>
    [Fact]
    public void MenuLayoutGroupsAndKeys()
    {
        var shape = string.Join(" ", CvDispMenu.Layout.Select(e => e.Cmd is { } c ? $"{c}:{e.Group}" : $"-:{e.Group}"));
        Check(shape == "Load:Load Save:Always -:Always Pan:Always -:Always Fit:Always ZoomIn:Always ZoomOut:Always -:Toolbar Clear:Toolbar",
            $"menu order and groups (got '{shape}')");

        var keys = string.Join(" ", Enum.GetValues(typeof(CvDispMenuCmd)).Cast<CvDispMenuCmd>().Select(CvDispMenu.LabelKey));
        Check(keys == "cv:LoadImage cv:SaveImage cv:Pan cv:FitView cv:ZoomIn cv:ZoomOut cv:ClearGraphics",
            $"label keys are the shipped translation keys (got '{keys}')");

        Check(CvDispMenu.IsVisible(CvDispMenuGroup.Always, false, false), "the always group survives a hidden toolbar and no load command");
        Check(!CvDispMenu.IsVisible(CvDispMenuGroup.Load, false, true) && CvDispMenu.IsVisible(CvDispMenuGroup.Load, true, false),
            "load follows the load command only");
        Check(!CvDispMenu.IsVisible(CvDispMenuGroup.Toolbar, true, false) && CvDispMenu.IsVisible(CvDispMenuGroup.Toolbar, false, true),
            "the clear group follows the toolbar only");

        Check(!CvDispMenu.IsEnabled(CvDispMenuCmd.Save, false) && CvDispMenu.IsEnabled(CvDispMenuCmd.Save, true),
            "save is grey without a frame");
        Check(CvDispMenu.IsEnabled(CvDispMenuCmd.Load, false) && CvDispMenu.IsEnabled(CvDispMenuCmd.Fit, false),
            "everything else stays enabled without a frame");
    }

    // ───────────── 도형 구독 ─────────────

    /// <summary>도형 구독은 화면에 올라와 있는 동안만 — 내려간 사이에 건 구독은 다시 풀릴 기회가 없어 누수가 된다(실측 +799MB/40개).
    /// 같은 상태로 두 번 와도 이중 구독이 없다.</summary>
    [Fact]
    public void ShapeSubscriptionsLiveOnlyWhileOnScreen()
    {
        var hits = 0;
        var sub = new CvShapeSub((_, _) => hits++);
        var a = new CvEditRect();
        var b = new CvEditRect();

        sub.Swap([a], isOnScreen: false);
        a.X += 1;
        Check(hits == 0 && !sub.IsAttached, "a list set while off screen is not subscribed");

        sub.Attach(true, isOnScreen: true);
        sub.Attach(true, isOnScreen: true);
        a.X += 1;
        Check(hits == 1, $"attaching twice subscribes once (hits={hits})");

        sub.Swap([b], isOnScreen: true);
        a.X += 1;
        b.X += 1;
        Check(hits == 2, $"swapping moves the subscription from the old list to the new one (hits={hits})");

        sub.Attach(false, isOnScreen: false);
        b.X += 1;
        Check(hits == 2 && !sub.IsAttached, "leaving the screen unsubscribes");

        sub.Attach(true, isOnScreen: false);
        b.X += 1;
        Check(hits == 2, "an attach request while still off screen is ignored");
    }

    // ───────────── 휠 · 편집기 ─────────────

    [Fact]
    public void WheelUpZoomsInAndEverythingElseZoomsOut()
    {
        Check(CvViewXform.WheelFactor(120) == CvViewXform.ZoomStep, "wheel up (positive delta) zooms in by one step");
        Check(CvViewXform.WheelFactor(0.25) == CvViewXform.ZoomStep, "only the sign counts — a fractional touchpad delta is still one step");
        Check(CvViewXform.WheelFactor(-120) == 1 / CvViewXform.ZoomStep, "wheel down zooms out by one step");
        Check(CvViewXform.WheelFactor(0) == 1 / CvViewXform.ZoomStep, "a zero delta counts as down (the shipped comparison is delta > 0)");
    }

    public sealed class EditSrc
    {
        public int Gain { get; set; } = 1;
    }

    /// <summary>Source 를 갈아 끼우면 옛 행을 뗀다 — 화면에 남은 옛 시각 트리의 늦은 커밋이 폐기된 POCO 를 바꾸면 안 된다.
    /// null 과 plain object 는 "typed 설정 없음" 센티널이라 빈 화면이다. 이 시험은 "떼었는가" 를 본다 — 떼기와 짓기의 순서는 동기 호출 안에서
    /// 관측할 수 없어 못 박지 않는다(순서는 CvPropEditSvc.Rebuild 의 주석이 지킨다).</summary>
    [Fact]
    public void EditorRebuildDetachesTheOldRowsAndHonoursTheSentinel()
    {
        Check(!CvPropEditSvc.IsEditable(null) && !CvPropEditSvc.IsEditable(new object()) && CvPropEditSvc.IsEditable(new EditSrc()),
            "null and a plain object are the empty sentinel; a typed POCO is editable");

        var committed = new List<string>();
        var first = new EditSrc();
        var groups = CvPropEditSvc.Rebuild(Array.Empty<CvPropGroupVm>(), first,
            committed.Add, _ => { }, _ => { }, (_, _) => { }, null, true);
        var oldRow = groups.SelectMany(g => g.Rows).OfType<CvPropNumRowVm>().Single();
        oldRow.Text = "3";
        Check(first.Gain == 3 && committed.Count == 1,
            $"control: before the rebuild the row writes and notifies, so the check below can fail (gain={first.Gain}, committed={committed.Count})");

        var next = CvPropEditSvc.Rebuild(groups, new object(), committed.Add, _ => { }, _ => { }, (_, _) => { }, null, true);
        Check(next.Count == 0, "the sentinel builds no rows");

        oldRow.Text = "9";
        Check(first.Gain == 3 && committed.Count == 1,
            $"the rebuild detached the old rows first, so a late commit neither writes nor notifies (gain={first.Gain}, committed={committed.Count})");
    }
}
