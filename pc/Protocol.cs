using System.Text;

namespace FeigeP2P;

/// <summary>
/// 飞鸽 P2P 协议 v1.0 的 C# 实现。与 shared/PROTOCOL.md 严格对应。
/// 纯文本协议，UTF-8，每行以 \n 结尾；文件名/设备名用 Base64 规避中文与分隔符歧义。
/// </summary>
internal static class Protocol
{
    public const string Magic = "FG_P2P";
    public const string FileMagic = "FG_FILE";
    public const string AckMagic = "FG_ACK";
    public const string DoneMagic = "FG_DONE";
    public const int Version = 1;

    public static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
    public static string UnB64(string s) => Encoding.UTF8.GetString(Convert.FromBase64String(s));

    // ---------- UDP 发现 ----------

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
            return new DiscoveryPacket(new Peer { Name = name, Ip = ip, TcpPort = port }, bye);
        }
        catch
        {
            return null;
        }
    }

    // ---------- TCP 文件传输 ----------

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

internal sealed record DiscoveryPacket(Peer Peer, bool Bye);
