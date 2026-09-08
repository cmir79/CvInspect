namespace CvInspect.Vision.Edit;

/// <summary>
/// object 로 들고 있는 툴 파라미터에서 편집 도형을 얻는 편의 — 파라미터가 <see cref="ICvShapeSource"/> 면 그 도형, 아니면 null.
/// 파라미터 종류별 분기는 없다: 어떤 도형을 갖는지는 파라미터 자신이 안다.
/// </summary>
public static class CvShapeBinder
{
    /// <summary>편집 도형 목록 — 편집 가능 영역이 없으면 null. onEdited 는 드래그가 파라미터에 반영될 때 호출(dirty 마킹·재검사).</summary>
    public static IReadOnlyList<CvEditShape>? For(object toolOpt, Action onEdited)
        => (toolOpt as ICvShapeSource)?.CreateShapes(onEdited);
}
