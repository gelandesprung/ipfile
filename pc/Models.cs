using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace FeigeP2P;

/// <summary>
/// 局域网内的一台对端设备。两种来源可并存：
/// ① UDP 广播自动发现（会超时下线）；② 二维码配对（会话内常驻）。
/// </summary>
public sealed class Peer
{
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public int TcpPort { get; set; }

    /// <summary>最近一次收到该设备广播的时间（仅局域网来源有意义）。</summary>
    public DateTime LastSeen { get; set; } = DateTime.Now;

    /// <summary>通过 UDP 广播发现，仍在广播周期内（超时会被清掉）。</summary>
    public bool IsLan { get; set; }

    /// <summary>通过扫码/被扫码配对建立，仅存内存，会话内常驻。</summary>
    public bool IsPaired { get; set; }

    public DateTime PairedAt { get; set; } = DateTime.Now;
    /// <summary>本次会话内刚配对成功，用于列表里显示「刚配对」标记。</summary>
    public bool JustPaired { get; set; }

    /// <summary>IPv6 必须用方括号包住地址再拼端口，否则冒号无法区分地址与端口。</summary>
    public string Address => Ip.Contains(':') ? $"[{Ip}]:{TcpPort}" : $"{Ip}:{TcpPort}";

    /// <summary>设备唯一键（地址 + 端口），IPv6 安全。</summary>
    public string Key => Address;

    public static string KeyOf(string ip, int tcpPort)
        => ip.Contains(':') ? $"[{ip}]:{tcpPort}" : $"{ip}:{tcpPort}";
}

/// <summary>设备列表的变化类型。</summary>
public enum PeerChange
{
    /// <summary>首次发现该设备。</summary>
    Added,
    /// <summary>已知设备的周期性广播（名称/端口可能有变），只刷新显示。</summary>
    Updated,
    /// <summary>收到 BYE 或超时，真正离线。</summary>
    Removed
}

/// <summary>一次二维码配对会话：token 一次性、10 分钟有效，收起/刷新即作废。</summary>
public sealed class PairSession
{
    public string Token { get; init; } = "";
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; init; } = DateTime.UtcNow;
    /// <summary>已被使用（配对成功）后不可重放。</summary>
    public bool Used { get; set; }
    /// <summary>配对成功时记录对方名字，便于日志。</summary>
    public string PeerName { get; set; } = "";

    public TimeSpan Remaining => ExpiresAtUtc - DateTime.UtcNow;
    public bool IsExpired => Remaining <= TimeSpan.Zero;
    public bool IsValid => !Used && !IsExpired;

    /// <summary>生成 32 位十六进制 token（16 字节密码学随机数）。</summary>
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}

/// <summary>出示方收到配对请求后，向用户确认所需的信息。</summary>
public sealed record PairRequest(string PeerName, string RemoteIp, int PeerPort)
{
    public string Address => Peer.KeyOf(RemoteIp, PeerPort);
}

/// <summary>收到的一笔发文件请求（用于向用户确认是否接收）。</summary>
public sealed record IncomingRequest(string FileName, long Size, string RemoteIp);

/// <summary>用户对接收请求的决定。<paramref name="SavePath"/> 为空表示存到默认收件夹。</summary>
public sealed record ReceiveDecision(bool Accept, string? SavePath = null);

/// <summary>一条传输记录（会话内显示）。</summary>
public sealed record TransferRecord(string Direction, string Name, string PeerName, string Status, DateTime Time);

/// <summary>最近传输的一个文件，点击可打开其所在位置。</summary>
public sealed class RecentItem
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public bool Received { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;
}

/// <summary>本地配置，JSON 持久化到 %APPDATA%/FeigeP2P/config.json。</summary>
public sealed class AppConfig
{
    public string DeviceName { get; set; } = Environment.MachineName;
    public int UdpPort { get; set; } = 2425;
    public int TcpPort { get; set; } = 2425;

    /// <summary>网页模式端口（浏览器访客访问）。默认 8080，被占用时服务会自动往后找可用端口。</summary>
    public int WebPort { get; set; } = 8080;

    /// <summary>最近传输的文件（最多 5 条，最新在前），关掉程序再打开还在。</summary>
    public List<RecentItem> Recent { get; set; } = new();

    private static string Dir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FeigeP2P");
    private static string ConfigPath => System.IO.Path.Combine(Dir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath));
                if (cfg != null && cfg.UdpPort > 0 && cfg.TcpPort > 0 && !string.IsNullOrWhiteSpace(cfg.DeviceName))
                {
                    cfg.Recent ??= new List<RecentItem>();
                    cfg.Recent.RemoveAll(r => r == null || string.IsNullOrWhiteSpace(r.Path));
                    return cfg;
                }
            }
        }
        catch { /* 配置损坏则用默认值 */ }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this));
        }
        catch { /* 保存失败不影响使用 */ }
    }

    /// <summary>接收目录：程序目录下的 received/。</summary>
    public static string ReceiveDirectory
    {
        get
        {
            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "received");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// 本机可被手机访问的地址，按优先级排序：全局 IPv6 → 唯一本地 IPv6（fdxx）→ IPv4。
    /// 跳过回环、链路本地 fe80::（手机无法凭 scope id 选网卡）、虚拟网卡与临时隐私地址优先降级。
    /// </summary>
    public static List<string> GetLocalIpCandidates()
    {
        var v6 = new List<(int Score, string Ip)>();
        var v4 = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                var name = ni.Name.ToLowerInvariant();
                if (name.Contains("vmware") || name.Contains("virtual") || name.Contains("vethernet") ||
                    name.Contains("wsl") || name.Contains("hyper-v") || name.Contains("docker"))
                    continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    var a = ua.Address;
                    if (IPAddress.IsLoopback(a)) continue;

                    if (a.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast || a.IsIPv6Teredo)
                            continue;

                        var bytes = a.GetAddressBytes();
                        var isUla = (bytes[0] & 0xFE) == 0xFC;        // fc00::/7
                        var isGlobal = (bytes[0] & 0xE0) == 0x20;     // 2000::/3
                        if (!isUla && !isGlobal) continue;

                        var score = isGlobal ? 100 : 50;
                        if (!ua.IsTransient) score += 20;             // 稳定的全局地址优先于隐私临时地址
                        v6.Add((score, a.ToString()));
                    }
                    else if (a.AddressFamily == AddressFamily.InterNetwork)
                    {
                        v4.Add(a.ToString());
                    }
                }
            }
        }
        catch { }

        return v6.OrderByDescending(x => x.Score).Select(x => x.Ip)
            .Concat(v4)
            .Distinct()
            .ToList();
    }

    /// <summary>取最优的一个局域网地址；都没有时回退回环地址。</summary>
    public static string GetLocalIp() => GetLocalIpCandidates().FirstOrDefault() ?? "127.0.0.1";

    /// <summary>
    /// 仅取 IPv6 地址；本机没有可用的公网/ULA IPv6 时返回 null。
    /// 网页模式的地址族切换必须用这个——不能用 GetLocalIp()，那个在没有 v6 时会返回 v4 地址，
    /// 拿去拼 <c>http://[v4]:port</c> 会得到一个对方根本连不上的地址。
    /// </summary>
    public static string? GetLocalIpV6Only()
        => GetLocalIpCandidates().FirstOrDefault(ip => ip.Contains(':'));

    /// <summary>仅取 IPv4 地址；没有时返回 null（网页模式据此隐藏/禁用 IPv4 选项）。</summary>
    public static string? GetLocalIpV4Only()
        => GetLocalIpCandidates().FirstOrDefault(ip => !ip.Contains(':'));


    /// <summary>
    /// 本机 IPv4 地址（UDP 广播用）。广播只能走 IPv4，所以这里必须跳过 IPv6 地址，
    /// 否则对端拿到的地址无法 ping 通，也无法回连。
    /// </summary>
    public static string GetLocalIpV4()
    {
        var candidates = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var name = ni.Name.ToLowerInvariant();
                if (name.Contains("vmware") || name.Contains("virtual") || name.Contains("vethernet") ||
                    name.Contains("wsl") || name.Contains("hyper-v") || name.Contains("docker"))
                    continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ua.Address))
                        candidates.Add(ua.Address.ToString());
                }
            }
        }
        catch { }
        return candidates.FirstOrDefault() ?? "127.0.0.1";
    }
}
