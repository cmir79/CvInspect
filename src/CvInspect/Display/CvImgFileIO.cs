// 디스플레이 우클릭 메뉴의 이미지 파일 불러오기·저장 규칙 — 확장자 목록, 기본 파일 이름, 디코드·인코드.
// UI 프레임워크 무관: 파일 대화상자와 오류 표시는 UI 어댑터 몫이고, 여기는 바이트와 Mat 사이만 다룬다.
// 경로 기반 ImRead/ImWrite 는 쓰지 않는다 — Windows 비ASCII(한글) 경로에서 조용히 실패한다. 바이트로 읽고 쓴다.

using System.IO;
using System.Linq;
using System.Windows.Input;
using OpenCvSharp;

namespace CvInspect.Display;

/// <summary>파일 대화상자 형식 하나 — 표시 이름과 패턴(<c>*.png</c> 꼴).</summary>
internal readonly record struct CvImgFileType(string Name, string[] Patterns);

/// <summary>이미지 파일 규칙.</summary>
internal static class CvImgFileIO
{
    /// <summary>불러오기 대화상자 형식 — 이미지 묶음, 그다음 전부.</summary>
    internal static readonly CvImgFileType[] OpenTypes =
    [
        new("Image", ["*.bmp", "*.png", "*.jpg", "*.jpeg", "*.tif", "*.tiff"]),
        new("All", ["*.*"]),
    ];

    /// <summary>저장 대화상자 형식 — 첫 형식(PNG)이 기본 이름의 확장자와 같다.</summary>
    internal static readonly CvImgFileType[] SaveTypes =
    [
        new("PNG", ["*.png"]),
        new("BMP", ["*.bmp"]),
        new("JPEG", ["*.jpg"]),
    ];

    /// <summary>Win32 파일 대화상자의 필터 문자열 — <c>이름|패턴;패턴|이름|패턴</c>.</summary>
    internal static string WinFilter(IEnumerable<CvImgFileType> types)
        => string.Join("|", types.Select(t => t.Name + "|" + string.Join(";", t.Patterns)));

    /// <summary>저장 대화상자의 기본 파일 이름 — <c>image_yyyyMMdd_HHmmss.png</c>(지역 시각).</summary>
    internal static string DefaultSaveName(DateTime now) => $"image_{now:yyyyMMdd_HHmmss}.png";

    /// <summary>바이트 → Mat(원본 채널·깊이 그대로). 디코드할 수 없으면 던진다. 돌려준 Mat 의 dispose 는 호출자 책임.</summary>
    /// <exception cref="InvalidOperationException">이미지로 읽을 수 없는 바이트.</exception>
    internal static Mat Decode(byte[] bytes)
    {
        var mat = Cv2.ImDecode(bytes, ImreadModes.Unchanged);
        if (mat.Empty())
        {
            mat.Dispose();
            throw new InvalidOperationException("Unsupported image format");
        }
        return mat;
    }

    /// <summary>파일을 읽어 불러오기 명령에 넘긴다. Mat 소유권은 명령 수신자(VM)로 넘어간다 — 받을 곳이 없으면 여기서 해제.
    /// CanExecute 는 묻지 않는다(넘길 Mat 이 대화상자를 닫은 뒤에야 생긴다 — 거절은 Execute 안에서 한다).</summary>
    internal static void LoadInto(string path, ICommand? loadFrameCommand)
    {
        var mat = Decode(File.ReadAllBytes(path));
        if (loadFrameCommand is { } cmd) cmd.Execute(mat);
        else mat.Dispose();
    }

    /// <summary>Mat → 파일 바이트. 형식은 경로의 확장자가 정하고, 확장자가 없으면 PNG 다. JPEG 는 알파를 못 담아 4채널(BGRA)을
    /// BGR 로 접어 넣는다.</summary>
    /// <exception cref="IOException">인코드 실패(모르는 확장자 등).</exception>
    internal static byte[] Encode(string path, Mat mat)
    {
        var isJpeg = Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg";
        if (isJpeg && mat.Channels() == 4)
        {
            using var bgr = new Mat();
            Cv2.CvtColor(mat, bgr, ColorConversionCodes.BGRA2BGR);
            return EncodeAs(path, bgr);
        }
        return EncodeAs(path, mat);
    }

    private static byte[] EncodeAs(string path, Mat mat)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        if (!Cv2.ImEncode(ext, mat, out var bytes))
            throw new IOException("Failed to encode image file");
        return bytes;
    }

    /// <summary>표시 중인 프레임을 파일로 쓴다. 원본은 프레임 버퍼의 픽셀 스냅샷이다 — 호스트가 넘긴 Mat 은 대입 뒤 호스트가
    /// dispose 했을 수 있어 건드리지 않는다.</summary>
    /// <exception cref="InvalidOperationException">표시 중인 프레임이 없다.</exception>
    /// <exception cref="IOException">인코드 실패.</exception>
    internal static void WriteFrame(string path, CvFrameBuf frame)
    {
        using var mat = frame.SnapMat()
            ?? throw new InvalidOperationException("Unsupported frame format");
        File.WriteAllBytes(path, Encode(path, mat));
    }
}
