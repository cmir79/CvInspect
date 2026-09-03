namespace CvInspect.Imaging.Gev;

/// <summary>
/// SCPD(패킷 간 지연) 값 계산. <b>여러 대를 한 포트로 받을 때만 필요하다.</b>
///
/// 카메라는 프레임을 프레임 주기에 걸쳐 고르게 보내지 않고 <b>회선 속도로 몰아친 뒤 쉰다.</b>
/// 그래서 프레임레이트를 낮춰 평균 대역이 남아돌아도 두 대의 버스트가 겹치면 1 Gbps 포트에
/// 2 Gbps 를 요구하게 되어 유실된다 — 실측에서 각 5 fps(합산 363 Mbps)로 묶은 두 대 중 한쪽이
/// 패킷의 20% 를 잃고 3000장 중 12장만 완성됐다. 패킷 사이를 벌리면 버스트가 서로 비켜 간다.
///
/// 값의 단위가 <b>장치 타임스탬프 틱</b>이라 같은 지연이라도 장치마다 숫자가 다르다
/// (150us 기준: 125 MHz 장치는 18,750, 66.67 MHz 장치는 10,000). 그래서 시간으로 받아
/// 장치가 보고한 틱 주파수로 환산한다 — 호스트가 장치별 숫자를 알 필요가 없게.
/// </summary>
public static class GevScpd
{
    /// <summary>여러 대를 한 NIC 로 받을 때의 출발점(us). 실측에서 이 값으로 누락 0 이 됐다.</summary>
    public const double MultiCamDefaultUs = 150.0;

    /// <summary>
    /// 지연 시간을 장치 틱으로 환산한다. 환산할 수 없으면 null —
    /// <paramref name="tickHz"/> 가 0 이면 장치가 틱 주파수를 알려 주지 않은 것이고,
    /// 그때는 조용히 0 을 쓰는 것보다 <b>적용하지 않았다고 알리는 편이</b> 낫다.
    /// </summary>
    public static int? TicksFor(double delayUs, ulong tickHz)
    {
        if (delayUs <= 0 || tickHz == 0) return null;

        var ticks = Math.Round(delayUs * tickHz / 1_000_000.0);
        if (ticks < 1) return 1;                       // 0 은 "건드리지 않음" 이라 1 로 올린다
        return ticks > int.MaxValue ? int.MaxValue : (int)ticks;
    }
}
