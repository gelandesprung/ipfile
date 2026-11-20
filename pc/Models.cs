using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace FeigeP2P;

/// <summary>局域网内的一台对端设备。</summary>
public sealed class Peer
{
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public int TcpPort { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.Now;
    public string Key => $"{Ip}:{TcpPort}";
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

/// <summary>一条传输记录（会话内显示）。</summary>
public sealed record TransferRecord(string Direction, string Name, string PeerName, string Status, DateTime Time);

/// <summary>本地配置，JSON 持久化到 %APPDATA%/FeigeP2P/config.json。</summary>
public sealed class AppConfig
{
    public string DeviceName { get; set; } = Environment.MachineName;
    public int UdpPort { get; set; } = 2425;
    public int TcpPort { get; set; } = 2425;

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
                    return cfg;
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

    /// <summary>取第一个可用的局域网 IPv4（跳过回环与虚拟网卡）。</summary>
    public static string GetLocalIp()
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
