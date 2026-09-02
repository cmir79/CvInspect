using OpenCvSharp;

namespace CvInspect.Imaging;

/// <summary>프레임 방향 보정 — 카메라 장착 방향(회전/플립)을 소비자에게 넘기기 전에 접는다.</summary>
public static class CamXform
{
    /// <summary>
    /// 회전·플립 적용. 둘 다 None 이면 <paramref name="src"/> 를 그대로 반환하고,
    /// 변환이 있으면 <b>새 Mat</b> 을 반환한다 — 반환이 src 와 다른 인스턴스면 src 정리는 호출자 몫.
    /// 적용 순서는 <b>회전 → 플립</b>이다 — 두 변환은 비가환이라 이 순서 자체가
    /// 설정값(<see cref="CamOpt.Flip"/>/<see cref="CamOpt.Rotation"/>) 해석의 계약이다.
    /// </summary>
    public static Mat Apply(Mat src, FlipMode flip, RotateMode rotation)
    {
        if (flip == FlipMode.None && rotation == RotateMode.None) return src;

        var work = new Mat();
        if (rotation != RotateMode.None)
        {
            Cv2.Rotate(src, work, rotation switch
            {
                RotateMode.Rotate90 => RotateFlags.Rotate90Clockwise,
                RotateMode.Rotate180 => RotateFlags.Rotate180,
                _ => RotateFlags.Rotate90Counterclockwise,
            });
        }
        else
        {
            src.CopyTo(work);
        }

        if (flip != FlipMode.None)
        {
            Cv2.Flip(work, work, flip switch
            {
                FlipMode.Horizontal => OpenCvSharp.FlipMode.Y,
                FlipMode.Vertical => OpenCvSharp.FlipMode.X,
                _ => OpenCvSharp.FlipMode.XY,
            });
        }
        return work;
    }
}
