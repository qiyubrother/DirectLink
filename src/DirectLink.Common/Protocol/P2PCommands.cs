namespace DirectLink.Common.Protocol;

/// <summary>
/// P2P 直连通道上的控制与元数据（二进制前导 + 可选 JSON）。
/// </summary>
public static class P2PCommands
{
    /// <summary>文件传输元数据（JSON 行）：FILE_META {"FileName","FileSize","FileHash","ResumeOffset"}</summary>
    public const string FileMeta = "FILE_META";

    /// <summary>后续为二进制块，无文本命令。小块传输利于弱网与进度反馈。</summary>
    public const int DefaultBlockSize = 16 * 1024;
}
