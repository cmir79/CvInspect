namespace CvInspect.Imaging;

/// <summary>연결 상태 변화 이벤트 인자.</summary>
public class ConnArgs : EventArgs
{
    public ConnArgs(bool isConnected)
    {
        IsConnected = isConnected;
    }

    public bool IsConnected { get; }
}
