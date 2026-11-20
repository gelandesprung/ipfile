using System.Net;
using System.Text;

namespace FeigeP2P;

/// <summary>
/// 飞鸽 P2P 协议的 C# 实现。与 shared/PROTOCOL.md 严格对应。
/// 纯文本协议，UTF-8，控制行以 \n 结尾；设备名/文件名用 Base64 规避中文与分隔符歧义。
/// 设备发现有两条并行通道：① UDP 广播（FG_P2P，局域网自动发现）；
/// ② 二维码配对（FG_QR / FG_HELLO / FG_HELLO_ACK，跨网段也能用）。文件传输格式两侧共用。
/// </summary>
internal static class Protocol
{
    public const string Magic = "FG_P2P";
    public const string QrMagic = "FG_QR";
    public const string HelloMagic = "FG_HELLO";
    public const string HelloAckMagic = "FG_HELLO_ACK";
    public const string FileMagic = "FG_FILE";
    public const string AckMagic = "FG_ACK";
    public const string DoneMagic = "FG_DONE";
    public const int Version = 1;

    public static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
    public static string UnB64(string s) => Encoding.UTF8.GetString(Convert.FromBase64String(s));

    // ---------- UDP 局域网发现 ----------

    /// <summary>构造发现广播包（普通或 BYE）。</summary>
    public static string BuildDiscovery(string name, string ip, int tcpPort, bool bye = false)
    {
        var line = $"{Magic}|{Version}|{B64(name)}|{ip}|{tcpPort}";
        if (bye) line += "|BYE";
        return line + "\n";
    }

    /// <summary>解析发现包。返回 null 表示非本协议包/版本不符/字段非法。</summary>
    public static DiscoveryPacket? ParseDiscovery(string line)
    {
        try
        {
            var parts = line.TrimEnd('\r', '\n').Split('|');
            if (parts.Length < 5) return null;
            if (parts[0] != Magic || parts[1] != Version.ToString()) return null;
            var name = UnB64(parts[2]);
            var ip = parts[3];
            if (!int.TryParse(parts[4], out var port)) return null;
            var bye = parts.Length >= 6 && parts[5] == "BYE";
            return new DiscoveryPacket(
                new Peer { Name = name, Ip = ip, TcpPort = port, IsLan = true }, bye);
        }
        catch
        {
            return null;
        }
    }

    // ---------- 二维码载荷 ----------

    /// <summary>
    /// 构造二维码内容（不含换行）：<c>FG_QR|1|name_b64|ipv6|port|token</c>。
    /// IP 可以是 IPv6 字面量（含 %scope），二维码只是文本，不需要方括号。
    /// </summary>
    public static string BuildQrPayload(string name, string ip, int tcpPort, string token)
        => $"{QrMagic}|{Version}|{B64(name)}|{ip}|{tcpPort}|{token}";

    /// <summary>解析扫到的二维码文本。非法/版本不符/字段异常返回 null。</summary>
    public static QrPayload? ParseQrPayload(string text)
    {
        try
        {
            var line = text.Trim().TrimEnd('\r', '\n');
            var parts = line.Split('|');
            if (parts.Length != 6) return null;
            if (parts[0] != QrMagic || parts[1] != Version.ToString()) return null;
            if (!IsToken(parts[5])) return null;
            var name = UnB64(parts[2]);
            var ip = parts[3].Trim();
            if (!IPAddress.TryParse(ip, out _)) return null;
            if (!int.TryParse(parts[4], out var port) || port < 1 || port > 65535) return null;
            if (string.IsNullOrWhiteSpace(name)) name = "未命名设备";
            return new QrPayload(name, ip, port, parts[5].ToLowerInvariant());
        }
        catch { return null; }
    }

    // ---------- 配对握手 ----------

    /// <summary>扫码方发给出示方：<c>FG_HELLO|1|token|name_b64|tcp_port</c>。</summary>
    public static string BuildHello(string token, string name, int tcpPort)
        => $"{HelloMagic}|{Version}|{token}|{B64(name)}|{tcpPort}\n";

    public static HelloRequest? ParseHello(string line)
    {
        try
        {
            var parts = line.TrimEnd('\r', '\n').Split('|');
            if (parts.Length < 4) return null;
            if (parts[0] != HelloMagic || parts[1] != Version.ToString()) return null;
            if (!IsToken(parts[2])) return null;
            var name = UnB64(parts[3]);
            if (string.IsNullOrWhiteSpace(name)) name = "未命名设备";
            var port = 2425;
            if (parts.Length >= 5 && (!int.TryParse(parts[4], out port) || port < 1 || port > 65535)) return null;
            return new HelloRequest(parts[2].ToLowerInvariant(), name, port);
        }
        catch { return null; }
    }

    /// <summary>出示方应答：<c>FG_HELLO_ACK|1|OK|name_b64|ip|tcp_port</c>，带回自己的地址供扫码方入列。</summary>
    public static string BuildHelloAckOk(string name, string ip, int tcpPort)
        => $"{HelloAckMagic}|{Version}|OK|{B64(name)}|{ip}|{tcpPort}\n";

    public static string BuildHelloAckReject(string reason)
        => $"{HelloAckMagic}|{Version}|REJECT|{B64(reason)}\n";

    public static HelloAck ParseHelloAck(string line)
    {
        try
        {
            var parts = line.TrimEnd('\r', '\n').Split('|');
            if (parts.Length < 3 || parts[0] != HelloAckMagic || parts[1] != Version.ToString())
                return HelloAck.Fail("应答格式错误");
            if (parts[2] == "REJECT")
                return HelloAck.Fail(parts.Length >= 4 ? UnB64(parts[3]) : "对方拒绝配对");
            if (parts[2] != "OK") return HelloAck.Fail("未知应答");
            if (parts.Length < 6) return HelloAck.Fail("应答缺少设备信息");
            if (!IPAddress.TryParse(parts[4], out _)) return HelloAck.Fail("应答地址非法");
            if (!int.TryParse(parts[5], out var port) || port < 1 || port > 65535) return HelloAck.Fail("应答端口非法");
            var name = UnB64(parts[3]);
            return HelloAck.Success(string.IsNullOrWhiteSpace(name) ? "对方" : name, parts[4], port);
        }
        catch { return HelloAck.Fail("应答解析失败"); }
    }

    /// <summary>token 为 32 位十六进制（16 字节随机数），拒绝任何带分隔符/空白的输入。</summary>
    private static bool IsToken(string s)
    {
        if (s.Length < 8 || s.Length > 128) return false;
        foreach (var c in s)
        {
            var hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex) return false;
        }
        return true;
    }

    // ---------- TCP 文件传输（与 v1 完全一致） ----------

    public static string BuildFileRequest(string fileName, long size, int fileCount = 1)
        => $"{FileMagic}|{Version}|{B64(fileName)}|{size}|{fileCount}\n";

    public static bool ParseFileRequest(string line, out string fileName, out long size, out int fileCount)
    {
        fileName = ""; size = 0; fileCount = 1;
        try
        {
            var parts = line.TrimEnd('\r', '\n').Split('|');
            if (parts.Length < 5 || parts[0] != FileMagic || parts[1] != Version.ToString()) return false;
            fileName = UnB64(parts[2]);
            if (!long.TryParse(parts[3], out size) || size < 0) return false;
            if (!int.TryParse(parts[4], out fileCount)) return false;
            return fileName.Length > 0;
        }
        catch { return false; }
    }

    public static string BuildAckOk() => $"{AckMagic}|{Version}|OK\n";
    public static string BuildAckReject(string reason) => $"{AckMagic}|{Version}|REJECT|{B64(reason)}\n";

    /// <summary>返回 true=OK；false=REJECT，reason 给出原因。</summary>
    public static bool ParseAck(string line, out string reason)
    {
        reason = "";
        try
        {
            var parts = line.TrimEnd('\r', '\n').Split('|');
            if (parts.Length < 3 || parts[0] != AckMagic || parts[1] != Version.ToString()) return false;
            if (parts[2] == "OK") return true;
            if (parts[2] == "REJECT" && parts.Length >= 4) { reason = UnB64(parts[3]); return false; }
            return false;
        }
        catch { return false; }
    }

    public static string BuildDone(long receivedSize) => $"{DoneMagic}|{Version}|{receivedSize}\n";

    public static bool ParseDone(string line, out long receivedSize)
    {
        receivedSize = -1;
        try
        {
            var parts = line.TrimEnd('\r', '\n').Split('|');
            if (parts.Length < 3 || parts[0] != DoneMagic || parts[1] != Version.ToString()) return false;
            return long.TryParse(parts[2], out receivedSize);
        }
        catch { return false; }
    }
}

internal sealed record QrPayload(string Name, string Ip, int TcpPort, string Token);

internal sealed record DiscoveryPacket(Peer Peer, bool Bye);

internal sealed record HelloRequest(string Token, string Name, int TcpPort);

internal sealed record HelloAck(bool Ok, string Reason, string Name, string Ip, int TcpPort)
{
    public static HelloAck Success(string name, string ip, int port) => new(true, "", name, ip, port);
    public static HelloAck Fail(string reason) => new(false, reason, "", "", 0);
}
