using OpenCvSharp;

namespace CvInspect.Imaging;

/// <summary>
/// OpenCV VideoCapture 기반 카메라 — 웹캠(장치 인덱스), 동영상 파일, 스트림 URL(RTSP 등)을
/// 같은 계약으로 공급한다. 소스는 <see cref="CamOpt.VideoSource"/> 로 지정.
///
/// 동영상 파일은 FrameRate(0 이하면 파일 자체 FPS)로 페이싱하고 끝에 닿으면 처음으로 되감아
/// 순환 재생한다 — 라이브 카메라를 파일로 대체하는 재현/시연 시나리오용. 장치·스트림은
/// 소스가 주는 속도 그대로 공급한다(Read 가 블로킹).
///
/// IsColor=false 면 3채널 입력을 그레이로 접어 발행한다(반대는 GRAY→BGR 확장).
/// 발행 프레임은 GC 소유 <see cref="CamFrame"/> 으로 실체화된다 — 보관·스레드 전달 자유.
/// 소스별 코덱/백엔드는 네이티브 videoio 가 제공한다 — 런타임 패키지는 소비자 선택.
/// </summary>
public sealed class VideoCaptureCam : ICam
{
    private const string LogSource = nameof(VideoCaptureCam);

    private readonly object _sync = new();
    private readonly CamOpt _opt;
    private readonly Mat _buf = new();
    private readonly Mat _conv = new();   // IsColor 채널 변환 버퍼

    private VideoCapture? _cap;
    private bool _isFileSource;
    private Thread? _liveThread;
    private CancellationTokenSource? _liveCts;
    private bool _disposed;

    public VideoCaptureCam(CamOpt opt)
    {
        _opt = opt ?? throw new ArgumentNullException(nameof(opt));
        Name = string.IsNullOrWhiteSpace(opt.Name) ? "VideoCaptureCam" : opt.Name;
    }

    public string Name { get; }
    public string ComType => "VideoCapture";
    public bool IsConnected { get; private set; }
    public bool IsGrabbing => _liveThread != null;

    public event EventHandler<CamFrame>? FrameAcquired;
    public event EventHandler<ConnArgs>? ConnectionChanged;
    public event EventHandler<bool>? GrabbingChanged;

    public void Open()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (IsConnected) return;

            var src = (_opt.VideoSource ?? "0").Trim();
            var isIndex = int.TryParse(src, out var index);
            var cap = isIndex ? new VideoCapture(index) : new VideoCapture(src);
            if (!cap.IsOpened())
            {
                cap.Dispose();
                throw new InvalidOperationException($"Failed to open video source '{src}'.");
            }

            _cap = cap;
            _isFileSource = !isIndex && File.Exists(src);
            IsConnected = true;
        }
        ConnectionChanged?.Invoke(this, new ConnArgs(true));
        WriteLog(CvLogLevel.Info, $"video source opened: '{_opt.VideoSource}' (file={_isFileSource})");
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
            _cap?.Dispose();
            _cap = null;
        }
        if (wasGrabbing) GrabbingChanged?.Invoke(this, false);
        if (wasConnected) ConnectionChanged?.Invoke(this, new ConnArgs(false));
        WriteLog(CvLogLevel.Info, "video source closed.");
    }

    public void GrabOne()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            EnsureConnected();
            EmitOnce();
        }
    }

    public void StartContinuous()
    {
        bool started = false;
        lock (_sync)
        {
            ThrowIfDisposed();
            EnsureConnected();
            if (_liveThread != null) return;

            _liveCts = new CancellationTokenSource();
            var token = _liveCts.Token;
            _liveThread = new Thread(() => LiveLoop(token)) { IsBackground = true, Name = $"{Name}.Live" };
            _liveThread.Start();
            started = true;
        }
        WriteLog(CvLogLevel.Info, "continuous grab started.");
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
            WriteLog(CvLogLevel.Info, "continuous grab stopped.");
            GrabbingChanged?.Invoke(this, false);
        }
    }

    public void SetExposureTimeUs(double timeUs)
    {
        lock (_sync)
        {
            // 노출 단위는 백엔드마다 다르다(초·log2초 등) — 초 단위 전달의 best-effort 이며 미지원 소스는 무시된다.
            var ok = _cap?.Set(VideoCaptureProperties.Exposure, timeUs / 1_000_000.0) ?? false;
            WriteLog(CvLogLevel.Debug, $"SetExposureTimeUs best-effort: value={timeUs}us applied={ok}");
        }
    }

    public void Dispose()
    {
        bool wasGrabbing = false;
        lock (_sync)
        {
            if (_disposed) return;
            wasGrabbing = StopContinuousCore();
            _disposed = true;
            IsConnected = false;
            _cap?.Dispose();
            _cap = null;
        }
        if (wasGrabbing) GrabbingChanged?.Invoke(this, false);
        _buf.Dispose();
        _conv.Dispose();
        WriteLog(CvLogLevel.Info, "disposed.");
    }

    /// <summary>라이브 정지 요청 + 스레드 종료 대기. 반환값: 호출 전에 grab 중이었으면 true.
    /// 프레임 이벤트 안(라이브 스레드 자신)에서 불리면 Join 을 생략한다 — 자기 대기 교착 방지.</summary>
    private bool StopContinuousCore()
    {
        if (_liveThread is null) return false;
        var thread = _liveThread;
        _liveCts?.Cancel();
        _liveThread = null;
        if (!ReferenceEquals(Thread.CurrentThread, thread))
        {
            // 락을 쥔 채 Join 하면 루프의 lock 획득과 교착한다 — 락 밖에서 기다리는 대신
            // 루프가 토큰만 보고 빠지도록 짧게만 기다린다 (배경 스레드라 방치돼도 무해).
            thread.Join(100);
        }
        _liveCts?.Dispose();
        _liveCts = null;
        return true;
    }

    private void LiveLoop(CancellationToken token)
    {
        var failStreak = 0;
        var sw = new System.Diagnostics.Stopwatch();
        while (!token.IsCancellationRequested)
        {
            sw.Restart();
            bool emitted;
            lock (_sync)
            {
                if (token.IsCancellationRequested || _cap is null) break;
                emitted = EmitOnce();
            }

            if (!emitted)
            {
                failStreak++;
                if (failStreak == 1)
                    WriteLog(CvLogLevel.Warning, "frame read failed — retrying.");
                if (token.WaitHandle.WaitOne(100)) break;
                continue;
            }
            if (failStreak > 0)
            {
                WriteLog(CvLogLevel.Info, $"frame read recovered after {failStreak} failures.");
                failStreak = 0;
            }

            // 파일 소스만 페이싱 — 장치/스트림은 Read 블로킹이 소스 속도를 그대로 전달한다.
            if (_isFileSource)
            {
                var fps = _opt.FrameRate > 0 ? _opt.FrameRate : (_cap?.Fps > 0 ? _cap!.Fps : 30);
                var remain = (int)(1000.0 / fps - sw.ElapsedMilliseconds);
                if (remain > 0 && token.WaitHandle.WaitOne(remain)) break;
            }
        }
    }

    /// <summary>한 프레임 읽기 → 채널 정합 → 방향 보정 → 발행. _sync 보유 전제. 실패 시 false.</summary>
    private bool EmitOnce()
    {
        if (_cap is null) return false;

        if (!_cap.Read(_buf) || _buf.Empty())
        {
            // 파일 끝 → 되감아 순환 재생
            if (!_isFileSource) return false;
            _cap.Set(VideoCaptureProperties.PosFrames, 0);
            if (!_cap.Read(_buf) || _buf.Empty()) return false;
        }

        var src = _buf;
        if (!_opt.IsColor && _buf.Channels() == 3)
        {
            Cv2.CvtColor(_buf, _conv, ColorConversionCodes.BGR2GRAY);
            src = _conv;
        }
        else if (_opt.IsColor && _buf.Channels() == 1)
        {
            Cv2.CvtColor(_buf, _conv, ColorConversionCodes.GRAY2BGR);
            src = _conv;
        }

        var processed = CamXform.Apply(src, _opt.Flip, _opt.Rotation);
        CamFrame frame;
        try
        {
            frame = CamFrame.FromMat(processed);   // GC 소유 실체화 — 발행 후 수명 계약 없음
        }
        finally
        {
            if (!ReferenceEquals(processed, src)) processed.Dispose();
        }
        FrameAcquired?.Invoke(this, frame);
        return true;
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
            throw new InvalidOperationException("Camera is not opened.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VideoCaptureCam));
    }

    private void WriteLog(CvLogLevel level, string message, Exception? exception = null)
    {
        CvLog.Publish(level, LogSource, $"[{Name}] {message}", exception);
    }
}
