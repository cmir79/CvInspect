using System.Collections.Concurrent;

namespace CvInspect.Imaging;

/// <summary>ComType 문자열 → <see cref="ICam"/> 구현체 생성. 벤더 SDK 어댑터는 외부 등록으로 붙인다.</summary>
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
                CvLog.Publish(CvLogLevel.Warning, nameof(CamFactory),
                    $"Unknown camera ComType '{opt.ComType}' -> Virtual fallback.");
                return new VirtualCam(opt);
        }
    }
}
