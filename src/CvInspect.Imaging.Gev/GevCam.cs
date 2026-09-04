using CvInspect.Imaging;
using GevSharp;
using GevSharp.GenApi;
using GevSharp.Pfnc;

namespace CvInspect.Imaging.Gev;

/// <summary>
/// GigE 카메라 취득 어댑터 — 벤더 SDK 없이 프로토콜로 직접 말하는 백엔드를 <see cref="ICam"/> 에 붙인다.
/// <see cref="CamFactory.Register"/> 로 등록해 쓴다.
///
/// 카메라는 <see cref="CamOpt.SerialNumber"/> 로 찾는다 — IP 는 DHCP 로 바뀌지만 시리얼은 안 바뀐다.
/// 못 찾으면 그때 보인 장치들을 문구에 실어 던지므로, 망 문제·시리얼 오설정·서브넷 불일치를 로그만으로 가를 수 있다.
///
/// <b>동기↔비동기 경계</b>: 취득 라이브러리는 전면 비동기고 ICam 은 동기라, 이 클래스가 그 다리를 전담한다.
/// 라이브러리가 모든 await 를 컨텍스트 없이 이어 붙이므로 UI 스레드에서 불러도 교착하지 않지만, 그 스레드를
/// 붙잡기는 하므로 호스트는 취득 조작을 UI 스레드에서 하지 않는 편이 낫다.
/// </summary>
public sealed class GevCam : ICam
{
    private const string LogSource = nameof(GevCam);

    /// <summary>정지 직후 이만큼 안에 온 드롭은 정지 경계로 본다 — 마지막 프레임 하나가 잘릴 뿐이다.</summary>
    private static readonly long StopBoundaryTicks = TimeSpan.FromSeconds(2).Ticks;

    /// <summary>연속 취득을 멈춘 시각(UTC ticks). 0 이면 아직 멈춘 적이 없다.</summary>
    private long _stoppedAtTicks;

    /// <summary>이 스트림의 계수가 시작된 시점 — 소비자가 "리셋됐다" 를 구분하는 표식이다.</summary>
    private DateTime _streamStartedUtc;

    /// <summary>발행 사이에서 장치 번호가 건너뛴 누적 장수. 일부러 버린 것은 세지 않는다.</summary>
    private long _neverArrivedFrames;

    /// <summary>마지막으로 발행한 프레임의 장치 번호. 단발 그랩이 <b>이보다 새 프레임만</b> 받아들이는 기준이다.</summary>
    private ulong _lastEmittedFrameId;

    /// <summary>장치 타임스탬프의 틱 주파수(Hz). 0 이면 장치가 알려 주지 않아 촬영 시각을 못 낸다.</summary>
    private ulong _tickHz;

    /// <summary>이 카메라가 쓰는 스트림 로컬 포트. 취득 라이브러리 로그는 카메라 이름을 모르고
    /// <b>포트로만 자기를 밝히므로</b>, 그 줄들을 이 카메라에 붙이려면 우리가 대응을 남겨야 한다.
    /// 열 때마다 바뀌므로 열린 판을 들고 있다가 닫을 때 같이 남긴다.</summary>
    private int _streamPort;

    private readonly CamOpt _opt;
    private readonly GevCamOpt _gev;
    private readonly object _sync = new();

    private GevDeviceInfo? _info;      // Close/Open 사이에 남겨 둔다 — 다시 여는 길이 짧아지고 NIC 도 그대로 고른다
    private GevDevice? _dev;
    private GenApiNodeMap? _nodes;
    private GevStream? _stream;

    private Thread? _pump;
    private CancellationTokenSource? _pumpCts;
    private bool _disposed;

    // 프레임 해석에 필요한 카메라 상태 — 열 때 한 번 읽는다(프레임마다 레지스터를 읽지 않는다)
    private CvBayerPattern? _bayerOverride;

    public GevCam(CamOpt opt, GevCamOpt? gev = null)
    {
        _opt = opt ?? throw new ArgumentNullException(nameof(opt));
        _gev = gev ?? new GevCamOpt();
        Name = string.IsNullOrWhiteSpace(opt.Name) ? "GevCam" : opt.Name;
        // 취득 라이브러리 진단이 어디로도 안 흐르면 설비에서 볼 수 있는 것은 예외 문구뿐이다.
        // 호스트가 자기 창구를 이미 꽂았으면 건드리지 않는다.
        GevLogBridge.AttachIfUnset();
    }

    public string Name { get; }
    public string ComType => "GigE";
    public bool IsConnected { get; private set; }
    public bool IsGrabbing => _pump != null;

    public event EventHandler<CamFrame>? FrameAcquired;
    public event EventHandler<ConnArgs>? ConnectionChanged;
    public event EventHandler<bool>? GrabbingChanged;

    // === 수명 ===

    public void Open()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_dev != null) return;
            Run(OpenCoreAsync);
            IsConnected = true;
        }
        ConnectionChanged?.Invoke(this, new ConnArgs(true));
        WriteLog(CvLogLevel.Info, $"opened (SN={_opt.SerialNumber})");
    }

    private async Task OpenCoreAsync(CancellationToken ct)
    {
        var info = await FindDeviceAsync(ct).ConfigureAwait(false);

        // 옵션 객체는 세션마다 새로 만든다 — 라이브러리가 참조로 붙들고 시작 과정에서 되쓰기도 한다.
        GevDevice dev;
        try
        {
            dev = await GevDevice.OpenAsync(info, new GevDeviceOpt
            {
                AccessMode = GevAccessMode.Control,   // ReadOnly 는 하트비트도 제어권 상실 통지도 없다
                GvcpTimeoutMs = _gev.GvcpTimeoutMs,
                HeartbeatTimeoutMs = _gev.HeartbeatTimeoutMs,
                XmlCacheDir = _gev.XmlCacheDir,
            }, ct).ConfigureAwait(false);
        }
        catch (GevControlLostException ex)
        {
            // GigE 는 제어권이 하나다 — 대개 벤더 뷰어나 이 앱의 다른 인스턴스가 아직 잡고 있다.
            // 원문만 올리면 설비에서 무엇을 하라는 것인지 알 수 없다.
            throw new InvalidOperationException(
                $"Camera SN='{info.SerialNumber.Trim()}' at {info.Address} is already controlled by another application. " +
                "Close the vendor viewer or any other instance holding it, then retry. " +
                "If nothing is holding it, wait for the previous session's heartbeat to time out " +
                $"({_gev.HeartbeatTimeoutMs} ms) and retry.", ex);
        }

        try
        {
            // 열고 나서 다시 확인한다 — Info 는 장치에서 새로 읽은 값이라 이쪽이 권위 있다.
            // 캐시한 주소로 열었는데 그 사이 DHCP 가 주소를 다른 카메라에 준 경우가 여기서 걸린다.
            if (!SerialMatches(dev.Info.SerialNumber))
                throw new InvalidOperationException(
                    $"Opened a different camera: expected SN='{_opt.SerialNumber}', found SN='{dev.Info.SerialNumber}' at {dev.Address}.");

            dev.ControlLost += OnControlLost;

            var nodes = await dev.GetNodeMapAsync(ct).ConfigureAwait(false);
            await LoadUserSetAsync(nodes, ct).ConfigureAwait(false);
            await ApplyExposureAsync(nodes, _opt.ExposureTimeUs, ct).ConfigureAwait(false);
            await LogCameraStateAsync(nodes, ct).ConfigureAwait(false);
            _bayerOverride = _gev.BayerPatternOverride ?? await ResolveBayerAsync(nodes, ct).ConfigureAwait(false);

            var streamOpt = new GevStreamOpt
            {
                BufferCount = _gev.BufferCount,
                SocketBufferBytes = _gev.SocketBufferBytes,
                PacketSizeMode = _gev.UseFixedPacketSize ? PacketSizeMode.Fixed : PacketSizeMode.Auto,
                PacketSize = _gev.PacketSize,
                DeliverIncompleteFrames = false,   // ICam 계약: 완전한 프레임만 발행
                PayloadSize = (int?)await TryReadIntAsync(nodes, "PayloadSize", ct).ConfigureAwait(false),
            };
            if (ResolveScpdTicks(dev) is { } scpd) streamOpt.InterPacketDelay = scpd;
            if (_gev.PacketTimeoutMs is { } packetTimeout) streamOpt.PacketTimeoutMs = packetTimeout;

            var stream = await dev.OpenStreamAsync(streamOpt, ct).ConfigureAwait(false);

            try
            {
                stream.FrameDropped += OnFrameDropped;
                await stream.StartAsync(ct).ConfigureAwait(false);
                // 소켓은 여기서 bind 된다 — 그 전에 읽으면 아직 0 이다.
                _streamPort = stream.LocalPort;
                _streamStartedUtc = DateTime.UtcNow;
                _neverArrivedFrames = 0;
                _lastEmittedFrameId = 0;
                _tickHz = dev.TimestampTickFrequency;
                LogSocketBuffer(stream, streamOpt.SocketBufferBytes);
                // 전송 파라미터 잠금은 스트림이 선 뒤, 취득을 걸기 전에 — 순서가 뒤바뀌면 장치가 거부한다.
                await dev.SetTlParamsLockedAsync(true, ct).ConfigureAwait(false);
            }
            catch
            {
                stream.FrameDropped -= OnFrameDropped;
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _info = info;
            _dev = dev;
            _nodes = nodes;
            _stream = stream;
        }
        catch
        {
            dev.ControlLost -= OnControlLost;
            await dev.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>시리얼로 카메라를 고른다. 못 찾으면 그때 보인 것들을 문구에 실어 던진다 —
    /// 목록이 비었으면 망·방화벽·NIC 쪽이고, 다른 시리얼이 보이면 설정이 틀렸고,
    /// 보이는데 서브넷이 어긋나면 주소 문제다. 이 문구가 없으면 설비에서 셋을 구분할 방법이 없다.</summary>
    private async Task<GevDeviceInfo> FindDeviceAsync(CancellationToken ct)
    {
        var found = await GevDiscovery.DiscoverAsync(
            new GevDiscoveryOpt { TimeoutMs = _gev.DiscoveryTimeoutMs }, ct).ConfigureAwait(false);

        var hit = found.FirstOrDefault(d => SerialMatches(d.SerialNumber));
        if (hit is null) throw new InvalidOperationException(DescribeMiss(found));

        if (!hit.IsReachableDirectly)
            WriteLog(CvLogLevel.Warning,
                $"camera {hit.Address}/{hit.Subnet} is not on the subnet of NIC {hit.InterfaceAddress} — streaming may fail even though discovery answered.");

        return hit;
    }

    private bool SerialMatches(string candidate)
        => string.Equals(candidate.Trim(), (_opt.SerialNumber ?? string.Empty).Trim(), StringComparison.Ordinal);

    /// <summary>
    /// 못 찾았을 때의 문구. <b>이 문구가 설비에서 원인을 가르는 유일한 근거</b>이고, 설정에 아직
    /// 시리얼을 못 적은 첫 기동에서는 <b>여기서 진짜 시리얼을 읽어 채우는</b> 용도로도 쓴다.
    /// 그래서 장치를 한 줄씩, 사람이 그대로 옮겨 적을 수 있는 모양으로 남긴다.
    /// </summary>
    private string DescribeMiss(IReadOnlyList<GevDeviceInfo> found)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"GigE camera not found: SerialNumber='{(_opt.SerialNumber ?? string.Empty).Trim()}'. ");

        if (found.Count == 0)
        {
            sb.Append("No device answered the discovery broadcast. Check that the camera has power and link, ")
              .Append("that the host firewall allows inbound UDP on the GigE control port, ")
              .Append("and that the NIC facing the camera is up with an IPv4 address.");
            return sb.ToString();
        }

        sb.Append($"Discovered {found.Count} device(s) — copy the serial you want into the camera settings:");
        foreach (var d in found)
        {
            var serial = d.SerialNumber.Trim();
            sb.Append(Environment.NewLine)
              .Append("  SerialNumber='").Append(serial.Length == 0 ? "(not reported by this camera)" : serial).Append('\'')
              .Append("  ").Append(d.Manufacturer).Append(' ').Append(d.Model)
              .Append("  ip=").Append(d.Address).Append('/').Append(d.Subnet)
              .Append("  mac=").Append(d.Mac)
              .Append("  nic=").Append(d.InterfaceAddress);

            if (!string.IsNullOrWhiteSpace(d.UserDefinedName))
                sb.Append("  name='").Append(d.UserDefinedName.Trim()).Append('\'');
            if (IsLinkLocal(d.Address))
                sb.Append("  [link-local address — the camera got no DHCP lease and fell back]");
            if (!d.IsReachableDirectly)
                sb.Append("  [SUBNET MISMATCH — this NIC cannot stream from that address]");
            if (serial.Length == 0)
                sb.Append("  [no serial register — bind this one by address instead]");
        }
        return sb.ToString();
    }

    /// <summary>169.254.x.x — DHCP 를 못 받아 스스로 붙인 주소다. 서브넷 불일치의 가장 흔한 원인이다.</summary>
    private static bool IsLinkLocal(System.Net.IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length == 4 && b[0] == 169 && b[1] == 254;
    }

    public void Close()
    {
        lock (_sync)
        {
            if (_disposed || _dev is null) return;
            StopPumpCore();
            Run(CloseCoreAsync, CancellationToken.None);
            IsConnected = false;
        }
        GrabbingChanged?.Invoke(this, false);
        ConnectionChanged?.Invoke(this, new ConnArgs(false));
        WriteLog(CvLogLevel.Info,
            _streamPort > 0 ? $"closed (stream was on local port {_streamPort})" : "closed");
        _streamPort = 0;
    }

    /// <summary>정지는 개시의 역순이고 <b>취소 없이</b> 끝까지 간다 — 중간에 그만두면 카메라가 죽은 소켓으로
    /// 계속 쏘거나 제어권이 걸린 채 남는다. 각 단계는 실패해도 다음 단계를 막지 않는다.</summary>
    private async Task CloseCoreAsync(CancellationToken ct)
    {
        var dev = _dev;
        var stream = _stream;
        var nodes = _nodes;
        _dev = null;
        _stream = null;
        _nodes = null;

        if (nodes != null) await TryExecuteAsync(nodes, "AcquisitionStop", ct).ConfigureAwait(false);
        if (dev != null) await SwallowAsync(() => dev.SetTlParamsLockedAsync(false, ct)).ConfigureAwait(false);

        if (stream != null)
        {
            stream.FrameDropped -= OnFrameDropped;
            await SwallowAsync(() => stream.StopAsync(ct)).ConfigureAwait(false);
            await SwallowAsync(async () => await stream.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
        }

        if (dev != null)
        {
            dev.ControlLost -= OnControlLost;
            await SwallowAsync(async () => await dev.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Close();
    }

    /// <summary>제어권 상실 — 하트비트가 끊겼거나 다른 응용이 카메라를 가져갔다. 스레드풀에서 오고
    /// 이 통지 뒤 장치는 못 쓴다. 되살리는 것은 상위 몫이다(<c>ReconnectingCam</c> 으로 감싸면 자동).</summary>
    private void OnControlLost(GevDevice dev, Exception? ex)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(dev, _dev)) return;   // 이미 교체·정리된 세션의 늦은 통지
            if (!IsConnected) return;
            IsConnected = false;
        }
        WriteLog(CvLogLevel.Warning, "control lost", ex);
        GrabbingChanged?.Invoke(this, false);
        ConnectionChanged?.Invoke(this, new ConnArgs(false));
    }

    /// <summary>버려진 프레임 진단. 수신 스레드에서 오므로 세는 것 말고는 하지 않는다 —
    /// 여기서 무거운 일을 하면 취득이 밀린다.</summary>
    /// <summary>
    /// 버려진 프레임을 남긴다. 다만 <b>우리가 멈춰서 잘린 프레임은 고장이 아니다</b> —
    /// 취득을 멈추면 카메라가 보내던 프레임 가운데서 송신을 그친다. 빠진 패킷은 잃은 것이 아니라
    /// <b>애초에 오지 않은 것</b>이라 재전송으로 메워지지도 않는다. 정지할 때마다 경고를 내면
    /// 진짜 유실이 났을 때 아무도 그 줄을 보지 않게 된다.
    /// </summary>
    private void OnFrameDropped(GevFrameDiag diag)
    {
        var body = $"frame {diag.FrameId} dropped: {diag.Reason} " +
                   $"(missing {diag.MissingPackets}/{diag.ExpectedPackets}, code 0x{diag.Code:X4})";

        var stoppedAt = Interlocked.Read(ref _stoppedAtTicks);
        if (stoppedAt != 0 && DateTime.UtcNow.Ticks - stoppedAt < StopBoundaryTicks)
        {
            WriteLog(CvLogLevel.Info, $"{body} — this frame was in flight when acquisition stopped, not a loss");
            return;
        }

        WriteLog(CvLogLevel.Warning, body);
    }

    // === 조작 ===

    public void GrabOne()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            var stream = EnsureOpen();
            if (_pump != null) return;   // 연속 취득 중이면 그 흐름이 이미 프레임을 낸다
            Run(ct => GrabOnceAsync(stream, ct));
        }
    }

    private async Task GrabOnceAsync(GevStream stream, CancellationToken ct)
    {
        var nodes = _nodes!;

        // 새로 찍기 전에 남은 것을 버린다 — 안 버리면 이 그랩이 옛 프레임을 가져간다.
        if (DrainStream(stream) is var dropped and > 0)
            WriteLog(CvLogLevel.Debug, $"discarded {dropped} queued frame(s) before grabbing a fresh one");

        await TrySetEnumAsync(nodes, "AcquisitionMode", "SingleFrame", ct).ConfigureAwait(false);
        await TryExecuteAsync(nodes, "AcquisitionStart", ct).ConfigureAwait(false);

        // 수신은 반드시 자기 토큰으로 끊는다 — 밖에서 시한을 씌우면 버려진 대기자가 다음 프레임을
        // 삼키고 그 버퍼가 영영 풀로 돌아오지 않는다.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Math.Max(1, _gev.GrabTimeoutMs));

        GevFrame? frame = null;
        try
        {
            frame = await stream.ReceiveAsync(timeout.Token).ConfigureAwait(false);

            // 배수와 경합해 옛 프레임이 손에 들어올 수 있다 — 번호로 걸러 낸다.
            // 장치 번호는 스트림 안에서 단조 증가하므로 이 비교가 곧 "이 호출 뒤에 온 것" 이다.
            while (frame.FrameId != 0 && frame.FrameId <= _lastEmittedFrameId)
            {
                frame.Dispose();
                frame = await stream.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            }

            Emit(frame);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 시한 초과의 원인은 대개 카메라 상태다 — 열 때 남긴 'camera state' 줄을 함께 보게 한다.
            throw new TimeoutException(
                $"No frame within {_gev.GrabTimeoutMs} ms. Check the 'camera state' line logged at open: " +
                "TriggerMode On means the camera waits for its trigger, and ChunkModeActive On means frames are dropped.");
        }
        finally
        {
            // 취소가 이겨도 프레임이 손에 들어올 수 있다 — 무엇이 오든 반납한다.
            frame?.Dispose();
        }
    }

    public void StartContinuous()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            var stream = EnsureOpen();
            if (_pump != null) return;

            Run(async ct =>
            {
                await TrySetEnumAsync(_nodes!, "AcquisitionMode", "Continuous", ct).ConfigureAwait(false);
                await TryExecuteAsync(_nodes!, "AcquisitionStart", ct).ConfigureAwait(false);
            });

            _pumpCts = new CancellationTokenSource();
            var token = _pumpCts.Token;
            _pump = new Thread(() => PumpLoop(stream, token)) { IsBackground = true, Name = $"{Name}.Gev" };
            _pump.Start();
        }
        GrabbingChanged?.Invoke(this, true);
        WriteLog(CvLogLevel.Info, "continuous grab started");
    }

    public void StopContinuous()
    {
        bool stopped;
        lock (_sync)
        {
            if (_disposed) return;
            stopped = StopPumpCore();
            if (stopped)
                Interlocked.Exchange(ref _stoppedAtTicks, DateTime.UtcNow.Ticks);
            if (stopped && _nodes != null)
                Run(ct => TryExecuteAsync(_nodes, "AcquisitionStop", ct), CancellationToken.None);

            // 멈춘 뒤에 대기열을 비운다 — 남겨 두면 다음 단발 그랩이 그것을 가져간다.
            if (stopped && _stream != null && DrainStream(_stream) is var left and > 0)
                WriteLog(CvLogLevel.Debug, $"discarded {left} frame(s) left in the queue when acquisition stopped");
        }
        if (stopped)
        {
            GrabbingChanged?.Invoke(this, false);
            WriteLog(CvLogLevel.Info, "continuous grab stopped");
        }
    }

    /// <summary>수신 루프 정지. 반환값: 호출 전에 돌고 있었으면 true. <see cref="_sync"/> 보유 전제.</summary>
    private bool StopPumpCore()
    {
        var pump = _pump;
        if (pump is null) return false;
        _pump = null;
        try { _pumpCts?.Cancel(); } catch (ObjectDisposedException) { }
        if (!ReferenceEquals(Thread.CurrentThread, pump)) pump.Join(2000);
        _pumpCts?.Dispose();
        _pumpCts = null;
        return true;
    }

    private void PumpLoop(GevStream stream, CancellationToken ct)
    {
        var stats = new GevPumpStats(_gev.PumpStatsIntervalMs);
        while (!ct.IsCancellationRequested)
        {
            GevFrame? frame = null;
            // 수신에서 얼마나 기다렸나 — 이것이 대기열 유무를 가른다. 곧바로 돌아오면
            // 완성 프레임이 이미 줄 서 있다는 뜻이고, 프레임 주기만큼 기다리면 줄이 비어 있다는 뜻이다.
            var waitFrom = stats.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try
            {
                frame = stream.ReceiveAsync(ct).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { break; }
            catch (GevStreamClosedException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                WriteLog(CvLogLevel.Warning, "frame receive failed", ex);
                break;
            }

            // 촬영 시각은 프레임을 놓기 전에 꺼낸다 — Dispose 뒤에는 못 읽는다.
            var waitTicks = stats.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() - waitFrom : 0;
            var capture = stats.Enabled ? CaptureTimeOf(frame) : null;
            var frameId = stats.Enabled ? frame.FrameId : 0;
            var started = stats.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try { Emit(frame); }
            catch (Exception ex) { WriteLog(CvLogLevel.Warning, "frame conversion failed", ex); }
            finally { frame.Dispose(); }

            if (!stats.Enabled) continue;
            var done = System.Diagnostics.Stopwatch.GetTimestamp();
            if (stats.Add(done - started, waitTicks, done, capture, frameId) is { } line)
                WriteLog(CvLogLevel.Info, line);
        }
    }

    public void SetExposureTimeUs(double timeUs)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_nodes is not { } nodes) return;   // 아직 안 열림 — 열 때 CamOpt 값으로 적용된다
            Run(ct => ApplyExposureAsync(nodes, timeUs, ct));
        }
    }

    // === 프레임 변환 ===

    /// <summary>취득 프레임을 <see cref="CamFrame"/> 으로 옮겨 발행한다. 프레임 버퍼는 호출이 끝나면
    /// 반납되므로 반드시 이 안에서 복사를 마친다.</summary>
    private void Emit(GevFrame frame)
    {
        // 무엇을 내보냈는지 여기 한 자리에서 기억한다 — 단발 그랩의 "새 프레임" 판정 기준이다.
        // 변환에 실패해 발행하지 못한 것도 이미 지나간 프레임이므로 기준에 넣는다.
        if (frame.FrameId > _lastEmittedFrameId)
        {
            // 번호가 건너뛰었으면 카메라가 보낸 것이 여기까지 오지 못한 것이다.
            if (_lastEmittedFrameId != 0 && frame.FrameId > _lastEmittedFrameId + 1)
                _neverArrivedFrames += (long)(frame.FrameId - _lastEmittedFrameId - 1);
            _lastEmittedFrameId = frame.FrameId;
        }

        var cam = Convert(frame);
        if (cam is null) return;
        FrameAcquired?.Invoke(this, cam);
    }

    private CamFrame? Convert(GevFrame frame)
    {
        var code = frame.PixelFormatCode;
        var w = frame.Width;
        var h = frame.Height;

        if (!PixelFormatInfo.IsKnown(code))
        {
            WriteLog(CvLogLevel.Warning, $"unsupported pixel format 0x{code:X8} ({PixelFormatInfo.Name(code)}) — frame dropped");
            return null;
        }

        // 압축 포맷은 8비트로 접어서 받는다. CamFrame 이 8비트뿐이라 16비트를 거쳐 가면 재변환이 낭비다.
        if (PixelUnpack.CanFoldToMono8(code))
        {
            // 줄 간격이 없는 프레임(줄이 바이트 경계에 안 떨어지는 압축 포맷)은 한 덩어리로 넘긴다.
            var src = frame.Data.Span.Slice(0, frame.ImageSize);
            var folded = frame.Stride > 0
                ? PixelUnpack.FoldToMono8(code, src, frame.Stride, w, h)
                : PixelUnpack.FoldToMono8(code, src, frame.ImageSize, w * h, 1);
            var layoutPacked = PixelFormatInfo.IsBayer(code) ? GevPixelLayout.Bayer : GevPixelLayout.Mono;
            return GevFrameConv.ToCamFrame(folded, w, h, w, layoutPacked, 8, BayerOf(code), _gev.BayerToMono,
                                           CaptureTimeOf(frame));
        }

        if (!TryDescribe(code, out var layout, out var significantBits))
        {
            WriteLog(CvLogLevel.Warning, $"unhandled pixel format {PixelFormatInfo.Name(code)} — frame dropped");
            return null;
        }

        // 화소를 우리 것으로 실체화한다 — 원본 버퍼는 이 호출이 끝나면 풀로 돌아간다.
        var pixels = frame.Data.Span.Slice(0, frame.ImageSize).ToArray();
        return GevFrameConv.ToCamFrame(pixels, w, h, frame.Stride, layout, significantBits, BayerOf(code),
                                       _gev.BayerToMono, CaptureTimeOf(frame));
    }

    /// <summary>
    /// 이 카메라의 취득 건강을 <b>한 번에</b> 뜬다 — 항목을 따로 읽어 서로 다른 시점 값이 섞이지 않게.
    /// 열려 있지 않으면 계수가 없으므로 null.
    /// 쓰는 법과 경보 기준은 <see cref="GevCamHealth"/> 에 적어 두었다.
    /// </summary>
    public GevCamHealth? GetHealth()
    {
        lock (_sync)
        {
            if (_disposed || _stream is null) return null;
            var s = _stream.Stats.Snapshot();
            return new GevCamHealth(
                Name: Name,
                DeviceAddress: _dev?.Address?.ToString() ?? string.Empty,
                StreamStartedUtc: _streamStartedUtc,
                CompletedFrames: s.FramesCompleted,
                IncompleteFrames: s.FramesIncomplete,
                DroppedNoBuffer: s.FramesDroppedNoBuffer,
                DroppedError: s.FramesDroppedError,
                DroppedUnsupported: s.FramesDroppedUnsupported,
                MissingPackets: s.PacketsMissing,
                ResendRequests: s.ResendRequests,
                ResendRecovered: s.ResendRecovered,
                NeverArrivedFrames: Interlocked.Read(ref _neverArrivedFrames));
        }
    }

    /// <summary>
    /// 대기열에 남아 있는 완성 프레임을 버린다. 버린 장수를 돌려준다.
    ///
    /// <b>연속 취득을 멈춰도 이미 받아 둔 프레임은 대기열에 남는다.</b> 그대로 두면 다음 단발 그랩이
    /// 새로 찍은 것이 아니라 그 옛것을 가져간다 — 화면이라면 한 장 늦은 그림이지만
    /// <b>검사라면 이전 대상을 판정한다.</b> 예외도 경고도 없이 조용히 틀린다.
    /// </summary>
    private int DrainStream(GevStream stream)
    {
        var dropped = 0;
        while (stream.TryReceive(out var stale) && stale != null)
        {
            // 버린 것도 지나간 프레임이다 — 기준을 올려 두지 않으면 다음 프레임이 "건너뛴 것" 으로 잡힌다.
            if (stale.FrameId > _lastEmittedFrameId) _lastEmittedFrameId = stale.FrameId;
            stale.Dispose();
            dropped++;
        }
        return dropped;
    }

    /// <summary>
    /// 장치가 프레임에 찍은 촬영 시각. 틱 주파수를 모르면 null —
    /// <b>0 을 대신 넣지 않는다</b>. 0 은 "전원 인가 직후에 찍혔다" 로 읽혀 지연이 거대해 보인다.
    /// </summary>
    private TimeSpan? CaptureTimeOf(GevFrame frame)
    {
        if (_tickHz == 0 || frame.Timestamp == 0) return null;
        return TimeSpan.FromSeconds(frame.Timestamp / (double)_tickHz);
    }

    /// <summary>PFNC 코드를 변환기 입력으로 푼다. 유효 비트(깊이)를 쓴다 — 코드가 차지하는 비트가 아니다.</summary>
    private static bool TryDescribe(uint code, out GevPixelLayout layout, out int significantBits)
    {
        significantBits = PixelFormatInfo.Depth(code);
        layout = GevPixelLayout.Mono;
        if (significantBits is < 8 or > 16) return false;   // 8비트 서브셋으로 접을 수 없는 것(부동소수·32비트 등)

        if (PixelFormatInfo.IsBayer(code)) { layout = GevPixelLayout.Bayer; return true; }
        if (PixelFormatInfo.IsMono(code)) { layout = GevPixelLayout.Mono; return true; }

        // 컬러는 8비트 채널만 받는다 — 채널당 비트가 다른 것은 위 깊이 판정이 아니라 여기서 갈린다.
        if (significantBits != 8) return false;
        switch (PixelFormatInfo.ToPixelFormat(code))
        {
            case PixelFormat.RGB8: layout = GevPixelLayout.Rgb; return true;
            case PixelFormat.BGR8: layout = GevPixelLayout.Bgr; return true;
            case PixelFormat.RGBa8: layout = GevPixelLayout.Rgba; return true;
            case PixelFormat.BGRa8: layout = GevPixelLayout.Bgra; return true;
            default: return false;
        }
    }

    private CvBayerPattern? BayerOf(uint code)
    {
        if (!PixelFormatInfo.IsBayer(code)) return null;
        if (_bayerOverride is { } forced) return forced;
        return ToCvPattern(PixelFormatInfo.BayerPattern(code));
    }

    private static CvBayerPattern? ToCvPattern(BayerPattern p) => p switch
    {
        BayerPattern.RG => CvBayerPattern.RG,
        BayerPattern.GR => CvBayerPattern.GR,
        BayerPattern.GB => CvBayerPattern.GB,
        BayerPattern.BG => CvBayerPattern.BG,
        _ => null,
    };

    // === 카메라 피처 ===

    /// <summary>저장된 설정 묶음을 불러온다. 불러오면 노드 값이 통째로 바뀌므로 캐시를 버린다.</summary>
    private async Task LoadUserSetAsync(GenApiNodeMap nodes, CancellationToken ct)
    {
        var set = (_opt.UserSettings ?? string.Empty).Trim();
        if (set.Length == 0) return;
        if (nodes.GetNode("UserSetSelector") is not IEnumeration sel || nodes.GetNode("UserSetLoad") is not ICommand load)
        {
            WriteLog(CvLogLevel.Warning, $"camera has no user set feature — '{set}' ignored");
            return;
        }
        try
        {
            await sel.SetAsync(set, ct).ConfigureAwait(false);
            await load.ExecuteAsync(ct).ConfigureAwait(false);
            await WaitForCommandAsync(load, ct).ConfigureAwait(false);
            // 캐시를 버려야 이후 읽기가 세트가 바꿔 놓은 값을 본다 — 안 버리면 로드 이전 값이 그대로 나온다.
            nodes.InvalidateAll();
            WriteLog(CvLogLevel.Info, $"user set '{set}' loaded");
        }
        catch (GenApiException ex)
        {
            WriteLog(CvLogLevel.Warning, $"failed to load user set '{set}'", ex);
        }
    }

    /// <summary>
    /// 명령이 끝나기를 기다린다. 완료 신호는 믿을 수 있을 때만 쓴다 — 카메라 XML 이 폴링 주기를 선언하지
    /// 않으면 완료 조회가 <b>무조건 참</b>을 돌려주므로, 그것만 보고 넘어가면 아직 적용 중인 값을 읽는다.
    /// 그래서 신호를 기다린 뒤 짧은 안정 시간을 한 번 더 준다.
    ///
    /// 이게 없으면 사용자 세트가 트리거를 켜는 구성에서 <b>"트리거 Off 로 찍혔는데 프레임이 안 온다"</b> 는
    /// 최악의 로그가 나온다 — 진단이 오히려 오도한다.
    /// </summary>
    private async Task WaitForCommandAsync(ICommand cmd, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, _gev.CommandTimeoutMs));
        while (DateTime.UtcNow < deadline)
        {
            bool done;
            try { done = await cmd.IsDoneAsync(ct).ConfigureAwait(false); }
            catch (GenApiException) { break; }   // 완료를 물을 수 없는 명령 — 안정 시간으로만 처리한다
            if (done) break;
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
        if (_gev.SettleMs > 0) await Task.Delay(_gev.SettleMs, ct).ConfigureAwait(false);
    }

    /// <summary>노출 시간(마이크로초) 적용. <b>노드 이름이 하나가 아니다</b> — 표준 세대가 갈려
    /// 현장에 신구 이름이 둘 다 있고 취득 라이브러리는 별칭을 만들어 주지 않는다.</summary>
    private async Task ApplyExposureAsync(GenApiNodeMap nodes, double timeUs, CancellationToken ct)
    {
        if (timeUs <= 0) return;
        var node = Find<IFloat>(nodes, "ExposureTime", "ExposureTimeAbs");
        if (node is null)
        {
            WriteLog(CvLogLevel.Warning, $"camera exposes no exposure-time node — {timeUs}us ignored");
            return;
        }
        try
        {
            await node.SetAsync(timeUs, ct).ConfigureAwait(false);
        }
        catch (GenApiException ex) when (TryReadGrid(ex, out var anchor, out var increment))
        {
            // 격자에서 벗어난 값이다. 여기서 포기하면 카메라가 이전 노출 그대로 돌아 밝기만 틀린 채
            // 예외도 없이 검사가 돈다 — 가장 가까운 격자 값으로 맞춰 다시 쓰고, 무엇을 썼는지 알린다.
            if (GevGrid.Snap(timeUs, anchor, increment) is not { } snapped)
            {
                WriteLog(CvLogLevel.Warning, $"failed to set exposure to {timeUs}us via '{node.Name}'", ex);
                return;
            }
            // 격자를 낸 노드가 우리가 쓴 노드와 다르면 사이에 변환이 있다는 뜻이라, 맞춘 값은 어림이다.
            var sameUnits = string.Equals(ex.NodeName, node.Name, StringComparison.Ordinal);
            try
            {
                await node.SetAsync(snapped, ct).ConfigureAwait(false);
                WriteLog(CvLogLevel.Warning,
                    $"exposure {timeUs}us is not on the camera's grid (anchor {anchor}, step {increment}) — " +
                    $"used {snapped}us instead. Put that value in the configuration to stop this message." +
                    (sameUnits
                        ? string.Empty
                        : $" The grid came from '{ex.NodeName}', not '{node.Name}', so a conversion sits in between " +
                          "and this value is an approximation — check the applied exposure below."));
            }
            catch (GenApiException retryEx)
            {
                // 변환 노드에 배율이 있으면 격자 단위가 우리 단위와 달라 이 재시도가 빗나간다.
                WriteLog(CvLogLevel.Warning,
                    $"exposure {timeUs}us is off-grid and the nearest value {snapped}us was refused too — " +
                    $"the camera keeps its previous exposure. Read the accepted range from the camera and set it explicitly.",
                    retryEx);
                return;
            }
        }
        catch (GenApiException ex)
        {
            WriteLog(CvLogLevel.Warning, $"failed to set exposure to {timeUs}us via '{node.Name}'", ex);
            return;
        }

        // 쓰기가 성공해도 카메라가 그 값을 그대로 쓴다는 보장은 없다 — 실제 값을 남긴다.
        // "썼으니 됐겠지" 가 이 바닥에서 제일 자주 틀리는 가정이다.
        try
        {
            var actual = await node.GetAsync(ct).ConfigureAwait(false);
            if (Math.Abs(actual - timeUs) > 0.5)
                WriteLog(CvLogLevel.Info, $"exposure requested {timeUs}us, camera applied {actual}us via '{node.Name}'");
        }
        catch (GenApiException) { /* 되읽기 실패는 진단 손실일 뿐이라 넘어간다 */ }
    }

    /// <summary>거절이 "격자 어긋남" 이면 기준점과 간격을 꺼낸다 — 예외에 값으로 실려 온다.</summary>
    private static bool TryReadGrid(GenApiException ex, out long anchor, out long increment)
    {
        anchor = 0;
        increment = 0;
        if (ex.Data[GenApiException.GridAnchorKey] is not long a) return false;
        if (ex.Data[GenApiException.GridIncrementKey] is not long i) return false;
        anchor = a;
        increment = i;
        return true;
    }

    /// <summary>
    /// 카메라가 지금 어떤 상태인지 한 줄로 남긴다 — <b>"프레임이 안 온다" 의 원인 대부분이 여기 보인다.</b>
    /// 특히 트리거가 켜져 있으면 신호가 오기 전까지 아무것도 오지 않는데, 그 사실은 어디에도 드러나지 않고
    /// 그냥 취득이 조용히 멈춘 것처럼 보인다. 청크가 켜진 카메라도 마찬가지로 프레임이 0 이 된다.
    /// </summary>
    private async Task LogCameraStateAsync(GenApiNodeMap nodes, CancellationToken ct)
    {
        var pixel = await TryReadEnumAsync(nodes, "PixelFormat", ct).ConfigureAwait(false);
        var w = await TryReadIntAsync(nodes, "Width", ct).ConfigureAwait(false);
        var h = await TryReadIntAsync(nodes, "Height", ct).ConfigureAwait(false);
        var payload = await TryReadIntAsync(nodes, "PayloadSize", ct).ConfigureAwait(false);
        var acq = await TryReadEnumAsync(nodes, "AcquisitionMode", ct).ConfigureAwait(false);
        var trigMode = await TryReadEnumAsync(nodes, "TriggerMode", ct).ConfigureAwait(false);
        var trigSrc = await TryReadEnumAsync(nodes, "TriggerSource", ct).ConfigureAwait(false);

        WriteLog(CvLogLevel.Info,
            $"camera state: pixelFormat={pixel ?? "?"} size={w?.ToString() ?? "?"}x{h?.ToString() ?? "?"} " +
            $"payloadSize={payload?.ToString() ?? "?"} acquisitionMode={acq ?? "?"} " +
            $"triggerMode={trigMode ?? "(absent)"} triggerSource={trigSrc ?? "(absent)"}");

        if (string.Equals(trigMode, "On", StringComparison.OrdinalIgnoreCase))
            WriteLog(CvLogLevel.Warning,
                $"TriggerMode is On (source '{trigSrc ?? "?"}') — no frames will arrive until the camera receives that trigger. " +
                "If free-running frames are expected, turn TriggerMode off on the camera or load a user set that has it off.");

        if (await TryReadFlagAsync(nodes, "ChunkModeActive", ct).ConfigureAwait(false) == true)
            WriteLog(CvLogLevel.Warning,
                "ChunkModeActive is On — frames carrying chunk payloads are not assembled and will be dropped, " +
                "so acquisition runs but delivers nothing. Turn chunk mode off on the camera.");
    }

    /// <summary>
    /// 전송 영상의 실효 Bayer 패턴을 판정한다. 미러와 홀수 ROI 오프셋은 2×2 배열의 시작점을 옮기는데,
    /// 표준은 그때 장치가 PixelFormat 을 고쳐 보고하도록 요구하지 않는다.
    ///
    /// 그래서 <b>계산값을 강요하지 않는다</b> — 이미 보정해 보고하는 펌웨어에서는 이중 보정이 된다.
    /// 선언값을 그대로 쓰되, 계산과 어긋나면 <b>둘 다 로그에 남겨</b> 현장이 판단할 근거를 준다.
    /// 못 박아야 하면 <see cref="GevCamOpt.BayerPatternOverride"/> 가 탈출구다.
    /// </summary>
    private async Task<CvBayerPattern?> ResolveBayerAsync(GenApiNodeMap nodes, CancellationToken ct)
    {
        var code = (uint?)await TryReadIntAsync(nodes, "PixelFormat", ct).ConfigureAwait(false) ?? 0u;
        if (code == 0 || !PixelFormatInfo.IsBayer(code)) return null;
        if (ToCvPattern(PixelFormatInfo.BayerPattern(code)) is not { } declared) return null;

        // 없는 노드는 중립값으로 — ReverseY 를 아예 선언하지 않는 카메라가 흔하다.
        var revX = await TryReadFlagAsync(nodes, "ReverseX", ct).ConfigureAwait(false) ?? false;
        var revY = await TryReadFlagAsync(nodes, "ReverseY", ct).ConfigureAwait(false) ?? false;
        var offX = (int)(await TryReadIntAsync(nodes, "OffsetX", ct).ConfigureAwait(false) ?? 0);
        var offY = (int)(await TryReadIntAsync(nodes, "OffsetY", ct).ConfigureAwait(false) ?? 0);

        // 미러의 기준 치수 — 센서 전체를 뒤집고 ROI 를 떼는 장치면 최대 치수, ROI 안에서 뒤집으면 ROI 치수다.
        // 어느 쪽인지 알 수 없으므로 최대 치수를 먼저 쓰고, 없으면 ROI 치수로 떨어진다.
        var maxW = (int)(await TryReadIntAsync(nodes, "WidthMax", ct).ConfigureAwait(false)
                         ?? await TryReadIntAsync(nodes, "Width", ct).ConfigureAwait(false) ?? 0);
        var maxH = (int)(await TryReadIntAsync(nodes, "HeightMax", ct).ConfigureAwait(false)
                         ?? await TryReadIntAsync(nodes, "Height", ct).ConfigureAwait(false) ?? 0);
        if (maxW <= 0 || maxH <= 0) return null;

        var computed = CvBayerPhase.Effective(declared, maxW, maxH, revX, revY, offX, offY);
        if (computed == declared) return null;   // 선언값을 그대로 쓴다

        WriteLog(CvLogLevel.Warning,
            $"Bayer phase disagreement: the camera reports {declared} but geometry implies {computed} " +
            $"(ReverseX={revX} ReverseY={revY} OffsetX={offX} OffsetY={offY} max={maxW}x{maxH}). " +
            $"Using the reported pattern — if colours look wrong, pin it with BayerPatternOverride.");
        return null;
    }

    // === 노드 접근 도우미 ===

    private static T? Find<T>(GenApiNodeMap nodes, params string[] names) where T : class, INode
    {
        foreach (var n in names)
            if (nodes.GetNode(n) is T hit) return hit;
        return null;
    }

    private static async Task<long?> TryReadIntAsync(GenApiNodeMap nodes, string name, CancellationToken ct)
    {
        try
        {
            return nodes.GetNode(name) switch
            {
                IInteger i => await i.GetAsync(ct).ConfigureAwait(false),
                IEnumeration e => await e.GetIntValueAsync(ct).ConfigureAwait(false),
                _ => null,
            };
        }
        catch (GenApiException) { return null; }   // 미구현·잠김·읽기 불가
    }

    /// <summary>
    /// On/Off 성격의 노드를 <b>선언된 종류와 무관하게</b> 읽는다.
    ///
    /// 같은 피처를 벤더마다 다른 종류로 선언한다 — 어떤 장치는 Boolean(true/false), 어떤 장치는
    /// Enumeration(On/Off) 이다. 한 종류만 보면 나머지 장치에서 <b>조용히 null 이 되어</b> 호출부의
    /// 기본값으로 흘러간다. ReverseX/ReverseY 가 그렇게 되면 Bayer 위상 계산이 어긋나 <b>색만 틀리고
    /// 예외도 경고도 안 난다</b> — 원인을 장치 쪽에서 찾게 되는 부류다.
    /// </summary>
    private static async Task<bool?> TryReadFlagAsync(GenApiNodeMap nodes, string name, CancellationToken ct)
    {
        try
        {
            switch (nodes.GetNode(name))
            {
                case IBoolean b:
                    return await b.GetAsync(ct).ConfigureAwait(false);

                case IEnumeration e:
                    return ParseFlag(await e.GetAsync(ct).ConfigureAwait(false));

                // 0/1 정수로 선언하는 장치도 있다.
                case IInteger i:
                    return await i.GetAsync(ct).ConfigureAwait(false) != 0;

                default:
                    return null;
            }
        }
        catch (GenApiException) { return null; }
    }

    /// <summary>알아볼 수 없는 낱말은 null — 억지로 false 로 접으면 꺼져 있다고 잘못 단정한다.</summary>
    private static bool? ParseFlag(string? symbolic) => symbolic?.Trim().ToUpperInvariant() switch
    {
        "ON" or "TRUE" or "ENABLED" or "1" => true,
        "OFF" or "FALSE" or "DISABLED" or "0" => false,
        _ => null,
    };

    /// <summary>
    /// OS 가 실제로 준 수신 버퍼 크기를 남긴다 — <b>요청한 크기가 아니라 받은 크기다.</b>
    ///
    /// 커널은 이 버퍼를 페이지 아웃되지 않는 영역에서 떼어 준다. 그래서 여러 대를 한 호스트에서
    /// 열면 <b>나중에 여는 쪽부터 덜 받기 쉽고</b>, 그 결과는 부하가 걸릴 때 유실로만 나타나
    /// 원인이 어디에도 보이지 않는다. 여덟 대를 열었으면 이 줄 여덟 개를 나란히 놓고
    /// 뒤쪽만 작은지 본다 — 그렇다면 요청을 낮추는 편이 앞쪽만 살찌우는 것보다 낫다.
    /// </summary>
    private void LogSocketBuffer(GevStream stream, int requested)
    {
        var granted = stream.SocketReceiveBufferBytes;
        var port = stream.LocalPort;
        if (granted >= requested)
        {
            WriteLog(CvLogLevel.Info,
                $"stream on local port {port}: socket receive buffer {granted} bytes (requested {requested})");
            return;
        }

        WriteLog(CvLogLevel.Warning,
            $"stream on local port {port}: the OS granted a socket receive buffer of {granted} bytes "
            + $"out of the {requested} requested. " +
            "Under load this camera will drop packets before the others do. If several cameras run on this host, " +
            "lower GevCamOpt.SocketBufferBytes for all of them rather than letting the first ones take it all.");
    }

    /// <summary>
    /// SCPD 를 장치 틱으로 정한다 — 못 박은 값이 있으면 그것, 없으면 시간에서 환산한다.
    /// 환산에 실패하면 <b>조용히 넘어가지 않는다</b>: 여러 대를 한 NIC 로 받는 구성에서 이것이
    /// 빠지면 버스트가 겹쳐 유실되는데, 증상이 "가끔 프레임이 빈다" 라 원인을 찾기 어렵다.
    /// </summary>
    private int? ResolveScpdTicks(GevDevice dev)
    {
        if (_gev.InterPacketDelayTicks is { } pinned)
        {
            WriteLog(CvLogLevel.Info, $"inter-packet delay pinned to {pinned} ticks");
            return pinned;
        }

        if (_gev.InterPacketDelayUs is not { } us) return null;

        if (GevScpd.TicksFor(us, dev.TimestampTickFrequency) is not { } ticks)
        {
            WriteLog(CvLogLevel.Warning,
                $"cannot apply the requested {us}us inter-packet delay: the camera reports a timestamp tick " +
                $"frequency of {dev.TimestampTickFrequency} Hz, so the tick value cannot be derived. " +
                "If several cameras share this NIC, pin the value with GevCamOpt.InterPacketDelayTicks.");
            return null;
        }

        WriteLog(CvLogLevel.Info,
            $"inter-packet delay {us}us -> {ticks} ticks at {dev.TimestampTickFrequency} Hz");
        return ticks;
    }

    private static async Task<string?> TryReadEnumAsync(GenApiNodeMap nodes, string name, CancellationToken ct)
    {
        if (nodes.GetNode(name) is not IEnumeration e) return null;
        try { return await e.GetAsync(ct).ConfigureAwait(false); }
        catch (GenApiException) { return null; }
    }

    private async Task TrySetEnumAsync(GenApiNodeMap nodes, string name, string symbolic, CancellationToken ct)
    {
        if (nodes.GetNode(name) is not IEnumeration e)
        {
            WriteLog(CvLogLevel.Info, $"camera has no {name} node — leaving acquisition mode as the camera has it");
            return;
        }
        if (e.GetEntry(symbolic) is null)
        {
            WriteLog(CvLogLevel.Info, $"{name} has no '{symbolic}' entry — leaving it as the camera has it");
            return;
        }
        try { await e.SetAsync(symbolic, ct).ConfigureAwait(false); }
        catch (GenApiException ex) { WriteLog(CvLogLevel.Warning, $"failed to set {name}={symbolic}", ex); }
    }

    /// <summary>명령 실행. <b>없는 명령을 조용히 넘기지 않는다</b> — AcquisitionStart 가 없으면 스트림은 서는데
    /// 프레임이 한 장도 안 오고, 그 원인이 로그에 없으면 원격에서 절대 못 가른다.</summary>
    private async Task TryExecuteAsync(GenApiNodeMap nodes, string name, CancellationToken ct)
    {
        if (nodes.GetNode(name) is not ICommand c)
        {
            WriteLog(CvLogLevel.Warning,
                $"camera has no {name} command — acquisition cannot be driven through it, so frames may never arrive");
            return;
        }
        try { await c.ExecuteAsync(ct).ConfigureAwait(false); }
        catch (GenApiException ex) { WriteLog(CvLogLevel.Warning, $"failed to execute {name}", ex); }
        catch (GevException ex) { WriteLog(CvLogLevel.Warning, $"failed to execute {name}", ex); }
    }

    // === 잡동사니 ===

    private GevStream EnsureOpen()
        => _stream ?? throw new InvalidOperationException("Camera is not opened.");

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GevCam));
    }

    /// <summary>비동기 호출을 동기 경계로 넘긴다. 라이브러리가 컨텍스트를 잡지 않으므로 교착하지 않는다.</summary>
    private static void Run(Func<CancellationToken, Task> body, CancellationToken ct = default)
        => Task.Run(() => body(ct), ct).GetAwaiter().GetResult();

    private static async Task SwallowAsync(Func<Task> body)
    {
        try { await body().ConfigureAwait(false); }
        catch (Exception ex) { CvLog.Publish(CvLogLevel.Debug, LogSource, "teardown step failed.", ex); }
    }

    private void WriteLog(CvLogLevel level, string message, Exception? ex = null)
        => CvLog.Publish(level, LogSource, $"[{Name}] {message}", ex);
}
