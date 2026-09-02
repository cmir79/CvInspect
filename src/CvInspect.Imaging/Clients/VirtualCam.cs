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
    private bool _disposed;

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
        }
        Emit();
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
            _timer = new Timer(_ => SafeEmit(), null, 0, intervalMs);
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
        IsConnected = false;
        if (wasGrabbing) GrabbingChanged?.Invoke(this, false);
        WriteLog(CvLogLevel.Info, "Virtual camera disposed.");
    }

    /// <summary>Timer 정지. 반환값: 호출 전에 grab 중이었으면 true.</summary>
    private bool StopContinuousCore()
    {
        if (_timer is null) return false;
        _timer.Dispose();
        _timer = null;
        return true;
    }

    private void SafeEmit()
    {
        // 이미지 파일 로드가 라이브 주기보다 느리면 틱을 스킵해 재진입/적체 방지
        if (Interlocked.Exchange(ref _emitBusy, 1) == 1) return;
        try
        {
            Emit();
        }
        catch (Exception ex)
        {
            WriteLog(CvLogLevel.Warning, "Virtual camera emit failed.", ex);
        }
        finally
        {
            Volatile.Write(ref _emitBusy, 0);
        }
    }

    private void Emit()
    {
        // 합성/파일 공급 모두 실 카메라와 동일 파이프라인 유지 — Flip/Rotation 적용 포함
        using var mat = TryLoadFolderFrame() ?? GenerateFrame();
        FrameAcquired?.Invoke(this, Materialize(mat));
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
