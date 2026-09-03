namespace CvInspect.Imaging.Gev;

/// <summary>
/// <see cref="GevCam"/> 의 GigE 전용 설정 — 카메라 자체의 성질(이름·시리얼·노출·방향 등)은
/// <see cref="CamOpt"/> 에 있고, 여기에는 전송·세션에 관한 것만 둔다.
///
/// 이 객체는 값만 담는다. 실제 취득 라이브러리 옵션은 <b>Open 마다 새로 만들어</b> 넘긴다 —
/// 그쪽 옵션 객체는 참조로 붙들려 세션 동안 살아 있고 시작 과정에서 값이 되쓰이기도 해서,
/// 하나를 돌려쓰면 카메라끼리 설정이 섞인다.
/// </summary>
public sealed class GevCamOpt
{
    /// <summary>탐색 응답을 모으는 시간(ms). 스위치가 느리거나 대수가 많으면 늘린다.</summary>
    public int DiscoveryTimeoutMs { get; set; } = 1000;

    /// <summary>GVCP 요청 하나의 응답 대기(ms).</summary>
    public int GvcpTimeoutMs { get; set; } = 500;

    /// <summary>장치가 제어권을 놓기까지의 하트비트 시한(ms). 짧을수록 호스트가 비정상 종료한 뒤
    /// 카메라가 빨리 풀리지만, <b>GvcpTimeoutMs 의 4.5배 밑으로 내리면 안 된다</b> —
    /// 그 아래에서는 느린 명령 한 번에 제어권을 잃는다.</summary>
    public int HeartbeatTimeoutMs { get; set; } = 3000;

    /// <summary>카메라 XML 캐시 폴더. 지정하면 두 번째 Open 부터 XML 전송(수십 KB 를 512바이트씩)을 건너뛴다 —
    /// Open 지연의 가장 큰 몫이다. 쓰기 가능한 폴더를 보장할 수 없으면 null 로 둔다(실패해도 치명적이지 않다).</summary>
    public string? XmlCacheDir { get; set; }

    /// <summary>수신 버퍼 개수. 조립 중 프레임이 최대 4장이므로 소비자가 붙들 장수 + 4 보다 넉넉해야 한다.</summary>
    public int BufferCount { get; set; } = 8;

    /// <summary>수신 소켓 버퍼 크기(바이트). OS 가 이보다 적게 주면 취득 시작 로그에 남는데,
    /// 그 줄이 부하 중 손실을 가장 잘 예고한다.</summary>
    public int SocketBufferBytes { get; set; } = 32 * 1024 * 1024;

    /// <summary>패킷 크기를 고정할지. 기본(false)은 경로 MTU 를 실제로 재서 정한다 —
    /// 안전하지만 취득 시작에 0.3~0.9초가 붙는다. 경로 MTU 를 통제하는 망에서만 고정으로 둔다.</summary>
    public bool UseFixedPacketSize { get; set; }

    /// <summary><see cref="UseFixedPacketSize"/> 일 때 쓸 패킷 크기(바이트).</summary>
    public int PacketSize { get; set; } = 1500;

    /// <summary>단발 그랩이 프레임을 기다리는 상한(ms).</summary>
    public int GrabTimeoutMs { get; set; } = 5000;

    /// <summary>Bayer 패턴을 장치 선언 대신 이 값으로 못 박는다(기본 null = 장치 신뢰).
    /// 펌웨어가 미러·ROI 오프셋을 패턴에 반영하지 않고 보고하는 카메라의 탈출구다 —
    /// 그런 장치에서는 색만 뒤바뀌고 예외도 경고도 없다.</summary>
    public CvBayerPattern? BayerPatternOverride { get; set; }

    /// <summary>Bayer 프레임을 컬러로 펴지 않고 흑백으로 바로 접는다. 색을 안 쓰는 검사에서
    /// 디모자이크 비용과 3배 메모리를 아낀다.</summary>
    public bool BayerToMono { get; set; }
}
