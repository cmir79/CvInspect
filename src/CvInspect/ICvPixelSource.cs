namespace CvInspect;

/// <summary>
/// 8비트 픽셀 버퍼 공급 계약 — 표시·검사 쪽이 공급자의 구체 타입을 몰라도 픽셀을 그대로 읽게 한다.
/// 취득 프레임처럼 자체 버퍼를 쥔 것이 구현한다. 포맷은 채널 수로 말하므로(1=Gray8, 3=Bgr24, 4=Bgra32)
/// 취득 쪽 열거형이 코어로 올라오지 않는다. 코어 자신은 이 계약을 쓰지 않는다 — 형제 패키지가 서로를
/// 참조하지 않고 만나는 자리다(<see cref="ICvOrderedCategory"/> 와 같은 성격).
///
/// <b>발행 뒤 <see cref="Pixels"/> 는 불변이어야 한다.</b> 소비자는 복사 없이 배열 참조를 붙잡아 둘 수 있고
/// (수명은 공급자가 아니라 GC 가 정한다), 그래서 표시 경로의 복사가 한 번으로 끝난다. 버퍼를 재사용하는
/// 공급자는 이 계약을 구현하면 안 된다. 어겼을 때 깨지는 것은 화면이 아니다 — 표시 계층은 대입 시점에 픽셀을
/// 자기 백버퍼로 옮기므로 화면은 그대로다. 깨지는 것은 그 배열을 뒤에 읽는 쪽(픽셀 값 조회·파일 저장)이며,
/// 화면과 다른 값을 내놓는다.
/// </summary>
public interface ICvPixelSource
{
    /// <summary>행 우선(top-down) 인터리브 8비트 픽셀. 길이는 <see cref="Stride"/>×<see cref="Height"/> 이상.</summary>
    byte[] Pixels { get; }
    int Width { get; }
    int Height { get; }
    /// <summary>행 바이트 수 — <see cref="Width"/>×<see cref="Channels"/> 이상(행 끝 패딩 허용). 소비자는 항상 이 값으로 행을 건다.</summary>
    int Stride { get; }
    /// <summary>1=Gray8, 3=Bgr24, 4=Bgra32. 그 밖의 값은 표시 불가.</summary>
    int Channels { get; }
}
