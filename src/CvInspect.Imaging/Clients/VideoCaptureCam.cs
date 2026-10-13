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
    private string _openedAs = string.Empty;   // 로그용 — 어떤 식별로 무엇을 열었는지
    private bool _isFileSource;
    private Thread? _liveThread;
    private CancellationTokenSource? _liveCts;
    private bool _disposed;
    private double? _setExposureUs;   // SetExposureTimeUs 로 받은 마지막 값 — 열 때 다시 넣는다(ICam 계약)

    // 단발 그랩이 진행 중이다(_markSync 아래에서 읽고 쓴다) — GrabOne 의 첫 검사에서 세우고, 읽기·발행이 끝나 돌아갈 때(예외로 끝나도)
    // 내린다. 서 있는 동안 연속 시작과 두 번째 그랩은 던진다(ICam 계약). 표식이 없을 때는 발행(락 밖) 사이에 시작이 서고 두 번째 그랩이
    // 돌아, 그랩을 부른 쪽의 구독에 라이브나 남의 그랩의 장이 섞여 들어왔다(LocalCamGrabContractTests: 발행 중인 그랩을 붙잡아 두고
    // 시작·그랩을 걸면 매번 섰다).
    // ⚠ 표식은 읽기 락(_sync)과 따로 짧은 락으로 지킨다. 읽기 락 아래에서 세우면, 앞 그랩이 읽는 동안 온 두 번째 그랩은 거절되지 않고
    // 그 락에서 기다리다가 앞 그랩이 표식을 내린 뒤 받아들여졌다 — 그 호출자의 구독에는 이미 앞 그랩의 장이 들어 있어, 남의 장을 예외 없이
    // 제 답으로 받고 제 장은 아무도 안 받았다(검토 실측: 겹친 GrabFrameAsync 두 개가 같은 장을 받은 쌍 200 중 196~200). 라이브 중 그랩도
    // 같은 락에서 라이브 루프의 읽기 뒤에 줄 서, 짧은 시한이면 거절이 나기 전에 시한이 다했다. 짧은 락으로 옮기면 둘 다 그 자리에서 던진다.
    // 락 순서: _sync → _markSync 만 쓴다(연속 시작이 읽기 락 안에서 이 표식을 본다). 반대로 쥐는 자리는 없다.
    private readonly object _markSync = new();
    private bool _grabPending;

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

            var api = ParseBackend(_opt.UserSettings);
            VideoCapture cap;
            string src;
            if (!string.IsNullOrWhiteSpace(_opt.SerialNumber))
            {
                // 고정 식별 — 장치 인덱스는 OS 가 그때그때 매기는 순서라 믿을 수 없다. 못 찾으면 예외에 현재 목록이 실린다.
                var dev = UsbCamId.Resolve(_opt.SerialNumber);
                src = $"SerialNumber='{_opt.SerialNumber.Trim()}' -> {dev}";
                cap = dev.DevicePath is not null ? new VideoCapture(dev.DevicePath, api) : new VideoCapture(dev.Index, api);
                _isFileSource = false;
            }
            else
            {
                src = (_opt.VideoSource ?? "0").Trim();
                var isIndex = int.TryParse(src, out var index);
                cap = isIndex ? new VideoCapture(index, api) : new VideoCapture(src, api);
                _isFileSource = !isIndex && File.Exists(src);
            }
            if (!cap.IsOpened())
            {
                cap.Dispose();
                throw new InvalidOperationException($"Failed to open video source {src}.");
            }

            _cap = cap;
            IsConnected = true;
            _openedAs = src;
            // 열기 전에 받아 둔 노출을 지금 넣는다. 다시 열 때도 같은 자리에서 복원된다 — 소스를 새로 열면
            // 속성은 기본값으로 돌아가므로, 여기서 넣지 않으면 운전 중에 맞춘 노출이 조용히 사라진다.
            if (_setExposureUs is { } exposure) ApplyExposureWhileLocked(exposure);
        }
        ConnectionChanged?.Invoke(this, new ConnArgs(true));
        WriteLog(CvLogLevel.Info, $"video source opened: {_openedAs} (file={_isFileSource})");
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
        var marked = false;   // 이 호출이 표식을 세웠다 — 진입 검사에서 던진 호출은 남(앞선 그랩)의 표식을 내리면 안 된다
        try
        {
            CamFrame? frame;
            // 겹침 검사는 읽기 락보다 먼저, 짧은 락에서 한다(_markSync 주석) — 앞 그랩의 읽기나 라이브 루프의 읽기 뒤에 줄 서지 않고 그 자리에서 던진다.
            lock (_markSync)
            {
                // 연속 취득 중에는 답할 수 없다 — 여기서 한 장 더 내도 부른 쪽은 그것과 흐르던 장을 가릴 수 없다.
                // (라이브 참조는 _sync 아래에서 바뀐다 — 여기서는 대략 보고, 아래 읽기 락에서 다시 본다.)
                if (Volatile.Read(ref _liveThread) != null) ThrowLiveRunning();
                // 그랩은 한 번에 하나다 — 앞 그랩이 읽거나 발행 중이면 둘이 서로의 구독에 장을 넣는다. 이 그랩이 낸 장의
                // 핸들러가 같은 카메라를 되불러도 여기서 걸린다(ICam.FrameAcquired).
                if (_grabPending)
                    throw new InvalidOperationException(
                        "A single grab is already waiting for its frame on this camera. Wait for that GrabOne() to return first.");
                _grabPending = marked = true;
            }
            lock (_sync)
            {
                ThrowIfDisposed();
                EnsureConnected();
                // ⚠ 위의 라이브 검사와 중복이 아니다 — 지우지 않는다. 연속 시작이 표식이 서기 전에 보고 지나간 뒤 위 검사(라이브 없음)가
                // 지나고 나서 라이브를 세우면, 여기서만 걸린다. 없으면 이 그랩은 받아들여지고 라이브의 장이 그 구독으로 흘러 남의 장을 예외 없이
                // 돌려준다(검토 변이: 이 줄을 지우면 시작·그랩 경합 2000회 중 541~665회). 회귀: VideoCaptureCamRechecksLiveUnderTheReadLock.
                if (_liveThread != null) ThrowLiveRunning();
                // 읽기 실패를 삼키지 않는다 — 사람이 부른 한 장이다. 종전에는 프레임도 예외도 로그도 없이
                // 돌아와서, 부른 쪽에서 성공과 구분되지 않았다(구독자가 아무것도 못 받은 것이 유일한 단서인데
                // 그것을 이유와 함께 설명해 주는 곳이 없었다). 연속 취득 루프는 같은 실패를 이미 남긴다.
                if (!TryReadFrame(out frame))
                    WriteLog(CvLogLevel.Warning, "frame read failed — the single grab produced no frame.");
            }
            if (frame != null) Publish(frame);
        }
        finally
        {
            if (marked) lock (_markSync) _grabPending = false;
        }
    }

    private static void ThrowLiveRunning()
        => throw new InvalidOperationException(
            "Continuous acquisition is running, so a single grab cannot tell its own frame from the " +
            "stream's. Call StopContinuous() first.");

    /// <summary>구독자에게 프레임을 넘긴다 — <b>구독자가 던져도 우리가 하는 일은 달라지지 않는다</b>(ICam 계약).
    /// 감싸지 않으면 라이브 루프에서는 예외가 루프 테두리까지 올라가 <b>취득이 통째로 끝난다</b>. 로그에는
    /// 루프가 죽었다고만 남아, 원인이 호스트 핸들러라는 것이 보이지 않는다.
    /// 삼키지는 않는다 — 남의 결함이지만 흔적은 우리 쪽에만 남는다.</summary>
    private void Publish(CamFrame frame)
    {
        try { FrameAcquired?.Invoke(this, frame); }
        catch (Exception ex) { WriteLog(CvLogLevel.Error, "a FrameAcquired subscriber threw.", ex); }
    }

    public void StartContinuous()
    {
        bool started = false;
        lock (_sync)
        {
            ThrowIfDisposed();
            EnsureConnected();
            if (_liveThread != null) return;
            // 단발 그랩이 장을 읽거나 내는 중이면 켜지 않는다 — 켜면 라이브의 장이 그 그랩을 부른 쪽의 구독(구독 → 그랩 → 반환)으로
            // 들어가 제 답과 가릴 수 없다. 그랩이 읽는 동안 온 시작은 위 락에서 기다렸다가, 그랩이 아직 발행 중이면 여기서 던진다.
            // (표식은 _markSync 가 지킨다 — 이 자리가 _sync → _markSync 순서로 쥐는 유일한 곳이다.)
            bool grabPending;
            lock (_markSync) grabPending = _grabPending;
            if (grabPending)
                throw new InvalidOperationException(
                    "A single grab is waiting for its frame; continuous acquisition cannot start until it returns. " +
                    "Wait for GrabOne() to return first.");

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

    /// <summary>노출 적용 — 아직 열지 않았으면 들고 있다가 <see cref="Open"/> 이 넣는다(ICam 계약).
    /// 소스가 없을 때 그냥 흘리면 "applied=False" 한 줄만 남고 값은 사라지는데, 그 줄은 미지원 소스와
    /// 구분되지 않아 부른 쪽은 들어간 줄 안다.</summary>
    public void SetExposureTimeUs(double timeUs)
    {
        lock (_sync)
        {
            // 0 이하는 "건드리지 않는다" 는 뜻이라 기억하지 않는다 — 다시 열 때 그것을 되넣으면
            // CamOpt 값으로 돌아갈 길까지 막힌다. 지금 이 호출은 종전대로 그대로 넘긴다.
            if (timeUs > 0) _setExposureUs = timeUs;
            ApplyExposureWhileLocked(timeUs);
        }
    }

    /// <summary><see cref="_sync"/> 보유 전제.</summary>
    private void ApplyExposureWhileLocked(double timeUs)
    {
        // 노출 단위는 백엔드마다 다르다(초·log2초 등) — 초 단위 전달의 best-effort 이며 미지원 소스는 무시된다.
        var ok = _cap?.Set(VideoCaptureProperties.Exposure, timeUs / 1_000_000.0) ?? false;
        WriteLog(CvLogLevel.Debug, $"SetExposureTimeUs best-effort: value={timeUs}us applied={ok}");
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
    /// 프레임 이벤트 안(라이브 스레드 자신)에서 불리면 Join 을 생략한다 — 자기 대기 교착 방지.
    ///
    /// ⚠ <b>이 호출이 돌아왔다고 루프가 끝난 것은 아니다.</b> 락을 쥔 채 기다릴 수 없어 시한이 짧고,
    /// 장치 읽기가 길어지거나 파일 소스의 페이싱 대기가 걸려 있으면 그 시한 안에 못 빠진다. 그때는
    /// <b>정지 뒤에 프레임이 한 장 더 발행될 수 있다</b> — 계약은 "요청했고 잠깐 기다렸다" 까지다.</summary>
    private bool StopContinuousCore()
    {
        if (_liveThread is null) return false;
        var thread = _liveThread;
        _liveCts?.Cancel();
        _liveThread = null;

        // 락을 쥔 채 Join 하면 루프의 lock 획득과 교착한다 — 락 밖에서 기다리는 대신
        // 루프가 토큰만 보고 빠지도록 짧게만 기다린다.
        var exited = !ReferenceEquals(Thread.CurrentThread, thread) && thread.Join(100);

        // 루프가 실제로 빠져나온 것을 확인했을 때만 CTS 를 놓는다. 시한을 넘겼거나 이 호출이 루프
        // 자신에게서 온 것이면 워커가 아직 살아 있고, 그 워커는 곧 token.WaitHandle 을 만진다 —
        // Dispose 된 CTS 의 토큰을 만지면 ObjectDisposedException 이고, 배경 스레드의 미처리 예외는
        // 프로세스를 통째로 내린다. <b>흘리는 CTS 한 개가 죽은 프로세스보다 싸다.</b>
        // (형제 ReconnectingCam 은 같은 함정을 Cancel 쪽에서 catch 로 막고 있다 — 여기만 빠져 있었다.)
        if (exited) _liveCts?.Dispose();
        _liveCts = null;
        return true;
    }

    private void LiveLoop(CancellationToken token)
    {
        // 배경 스레드의 미처리 예외는 프로세스를 내린다 — 취득 루프 하나가 호스트를 통째로 데려갈 수는 없다.
        // (형제 GevCam.PumpLoop·ReconnectingCam.ReconnectLoop 도 같은 이유로 감싸져 있다.)
        try
        {
            LiveLoopCore(token);
        }
        catch (ObjectDisposedException) when (token.IsCancellationRequested)
        {
            // 정지와의 경합 — 정지가 먼저 취소하고 나서 자원을 놓으므로, 취소가 서 있을 때만 이 경합이다. 정지하려던 참이므로 조용히 물러난다.
            // 취소 없이 온 같은 형은 경합이 아니라 고장이라 아래로 간다 — 종전에는 이 갈래가 형만 보고 삼켜, 놓인 캡처를 읽다 죽은 루프가
            // 로그 한 줄 없이 사라졌다(LocalCamGrabContractTests (5) disposed-capture).
        }
        catch (Exception ex)
        {
            WriteLog(CvLogLevel.Error, $"the live loop stopped on an unexpected error: {ex.GetType().Name}", ex);
        }
        // 취소 없이 여기 왔다 = 루프가 우리 뜻과 무관하게 끝났다. 정지 경로의 취소는 정지가 상태를 내리고 알리므로 건드리지 않는다.
        if (!token.IsCancellationRequested) LiveEndedBySelf();
    }

    /// <summary>라이브 루프가 정지 요청 없이 끝났을 때의 뒷정리 — <b>라이브 스레드 자신에서</b> 부른다.
    /// 여기서 손을 놓으면 <see cref="_liveThread"/> 참조가 남아 <see cref="IsGrabbing"/> 이 영영 참으로 굳고, 꺼짐 통지가 안 나가며,
    /// 다음 <see cref="StartContinuous"/> 는 "이미 도는 중" 으로 읽혀 <b>예외도 로그도 없이</b> 돌아간다 — 프레임은 영영 안 오는데 아무도
    /// 모른다. 감싼 <see cref="ReconnectingCam"/> 도 그 꺼짐 통지로만 청하지 않은 정지를 알아 세션을 다시 세운다
    /// (LocalCamGrabContractTests (5): 루프 안에 예외를 넣으면 종전에는 IsGrabbing=true·꺼짐 0번·세션 1개로 굳었다).
    ///
    /// <b>참조가 아직 이 스레드일 때만</b> 내린다 — 그 사이 정지가 먼저 닿았으면 통지는 그쪽이 냈고, 정지 뒤 다시 켰으면 지금 참조는 새 루프다
    /// (여기서 지우면 멀쩡히 도는 새 라이브를 끈다). 상태를 락 안에서 먼저 내리고 통지는 락 밖에서 낸다(<see cref="ICam.GrabbingChanged"/> 의
    /// 구현자 안내). 그래서 정지·닫기·해제와 겹쳐도 꺼짐은 먼저 내린 쪽이 한 번만 낸다.
    /// 장치는 닫지 않는다 — 연결은 그대로 두고(<see cref="IsConnected"/> 참) 정리는 다음 닫기에 맡긴다.</summary>
    private void LiveEndedBySelf()
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_liveThread, Thread.CurrentThread)) return;
            _liveThread = null;
            // 이 취소원을 만지는 것은 이 스레드와 정지뿐인데, 참조를 위에서 내렸으니 정지는 더 닿지 않고 이 스레드도 곧 끝난다 — 놓아도 된다.
            _liveCts?.Dispose();
            _liveCts = null;
        }
        WriteLog(CvLogLevel.Warning,
            "the live loop ended without a stop request — continuous grab is no longer running. Call StartContinuous again.");
        // 이 스레드에는 예외를 받을 사람이 없다 — 구독자가 던진 것은 여기서 받아 남긴다(삼키지 않는다).
        try { GrabbingChanged?.Invoke(this, false); }
        catch (Exception ex) { WriteLog(CvLogLevel.Error, "a GrabbingChanged subscriber threw.", ex); }
    }

    private void LiveLoopCore(CancellationToken token)
    {
        var failStreak = 0;
        var sw = new System.Diagnostics.Stopwatch();
        while (!token.IsCancellationRequested)
        {
            sw.Restart();
            bool emitted;
            CamFrame? frame;
            lock (_sync)
            {
                if (token.IsCancellationRequested || _cap is null) break;
                emitted = TryReadFrame(out frame);
            }
            if (frame != null) Publish(frame);

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
    /// <summary>한 장을 읽어 <see cref="CamFrame"/> 으로 만든다 — <see cref="_sync"/> 아래에서 부른다.
    /// 발행은 부른 쪽이 <b>락 밖에서</b> 한다. 락 안에서 발행하면 구독자가 이 카메라를 되부르는 순간
    /// 서로를 붙잡는다. 프레임은 GC 소유 복사본이라 락을 놓은 뒤 넘겨도 수명 계약이 없다.</summary>
    private bool TryReadFrame(out CamFrame? frame)
    {
        frame = null;
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
        try
        {
            frame = CamFrame.FromMat(processed);   // GC 소유 실체화 — 발행 후 수명 계약 없음
        }
        finally
        {
            if (!ReferenceEquals(processed, src)) processed.Dispose();
        }
        return true;
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
            throw new InvalidOperationException("Camera is not opened.");
    }

    /// <summary>UserSettings 의 "backend=dshow|msmf|v4l2|any" — 세미콜론으로 구분한 key=value 중 backend 만 본다.
    /// 지정이 없으면 ANY(OpenCV 가 고른다). Windows 에서 열거 순번과 인덱스가 어긋나 보이면 여기서 백엔드를 바꿔 본다.</summary>
    internal static VideoCaptureAPIs ParseBackend(string? userSettings)
    {
        foreach (var kv in (userSettings ?? string.Empty).Split(';'))
        {
            var eq = kv.IndexOf('=');
            if (eq < 0 || !kv.Substring(0, eq).Trim().Equals("backend", StringComparison.OrdinalIgnoreCase)) continue;
            switch (kv.Substring(eq + 1).Trim().ToLowerInvariant())
            {
                case "dshow": return VideoCaptureAPIs.DSHOW;
                case "msmf": return VideoCaptureAPIs.MSMF;
                case "v4l2": return VideoCaptureAPIs.V4L2;
                case "any": case "": return VideoCaptureAPIs.ANY;
                default: throw new ArgumentException($"Unknown VideoCapture backend '{kv.Substring(eq + 1).Trim()}' in UserSettings (use dshow, msmf, v4l2 or any).");
            }
        }
        return VideoCaptureAPIs.ANY;
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
