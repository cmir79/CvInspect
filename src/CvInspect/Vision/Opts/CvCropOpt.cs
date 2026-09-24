using System.ComponentModel;
using CvInspect.Vision.Edit;
using CvInspect.Vision.Overlay;

namespace CvInspect.Vision.Opts;

/// <summary>
/// 자르기 — 볼 자리만 남긴다. 대상이 화면의 일부뿐이면 나머지는 탐색 비용이자 오검출 거리다.
/// 체인의 첫 툴로 두며 좌표는 <b>원본 이미지 공간</b>이다(이 툴의 입력이 원본이라 "툴 좌표 = 그 툴 입력 공간" 규칙과 같다).
/// 이후 툴은 잘린 이미지 공간에서 돌고, 결과를 원본으로 되돌리는 환산은 <c>CvImageOps.MapOf(used, pre)</c> 가
/// 자르기 원점과 축소 배율을 함께 넣는다. 실행은 <c>CvImageOps.Crop</c>.
///
/// 속성 이름은 0.26 까지 <see cref="CvImageProcessOpt"/> 가 들고 있던 것과 같다 — 옛 전처리 저장본을 이 타입으로
/// 그대로 역직렬화해도 값이 들어온다. 다만 옛 파일을 계속 쓰는 호스트는 전처리 쪽 크롭도 꺼야 하므로
/// (켜진 채 남으면 <c>Preprocess</c> 가 던진다) <see cref="FromLegacy"/> 로 옮긴다.
/// </summary>
public sealed class CvCropOpt : ICvShapeSource
{
    /// <summary>잘라낼지. 바꾸면 이후 툴 좌표의 원점이 옮겨가 재티칭 대상이다.</summary>
    [CvCategory("cv:CatCrop", 1)]
    [CvName("cv:UseCrop")]
    [CvDesc("cv:UseCropDesc")]
    public bool UseCrop { get; set; }

    // 잘라낼 자리 — 원본 이미지 공간. 표시 위에서 끌어 편집한다(PG 미노출 — 정적 스냅샷이라 실시간 미반영).
    [Browsable(false)] public double CropX { get; set; }
    [Browsable(false)] public double CropY { get; set; }
    [Browsable(false)] public double CropW { get; set; } = 640;
    [Browsable(false)] public double CropH { get; set; } = 480;

    /// <summary>편집 도형 — 잘라내기 사각(UseCrop 일 때만).</summary>
    public IReadOnlyList<CvEditShape>? CreateShapes(Action onEdited)
    {
        if (!UseCrop) return null;
        var crop = new CvEditRect { Color = ViOverlayColor.Orange, Label = "Crop" };
        crop.Set(CropX, CropY, CropW, CropH);
        crop.Changed += (_, _) =>
        {
            CropX = crop.X;
            CropY = crop.Y;
            CropW = crop.W;
            CropH = crop.H;
            onEdited();
        };
        return [crop];
    }

    /// <summary>
    /// 0.26 까지 전처리에 들어 있던 크롭을 이 툴로 옮긴다 — 레시피를 읽은 직후에 부른다.
    /// 옛 크롭이 켜져 있으면 그 값으로 만든 자르기 툴을 돌려주고 <paramref name="ip"/> 쪽은 끈다(끄지 않으면
    /// <c>Preprocess</c> 가 계속 던진다). 옮길 것이 없으면 null — 옛 크롭이 꺼져 있던 저장본도 여기에 든다.
    ///
    /// <b>옮긴 뒤 전처리 파일도 다시 저장해야 한다.</b> 옛 값은 켜져 있는 동안 저장에 그대로 실려(이관 전에 잃지
    /// 않게 하려고) 파일에 남는다. 다시 쓰지 않으면 다음 로드 때 또 옮겨지며 그 사이 고친 자르기 툴을 덮는다.
    /// </summary>
    public static CvCropOpt? FromLegacy(CvImageProcessOpt ip)
    {
        if (ip is null) throw new ArgumentNullException(nameof(ip));
        if (ip.LegacyCrop is not { } c) return null;
        ip.ClearLegacyCrop();
        // 정상 경로의 한 줄 — 현장 로그에서 "이관이 일어났는가" 를 물을 수 있게 남긴다.
        CvLog.Publish(CvLogLevel.Info, nameof(CvCropOpt),
            $"Moved the crop saved in CvImageProcessOpt ({c.X:F0},{c.Y:F0} {c.W:F0}x{c.H:F0}) to CvCropOpt — save the image-process file again so it is not moved twice.");
        return new CvCropOpt { UseCrop = true, CropX = c.X, CropY = c.Y, CropW = c.W, CropH = c.H };
    }
}
