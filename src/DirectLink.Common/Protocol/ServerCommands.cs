namespace DirectLink.Common.Protocol;

/// <summary>
/// 服务端与客户端之间的文本协议命令（一行一条）。
/// </summary>
public static class ServerCommands
{
    /// <summary>注册：REGISTER &lt;ClientId&gt;</summary>
    public const string Register = "REGISTER";

    /// <summary>端口映射上报：PORT_MAP &lt;ClientId&gt; —— 当前连接的 RemoteEndPoint 即 P2P 端点</summary>
    public const string PortMap = "PORT_MAP";

    /// <summary>查询对端：QUERY &lt;SelfId&gt; &lt;PeerId&gt;</summary>
    public const string Query = "QUERY";

    /// <summary>返回对端 P2P 地址：P2P &lt;IP&gt; &lt;Port&gt;（已废弃，仅走中转时返回 RELAY）</summary>
    public const string P2P = "P2P";

    /// <summary>对端在线、可经中继发送：RELAY</summary>
    public const string RelayOk = "RELAY";

    /// <summary>通知被呼叫方：INCOMING &lt;CallerId&gt; &lt;IP&gt; &lt;Port&gt;（已废弃）</summary>
    public const string Incoming = "INCOMING";

    /// <summary>成功</summary>
    public const string Ok = "OK";

    /// <summary>错误：ERR &lt;message&gt;</summary>
    public const string Err = "ERR";

    /// <summary>心跳：PING / PONG</summary>
    public const string Ping = "PING";
    public const string Pong = "PONG";

    /// <summary>中继注册（接收方在 50001 端口）：RELAY_REG &lt;ClientId&gt;</summary>
    public const string RelayReg = "RELAY_REG";

    /// <summary>经中继发送：RELAY &lt;FromId&gt; &lt;ToId&gt;</summary>
    public const string Relay = "RELAY";
}
