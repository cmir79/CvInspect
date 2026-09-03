using System.Collections.Concurrent;

namespace CvInspect.Imaging;

/// <summary>
/// ComType 문자열 → <see cref="ICam"/> 구현체 생성. 벤더 SDK 어댑터는 외부 등록으로 붙인다.
///
/// 만들 수 없는 ComType 은 <see cref="DeadCam"/> 으로 돌려준다 — 던지지 않으므로 여러 대 중 한 자리가
/// 잘못돼도 나머지는 뜨고, 그 자리는 연결되지 않은 채 이유를 들고 있다. <b>가상 카메라로 떨어뜨리지 않는다</b>:
/// 그건 합성 소스를 원해서 고른 경우의 것이고, 여기서 쓰면 검사가 조용히 가짜 프레임을 판정한다.
/// </summary>
public static class CamFactory
{
    private static readonly ConcurrentDictionary<string, Func<CamOpt, ICam>> _extern =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 외부(벤더 SDK 어댑터 패키지·앱)에서 ComType → 생성기를 등록. 호스트 기동 시 1회 호출.
    /// 무거운 SDK 를 이 패키지가 직참조하지 않도록 하는 주입 지점이다.
    /// </summary>
    public static void Register(string comType, Func<CamOpt, ICam> creator)
    {
        if (string.IsNullOrWhiteSpace(comType))
            throw new ArgumentException("comType is empty.", nameof(comType));
        _extern[comType.Trim()] = creator ?? throw new ArgumentNullException(nameof(creator));
    }

    public static ICam Create(CamOpt opt)
    {
        if (opt == null)
            throw new ArgumentNullException(nameof(opt));

        var key = (opt.ComType ?? string.Empty).Trim();

        if (_extern.TryGetValue(key, out var custom))
        {
            CvLog.Publish(CvLogLevel.Info, nameof(CamFactory), $"Provider selected (extern): {key}");
            return custom(opt);
        }

        switch (key.ToUpperInvariant())
        {
            case "VIRTUAL":
            case "VRT":
            case "SIM":
            case "MOCK":
                CvLog.Publish(CvLogLevel.Info, nameof(CamFactory), "Provider selected: Virtual");
                return new VirtualCam(opt);

            case "VIDEOCAPTURE":
            case "VIDEO":
            case "WEBCAM":
            case "FILE":
            case "RTSP":
                CvLog.Publish(CvLogLevel.Info, nameof(CamFactory), "Provider selected: VideoCapture");
                return new VideoCaptureCam(opt);

            default:
                // 가상 카메라로 떨어뜨리지 않는다 — 용도가 다르다. 가상 카메라는 "합성 소스를 원해서 고른 것"
                // 이고 여기는 "고른 것을 만들지 못한 것"이다. 그 둘을 같게 다루면 검사 앱이 예외 하나 없이
                // 합성 그라데이션을 진짜 프레임으로 받아 판정한다 — 조용히 틀리는 것이 멈추는 것보다 나쁘다.
                var reason = $"no provider is registered for ComType '{opt.ComType}'. Registered: {string.Join(", ", KnownComTypes())}";
                CvLog.Publish(CvLogLevel.Error, nameof(CamFactory), $"[{opt.Name}] {reason}");
                return new DeadCam(opt, reason);
        }
    }

    /// <summary>지금 만들 수 있는 ComType 목록 — 오설정 진단에 그대로 싣는다.</summary>
    private static IEnumerable<string> KnownComTypes()
        => new[] { "Virtual", "VideoCapture" }.Concat(_extern.Keys).OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
}
