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
public sealed class GevCam : ICam, ICamGrabAsync
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

    private readonly CamOpt _opt;
    private readonly GevCamOpt _gev;
    private readonly object _sync = new();

    private GevDeviceInfo? _info;      // Close/Open 사이에 남겨 둔다 — 다시 여는 길이 짧아지고 NIC 도 그대로 고른다
    private GevDevice? _dev;
    private GenApiNodeMap? _nodes;
    private GevStream? _stream;

    private Thread? _pump;

    /// <summary>진행 중인 단발 그랩. 제어 상실과 닫기가 이것을 끊어 그랩이 시한을 다 채우지 않게 한다.
    /// <see cref="_sync"/> 아래에서만 만들고 지운다.</summary>
    private CancellationTokenSource? _grabCts;

    /// <summary>그랩을 끊은 쪽이 남긴 사유 — 끊긴 그랩이 부른 쪽에 던질 문구다. 끊기 전에 적는다.</summary>
    private string? _grabAbortReason;
    private CancellationTokenSource? _pumpCts;
    private bool _disposed;

    /// <summary><see cref="SetExposureTimeUs"/> 로 받은 마지막 값 — 아직 안 열렸으면 열 때 쓰고,
    /// 열려 있어도 들고 있다가 다음 열기에 다시 적용한다. null 이면 <see cref="CamOpt"/> 값을 쓴다.</summary>
    private double? _setExposureUs;

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
        string opened;
        lock (_sync)
        {
            ThrowIfDisposed();
            // 제어를 잃은 세션은 장치 참조만 남아 있고 쓸 수는 없다. 그 상태에서 _dev 만 보고 돌아가면
            // "이미 열려 있다" 로 읽혀 예외도 로그도 없이 아무 일도 일어나지 않는다 — 부른 쪽은 다시
            // 열렸다고 여긴 채 오지 않을 프레임을 기다린다. 부른 뜻은 "다시 열어라" 이므로 먼저 접는다.
            // 통지는 내지 않는다 — 끊겼다는 말은 제어 상실 때 이미 나갔고, 여기서 한 번 더 내면
            // 연결이 false 로 두 번 떨어졌다가 올라온 것처럼 보인다.
            if (_dev != null && !IsConnected)
            {
                WriteLog(CvLogLevel.Info, "reopening after control loss — folding the dead session first");
                CloseWhileLocked();
            }
            if (_dev != null) return;
            Run(OpenCoreAsync);
            IsConnected = true;
            // 문구는 락 안에서 뜬다 — 밖에서 장치를 읽으면 곧바로 닫는 흐름과 겹친 순간 신원도 주소도
            // 빠진 줄이 남는다.
            opened = DescribeOpened();
        }
        ConnectionChanged?.Invoke(this, new ConnArgs(true));
        // 이름과 장치 신원을 한 번 이어 둔다 — 취득 라이브러리는 자기 줄에 주소를 달고 우리 이름은
        // 모르므로, 이 한 줄이 있어야 그쪽 줄들이 이 카메라 것으로 읽힌다. 주소는 세션 내내 안 바뀐다.
        WriteLog(CvLogLevel.Info, opened);
    }

    /// <summary>
    /// 여는 줄에 실을 카메라 신원. <see cref="_sync"/> 보유 전제 — <see cref="_dev"/> 를 읽는다.
    ///
    /// 제조사·모델·장치버전은 <b>열 때 장치에서 이미 읽어 둔 값</b>이라 추가 통신이 없다. 이것을 안 남기면
    /// 로그만으로는 어느 기종이 어느 펌웨어로 돌았는지 알 수 없어, 나중에 설비에 사람을 보내 물어야 한다.
    ///
    /// 시리얼은 장치가 보고한 값을 쓴다 — 설정값과 같다는 것은 여는 길에서 이미 확인했다. 시리얼 레지스터를
    /// 내놓지 않는 기종에서만 설정값으로 떨어지고, 그런 장치에서는 설정값도 비어 있다.
    ///
    /// <b>빈 값은 자리도 남기지 않는다</b> — "ver=" 같은 빈 칸은 값으로 오독된다. 장치가 아무것도 알려 주지
    /// 않으면 시리얼과 주소만 남아, 신원을 싣기 전의 줄과 같은 모양이 된다.
    /// </summary>
    private string DescribeOpened()
    {
        var info = _dev?.Info;
        var sb = new System.Text.StringBuilder("opened");

        if (Trimmed(info?.Manufacturer) is { Length: > 0 } vendor) sb.Append(' ').Append(vendor);
        if (Trimmed(info?.Model) is { Length: > 0 } model) sb.Append(' ').Append(model);

        var serial = Trimmed(info?.SerialNumber);
        if (serial.Length == 0) serial = Trimmed(_opt.SerialNumber);
        sb.Append(" (SN=").Append(serial.Length == 0 ? "(not reported)" : serial);

        if (Trimmed(info?.DeviceVersion) is { Length: > 0 } version) sb.Append(" ver=").Append(version);
        sb.Append(" at ").Append(_dev?.Address).Append(')');
        return sb.ToString();
    }

    /// <summary>없는 값과 공백뿐인 값을 한 가지로 접는다 — 문자열 레지스터를 공백으로 채워 보내는 장치가 있다.</summary>
    private static string Trimmed(string? text) => text?.Trim() ?? string.Empty;

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
            // SetExposureTimeUs 로 받아 둔 값이 있으면 그쪽이 우선이다 — 나중에 부른 쪽이 최신이고,
            // 여기서 설정값으로 덮으면 운전 중에 맞춘 노출이 재연결마다 원래대로 돌아간다.
            await ApplyExposureAsync(nodes, _setExposureUs ?? _opt.ExposureTimeUs, ct).ConfigureAwait(false);
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
        bool closed;
        lock (_sync)
        {
            if (_disposed) return;
            closed = CloseWhileLocked();
        }
        if (closed) RaiseClosed();
    }

    /// <summary><see cref="_sync"/> 보유 전제. 열려 있었으면 정리하고 <c>true</c>.</summary>
    private bool CloseWhileLocked()
    {
        if (_dev is null) return false;
        AbortGrabWhileLocked("The camera was closed while the single grab waited for its frame.");
        StopPumpCore();
        Run(CloseCoreAsync, CancellationToken.None);
        IsConnected = false;
        return true;
    }

    /// <summary>닫힘 통지 — <b>락 밖에서</b> 부른다. 핸들러가 이 카메라를 다시 부를 수 있다.</summary>
    private void RaiseClosed()
    {
        GrabbingChanged?.Invoke(this, false);
        ConnectionChanged?.Invoke(this, new ConnArgs(false));
        WriteLog(CvLogLevel.Info, "closed");
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

    /// <summary>
    /// 닫고 버린다. <b>정리를 먼저 하고 표시를 나중에 한다</b> — 순서가 뒤집히면 정리 경로가 자기
    /// "버려졌음" 검사에 걸려 아무것도 하지 않고 돌아간다. 그러면 장치가 열린 채 남아 제어권이
    /// 하트비트가 만료될 때까지 잡히고(다음 열기가 "다른 응용이 잡고 있다" 로 실패한다), 스트림 소켓도
    /// 반납되지 않으며, 수신 펌프는 배경 스레드라 프로세스가 끝날 때까지 프레임을 계속 발행한다.
    /// </summary>
    public void Dispose()
    {
        bool closed;
        lock (_sync)
        {
            if (_disposed) return;
            closed = CloseWhileLocked();
            _disposed = true;
        }
        if (closed) RaiseClosed();
    }

    /// <summary>제어권 상실 — 하트비트가 끊겼거나 다른 응용이 카메라를 가져갔다. 스레드풀에서 오고
    /// 이 통지 뒤 장치는 못 쓴다. 되살리는 것은 상위 몫이다(<c>ReconnectingCam</c> 으로 감싸면 자동).</summary>
    /// <summary>
    /// 제어권 상실 통지. <b>여기서 수신 펌프를 접는다.</b>
    ///
    /// 제어를 잃어도 스트림은 닫히지 않는다 — 취득 계층에서 수신 대기는 큐가 닫힐 때까지 풀리지 않으므로,
    /// 접지 않으면 펌프 스레드가 그대로 살아 <b>연결이 끊겼다고 알린 뒤에도 프레임을 계속 발행한다.</b>
    /// 그리고 펌프 참조가 남아 있는 동안 <see cref="GrabOne"/> 과 <see cref="StartContinuous"/> 는
    /// "이미 연속 취득 중" 으로 읽혀 <b>예외도 로그도 없이 조용히 돌아간다</b> — 부른 쪽은 오지 않을
    /// 프레임을 시한이 다 될 때까지 기다린다.
    ///
    /// 접고 나면 <see cref="IsGrabbing"/> 이 방금 낸 <see cref="GrabbingChanged"/> 와 같은 말을 하고,
    /// 그 뒤의 그랩은 장치가 던지는 제어 상실 예외로 <b>시끄럽게</b> 실패한다.
    ///
    /// 이 통지는 취득 계층이 스레드 풀에서 올린다 — 수신 펌프 자신이 아니므로 여기서 펌프를 기다려도
    /// 자기 자신을 기다리지 않는다(그 경우도 <see cref="StopPumpCore"/> 가 따로 막는다).
    /// </summary>
    private void OnControlLost(GevDevice dev, Exception? ex)
    {
        bool wasGrabbing;
        lock (_sync)
        {
            if (!ReferenceEquals(dev, _dev)) return;   // 이미 교체·정리된 세션의 늦은 통지
            if (!IsConnected) return;
            IsConnected = false;
            // 기다리던 그랩을 지금 끊는다. 두면 시한을 다 채우고 "프레임이 안 왔다, 카메라 설정을 보라" 로
            // 끝나 조작자를 엉뚱한 데로 보낸다 — 원인은 제어 상실이다.
            AbortGrabWhileLocked(
                "Control of the camera was lost while the single grab waited for its frame; reopen the camera.");
            wasGrabbing = StopPumpCore();
        }
        WriteLog(CvLogLevel.Warning, "control lost", ex);
        if (wasGrabbing) GrabbingChanged?.Invoke(this, false);
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

    public void GrabOne() => RunGrab(null, CancellationToken.None);

    /// <summary>한 장을 찍어 돌려준다 — <see cref="ICamGrabAsync"/>. 발행은 그대로 일어나므로
    /// <see cref="FrameAcquired"/> 구독자도 이 장을 받는다(같은 취득이다).
    ///
    /// <b>이 백엔드가 표식을 다는 이유</b>: 프레임이 취득 계층의 다른 스레드로 들어오므로 "이 그랩의 장" 과
    /// "때마침 온 장" 을 밖에서는 가릴 수 없다. 여기서는 가른다 — 찍기 전에 대기열을 비우고, 받은 장의
    /// 번호를 마지막으로 내보낸 번호와 <b>16비트 거리</b>로 대 본다(되돌이를 도는 번호라 크기 비교는
    /// 한 바퀴 뒤 프레임을 전부 옛것으로 기각한다).</summary>
    public Task<CamFrame?> GrabFrameAsync(TimeSpan timeout, CancellationToken ct = default)
        => Task.Run(() => RunGrab(timeout, ct), ct);

    /// <summary>단발 그랩 한 번 — <see cref="GrabOne"/> 과 <see cref="GrabFrameAsync"/> 의 공통 몸통.
    /// 가드·취소원 수명·끊긴 그랩의 사유 변환이 한 자리에 있어야 둘이 갈리지 않는다.</summary>
    /// <param name="timeout">null 이면 <see cref="GevCamOpt.GrabTimeoutMs"/> 를 쓴다.</param>
    /// <param name="ct">부른 쪽의 취소 — 이 그랩을 끊는다.</param>
    private CamFrame? RunGrab(TimeSpan? timeout, CancellationToken ct)
    {
        GevStream stream;
        CancellationTokenSource cts;
        lock (_sync)
        {
            ThrowIfDisposed();
            stream = EnsureOpen();
            // 연속 취득 중에는 답할 수 없다 — 흐르는 장과 이 호출의 답을 부른 쪽이 가릴 수 없다.
            // 조용히 돌아가면 그 자리에 자유 실행 프레임이 들어와 다른 대상을 판정한다.
            if (_pump != null)
                throw new InvalidOperationException(
                    "Continuous acquisition is running, so a single grab cannot tell its own frame from the " +
                    "stream's. Call StopContinuous() first.");
            // 그랩은 한 번에 하나다 — 둘이 같은 대기열을 다투면 어느 쪽이 어느 프레임을 받았는지 알 수 없다.
            if (_grabCts != null)
                throw new InvalidOperationException(
                    "A single grab is already waiting for its frame on this camera.");
            cts = _grabCts = new CancellationTokenSource();
            _grabAbortReason = null;
        }

        // 락을 놓고 기다린다. 쥔 채 기다리면 시한만큼 제어 상실 통지·닫기·재연결이 밀리고, 발행이 락
        // 아래에서 나가 구독자가 이 카메라를 되부르는 순간 서로를 붙잡는다 — 그랩의 시한은 프레임을 이미
        // 받은 뒤라 그것을 풀지 못한다. 락이 지키던 것은 위에서 확보한 상태뿐이고, 그 뒤 스트림이 닫히면
        // 취득 계층이 던져서 알린다.
        // 부른 쪽의 취소도 이 그랩을 끊는다 — 없으면 비동기로 불러 놓고 취소해도 시한을 다 채운다.
        using var linked = ct.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cts.Token, ct)
            : null;
        var grabToken = linked?.Token ?? cts.Token;
        try
        {
            return Run(token => GrabOnceAsync(stream, timeout, token), grabToken);
        }
        catch (Exception ex) when (cts.IsCancellationRequested)
        {
            // 우리가 끊은 그랩의 뒤끝은 하나로 접는다 — 취소·닫힌 스트림·버려진 장치 중 무엇에 걸려
            // 나왔든 원인은 끊은 쪽에 있고, 그 사유가 부른 쪽이 들어야 할 말이다.
            string reason;
            lock (_sync) reason = _grabAbortReason ?? "The single grab was cancelled while it waited for its frame.";
            throw new InvalidOperationException(reason, ex);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_grabCts, cts)) { _grabCts = null; _grabAbortReason = null; }
            }
            cts.Dispose();
        }
    }

    /// <summary><see cref="_sync"/> 보유 전제. 진행 중인 단발 그랩을 사유와 함께 끊는다 — 사유를 먼저 적고
    /// 끊어야 끊긴 쪽이 그것을 읽는다. 없으면 아무 일도 안 한다.</summary>
    private void AbortGrabWhileLocked(string reason)
    {
        if (_grabCts is null) return;
        _grabAbortReason = reason;
        try { _grabCts.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task<CamFrame?> GrabOnceAsync(GevStream stream, TimeSpan? timeout, CancellationToken ct)
    {
        var nodes = _nodes!;

        // 새로 찍기 전에 남은 것을 버린다 — 안 버리면 이 그랩이 옛 프레임을 가져간다.
        // ⚠ **이 줄은 청소가 아닐 수 있다 — 하중을 받는 자리로 의심하라.** 아래 번호 검사와 함께
        // "늦게 도착한 옛 장" 을 막는 두 겹 중 앞쪽이다. 성능을 이유로 걷어내면 뒤쪽 검사만 남는다.
        // 근거는 **다른 스택의 실측**이다(형제 저장소, 같은 모양의 비우기 + 짝짓기 검사 두 겹):
        // 시한이 만료된 취득이 살아 있다가(만료 직후 대기 1건) 다음 그랩의 비우기를 지나며 사라졌고,
        // 그래서 뒤쪽 검사가 걸린 횟수가 0 이었다.
        // ⚠ **이쪽에서 같은 몫을 하는지는 안 쟀다.** 구조가 닮았다는 것만으로 "여기서도 앞줄이 다 막는다"
        // 로 닫지 않는다 — 걷어낼 일이 생기면 그때 이 저장소에서 재고 판단한다.
        //
        // **언제 버릴 것이 생기는가 — 라이브를 켰다 끈 뒤의 그랩에서 생길 수 있다.** 티칭 화면의 기본
        // 조작이다(실측: 라이브 1.5초 × 2회로 42장을 받은 뒤 그랩하니 여기서 1장을 버렸다). 이 카메라에서는
        // 간헐적이다 — 같은 조작을 다시 돌리면 3회 중 1회꼴이었다(정지할 때의 드레인이 대개 먼저 치우고,
        // 그 뒤에 완성되는 낙오만 이 그랩 몫으로 남는다). 현장(다른 기종)에서는 두 번 연속 났다.
        // 여기서 드물다고 이 줄을 "대개 빈손인 청소" 로 읽으면 안 된다 — 0.26.1 은 이 자리에서 기준을 옛
        // 에포크 번호로 세웠고, 그래서 번호를 다시 세는 기종의 현장에서 **라이브를 켰다 끈 다음 촬영마다**
        // 5초 만료로 죽었다(소비자 실측, 배포 다음 날).
        // 지금은 **시계를 찍은 그랩(열 때 로그의 `grabKey=deviceClock`)에서는** 판정이 번호가 아니라
        // 시작선이라 그 길이 막혀 있다. ⚠ 시계를 못 찍으면(`grabKey=frameId`) 번호 판정으로 떨어지고, 이
        // 드레인이 기준을 버린 장의 번호로 세우므로(DrainStream) 0.26.1 의 길이 **그대로 열려
        // 있다** — 번호를 다시 세면서 시계도 안 주는 기종에서 드러난다(지금까지 본 두 기종은 둘 다 시계를 준다).
        // 버린 것이 있을 때만 남긴다(버릴 것이 없는 그랩에서는 안 난다) — 그리고 Debug 가 아니라 Info 다.
        // **이 줄이 "드레인이 낡은 장을 만났다" 는 유일한 증거**이기 때문이다. 없으면 그랩이 성공했을 때
        // "구멍을 밟고도 통과" 인지 "구멍을 안 밟은 통과" 인지 밖에서 가릴 수 없다 — 소비자가 현장 검증에서
        // 정확히 그 자리에 걸렸다(파일 로그 최소 레벨이 Info 라 이 줄이 안 남아, 통과를 추론으로 닫아야 했다).
        // ⚠ 증거는 한쪽 갈래뿐이다: 드레인 뒤에 완성돼 수신으로 곧장 온 낡은 장은 아래 시작선 검사(while)가
        // **로그 없이** 버린다. 그러니 이 줄이 없다고 해서 낡은 장을 안 만났다는 뜻은 아니다.
        if (DrainStream(stream, out var drainedUpTo) is var dropped and > 0)
            WriteLog(CvLogLevel.Info,
                $"discarded {dropped} queued frame(s) up to frame {drainedUpTo} before grabbing a fresh one");

        await TrySetEnumAsync(nodes, "AcquisitionMode", "SingleFrame", ct).ConfigureAwait(false);

        // **이 그랩의 시작선을 장치 시계로 찍어 둔다.** 이보다 이른 장은 이 그랩의 답이 아니다.
        // 시작 직전이어야 한다 — 뒤에 찍으면 우리 장까지 시작선보다 이르게 나온다.
        var startedAt = await LatchDeviceTimestampAsync(nodes, ct).ConfigureAwait(false);

        await TryExecuteAsync(nodes, "AcquisitionStart", ct).ConfigureAwait(false);

        // 호출이 준 시한이 설정값을 이긴다 — 호출 자리의 사정이 더 최신이다. 안 주면 설정값을 쓴다.
        // InfiniteTimeSpan 은 "내 시한을 걸지 말라" 는 뜻이라 수신 대기에 상한을 두지 않는다.
        var budgetMs = timeout is { } want
            ? (want == Timeout.InfiniteTimeSpan ? Timeout.Infinite : (int)Math.Max(1, want.TotalMilliseconds))
            : Math.Max(1, _gev.GrabTimeoutMs);

        // 수신은 반드시 자기 토큰으로 끊는다 — 밖에서 시한을 씌우면 버려진 대기자가 다음 프레임을
        // 삼키고 그 버퍼가 영영 풀로 돌아오지 않는다.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(budgetMs);

        GevFrame? frame = null;
        try
        {
            frame = await stream.ReceiveAsync(deadline.Token).ConfigureAwait(false);

            // 배수와 경합해 옛 프레임이 손에 들어올 수 있다 — 시작선보다 이른 장을 버린다.
            while (IsStale(frame, startedAt))
            {
                frame.Dispose();
                frame = await stream.ReceiveAsync(deadline.Token).ConfigureAwait(false);
            }

            return Emit(frame);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 시한 초과의 원인은 대개 카메라 상태다 — 열 때 남긴 'camera state' 줄을 함께 보게 한다.
            throw new TimeoutException(
                $"No frame within {budgetMs} ms. Check the 'camera state' line logged at open: " +
                "TriggerMode On means the camera waits for its trigger, and ChunkModeActive On means frames are dropped.");
        }
        finally
        {
            // 취소가 이겨도 프레임이 손에 들어올 수 있다 — 무엇이 오든 반납한다.
            frame?.Dispose();

            // 찍었으면 멈춘다. AcquisitionStart 는 표준상 AcquisitionMode 를 잠그고 그 잠금은
            // AcquisitionStop 까지 안 풀린다 — 안 멈추면 세션의 첫 그랩 뒤로 모드 쓰기가 전부 거절되어
            // 그랩마다 경고가 한 줄씩 쌓이고(현장 실측: 하루 258줄 = 그랩 260회 − 카메라별 최초 1회,
            // 검사 호스트 로그의 69%), 더 나쁘게는 그다음 StartContinuous 의 Continuous 전환까지 막힌다.
            // 그러면 장치는 SingleFrame 그대로라 한 장만 보내고, 수신 대기에는 시한이 없어 펌프가
            // 로그 한 줄 없이 영영 선다.
            // 취소 토큰을 쓰지 않는다 — 시한 초과나 중단으로 끊긴 그랩일수록 장치를 멈춰 두어야 한다.
            await TryExecuteAsync(nodes, "AcquisitionStop", CancellationToken.None).ConfigureAwait(false);

            // ⚠ **멈췄으면 번호 기준을 버린다.** 취득을 멈춘 뒤 다시 걸었을 때 장치가 프레임 번호를
            // 이어 준다는 보장이 없다 — **1부터 다시 세는 기종이 있다**(실측: Crevis MG-A320K-35 펌웨어
            // 3.6.2.9). 기준을 들고 가면 다음 그랩이 받은 장이 "옛것" 으로 판정돼 위 while 이 전부 버리고,
            // 오지 않을 새 번호를 기다리다 시한이 끝난다. 그 기종에서 30회 중 1회만 성공했다(첫 회만
            // 기준이 0 이라 필터가 꺼져 있다). 장치는 30장을 온전히 보냈고 스트림도 다 받았다
            // (GevCamHealth CompletedFrames=30, 손실 0) — 우리가 버린 것이다.
            ResetFrameIdBaseline();
        }
    }

    public void StartContinuous()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            var stream = EnsureOpen();
            if (_pump != null) return;
            // 단발 그랩이 대기열을 기다리는 동안 펌프를 세우면 둘이 같은 대기열을 다툰다.
            if (_grabCts != null)
                throw new InvalidOperationException(
                    "A single grab is waiting for its frame; continuous acquisition cannot start until it returns.");

            Run(async ct =>
            {
                // 모드 전환이 거절되면 그것을 여기서 말해야 한다 — 삼키면 장치는 옛 모드(대개 SingleFrame)
                // 그대로라 한 장만 보내고, 수신 대기에는 시한이 없어 펌프가 로그 한 줄 없이 영영 선다.
                // IsGrabbing 은 true 로 남고 프레임만 안 온다 — 밖에서 가를 단서가 아무것도 없는 상태다.
                if (!await TrySetEnumAsync(_nodes!, "AcquisitionMode", "Continuous", ct).ConfigureAwait(false))
                    WriteLog(CvLogLevel.Warning,
                        "the camera kept its previous acquisition mode, so continuous acquisition may deliver one " +
                        "frame and then wait forever. Close and reopen the camera to clear the acquisition lock.");
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
            {
                Run(ct => TryExecuteAsync(_nodes, "AcquisitionStop", ct), CancellationToken.None);
                // 단발 경로와 같은 이유 — 멈춘 뒤 번호가 이어진다는 보장이 없다. 펌프는 위에서 이미 섰다.
                ResetFrameIdBaseline();
            }

            // 멈춘 뒤에 대기열을 비운다 — 남겨 두면 다음 단발 그랩이 그것을 가져간다.
            // 펌프를 세운 다음이라야 배수가 수신과 겹치지 않는다 — 이 자리를 락 밖으로도, 정지 앞으로도 옮기지 않는다.
            if (stopped && _stream != null && DrainStream(_stream, out var drainedUpTo) is var left and > 0)
                WriteLog(CvLogLevel.Debug,
                    $"discarded {left} frame(s) left in the queue when acquisition stopped (up to frame {drainedUpTo})");
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

    /// <summary>수신 스레드의 바깥 테두리 — <b>취득 루프가 호스트를 데려가지 않는다.</b> 이 스레드는 우리가
    /// 만든 배경 스레드라 위에 아무도 없고, 미처리 예외 하나가 프로세스를 통째로 내린다. 안쪽에 단계별
    /// try 가 있어도 테두리가 필요하다 — 루프 뼈대와 <see cref="PumpEndedBySelf"/> 가 그 밖에 있고,
    /// 뒤엣것은 <b>호스트 핸들러로 들어가는 자리</b>다(구독자가 던지면 그 예외가 이 스레드로 돌아온다).</summary>
    private void PumpLoop(GevStream stream, CancellationToken ct)
    {
        try
        {
            PumpLoopCore(stream, ct);
        }
        catch (Exception ex)
        {
            WriteLog(CvLogLevel.Error, "receive pump stopped on an unhandled error.", ex);
            // 테두리로 떨어졌어도 정리는 해야 한다 — 안 하면 펌프 참조가 남아 IsGrabbing 이 true 로 굳고
            // 다음 StartContinuous 가 조용히 돌아간다(그 결함을 고치려고 둔 경로다).
            if (!ct.IsCancellationRequested) PumpEndedBySelf();
        }
    }

    private void PumpLoopCore(GevStream stream, CancellationToken ct)
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

        // 취소 없이 루프가 끝났다 = 수신이 우리 뜻과 무관하게 접혔다(스트림이 닫혔거나 수신이 실패).
        if (!ct.IsCancellationRequested) PumpEndedBySelf();
    }

    /// <summary>펌프가 스스로 끝났을 때의 뒷정리. 여기서 손을 놓으면 <see cref="_pump"/> 참조가 남아
    /// <see cref="IsGrabbing"/> 이 영영 true 로 굳고, 그다음 <see cref="StartContinuous"/> 는 "이미 도는 중"
    /// 으로 읽혀 <b>예외도 로그도 없이</b> 돌아간다 — 카메라는 다시 돌지 않는데 아무도 그 사실을 모른다.
    ///
    /// 카메라에 AcquisitionStop 을 보내지는 않는다 — 여기는 펌프 스레드이고 <see cref="_sync"/> 를 쥔 채
    /// 죽은 링크로 제어 명령을 던지면 그쪽 시한이 다 찰 때까지 <b>닫기까지 함께 막힌다</b>. 남은 정리는
    /// <see cref="Close"/> 가 한다.</summary>
    private void PumpEndedBySelf()
    {
        bool wasGrabbing;
        lock (_sync)
        {
            if (_disposed) return;
            // 정지 경로가 먼저 닿았으면 false 가 돌아온다 — 통지는 그쪽이 낸다.
            wasGrabbing = StopPumpCore();
            // _stoppedAtTicks 는 건드리지 않는다 — 그 표식은 "우리가 멈춰서 잘린 프레임" 을 가려내는
            // 것인데, 여기서는 아무도 멈추지 않았다. 지금부터 오는 드롭이야말로 무슨 일이 났는지
            // 말해 주는 줄이라, 정지 경계로 접어 Info 로 내려 버리면 안 된다.
        }
        if (!wasGrabbing) return;
        WriteLog(CvLogLevel.Warning,
            "the receive loop ended without a stop request — continuous grab is no longer running. " +
            "Check the connection and call StartContinuous again.");
        // 구독자 예외를 여기서 받는다 — 정리는 위에서 이미 끝났으므로, 핸들러 하나가 던졌다고 이 스레드가
        // 죽을 이유가 없다. 삼키지는 않는다: 남의 결함을 우리 로그에 사유까지 실어 남긴다.
        try { GrabbingChanged?.Invoke(this, false); }
        catch (Exception ex) { WriteLog(CvLogLevel.Error, "a GrabbingChanged subscriber threw.", ex); }
    }

    /// <summary>노출 시간(마이크로초) 적용. <b>언제 불러도 된다</b> — 아직 열지 않았으면 들고 있다가
    /// 열 때 적용하고, 그 값은 세션이 바뀌어도 남아 다음 열기에도 다시 들어간다(<see cref="CamOpt"/> 의
    /// 초기값보다 나중에 부른 이쪽이 우선이다). 버리면 부른 쪽은 값이 들어간 줄 알고, 정작 카메라는
    /// 예외도 로그도 없이 다른 노출로 돈다 — 밝기만 틀린 채 검사가 통과한다.
    /// 0 이하는 "건드리지 않는다" 는 뜻이라 기억하지도 않는다.</summary>
    public void SetExposureTimeUs(double timeUs)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            // 장치에 넣기 **전에** 적는다 — 기억하는 것은 요청 값이지 장치가 받아들인 값이 아니다(ICam 계약).
            // 순서를 뒤집어 "성공했을 때만" 적으면, 적용이 실패한 뒤 다시 열 때 낡은 성공 값이 그사이 갱신된
            // CamOpt 값을 이겨 "저장했는데 밝기가 안 바뀐다" 가 된다.
            if (timeUs > 0) _setExposureUs = timeUs;
            if (_nodes is not { } nodes) return;   // 아직 안 열림 — 열 때 위에 적어 둔 값으로 적용된다
            Run(ct => ApplyExposureAsync(nodes, timeUs, ct));
        }
    }

    // === 프레임 변환 ===

    /// <summary>취득 프레임을 <see cref="CamFrame"/> 으로 옮겨 발행한다. 프레임 버퍼는 호출이 끝나면
    /// 반납되므로 반드시 이 안에서 복사를 마친다. <b>발행한 장을 돌려준다</b> — 단발 그랩이 그것을
    /// 부른 쪽에 넘긴다(변환에 실패했으면 null, 그때도 번호 기준은 갱신된 뒤다).</summary>
    private CamFrame? Emit(GevFrame frame)
    {
        // 무엇을 내보냈는지 여기 한 자리에서 기억한다 — 단발 그랩의 "새 프레임" 판정 기준이다.
        // 변환에 실패해 발행하지 못한 것도 이미 지나간 프레임이므로 기준에 넣는다.
        // 기준이 0 이면 아직 아무것도 안 지나갔다 — 첫 번호가 무엇이든 그대로 기준이 된다.
        if (_lastEmittedFrameId == 0 || IsNewerFrameId(frame.FrameId, _lastEmittedFrameId, frame.IsExtendedId))
        {
            // 번호가 건너뛰었으면 카메라가 보낸 것이 여기까지 오지 못한 것이다.
            // 되돌이가 있는 쪽은 간격도 16비트 안에서 센다 — 되돌이를 걸친 간격을 크기로 빼면
            // 65,000 장이 한꺼번에 사라진 것처럼 보인다. 확장 번호는 되돌지 않으니 그냥 뺀다.
            var gap = frame.IsExtendedId
                ? frame.FrameId - _lastEmittedFrameId
                : (frame.FrameId - _lastEmittedFrameId) & 0xFFFF;
            if (_lastEmittedFrameId != 0 && gap > 1)
                _neverArrivedFrames += (long)(gap - 1);
            _lastEmittedFrameId = frame.FrameId;
        }

        var cam = Convert(frame);
        if (cam is null) return null;
        var ready = ApplyMountXform(cam);
        // 발행은 변환과 갈라서 감싼다. 안 가르면 구독자가 던진 것이 부르는 쪽 catch 에서 "frame conversion
        // failed" 로 적힌다 — 우리 변환은 멀쩡한데 남의 핸들러가 원인이라는 사실이 로그에서 지워진다.
        // 그리고 구독자 예외가 카메라가 하는 일을 바꾸지 않는다(ICam 계약).
        try { FrameAcquired?.Invoke(this, ready); }
        catch (Exception ex) { WriteLog(CvLogLevel.Error, "a FrameAcquired subscriber threw.", ex); }
        return ready;
    }

    /// <summary>장착 방향 보정(<see cref="CamOpt.Flip"/>·<see cref="CamOpt.Rotation"/>) — 취득 계약이
    /// "발행되는 프레임에는 이미 적용돼 있다" 이므로 여기서 접는다.
    ///
    /// 이 백엔드만 그 계약을 안 지키고 있었다 — USB·파일 소스와 가상 카메라는 <see cref="CamXform"/> 를
    /// 부르는데 GigE 만 안 불렀다. 그래서 같은 레시피로 백엔드를 갈아 끼우면 <b>화면 방향이 말없이 달라졌다</b>
    /// (오류도 로그도 없이). 계약이 셋 중 둘에서만 참인 것은 계약이 아니다.
    ///
    /// 둘 다 None(기본)이면 프레임을 그대로 통과시킨다 — 그 경우 비용이 0 이라 안 쓰는 설비는 아무것도 달라지지 않는다.
    /// 장치 시각은 넘겨 보존한다(펌프 통계가 그 값을 쓴다).
    /// <c>internal</c> 인 것은 회귀가 장치 없이 이 계약을 부를 수 있게 하려는 것이다 — 발행 경로 전체를
    /// 돌리려면 카메라가 있어야 하는데, 그러면 이 계약은 실기에서만 지켜지는지 알 수 있다.</summary>
    internal CamFrame ApplyMountXform(CamFrame cam)
    {
        if (_opt.Flip == FlipMode.None && _opt.Rotation == RotateMode.None) return cam;

        using var view = cam.AsMat();
        var processed = CamXform.Apply(view, _opt.Flip, _opt.Rotation);
        try
        {
            return CamFrame.FromMat(processed, cam.DeviceTimestamp);
        }
        finally
        {
            if (!ReferenceEquals(processed, view)) processed.Dispose();
        }
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
    /// <remarks>비우는 절차는 취득 라이브러리 것을 쓴다 — 버린 장수와 <b>마지막으로 버린 프레임 번호</b>를
    /// 함께 준다. 손으로 하나씩 꺼내면 프레임마다 Dispose 를 지켜야 버퍼가 풀로 돌아오는데, 그 하나를
    /// 빠뜨리면 비우려던 것이 오히려 취득을 굶긴다. 이미 접힌 대기열에서는 꺼내기가 예외까지 던진다.
    ///
    /// 버린 프레임의 번호는 <b>기준으로 삼는다</b> — 안 옮기면 다음 프레임이 "카메라가 보냈는데 안 온 것"
    /// 으로 잡혀 우리가 만든 배수가 스스로 거짓 경보를 낸다(<see cref="GevCamHealth"/> 의 미도착 계수).
    ///
    /// <b>조건은 버린 장수에 건다. 번호의 크기에 걸면 안 된다.</b> 장치 번호는 우리가 세는 카운터가 아니라
    /// 전송 블록 번호 그대로라, 16비트면 65535 에서 한 바퀴 돈다 — 연속 취득 여덟 시간이면 실제로 돈다.
    /// 되돌이를 걸쳐 버렸을 때 "더 큰 값일 때만" 올리면 기준이 되돌이 앞에 멈추고, 그 뒤 오는 프레임이
    /// 전부 옛것으로 기각되어 단발 그랩이 시한을 넘긴다. 버린 것이 없을 때 번호가 0 으로 오는 것은 장수가
    /// 0 이라 딸려 오는 값이므로, 장수로 거르면 그 0 이 기준을 지우는 일도 없다.
    ///
    /// 여기서 비워지는 것은 <b>대기열에 든 완성 프레임뿐이다.</b> 조립 중이던 것은 그대로 남아 이 호출 뒤에
    /// 완성되고, 이미 기다리고 있는 수신자에게는 대기열을 거치지 않고 바로 건네진다 — 그 갈래는 단발
    /// 그랩의 번호 가드가 막는다.</remarks>
    /// <param name="stream">비울 스트림.</param>
    /// <param name="lastDiscardedFrameId">버린 것 가운데 마지막 프레임의 장치 번호. 버린 것이 없으면 0.</param>
    private int DrainStream(GevStream stream, out ulong lastDiscardedFrameId)
    {
        var dropped = stream.DiscardQueuedFrames(out lastDiscardedFrameId);
        if (dropped > 0) _lastEmittedFrameId = lastDiscardedFrameId;
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
        var written = timeUs;   // 실제로 장치에 쓴 값 — 격자에 맞춰 바뀌었을 수 있다
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
                written = snapped;
                // 경고가 아니라 보고다 — **피할 수 없는 양자화**를 경고로 내면 여는 길마다 뜨고,
                // 그러면 진짜 경고가 안 읽힌다. 조작자가 할 수 있는 일은 격자 값을 적어 넣는 것뿐이라
                // 안내는 남기되 등급만 내린다.
                //
                // 실측 — **Basler acA2500-14gm(격자 anchor 35 · step 35)**: 50→35 · 70→70 · 100→105 ·
                // 200→210 · 1000→1015 · 5000→5005 · 12000→12005. 그 격자에서 벗어난 설정은 **어느 것이든**
                // 이 길을 지난다 — 평범한 현장 값이 전부 걸린다.
                // 짧은 쪽도 함께 잰 이유: 격자 한 칸이 요청값에 비해 커지는 구간이라, **비율**로 판정하는
                // 구현은 바로 여기서 거짓 경보를 낸다(50→35 는 최근접 격자점인데 오차가 30% 다).
                // 재는 점을 늘리는 것과 재는 구간을 넓히는 것은 다른 일이다.
                //
                // ⚠ **거절하는 것은 장치가 아니라 이 스택이다.** 카메라 XML 이 노출 사슬 안의 `<Integer>`
                // 노드에 선언한 `Inc`(또는 pInc)를 GenApi 검증이 **쓰기 전에** 강제해 던지는 것이고, 장치는 그
                // 값을 본 적도 없다(`IntegerNodeBase.ValidateAsync` — `inc > 1` 일 때만 격자를 본다. 실수 노드는
                // 격자 검사를 아예 안 한다). 그러니 **거절되느냐**는 장치의 성질이 아니라 "XML 이 1보다 큰 Inc 를
                // 선언했는가" 다. 같은 개체를 다른 취득 스택으로 다루면 같은 선언을 **조용한 스냅**으로
                // 강제하기도 한다 — 거절이냐 스냅이냐는 **스택이 정한다**.
                //
                // ⚠ 그 선언을 낼 수 있는 것은 **`<Integer>` 노드뿐**이다(Inc·pInc 를 가진다). 노출 사슬이
                // `Float→Converter→IntReg` 로 바로 끝나면 검증은 돌지만 **레지스터 노드는 언제나 증분 1 을
                // 답하므로**(`IntRegNode.GetIncAsync`) 그냥 통과한다 — 소비자 기종이 그 모양이다. 격자가
                // 나오려면 사이에 `<Integer>` 가 끼어 있어야 한다. 그래서 "검사가 안 걸린다" 는 사슬 모양의
                // 문제지 장치가 무엇을 받아들이는가의 답이 아니다.
                //
                // 이 개체는 그 `<Integer>` 의 Inc 가 리터럴이 아니라 **pInc 로 다른 노드를 가리키고**, 그
                // 대상을 따라가면 **장치 포트에 정적 주소를 가진 읽기 전용 레지스터**다(소비자 실측: 이 한 대).
                // 즉 격자 선언 자체가 **장치가 그때 답한 값**이지 문서에 박힌 상수가 아니다.
                // ⚠ 기종 일반이 아니다 — pInc 는 계산 노드나 상수도 가리킬 수 있다. 격자의 출처를 근거로
                // 무엇을 판단하려면 **그 장치에서 pInc 대상을 따라가 종류부터 본다.**
                //
                // 그래서 다른 기종에서 이 줄이 0 건인 것은 "격자가 없다" 가 아니라 **"검증이 통과시켰다"**
                // 는 뜻일 뿐이다(소비자 관측: Crevis MG-A320K-35 펌웨어 3.6.2.9 에 30000us, 하루 260그랩 —
                // 이 줄 0건. 같은 장치가 1us·30011us 도 거절 없이 받아 그대로 되읽혔다. 그쪽 실제 눈금은
                // 아무도 모른다 — 우리 검사가 안 걸린다는 것과 장치가 임의 값을 그대로 쓴다는 것은 다르다).
                // 기종을 늘려 재기 전까지 위 숫자는 **이 한 대의 것**이다 — 말 그대로 한 대다.
                // 이 격자를 읽은 관측이 셋인데(GevSharp 직결 · Cognex VisionPro 경유 · 다른 호스트의
                // GevCam 로그, 요청 10000→적용 10010) **전부 같은 개체**다(SN 24426379). 그래서 이 격자 값이
                // **스택의 해석이 아니라 장치의 성질**이라는 데까지는 서로 다른 세 경로가 받쳐 주지만,
                // **기종 일반화에는 한 고리도 보태지 못한다.** 관측 수가 느는 것과 표본이 느는 것은 다르다.
                WriteLog(CvLogLevel.Info,
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
        //
        // **이 되읽기가 우리가 쓴 값의 메아리일 수 있는가**(그러면 이 검사는 항상 통과하는 죽은 검사기다).
        // 조건은 **둘 다**여야 한다 — ① 캐시가 도는 레지스터일 것: `Cachable != NoCache` 이고 폴링 주기가
        // 없을 것(GevSharp `RegisterCore.IsCacheable`. 폴링이 붙은 레지스터는 장치가 스스로 값을 바꾸는
        // 것이라 캐시 경로를 통째로 끈다 — 주기로 만료되는 게 아니다) ② 쓰기가 캐시를 채울 것:
        // `Cachable == WriteThrough`(`WriteAround` 는 쓰기 뒤 캐시를 버린다 — `RegisterCore.WriteAsync`).
        // "쓸 수 있을 것" 은 여기서는 늘 참이다 — 되읽는 노드가 방금 쓴 노드다. 그 절은 메아리를 **재현할
        // 노드를 고를 때** 거르는 기준이다(읽기 전용 노드에는 우리가 쓴 값이 애초에 캐시에 안 들어간다).
        //
        // ⚠ 쓰지 않아도 캐시는 돈다 — **읽기도 캐시를 채운다.** ①만 맞으면 첫 읽기가 캐시에 남아 **장치가
        // 바꾼 값을 못 본다**(그래서 래치 읽기는 읽기 전에 `Invalidate()` 를 부른다). 두 갈래를 섞지 않는다:
        // ①+②는 **우리가 쓴 값이 돌아오는** 메아리의 조건이고, ①만으로는 **낡은 값이 돌아오는** 것의 조건이다.
        // 지금까지 본 두 대는 둘 다 ①에서 빠진다: Basler acA2500-14gm 은 `NoCache` 에 `PollingTime=5000` 까지
        // (직접 실측), Crevis MG-A320K-35 는 `WriteThrough` 지만 `PollingTimeMs=1000`(소비자 관측 한 대). 둘 다
        // 되읽기가 진짜 장치 값이다. 그래서 `Invalidate()` 를 넣지 않았다.
        //
        // ⚠ 그러므로 **되읽기가 요청값과 같다고 해서 장치가 그 노출로 찍는다는 뜻은 아니다.** Crevis 는
        // 1us 와 30011us 를 거절도 않고 그대로 되돌려 주었다(소비자 관측) — 그 장치의 격자는 아직 모른다.
        // 세 번째 조합(`WriteThrough` + 폴링 없음)이 실재하면 그때는 이 검사가 죽으므로, 그 기종이
        // 나오면 여기에 무효화를 넣고 그 장비로 검증해야 한다.
        //
        // 비교 대상은 요청값이 아니라 **우리가 쓴 값**이다. 격자에 맞춰 바꿔 쓴 것은 위에서 이미
        // 보고했으므로 여기서 또 말하면 같은 사실이 두 줄이 된다. 그리고 등급이 여기서 갈린다 —
        // 맞춰 쓴 값과 다르면 그건 양자화가 아니라 **장치가 말없이 다른 값을 들고 있는 것**이고,
        // 그 경우에만 경고다. 임계값을 지어내지 않는다: 기준은 우리가 쓴 값 그 자체다.
        try
        {
            var actual = await node.GetAsync(ct).ConfigureAwait(false);
            if (Math.Abs(actual - written) <= 0.5) return;
            var snapped = Math.Abs(written - timeUs) > 0.5;
            WriteLog(snapped ? CvLogLevel.Info : CvLogLevel.Warning,
                $"exposure requested {timeUs}us, wrote {written}us, camera applied {actual}us via '{node.Name}'" +
                (snapped ? "" : " — the camera took a different value without refusing the write."));
        }
        catch (GenApiException) { /* 되읽기 실패는 진단 손실일 뿐이라 넘어간다 */ }
    }

    /// <summary>
    /// 이 그랩의 <b>시작선</b>을 장치 시계로 찍는다 — 못 찍으면 <c>null</c>.
    ///
    /// 쓰는 것은 GigE Vision <b>표준 부트스트랩</b> 레지스터다(벤더 확장이 아니다): 래치 명령이 그 순간의
    /// 장치 시각을 레지스터에 옮기고, 그것을 읽는다. 값은 <b>반드시 캐시를 버리고</b> 읽는다 — 래치는
    /// 장치가 값을 바꾸는 일이라, 안 버리면 앞서 읽은 값이 그대로 돌아온다.
    ///
    /// <b>왜 번호가 아니라 시각인가.</b> 프레임 번호는 <b>에포크를 우리가 못 정한다</b> — 기종마다 언제
    /// 1로 돌아가는지가 다르다(실측: Basler acA2500-14gm 은 스트림을 열 때, Crevis MG-A320K-35 펌웨어
    /// 3.6.2.9 는 <b>취득을 멈출 때마다</b>). 뒤엣것에서는 옛 장과 새 장이 <b>둘 다 번호 1</b> 로 와서
    /// 번호로는 원리적으로 못 가린다(소비자 실측: 옛 1·2·3·4 뒤에 새 1). 시각은 우리가 직접 찍으므로
    /// 에포크를 우리가 정한다 — 그래서 기종을 안 탄다.
    ///
    /// 실측으로 받쳐 둔 전제 둘(양쪽 기종에서 확인): ① 프레임 헤더의 시각과 이 래치가 <b>같은 시계</b>다
    /// (래치 → 그랩 → 래치 했을 때 프레임 시각이 두 래치 사이에 들어온다) ② 하루 단위로 도는 값이 아니다
    /// (Basler 125MHz 에서 13.6일치, Crevis 66.7MHz 에서 하루치를 넘는 값이 누적돼 있었다).
    ///
    /// ⚠ 이 시계도 <c>GevTimestampControlReset</c> 으로 되돌릴 수 있다. 다른 응용이 그것을 부르면 판정이
    /// 어긋나는데, 방향이 <b>거짓 기각</b>(시한 만료로 시끄럽게 실패)이라 남의 장을 답으로 내주는 것보다 낫다.
    /// </summary>
    private async Task<ulong?> LatchDeviceTimestampAsync(GenApiNodeMap nodes, CancellationToken ct)
    {
        if (nodes.GetNode("GevTimestampControlLatch") is not ICommand latch) return null;
        if (nodes.GetNode("GevTimestampValue") is not IInteger value) return null;
        try
        {
            await latch.ExecuteAsync(ct).ConfigureAwait(false);
            value.Invalidate();
            var now = (ulong)await value.GetAsync(ct).ConfigureAwait(false);
            // 0 은 "시계를 안 준다" 는 뜻으로 읽는다 — 그 값으로는 아무것도 못 가른다.
            return now == 0 ? null : now;
        }
        catch (GenApiException)
        {
            // 진단이 그랩을 깨뜨리지 않는다 — 못 찍으면 번호 쪽으로 떨어진다.
            return null;
        }
    }

    /// <summary>이 그랩의 답이 아닌 장인가.
    ///
    /// 시작선을 찍었으면 <b>그보다 이른 장</b>이 낡은 것이다 — 번호를 보지 않는다.
    /// 못 찍었으면(시계를 안 주는 장치) 종전대로 번호로 가린다: 마지막으로 내보낸 것보다 새것이 아니면 낡았다.
    /// 번호는 되돌이를 도므로 크기가 아니라 16비트 안의 거리로 본다. 기준이 0 이면 아직 아무것도 지나가지
    /// 않은 것이라 무엇이든 받는다.</summary>
    private bool IsStale(GevFrame frame, ulong? startedAt)
    {
        if (startedAt is { } since) return frame.Timestamp != 0 && frame.Timestamp <= since;
        return frame.FrameId != 0 && _lastEmittedFrameId != 0
               && !IsNewerFrameId(frame.FrameId, _lastEmittedFrameId, frame.IsExtendedId);
    }

    /// <summary>프레임 번호 기준을 버린다 — <b>취득을 멈춘 자리에서 부른다.</b>
    ///
    /// 이 기준은 "받은 장이 지난번보다 새것인가" 를 재는 자 인데, 그 비교는 <b>번호가 이어질 때만</b>
    /// 뜻이 있다. 취득을 멈췄다 다시 걸면 이어 주는 기종도 있고 <b>1부터 다시 세는 기종도 있다</b> —
    /// 뒤엣것에서 기준을 들고 가면 멀쩡한 새 장이 전부 "옛것" 으로 기각된다. 멈춤은 그 자를 못 믿게
    /// 만드는 사건이므로, 자를 버리고 다음 장을 무엇이든 받는다(첫 장을 그렇게 받는 것과 같은 자리다).
    ///
    /// 유실 계수(<c>_neverArrivedFrames</c>)도 함께 끊는다 — 멈춤을 걸친 번호 간격은 잃은 것이 아니다.
    ///
    /// ⚠ <b>기종에 따라 갈리므로, 한 대에서 멀쩡하다고 이 자리를 지우지 마라.</b> 실측으로 양쪽을 다 봤다:
    /// Crevis MG-A320K-35(펌웨어 3.6.2.9)는 멈출 때마다 1부터 다시 세어 단발 그랩이 30회 중 1회만 성공했고,
    /// Basler acA2500-14gm 은 <b>이어 준다</b> — 그랩마다 멈추며 4회 돌려 번호가 1·2·3·4 였고, 멈추지 않는
    /// 기준선이 그 뒤를 5·6·7·8 로 이어받았다(계수기가 실제로 도는 것도 그 기준선이 보인다).
    /// 그래서 <b>Basler 로는 이 결함이 원리적으로 안 드러난다</b> — 그 기종에서 번호가 1로 돌아가는 것은
    /// 스트림 세션을 열 때뿐이고, 거기서는 <see cref="Open"/> 이 스트림을 세우며 이미 기준을 0 으로 되돌린다.</summary>
    private void ResetFrameIdBaseline()
    {
        _lastEmittedFrameId = 0;
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

        // 단발 그랩이 "이 장이 내 것인가" 를 무엇으로 가리는지 한 번 남긴다 — 그랩마다가 아니라 여기서
        // 한 번이다. 이 한 줄이 있어야 나중에 "그 판정이 어느 길로 갔는가" 를 물을 수 있다.
        // deviceClock 이면 우리가 찍은 시작선으로 가리고(기종 무관), frameId 면 시계를 못 얻어
        // 번호로 떨어진 것이다(번호는 기종마다 리셋 시점이 달라 약하다 — ResetFrameIdBaseline 참조).
        var grabKey = await LatchDeviceTimestampAsync(nodes, ct).ConfigureAwait(false) is not null
            ? "deviceClock"
            : "frameId";

        WriteLog(CvLogLevel.Info,
            $"camera state: pixelFormat={pixel ?? "?"} size={w?.ToString() ?? "?"}x{h?.ToString() ?? "?"} " +
            $"payloadSize={payload?.ToString() ?? "?"} acquisitionMode={acq ?? "?"} " +
            $"triggerMode={trigMode ?? "(absent)"} triggerSource={trigSrc ?? "(absent)"} " +
            $"grabKey={grabKey}");

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

        // 조용히 돌아가면 계산이 아예 돌지 않았다는 사실이 아무 데도 남지 않는다 — 그러면 "어긋남 경고가
        // 없다" 가 "패턴이 맞았다" 로 읽힌다. 갈래마다 왜 못 돌았는지 남기되, 정상 구성이 지나는 갈래에는
        // 넣지 않는다. 상시 발화하는 경고는 진짜 경고까지 안 읽히게 만든다.
        if (code == 0)
        {
            // 흑백 카메라도 지나는 자리다 — 색을 걸어 경고하면 거짓 경보가 된다.
            WriteLog(CvLogLevel.Info,
                "PixelFormat could not be read, so the Bayer phase check did not run for this session. " +
                "Frame conversion is unaffected — it uses the format each frame declares — but a mirror or an odd " +
                "ROI offset would swap red and blue with nothing logged. If this is a colour camera and the colours " +
                "look wrong, pin the pattern with BayerPatternOverride.");
            return null;
        }

        // 코드 0 을 먼저 걸러야 한다 — 0 도 "표에 없는 코드" 라 이 검사에 먼저 걸리면 문구가 틀린다.
        if (!PixelFormatInfo.IsKnown(code))
        {
            WriteLog(CvLogLevel.Warning,
                $"PixelFormat 0x{code:X8} is not a format this package can convert, so every frame in it is " +
                "dropped and the Bayer phase check cannot run. Set the camera to a supported format " +
                "(Mono 8/10/12/16 including the packed ones, Bayer 8/10/12/16, RGB8, BGR8, RGBa8, BGRa8).");
            return null;
        }

        // 흑백·RGB 카메라의 정상 경로 — 남길 것이 없다. 화소 포맷은 "camera state" 줄에 이미 있다.
        if (!PixelFormatInfo.IsBayer(code)) return null;

        if (ToCvPattern(PixelFormatInfo.BayerPattern(code)) is not { } declared)
        {
            // 지금의 화소 포맷 표에서는 나올 수 없는 갈래다 — Bayer 포맷 전부가 시작 패턴을 들고 있다.
            // 그래도 남긴다: 표가 늘어 이 자리가 열리면 프레임마다 변환이 터지는데 알릴 곳이 여기뿐이다.
            WriteLog(CvLogLevel.Warning,
                $"{PixelFormatInfo.Name(code)} is a Bayer format whose start pattern this package cannot name, " +
                "so the phase check cannot run and conversion has no pattern to demosaic with — every frame will " +
                "fail. Pin the pattern with BayerPatternOverride to keep acquisition working.");
            return null;
        }

        // 없는 노드는 중립값으로 — ReverseY 를 아예 선언하지 않는 카메라가 흔하다.
        // 다만 "노드가 없다" 와 "노드는 있는데 값을 못 읽었다" 는 갈라야 한다. 둘 다 null 로 오지만,
        // 뒤쪽은 미러가 켜져 있어도 꺼진 것으로 계산되어 위상이 어긋난 채 아무 줄도 남지 않는다.
        var revXRead = await TryReadFlagAsync(nodes, "ReverseX", ct).ConfigureAwait(false);
        var revYRead = await TryReadFlagAsync(nodes, "ReverseY", ct).ConfigureAwait(false);
        NoteUnreadMirror(nodes, "ReverseX", revXRead);
        NoteUnreadMirror(nodes, "ReverseY", revYRead);
        var revX = revXRead ?? false;
        var revY = revYRead ?? false;
        var offX = (int)(await TryReadIntAsync(nodes, "OffsetX", ct).ConfigureAwait(false) ?? 0);
        var offY = (int)(await TryReadIntAsync(nodes, "OffsetY", ct).ConfigureAwait(false) ?? 0);

        // 미러의 기준 치수 — 센서 전체를 뒤집고 ROI 를 떼는 장치면 최대 치수, ROI 안에서 뒤집으면 ROI 치수다.
        // 어느 쪽인지 알 수 없으므로 최대 치수를 먼저 쓰고, 없으면 ROI 치수로 떨어진다.
        var maxW = (int)(await TryReadIntAsync(nodes, "WidthMax", ct).ConfigureAwait(false)
                         ?? await TryReadIntAsync(nodes, "Width", ct).ConfigureAwait(false) ?? 0);
        var maxH = (int)(await TryReadIntAsync(nodes, "HeightMax", ct).ConfigureAwait(false)
                         ?? await TryReadIntAsync(nodes, "Height", ct).ConfigureAwait(false) ?? 0);
        if (maxW <= 0 || maxH <= 0)
        {
            // 여기까지 왔으면 Bayer 카메라가 확실하다 — 기준 치수를 못 읽어 검사가 멈추면 색 뒤바뀜이
            // 그대로 지나간다. 흑백은 위에서 이미 빠졌으므로 이 줄이 정상 구성에서 나오지 않는다.
            WriteLog(CvLogLevel.Warning,
                $"the Bayer phase check did not run for this {PixelFormatInfo.Name(code)} camera: neither " +
                $"WidthMax/HeightMax nor Width/Height could be read (got {maxW}x{maxH}), and the mirror reference " +
                "size is what decides the phase. A mirror or an odd ROI offset would swap red and blue with " +
                "nothing logged — check that the camera exposes Width/Height, and pin the pattern with " +
                "BayerPatternOverride if the colours are wrong.");
            return null;
        }

        var computed = CvBayerPhase.Effective(declared, maxW, maxH, revX, revY, offX, offY);
        if (computed == declared)
        {
            // 이 한 줄이 있어야 "경고가 없다" 를 "계산이 돌았고 맞았다" 로 읽을 수 있다 — 없으면 계산이
            // 못 돈 경우와 구분되지 않는다. 여는 때 한 번뿐이라 흐름을 어지럽히지 않는다.
            WriteLog(CvLogLevel.Info,
                $"Bayer phase checked: the reported {declared} matches the geometry " +
                $"(ReverseX={revX} ReverseY={revY} OffsetX={offX} OffsetY={offY} max={maxW}x{maxH}).");
            return null;   // 선언값을 그대로 쓴다
        }

        WriteLog(CvLogLevel.Warning,
            $"Bayer phase disagreement: the camera reports {declared} but geometry implies {computed} " +
            $"(ReverseX={revX} ReverseY={revY} OffsetX={offX} OffsetY={offY} max={maxW}x{maxH}). " +
            $"Using the reported pattern — if colours look wrong, pin it with BayerPatternOverride.");
        return null;
    }

    /// <summary>
    /// 미러 플래그를 <b>못 읽은 것</b>을 남긴다. 노드를 아예 선언하지 않은 카메라는 흔하고 그건 정상이라
    /// 조용히 넘긴다 — 선언이 없으면 켜져 있을 수도 없다. 그러나 <b>노드는 있는데 값을 못 읽은 것</b>은
    /// 다르다: 위상 계산이 꺼진 것으로 단정해 실효 패턴을 잘못 잡고, 그 결과가 "어긋남 없음" 이라 아무 줄도
    /// 남지 않는다. 못 읽는 사유는 셋이다 — 다루지 않는 노드 종류, 알아볼 수 없는 낱말, 읽기 실패.
    /// 종류를 문구에 실어 가리게 한다.
    ///
    /// 노드 유무만 본다. 접근 모드까지 캐물으면 더 정확하지만 그 조회는 장치 거절·시한 초과를 그대로
    /// 올려보내 <b>진단이 여는 과정을 깨뜨릴 수 있다</b>. 여기 있는 것은 맵 조회 하나뿐이라 던지지 않는다.
    ///
    /// 수준을 Info 로 두는 것은 <b>선언만 해 두고 구현하지 않은 카메라</b>에서 이 줄이 잘못 나올 수 있기
    /// 때문이다. 정상 장비에서 발화하는 경고를 만드는 것이 이 저장소가 이미 치른 대가라, 확인을 청할 뿐
    /// 조치를 지시하지 않는다.
    /// </summary>
    private void NoteUnreadMirror(GenApiNodeMap nodes, string name, bool? read)
    {
        if (read is not null) return;
        if (nodes.GetNode(name) is not { } node) return;   // 선언 자체가 없다 — 정상이다

        WriteLog(CvLogLevel.Info,
            $"{name} is declared on this camera ({node.Kind}) but its value could not be read, so the Bayer phase " +
            "check assumed the mirror is off. If it is actually on, the transmitted pattern differs from the " +
            $"reported one and nothing else will say so. Read {name} with the gevprobe sample; if red and blue are " +
            "swapped, pin the pattern with BayerPatternOverride.");
    }

    /// <summary>
    /// <paramref name="id"/> 가 <paramref name="newest"/> 보다 <b>새 프레임</b>인가.
    ///
    /// 장치 번호는 우리가 세는 카운터가 아니라 전송 블록 번호 그대로다. 16비트면 65535 에서 한 바퀴 돌아
    /// <b>새 프레임의 번호가 작아진다</b> — 그때 크기로 비교하면 새것이 전부 옛것으로 보여, 단발 그랩이
    /// 오는 프레임을 모두 기각하고 시한을 넘긴다. 카메라를 다시 열기 전까지 풀리지 않는다.
    /// 연속 취득 여덟 시간이면 실제로 두 바퀴 돈다.
    ///
    /// 그래서 되돌이가 있는 쪽은 크기가 아니라 <b>16비트 안의 거리</b>로 본다: 뒤로 간 거리가 반 바퀴를
    /// 넘으면 앞으로 간 것으로 읽는다. 취득 계층의 수신부가 쓰는 것과 같은 식이다 — 두 층이 다른 식을
    /// 쓰면 같은 프레임을 두고 판정이 갈린다. 장치가 촬영을 다시 시작해 번호를 처음부터 세는 경우도 이
    /// 식이 자연스럽게 새것으로 본다.
    ///
    /// <b>확장 번호를 쓰는 장치는 되돌이가 없으므로 그냥 크기로 본다.</b> 거리 식을 그대로 쓰면 간격이
    /// 반 바퀴를 넘는 순간 옛것을 새것으로 읽는다 — 폭을 프레임이 알려 주니 식을 가른다.
    /// </summary>
    /// <param name="id">판정할 프레임의 장치 번호.</param>
    /// <param name="newest">지금까지 지나간 것 중 가장 새 번호.</param>
    /// <param name="extendedIds">그 번호가 64비트 확장 블록 ID 인가 — 프레임이 스스로 밝힌다.</param>
    internal static bool IsNewerFrameId(ulong id, ulong newest, bool extendedIds)
        => extendedIds
            ? id > newest
            : id != newest && ((newest - id) & 0xFFFF) >= 0x8000;

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
        if (granted >= requested)
        {
            WriteLog(CvLogLevel.Info, $"socket receive buffer {granted} bytes (requested {requested})");
            return;
        }

        WriteLog(CvLogLevel.Warning,
            $"the OS granted a socket receive buffer of {granted} bytes out of the {requested} requested. " +
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

    /// <returns>장치가 우리가 바라는 값으로 돈다고 볼 수 있으면 <c>true</c>. <b>쓰기를 시도했는데 거절당한
    /// 경우에만 <c>false</c></b> — 노드나 항목이 없는 장치는 "그 개념이 없다" 는 뜻이라 참으로 둔다.
    /// 부르는 쪽이 이 값을 봐야 하는 이유는, 거절을 삼키면 <b>장치가 옛 모드 그대로 도는데 우리는 바꾼 줄
    /// 아는</b> 상태가 되기 때문이다.</returns>
    private async Task<bool> TrySetEnumAsync(GenApiNodeMap nodes, string name, string symbolic, CancellationToken ct)
    {
        if (nodes.GetNode(name) is not IEnumeration e)
        {
            WriteLog(CvLogLevel.Info, $"camera has no {name} node — leaving acquisition mode as the camera has it");
            return true;
        }
        if (e.GetEntry(symbolic) is null)
        {
            WriteLog(CvLogLevel.Info, $"{name} has no '{symbolic}' entry — leaving it as the camera has it");
            return true;
        }
        try { await e.SetAsync(symbolic, ct).ConfigureAwait(false); return true; }
        catch (GenApiException ex) { WriteLog(CvLogLevel.Warning, $"failed to set {name}={symbolic}", ex); return false; }
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

    private static T Run<T>(Func<CancellationToken, Task<T>> body, CancellationToken ct = default)
        => Task.Run(() => body(ct), ct).GetAwaiter().GetResult();

    private static async Task SwallowAsync(Func<Task> body)
    {
        try { await body().ConfigureAwait(false); }
        catch (Exception ex) { CvLog.Publish(CvLogLevel.Debug, LogSource, "teardown step failed.", ex); }
    }

    private void WriteLog(CvLogLevel level, string message, Exception? ex = null)
        => CvLog.Publish(level, LogSource, $"[{Name}] {message}", ex);
}
