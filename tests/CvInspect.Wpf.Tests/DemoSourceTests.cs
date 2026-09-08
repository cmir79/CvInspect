// 데모 소스 목록 — 합성 소스가 항상 첫째이고, 열거된 웹캠은 고정 식별자(VID/PID 또는 인스턴스 ID)로 열리게 만들어지는지.
// 카메라가 없는 러너에서는 합성 하나만 있어야 한다.
using System.IO;
using CvInspect.Demo;
using CvInspect.Imaging;
using Xunit;

namespace CvInspect.Wpf.Tests;

public class DemoSourceTests
{
    [Fact]
    public void SyntheticFirstAndWebcamsCarryAFixedIdentity()
    {
        var list = DemoSource.List(Path.GetTempPath());
        Assert.True(list.Count >= 1 && list[0].IsSynthetic && list[0].Key == "synthetic" && list[0].Opt.ComType == "Virtual", "the synthetic part is always the first source");
        foreach (var s in list.Skip(1))
        {
            Assert.True(!s.IsSynthetic && s.Opt.ComType == "Webcam" && s.Opt.IsColor, $"'{s.Title}' opens as a colour webcam");
            Assert.True(UsbCamId.TryParse(s.Opt.SerialNumber, out _, out _, out _), $"'{s.Title}' is opened by identity, not by index: SerialNumber='{s.Opt.SerialNumber}'");
            Assert.Equal(s.Key, s.Opt.SerialNumber);
        }
        Assert.Equal(list.Count, list.Select(s => s.Key).Distinct().Count());
    }
}
