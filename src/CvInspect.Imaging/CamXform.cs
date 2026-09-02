using OpenCvSharp;

namespace CvInspect.Imaging;

/// <summary>프레임 방향 보정 — 카메라 장착 방향(플립/회전)을 소비자에게 넘기기 전에 접는다.</summary>
public static class CamXform
{
    /// <summary>
    /// 플립·회전 적용. 둘 다 None 이면 <paramref name="src"/> 를 그대로 반환하고,
    /// 변환이 있으면 <b>새 Mat</b> 을 반환한다 — 반환이 src 와 다른 인스턴스면 src 정리는 호출자 몫.
    /// 적용 순서는 플립 → 회전.
    /// </summary>
    public static Mat Apply(Mat src, FlipMode flip, RotateMode rotation)
    {
        if (flip == FlipMode.None && rotation == RotateMode.None) return src;

        var work = new Mat();
        if (flip != FlipMode.None)
        {
            var mode = flip switch
            {
                FlipMode.Horizontal => OpenCvSharp.FlipMode.Y,
                FlipMode.Vertical => OpenCvSharp.FlipMode.X,
                _ => OpenCvSharp.FlipMode.XY,
            };
            Cv2.Flip(src, work, mode);
        }
        else
        {
            src.CopyTo(work);
        }

        if (rotation == RotateMode.None) return work;

        var rotated = new Mat();
        Cv2.Rotate(work, rotated, rotation switch
        {
            RotateMode.Rotate90 => RotateFlags.Rotate90Clockwise,
            RotateMode.Rotate180 => RotateFlags.Rotate180,
            _ => RotateFlags.Rotate90Counterclockwise,
        });
        work.Dispose();
        return rotated;
    }
}
