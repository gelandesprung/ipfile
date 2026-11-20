using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Concurrent;

namespace FeigeP2P;

/// <summary>
/// UDP 局域网发现：每 2 秒广播自己，监听他人广播，6 秒未收到则移除。
/// 所有事件回调发生在后台线程，UI 层需自行 Invoke。
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    private readonly AppConfig _config;
    private UdpClient? _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, Peer> _peers = new();
    private readonly string _localIp;
    private string _deviceName;

    /// <summary>设备离线判定的静默时长：广播间隔 2 秒，容忍 6 次丢包。</summary>
    private const int TtlSeconds = 12;

    public IReadOnlyDictionary<string, Peer> Peers => _peers;
    public event Action<Peer, PeerChange>? PeerChanged;
    public event Action<string>? Diag; // 诊断日志（调试发现问题时用）

    public DiscoveryService(AppConfig config)
    {
        _config = config;
        _localIp = AppConfig.GetLocalIp();
        _deviceName = config.DeviceName;
    }

    public string LocalIp => _localIp;

    public void Start()
    {
        // 必须先设 ReuseAddress 再 Bind，否则 .NET 抛 "socket must not be bound"。
        // 用无参构造 + Client.SetSocketOption + Bind 的顺序。
        _udp = new UdpClient
        {
            EnableBroadcast = true,
            ExclusiveAddressUse = false
        };
        try { _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); }
        catch { }
        try { _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _config.UdpPort)); }
        catch (Exception ex)
        {
            _udp.Close();
            _udp = null;
            throw new InvalidOperationException($"无法绑定 UDP 端口 {_config.UdpPort}（可能被其他程序占用）：{ex.Message}", ex);
        }
        _ = Task.Run(ReceiveLoopAsync);
        _ = Task.Run(BroadcastLoopAsync);
        _ = Task.Run(CleanupLoopAsync);
        BroadcastOnce(); // 启动即宣告，缩短发现延迟
    }

    public void SetDeviceName(string newName)
    {
        _deviceName = newName;
        BroadcastOnce(); // 立即让对方刷新显示名
    }

    public void BroadcastOnce(bool bye = false)
    {
        try
        {
            var text = Protocol.BuildDiscovery(_deviceName, _localIp, _config.TcpPort, bye);
            var bytes = Encoding.UTF8.GetBytes(text);
            _udp?.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, _config.UdpPort));
        }
        catch { }
    }

    private async Task ReceiveLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var result = await _udp!.ReceiveAsync(_cts.Token);
                var text = Encoding.UTF8.GetString(result.Buffer);
                var pkt = Protocol.ParseDiscovery(text);
                if (pkt == null) { Diag?.Invoke($"丢弃非法包：{text.Trim()}"); continue; }
                var peer = pkt.Peer;
                if (peer.Ip == _localIp) continue; // 忽略自己（含本机多实例）

                Diag?.Invoke($"收到{(pkt.Bye ? "BYE" : "广播")}：{peer.Name} {peer.Ip}:{peer.TcpPort}");

                if (pkt.Bye)
                {
                    if (_peers.TryRemove(peer.Key, out _)) PeerChanged?.Invoke(peer, PeerChange.Removed);
                    continue;
                }

                peer.LastSeen = DateTime.Now;
                var isNew = !_peers.ContainsKey(peer.Key);
                _peers[peer.Key] = peer;
                // 注意：周期性广播会反复命中同一设备，必须区分「新增」和「刷新」，
                // 否则会被误判成「离线」，把设备从列表里删掉。
                PeerChanged?.Invoke(peer, isNew ? PeerChange.Added : PeerChange.Updated);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async Task BroadcastLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            BroadcastOnce();
            try { await Task.Delay(2000, _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task CleanupLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(1000, _cts.Token); }
            catch (OperationCanceledException) { break; }
            var now = DateTime.Now;
            foreach (var kv in _peers)
            {
                if ((now - kv.Value.LastSeen).TotalSeconds > TtlSeconds &&
                    _peers.TryRemove(kv.Key, out var removed))
                    PeerChanged?.Invoke(removed, PeerChange.Removed);
            }
        }
    }

    public void Dispose()
    {
        BroadcastOnce(bye: true);
        _cts.Cancel();
        _udp?.Close();
        _cts.Dispose();
    }
}
