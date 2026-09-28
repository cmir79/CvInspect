using OpenCvSharp;

namespace CvInspect.Imaging;

/// <summary>
/// 실 카메라 없이 프레임을 발행하는 가상 카메라. 기본은 테스트 패턴(시프팅 그라데이션)을 FrameRate 주기로 발행.
/// CamOpt.VirtualImageDir(또는 <see cref="ImageDirProvider"/> 주입)로 폴더를 지정하면 그 폴더의 이미지 파일을
/// 이름순 순환 공급 — 폴더 무효·이미지 없음·로드 실패 시 테스트 패턴 폴백(항상 프레임 발행 — 그랩 대기 타임아웃 방지).
/// 폴더 경로는 매 프레임 평가되고 목록은 변경 감지(LastWriteTime) 재열거 — 실행 중 이미지 교체 즉시 반영.
/// 컬러 이미지의 그레이 변환(IsColor=false)은 OpenCV 디코더 계수를 따른다 — 다른 변환 체계
/// (GDI 루마 등)로 만든 기대값과 화소값이 조금 다를 수 있으니 회귀 기준은 같은 경로로 만들 것.
/// 개발/CI 환경용. SerialNumber/UserSettings 무시.
/// </summary>
public sealed class VirtualCam : ICam
{
    private const string LogSource = nameof(VirtualCam);

    private static readonly string[] ImageExts = { ".bmp", ".png", ".jpg", ".jpeg", ".tif", ".tiff" };

    /// <summary>이미지 폴더가 없을 때 그리는 테스트 패턴의 크기. 설정 항목으로 두지 않는다 —
    /// 실제 프레임 크기는 공급하는 이미지가 정하고, 이 값은 볼 것이 아무것도 없을 때의 자리 표시다.</summary>
    private const int TestPatternWidth = 640;
    private const int TestPatternHeight = 480;

    private readonly object _sync = new();
    private readonly object _folderSync = new();   // 폴더 캐시/순환 인덱스 보호 — 타이머·GrabOne 동시 진입 대비
    private readonly CamOpt _opt;
    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;
    private readonly bool _color;

    private Timer? _timer;
    private int _frameCounter;
    private int _emitBusy;   // 라이브 틱 재진입 방지 — 이미지 로드가 주기보다 느릴 때 틱 스킵
    private int _emitThreadId;   // 지금 틱을 도는 스레드 — 그 스레드가 부른 정지는 자기를 기다리면 안 된다
    private bool _disposed;

    // 정지가 기다리는 것 — 타이머의 폐기 대기(Dispose(WaitHandle))가 아니라 우리가 센 "발행 중인 틱". 폐기 대기는 그것을 부른 첫 정지만
    // 받아서, 겹쳐 부른 두 번째 정지·닫기·해제·단발 그랩은 기다리지 않고 돌아가 그 뒤에 옛 틱의 프레임이 나갔다(다음 단발 그랩의 답으로
    // 읽힌다 — 검토가 찾음). 그리고 폐기 대기가 도는 콜백을 기다리는지는 런타임 구현에 달려 있다(.NET 8 은 기다린다, 다른 런타임은 확인 안 함).
    private readonly object _tickSync = new();
    private readonly ManualResetEventSlim _tickIdle = new(true);   // 발행 중인 틱이 없다. 폐기하지 않는다 — 시한 뒤에 끝난 틱이 세운다
    private int _liveGen;                                         // 타이머 세대 — 정지마다 올린다. 옛 세대의 틱은 발행하지 않는다
    private int _tickSeq;                                         // 발행에 들어선 틱의 번호(1부터)
    private int _runningTick;                                     // 지금 발행 중인 틱의 번호(없으면 0)
    private int _givenUpTick;                                     // 누군가 시한까지 기다리고 포기한 틱 — 같은 틱을 또 기다리지 않는다
    private const int DrainCapMs = 1000;

    // 이미지 폴더 파일 목록 캐시 — 경로 또는 폴더 LastWriteTime(파일 추가/삭제) 변경 시에만 재열거
    private string _dirResolved = "";
    private DateTime _dirLastWrite = DateTime.MinValue;
    private string[] _files = Array.Empty<string>();
    private int _fileIdx;

    /// <summary>
    /// 이미지 폴더 공급자(선택) — 미주입이면 CamOpt.VirtualImageDir 를 사용한다.
    /// 유효한 폴더를 반환하면 그 폴더의 이미지 파일(bmp/png/jpg/tif)을 이름순 순환 공급,
    /// null/빈 값/폴더 없음/이미지 없음이면 테스트 패턴 폴백.
    /// 매 프레임 평가되므로 실행 중 경로 변경(호스트가 설정 재로드를 배선한 경우)이 즉시 반영된다.
    /// </summary>
    public Func<string?>? ImageDirProvider { get; set; }

    public VirtualCam(CamOpt opt)
    {
        _opt = opt ?? throw new ArgumentNullException(nameof(opt));
        Name = string.IsNullOrWhiteSpace(opt.Name) ? "VirtualCam" : opt.Name;
        // 프레임 크기는 설정에 없다. 이미지 폴더를 쓰면 그 이미지가 크기를 정하고,
        // 폴더가 없을 때만 테스트 패턴을 이 크기로 그린다.
        _width = TestPatternWidth;
        _height = TestPatternHeight;
        _color = opt.IsColor;
        _stride = _width * (_color ? 3 : 1);
    }

    public string Name { get; }
    public string ComType => "Virtual";
    public bool IsConnected { get; private set; }
    public bool IsGrabbing => _timer != null;

    public event EventHandler<CamFrame>? FrameAcquired;
    public event EventHandler<ConnArgs>? ConnectionChanged;
    public event EventHandler<bool>? GrabbingChanged;

    public void Open()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (IsConnected) return;
            IsConnected = true;
        }
        ConnectionChanged?.Invoke(this, new ConnArgs(true));
        WriteLog(CvLogLevel.Info, "Virtual camera opened.");
    }

    public void Close()
    {
        bool wasGrabbing = false;
        bool wasConnected = false;
        lock (_sync)
        {
            if (_disposed) return;
            wasGrabbing = StopContinuousCore();
            wasConnected = IsConnected;
            IsConnected = false;
        }
        WaitTicksOutsideLock();
        if (wasGrabbing) GrabbingChanged?.Invoke(this, false);
        if (wasConnected) ConnectionChanged?.Invoke(this, new ConnArgs(false));
        WriteLog(CvLogLevel.Info, "Virtual camera closed.");
    }

    public void GrabOne()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            EnsureConnected();
            // 연속 취득 중에는 답할 수 없다 — 여기서 한 장 더 내도 부른 쪽은 그것과 흐르던 장을 가릴 수 없다.
            if (_timer != null)
                throw new InvalidOperationException(
                    "Continuous acquisition is running, so a single grab cannot tell its own frame from the " +
                    "stream's. Call StopContinuous() first.");
        }
        WaitTicksOutsideLock();   // 정지된 라이브의 마지막 틱이 아직 돌면 그 프레임이 이 그랩의 답으로 읽힌다
        // 기다리는 사이(최대 1 s) 닫히거나 해제됐거나 라이브가 다시 켜졌을 수 있다 — 다시 보고 낸다.
        lock (_sync)
        {
            ThrowIfDisposed();
            EnsureConnected();
            if (_timer != null)
                throw new InvalidOperationException(
                    "Continuous acquisition was started while the single grab waited, so the grab cannot tell its own " +
                    "frame from the stream's. Call StopContinuous() first.");
        }
        Emit(null);
    }

    public void StartContinuous()
    {
        bool started = false;
        lock (_sync)
        {
            ThrowIfDisposed();
            EnsureConnected();
            if (_timer != null) return;

            var fps = _opt.FrameRate > 0 ? _opt.FrameRate : 30;
            var intervalMs = (int)Math.Max(1, Math.Round(1000.0 / fps));
            var gen = Volatile.Read(ref _liveGen);
            _timer = new Timer(_ => SafeEmit(gen), null, 0, intervalMs);
            started = true;
        }
        WriteLog(CvLogLevel.Info, "Virtual camera continuous grab started.");
        if (started) GrabbingChanged?.Invoke(this, true);
    }

    public void StopContinuous()
    {
        bool stopped;
        lock (_sync)
        {
            stopped = StopContinuousCore();
        }
        WaitTicksOutsideLock();   // 첫 정지든 겹친 정지든 — 돌던 틱이 끝나야 돌아간다
        if (stopped)
        {
            WriteLog(CvLogLevel.Info, "Virtual camera continuous grab stopped.");
            GrabbingChanged?.Invoke(this, false);
        }
    }

    public void SetExposureTimeUs(double timeUs)
    {
        // 가상 카메라는 노출 시간 무시. 로그만.
        WriteLog(CvLogLevel.Debug, $"SetExposureTimeUs ignored on virtual camera. value={timeUs}");
    }

    public void Dispose()
    {
        bool wasGrabbing = false;
        lock (_sync)
        {
            if (_disposed) return;
            wasGrabbing = StopContinuousCore();
            _disposed = true;
        }
        WaitTicksOutsideLock();
        IsConnected = false;
        if (wasGrabbing) GrabbingChanged?.Invoke(this, false);
        WriteLog(CvLogLevel.Info, "Virtual camera disposed.");
    }

    /// <summary>Timer 정지(<see cref="_sync"/> 보유 전제). 반환값: 호출 전에 grab 중이었으면 true.
    /// 세대를 올려 <b>아직 발행에 들어서지 않은 틱</b>(타이머가 이미 꺼낸 늦은 틱 포함)은 발행하지 않게 하고, 이미 들어선 틱은
    /// 호출자가 락 밖에서 <see cref="WaitTicksOutsideLock"/> 로 기다린다.
    ///
    /// <see cref="ICam.StopContinuous"/> 는 "남아 있는 프레임을 버린다 — 다음 GrabOne 이 그것을 집어 가지
    /// 않게" 를 계약으로 적어 두었다. 기다리지 않으면 정지가 돌아온 뒤 한 장이 더 발행되어 <b>이어지는 단발 그랩의 답으로 읽힌다</b>.</summary>
    private bool StopContinuousCore()
    {
        if (_timer is null) return false;
        // 세대를 먼저 올린다 — IsGrabbing(_timer 를 락 없이 읽는다)이 거짓으로 보이는 순간 옛 틱의 발행은 이미 막혀 있어야 한다.
        // 그래야 거짓을 보고 구독한 단발 그랩이 옛 틱의 프레임을 답으로 받지 않는다.
        lock (_tickSync) _liveGen++;
        _timer.Dispose();   // 폐기 대기는 쓰지 않는다 — 도는 틱은 세대와 유휴 신호로 가른다(필드 주석)
        _timer = null;
        return true;
    }

    /// <summary>발행 중인 틱이 끝나기를 <b>락 밖에서</b> 기다린다 — 정지·닫기·해제·단발 그랩이 <b>누가 먼저 멈췄든</b> 부른다.
    /// ⚠ 락 밖이어야 한다 — 기다리는 대상이 구독자의 프레임 핸들러이고, 그 핸들러가 이 카메라를 다시 부르면 락을 쥔 채 자기를 기다린다.
    /// ⚠ 발행 스레드 자신이 부른 것(핸들러 안의 정지)은 기다리지 않는다 — 자기가 끝나기를 기다리며 시한을 통째로 쓴다.
    /// 시한(<see cref="DrainCapMs"/>)을 넘기면 기다리지 않고 돌아간다 — 그 틱의 프레임은 돌아간 뒤에 나갈 수 있다(ICam.StopContinuous 의 한계:
    /// 이 시한에는 구독자 핸들러뿐 아니라 이 카메라가 폴더 이미지를 읽는 시간도 들어간다).</summary>
    private void WaitTicksOutsideLock()
    {
        if (Volatile.Read(ref _emitThreadId) == Environment.CurrentManagedThreadId) return;
        // 누군가 이미 시한까지 기다리고 포기한 그 틱이면 또 기다리지 않는다 — 안 그러면 멈춰 선 틱 하나에 정지·닫기·해제가 각자 1 s 씩 물었다.
        var running = Volatile.Read(ref _runningTick);
        if (running != 0 && running == Volatile.Read(ref _givenUpTick)) return;
        if (!_tickIdle.Wait(DrainCapMs) && running != 0) Volatile.Write(ref _givenUpTick, running);
    }

    private void SafeEmit(int gen)
    {
        // 이미지 파일 로드가 라이브 주기보다 느리면 틱을 스킵해 재진입/적체 방지
        if (Interlocked.Exchange(ref _emitBusy, 1) == 1) return;
        lock (_tickSync)
        {
            // 정지가 이 타이머를 이미 내렸다 — 발행하지 않는다. 세대 확인과 "발행 중" 표시가 같은 락이라, 정지는 이 틱을 기다리거나
            // 이 틱이 정지를 보고 물러나거나 둘 중 하나다.
            if (gen != _liveGen) { Volatile.Write(ref _emitBusy, 0); return; }
            _tickIdle.Reset();
            _runningTick = ++_tickSeq;
        }
        // 이 틱이 도는 스레드를 남긴다 — 구독자 핸들러가 여기서 정지를 부르면 자기를 기다리면 안 된다.
        Volatile.Write(ref _emitThreadId, Environment.CurrentManagedThreadId);
        try
        {
            Emit(gen);
        }
        catch (Exception ex)
        {
            WriteLog(CvLogLevel.Warning, "Virtual camera emit failed.", ex);
        }
        finally
        {
            Volatile.Write(ref _emitThreadId, 0);
            Volatile.Write(ref _runningTick, 0);
            _tickIdle.Set();                     // 바쁨을 풀기 전에 — 풀고 나서 세우면 다음 틱이 되세운 "발행 중" 을 이 Set 이 지운다
            Volatile.Write(ref _emitBusy, 0);
        }
    }

    /// <param name="gen">라이브 틱이면 그 타이머의 세대, 단발 그랩이면 null.</param>
    private void Emit(int? gen)
    {
        // 합성/파일 공급 모두 실 카메라와 동일 파이프라인 유지 — Flip/Rotation 적용 포함
        using var mat = TryLoadFolderFrame() ?? GenerateFrame();
        var frame = Materialize(mat);
        // 라이브 틱은 <b>내기 직전에</b> 세대를 다시 보고 구독자 목록을 같은 락 안에서 집는다. 들어설 때만 보면, 이미지를 읽는 사이 정지가
        // 오고 그 뒤 구독한 단발 그랩(CamGrabExt.GrabFrameAsync: 구독 → GrabOne)이 옛 틱의 프레임을 제 답으로 받았다(검토가 찾음).
        // 여기서 막히면 그 틱이 끝나기를 기다리던 정지·그랩은 곧바로 풀린다. 이미 목록을 집은 뒤에 온 정지는 이 틱이 끝나기를 기다린다.
        EventHandler<CamFrame>? handlers;
        if (gen is { } g)
        {
            lock (_tickSync)
            {
                if (g != _liveGen) return;
                handlers = FrameAcquired;
            }
        }
        else handlers = FrameAcquired;
        // 발행은 생성과 갈라서 감싼다 — 안 가르면 구독자가 던진 것이 부르는 쪽 catch 에서 "emit failed" 로
        // 적혀, 우리 생성은 멀쩡한데 남의 핸들러가 원인이라는 사실이 로그에서 지워진다(ICam 계약).
        try { handlers?.Invoke(this, frame); }
        catch (Exception ex) { WriteLog(CvLogLevel.Error, "a FrameAcquired subscriber threw.", ex); }
    }

    /// <summary>방향 보정 적용 후 GC 소유 <see cref="CamFrame"/> 으로 실체화 — 발행 프레임은 수명 계약이 없다.</summary>
    private CamFrame Materialize(Mat src)
    {
        var processed = CamXform.Apply(src, _opt.Flip, _opt.Rotation);
        try
        {
            return CamFrame.FromMat(processed);
        }
        finally
        {
            if (!ReferenceEquals(processed, src)) processed.Dispose();
        }
    }

    /// <summary>폴더 순환 프레임 — 폴더 무효·이미지 없음·로드 실패면 null(테스트 패턴 폴백).</summary>
    private Mat? TryLoadFolderFrame()
    {
        string? file = null;
        try
        {
            var dir = ImageDirProvider is not null ? ImageDirProvider.Invoke() : _opt.VirtualImageDir;
            lock (_folderSync)
            {
                file = NextImageFile(dir);
            }
            if (file is null) return null;

            // ImRead 는 Windows 비ASCII(한글) 경로에서 실패한다 — 바이트로 읽어 디코드한다.
            var mat = Cv2.ImDecode(File.ReadAllBytes(file), _color ? ImreadModes.Color : ImreadModes.Grayscale);
            if (mat.Empty())
            {
                mat.Dispose();
                throw new IOException($"unreadable image file: {file}");
            }
            return mat;
        }
        catch (Exception ex)
        {
            WriteLog(CvLogLevel.Warning, $"folder frame load failed: {file}", ex);
            return null;
        }
    }

    /// <summary>다음 순환 이미지 파일 — 폴더 목록은 변경 감지 시에만 재열거. 공급할 파일 없으면 null.</summary>
    private string? NextImageFile(string? dir)
    {
        dir = dir?.Trim() ?? "";
        if (dir.Length == 0 || !Directory.Exists(dir))
        {
            ResetFolderCache();
            return null;
        }

        // 파일 추가/삭제/이름변경 시 폴더 LastWriteTime 이 갱신됨 — 그때만 재열거 (이미지 자체는 매번 새로 로드)
        var write = Directory.GetLastWriteTimeUtc(dir);
        if (!string.Equals(dir, _dirResolved, StringComparison.OrdinalIgnoreCase) || write != _dirLastWrite)
        {
            _files = Directory.EnumerateFiles(dir)
                .Where(f => ImageExts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _dirResolved = dir;
            _dirLastWrite = write;
            _fileIdx = 0;
            WriteLog(CvLogLevel.Info, $"image folder loaded: {dir} ({_files.Length} files)");
        }

        if (_files.Length == 0) return null;
        var file = _files[_fileIdx % _files.Length];
        _fileIdx = (_fileIdx + 1) % _files.Length;
        return file;
    }

    private void ResetFolderCache()
    {
        _dirResolved = "";
        _dirLastWrite = DateTime.MinValue;
        _files = Array.Empty<string>();
        _fileIdx = 0;
    }

    /// <summary>테스트 패턴 프레임 — 시프팅 그라데이션. IsColor 에 맞춰 1/3채널.</summary>
    private Mat GenerateFrame()
    {
        var buffer = new byte[_stride * _height];
        var tick = Interlocked.Increment(ref _frameCounter);
        var offset = tick * 2;

        if (!_color)
        {
            for (var y = 0; y < _height; y++)
            {
                var rowStart = y * _stride;
                for (var x = 0; x < _width; x++)
                {
                    buffer[rowStart + x] = (byte)((x + y + offset) & 0xFF);
                }
            }
        }
        else
        {
            // BGR24
            for (var y = 0; y < _height; y++)
            {
                var rowStart = y * _stride;
                for (var x = 0; x < _width; x++)
                {
                    var p = rowStart + x * 3;
                    buffer[p + 0] = (byte)((x + offset) & 0xFF);         // B
                    buffer[p + 1] = (byte)((y + offset) & 0xFF);         // G
                    buffer[p + 2] = (byte)((x + y + offset) & 0xFF);     // R
                }
            }
        }

        // 버퍼 래핑 Mat — Emit 파이프라인 안에서만 살고 실체화(CamFrame) 후 정리된다.
        return Mat.FromPixelData(_height, _width, _color ? MatType.CV_8UC3 : MatType.CV_8UC1, buffer, _stride);
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
            throw new InvalidOperationException("Camera is not opened.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VirtualCam));
    }

    private void WriteLog(CvLogLevel level, string message, Exception? exception = null)
    {
        CvLog.Publish(level, LogSource, $"[{Name}] {message}", exception);
    }
}
