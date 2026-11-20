using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace FeigeP2P;

/// <summary>
/// TCP 点对点文件传输 + 二维码配对会话。
/// 监听 IPv6 双栈（同时接受 IPv4），文件协议仍是 FG_FILE v1，配对新增 FG_HELLO/FG_HELLO_ACK。
/// 事件回调在后台线程，UI 层需 Invoke。
/// </summary>
public sealed class TransferService : IDisposable
{
    private readonly AppConfig _config;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _localIp;

    // 事件：(方向, 文件名, 对端名, 大小, 是否成功, 说明)
    public event Action<string, string, string, long, bool, string>? TransferFinished;
    // 进度：(方向, 文件名, 已传输字节, 总字节)
    public event Action<string, string, long, long>? Progress;
    // 通用日志
    public event Action<string>? Log;
    // 接收确认：收到发文件请求时询问用户是否接收，并带回用户选择的保存位置。无人订阅则按自动接收处理。
    public event Func<IncomingRequest, Task<ReceiveDecision>>? ConfirmReceive;
    // 配对确认：收到合法的 FG_HELLO 时询问用户是否允许该设备配对。无人订阅则自动允许。
    public event Func<PairRequest, Task<bool>>? ConfirmPair;
    // 配对成功：把对方交给 UI 写入设备列表。
    public event Action<Peer>? PeerPaired;

    private const int BufferSize = 64 * 1024;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    /// <summary>接收方等待用户点「接收/拒绝」的最长时间，超时按拒绝处理。</summary>
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(60);
    /// <summary>发送方等待对方应答的时间，必须长于接收方的确认窗口。</summary>
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(90);
    /// <summary>等待本机用户点「允许配对」的时间。</summary>
    private static readonly TimeSpan PairConfirmTimeout = TimeSpan.FromSeconds(30);
    /// <summary>扫码方等待出示方配对应答的时间。</summary>
    private static readonly TimeSpan PairAckTimeout = TimeSpan.FromSeconds(15);

    private readonly object _pairLock = new();
    private PairSession? _session;

    /// <summary>当前配对会话（收起/刷新后为 null）。</summary>
    public PairSession? CurrentSession
    {
        get { lock (_pairLock) return _session; }
    }

    /// <summary>二维码有效期，默认 10 分钟；测试可注入更短的值。</summary>
    public TimeSpan PairLifetime { get; }

    public TransferService(AppConfig config, TimeSpan? pairLifetime = null)
    {
        _config = config;
        PairLifetime = pairLifetime ?? TimeSpan.FromMinutes(10);
        _localIp = AppConfig.GetLocalIp();

        // 双栈：一个端口同时收 IPv4 与 IPv6；系统不支持 IPv6 时退回纯 IPv4。
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.IPv6Any, config.TcpPort);
            listener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
        }
        catch
        {
            listener = new TcpListener(IPAddress.Any, config.TcpPort);
        }
        _listener = listener;
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>生成新的配对会话（旧 token 立即作废），返回会话供界面生成二维码。</summary>
    public PairSession BeginPairing()
    {
        var session = new PairSession
        {
            Token = PairSession.NewToken(),
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow + PairLifetime
        };
        lock (_pairLock) _session = session;
        return session;
    }

    /// <summary>收起/刷新时作废当前二维码。</summary>
    public void CancelPairing()
    {
        lock (_pairLock) _session = null;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandleIncomingAsync(client));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log?.Invoke($"接收连接异常：{ex.Message}"); }
        }
    }

    private async Task HandleIncomingAsync(TcpClient client)
    {
        var tempPath = "";
        try
        {
            client.ReceiveTimeout = (int)HandshakeTimeout.TotalMilliseconds;
            client.SendTimeout = (int)HandshakeTimeout.TotalMilliseconds;
            var stream = client.GetStream();

            var ep = client.Client.RemoteEndPoint as IPEndPoint;
            var remoteIp = NormalizeIp(ep);
            // 打出对端 IP：若手机发文件时这里连这一行都没有，说明连接根本没到本机（防火墙/网段问题），
            // 而不是协议解析问题。这是区分两类故障最直接的证据。
            if (ep != null) Log?.Invoke($"收到来自 {remoteIp} 的连接");

            var header = await ReadLineAsync(stream, HandshakeTimeout);
            if (header == null) { Log?.Invoke("接收：未收到请求头，已断开"); return; }

            // 配对握手：短连接，校验 token 后即断开，不进入文件流程
            if (header.StartsWith(Protocol.HelloMagic + "|", StringComparison.Ordinal))
            {
                await HandlePairAsync(client, stream, header, remoteIp);
                return;
            }

            if (!Protocol.ParseFileRequest(header, out var fileName, out var size, out _))
            {
                var reject = Encoding.UTF8.GetBytes(Protocol.BuildAckReject("请求头格式错误"));
                await stream.WriteAsync(reject);
                Log?.Invoke("接收：请求头非法，已拒绝");
                return;
            }

            // 安全检查：只允许文件名，不允许路径分量
            fileName = Path.GetFileName(fileName.Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "未命名文件";

            // － 接收确认：先问用户，同意才建文件、才回 ACK OK －
            var decision = new ReceiveDecision(true);
            var confirm = ConfirmReceive;
            if (confirm != null)
            {
                Log?.Invoke($"收到发送请求：{fileName} ({FormatSize(size)}) 来自 {remoteIp}，等待确认…");
                try
                {
                    var ask = confirm(new IncomingRequest(fileName, size, remoteIp));
                    var finished = await Task.WhenAny(ask, Task.Delay(ConfirmTimeout));
                    decision = finished == ask ? await ask : new ReceiveDecision(false);
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"接收确认异常：{ex.Message}");
                    decision = new ReceiveDecision(false);
                }
            }
            else
            {
                Log?.Invoke($"收到发送请求：{fileName} ({FormatSize(size)})");
            }

            if (!decision.Accept)
            {
                try
                {
                    var reject = Encoding.UTF8.GetBytes(Protocol.BuildAckReject("对方拒绝接收"));
                    await stream.WriteAsync(reject);
                }
                catch { /* 对方可能已超时断开，不影响结论 */ }
                Log?.Invoke($"已拒绝接收：{fileName}（已通知对方）");
                TransferFinished?.Invoke("收到", fileName, remoteIp, size, false, "已拒绝接收");
                return;
            }

            // 保存位置：用户在确认框里指定的优先，否则用默认收件夹（自动避免覆盖）
            string finalPath;
            if (!string.IsNullOrWhiteSpace(decision.SavePath))
            {
                finalPath = decision.SavePath!;
                try
                {
                    var parent = Path.GetDirectoryName(finalPath);
                    if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"创建保存目录失败：{ex.Message}，改用默认收件夹");
                    finalPath = NonOverwritePath(AppConfig.ReceiveDirectory, fileName);
                }
            }
            else
            {
                finalPath = NonOverwritePath(AppConfig.ReceiveDirectory, fileName);
            }
            tempPath = finalPath + ".part";

            var ack = Encoding.UTF8.GetBytes(Protocol.BuildAckOk());
            await stream.WriteAsync(ack);

            // 传输阶段：关闭握手超时，改用 ReadTimeout 保活
            client.ReceiveTimeout = 60000;
            long received = 0;
            var buffer = new byte[BufferSize];
            // 用显式 using 块：必须在重命名前释放文件流（Windows 不允许移动被占用的文件）
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                while (received < size)
                {
                    var want = (int)Math.Min(buffer.Length, size - received);
                    var read = await stream.ReadAsync(buffer.AsMemory(0, want), _cts.Token);
                    if (read == 0) throw new IOException("对端在传输完成前断开");
                    await fs.WriteAsync(buffer.AsMemory(0, read), _cts.Token);
                    received += read;
                    Progress?.Invoke("收到", fileName, received, size);
                }
                await fs.FlushAsync();
            } // 文件流已关闭，锁释放

            // 先落盘再回 DONE：让发送方的「完成」意味着文件确实已就位
            // 用户自己指定过路径时允许覆盖（保存对话框里已确认过）；否则绝不覆盖同名文件
            File.Move(tempPath, finalPath, overwrite: !string.IsNullOrWhiteSpace(decision.SavePath));
            tempPath = "";

            var done = Encoding.UTF8.GetBytes(Protocol.BuildDone(received));
            await stream.WriteAsync(done);

            Log?.Invoke($"接收完成：{fileName} → {finalPath}");
            TransferFinished?.Invoke("收到", fileName, "对方", size, true, finalPath);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"接收失败：{ex.Message}");
            TransferFinished?.Invoke("收到", "未知", "对方", 0, false, ex.Message);
        }
        finally
        {
            if (tempPath.Length > 0)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
            try { client.Close(); } catch { }
        }
    }

    // ---------------- 配对握手 ----------------

    private async Task HandlePairAsync(TcpClient client, NetworkStream stream, string header, string remoteIp)
    {
        var req = Protocol.ParseHello(header);
        if (req == null)
        {
            await TryWriteAsync(stream, Protocol.BuildHelloAckReject("配对请求格式错误"));
            Log?.Invoke("配对：请求格式非法，已拒绝");
            return;
        }

        var (ok, reason) = ValidateSession(req.Token);
        if (!ok)
        {
            await TryWriteAsync(stream, Protocol.BuildHelloAckReject(reason));
            Log?.Invoke($"配对被拒绝：{req.Name}（{reason}）");
            return;
        }

        Log?.Invoke($"收到配对请求：{req.Name} ({remoteIp})，等待确认…");

        var accepted = true;
        var ask = ConfirmPair;
        if (ask != null)
        {
            try
            {
                var task = ask(new PairRequest(req.Name, remoteIp, req.TcpPort));
                var finished = await Task.WhenAny(task, Task.Delay(PairConfirmTimeout));
                accepted = finished == task && await task;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"配对确认异常：{ex.Message}");
                accepted = false;
            }
        }

        if (!accepted)
        {
            await TryWriteAsync(stream, Protocol.BuildHelloAckReject("对方拒绝配对"));
            Log?.Invoke($"已拒绝配对：{req.Name}（已通知对方）");
            return;
        }

        // 用户确认期间 token 可能已过期，或被另一个并发请求用掉：这里必须再校验一次并原子消费
        var (consumed, consumeReason) = ConsumeSession(req.Token, req.Name);
        if (!consumed)
        {
            await TryWriteAsync(stream, Protocol.BuildHelloAckReject(consumeReason));
            Log?.Invoke($"配对失败：{req.Name}（{consumeReason}）");
            return;
        }

        await TryWriteAsync(stream, Protocol.BuildHelloAckOk(_config.DeviceName, _localIp, _config.TcpPort));

        var peer = new Peer
        {
            Name = req.Name,
            Ip = remoteIp,
            TcpPort = req.TcpPort,
            IsPaired = true,
            PairedAt = DateTime.Now,
            JustPaired = true
        };
        Log?.Invoke($"配对成功：{peer.Name}（{peer.Address}）已加入设备列表");
        PeerPaired?.Invoke(peer);
    }

    private (bool Ok, string Reason) ValidateSession(string token)
    {
        lock (_pairLock)
        {
            if (_session == null) return (false, "未在等待配对，请让对方刷新二维码");
            if (_session.Used) return (false, "该二维码已被使用，请刷新后重试");
            if (_session.IsExpired) return (false, "二维码已过期，请让对方刷新");
            if (!FixedTimeEquals(_session.Token, token)) return (false, "配对码不正确，请重新扫码");
            return (true, "");
        }
    }

    private (bool Ok, string Reason) ConsumeSession(string token, string peerName)
    {
        lock (_pairLock)
        {
            if (_session == null) return (false, "配对会话已结束，请刷新二维码");
            if (_session.Used) return (false, "该二维码已被使用，请刷新后重试");
            if (_session.IsExpired) return (false, "二维码已过期，请让对方刷新");
            if (!FixedTimeEquals(_session.Token, token)) return (false, "配对码不正确，请重新扫码");
            _session.Used = true;
            _session.PeerName = peerName;
            return (true, "");
        }
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a);
        var y = Encoding.UTF8.GetBytes(b);
        return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
    }

    private static async Task TryWriteAsync(NetworkStream stream, string text)
    {
        try { await stream.WriteAsync(Encoding.UTF8.GetBytes(text)); } catch { }
    }

    // ---------------- 发送 ----------------

    /// <summary>扫码方：连接出示方完成配对，返回对方信息（由 UI 写入设备列表）。</summary>
    public async Task<(bool Ok, string Message, Peer? Peer)> PairWithAsync(
        string ip, int tcpPort, string token, string myName)
    {
        var host = NormalizeHost(ip);
        using var client = new TcpClient();
        try
        {
            var connectTask = ConnectAsync(client, host, tcpPort);
            if (await Task.WhenAny(connectTask, Task.Delay(HandshakeTimeout)) != connectTask)
                return (false, "连接超时，请确认两台设备在同一局域网", null);
            await connectTask;

            client.ReceiveTimeout = (int)PairAckTimeout.TotalMilliseconds;
            client.SendTimeout = (int)HandshakeTimeout.TotalMilliseconds;
            var stream = client.GetStream();

            await stream.WriteAsync(Encoding.UTF8.GetBytes(Protocol.BuildHello(token, myName, _config.TcpPort)));
            var ackLine = await ReadLineAsync(stream, PairAckTimeout);
            if (ackLine == null) return (false, "对方未回应配对请求", null);

            var ack = Protocol.ParseHelloAck(ackLine);
            if (!ack.Ok) return (false, string.IsNullOrWhiteSpace(ack.Reason) ? "对方拒绝配对" : ack.Reason, null);

            var peer = new Peer
            {
                Name = ack.Name,
                Ip = NormalizeHost(ack.Ip),
                TcpPort = ack.TcpPort,
                IsPaired = true,
                PairedAt = DateTime.Now,
                JustPaired = true
            };
            Log?.Invoke($"配对成功：{peer.Name}（{peer.Address}）已加入设备列表");
            return (true, $"已与 {peer.Name} 配对", peer);
        }
        catch (SocketException)
        {
            return (false, "设备不可达，请让对方重新出示二维码", null);
        }
        catch (Exception ex)
        {
            return (false, $"配对失败：{ex.Message}", null);
        }
    }

    /// <summary>向指定设备发送单个文件。返回 (成功, 说明)。</summary>
    public async Task<(bool, string)> SendFileAsync(Peer target, string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var size = new FileInfo(filePath).Length;
        using var client = new TcpClient();
        try
        {
            var connectTask = ConnectAsync(client, NormalizeHost(target.Ip), target.TcpPort);
            var timeoutTask = Task.Delay(HandshakeTimeout);
            if (await Task.WhenAny(connectTask, timeoutTask) != connectTask)
                return (false, "连接超时，设备可能已离线");
            await connectTask;

            client.ReceiveTimeout = (int)HandshakeTimeout.TotalMilliseconds;
            client.SendTimeout = (int)HandshakeTimeout.TotalMilliseconds;
            var stream = client.GetStream();

            var header = Encoding.UTF8.GetBytes(Protocol.BuildFileRequest(fileName, size));
            await stream.WriteAsync(header);

            // 对方要先弹窗询问用户是否接收，等待时间必须覆盖它的 60 秒确认窗口
            Log?.Invoke($"已发送请求，等待 {target.Name} 确认接收…");
            client.ReceiveTimeout = (int)AckTimeout.TotalMilliseconds;
            var ackLine = await ReadLineAsync(stream, AckTimeout);
            if (ackLine == null) return (false, "对方未在规定时间内确认接收");
            if (!Protocol.ParseAck(ackLine, out var reason))
                return (false, string.IsNullOrWhiteSpace(reason) ? "对方拒绝了本次传输" : $"对方拒绝：{reason}");

            // 传输阶段
            client.ReceiveTimeout = 60000;
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            var buffer = new byte[BufferSize];
            long sent = 0;
            while (sent < size)
            {
                var want = (int)Math.Min(buffer.Length, size - sent);
                var read = await fs.ReadAsync(buffer.AsMemory(0, want), _cts.Token);
                if (read == 0) break;
                await stream.WriteAsync(buffer.AsMemory(0, read), _cts.Token);
                sent += read;
                Progress?.Invoke("发送", fileName, sent, size);
            }
            await stream.FlushAsync();

            var doneLine = await ReadLineAsync(stream, HandshakeTimeout);
            long received = -1;
            if (doneLine == null || !Protocol.ParseDone(doneLine, out received) || received != sent)
                return (false, $"对方确认数不符（期望 {sent}，实收 {received}）");

            var msg = $"已发送 {fileName} 给 {target.Name} ({FormatSize(size)})";
            Log?.Invoke(msg);
            TransferFinished?.Invoke("发送", fileName, target.Name, size, true, msg);
            return (true, msg);
        }
        catch (SocketException)
        {
            Log?.Invoke("发送失败：设备不可达");
            TransferFinished?.Invoke("发送", fileName, target.Name, size, false, "设备不可达");
            return (false, "设备不可达，请让对方重新出示二维码后重试");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"发送失败：{ex.Message}");
            TransferFinished?.Invoke("发送", fileName, target.Name, size, false, ex.Message);
            return (false, ex.Message);
        }
    }

    private static async Task ConnectAsync(TcpClient client, string host, int port)
    {
        if (IPAddress.TryParse(host, out var addr))
            await client.ConnectAsync(addr, port);
        else
            await client.ConnectAsync(host, port);
    }

    private static string NormalizeHost(string ip) => ip.Trim().Trim('[', ']');

    private static string NormalizeIp(IPEndPoint? ep)
    {
        var a = ep?.Address;
        if (a == null) return "对方";
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        return a.ToString();
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var sb = new StringBuilder();
        var one = new byte[1];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(one.AsMemory(0, 1), cts.Token);
                if (read == 0) break;
                var ch = (char)one[0];
                if (ch == '\n') break;
                if (ch != '\r') sb.Append(ch);
                if (sb.Length > 8192) break; // 防止恶意超长行
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch (OperationCanceledException) { return null; }
        catch { return null; }
    }

    private static string NonOverwritePath(string dir, string fileName)
    {
        var full = Path.Combine(dir, fileName);
        if (!File.Exists(full)) return full;
        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{name} ({Guid.NewGuid():N}){ext}");
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB"
    };

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        CancelPairing();
        _cts.Dispose();
    }
}
