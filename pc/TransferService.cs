using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FeigeP2P;

/// <summary>
/// TCP 点对点文件传输：既做服务端接收，也做客户端发送。
/// 严格遵循 PROTOCOL.md 第 3 节。事件回调在后台线程，UI 层需 Invoke。
/// </summary>
public sealed class TransferService : IDisposable
{
    private readonly AppConfig _config;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private const int BufferSize = 64 * 1024;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    // 事件：(方向, 文件名, 对端名, 大小, 是否成功, 说明)
    public event Action<string, string, string, long, bool, string>? TransferFinished;
    // 进度：(方向, 文件名, 已传输字节, 总字节)
    public event Action<string, string, long, long>? Progress;
    // 通用日志
    public event Action<string>? Log;

    public TransferService(AppConfig config)
    {
        _config = config;
        _listener = new TcpListener(IPAddress.Any, config.TcpPort);
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
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

            var header = await ReadLineAsync(stream, HandshakeTimeout);
            if (header == null) { Log?.Invoke("接收：未收到请求头，已断开"); return; }

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

            var dir = AppConfig.ReceiveDirectory;
            var finalPath = NonOverwritePath(dir, fileName);
            tempPath = finalPath + ".part";

            Log?.Invoke($"收到发送请求：{fileName} ({FormatSize(size)})");
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

            var done = Encoding.UTF8.GetBytes(Protocol.BuildDone(received));
            await stream.WriteAsync(done);

            File.Move(tempPath, finalPath, overwrite: false);
            tempPath = "";
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

    /// <summary>向指定设备发送单个文件。返回 (成功, 说明)。</summary>
    public async Task<(bool, string)> SendFileAsync(Peer target, string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var size = new FileInfo(filePath).Length;
        using var client = new TcpClient();
        try
        {
            var connectTask = client.ConnectAsync(target.Ip, target.TcpPort);
            var timeoutTask = Task.Delay(HandshakeTimeout);
            if (await Task.WhenAny(connectTask, timeoutTask) != connectTask)
                return (false, "连接超时");
            await connectTask;

            client.ReceiveTimeout = (int)HandshakeTimeout.TotalMilliseconds;
            client.SendTimeout = (int)HandshakeTimeout.TotalMilliseconds;
            var stream = client.GetStream();

            var header = Encoding.UTF8.GetBytes(Protocol.BuildFileRequest(fileName, size));
            await stream.WriteAsync(header);

            var ackLine = await ReadLineAsync(stream, HandshakeTimeout);
            if (ackLine == null) return (false, "等待对方应答超时");
            if (!Protocol.ParseAck(ackLine, out var reason))
                return (false, $"对方拒绝：{reason}");

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
        catch (Exception ex)
        {
            Log?.Invoke($"发送失败：{ex.Message}");
            TransferFinished?.Invoke("发送", fileName, target.Name, size, false, ex.Message);
            return (false, ex.Message);
        }
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
        _cts.Dispose();
    }
}
