using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FeigeP2P;

/// <summary>浏览器访客：只活在网页模式里，不进 P2P 设备列表（两者是不同的传输通道）。</summary>
public sealed class WebGuest
{
    public string Id { get; init; } = "";
    public string Nickname { get; set; } = "";
    public string RemoteIp { get; init; } = "";
    public DateTime JoinedAt { get; init; } = DateTime.Now;
    /// <summary>列表里显示的当前状态：空闲 / 待确认 / 传输中 / 上传中。</summary>
    public string Status { get; set; } = "空闲";
    internal SseChannel? Channel { get; set; }

    /// <summary>列表控件直接显示昵称。</summary>
    public override string ToString() => Nickname;
}

/// <summary>
/// 一条 SSE 通道（浏览器 EventSource）。写失败即判定断开，
/// 与 WebSocket 相比少了一层帧编解码，语义完全相同：只走信令，文件本体走普通 HTTP。
/// </summary>
internal sealed class SseChannel
{
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SseChannel(NetworkStream stream) => _stream = stream;

    public bool Alive { get; private set; } = true;

    public async Task SendAsync(string json, CancellationToken ct)
    {
        if (!Alive) return;
        await SendRawAsync($"data: {json}\n\n", ct);
    }

    public async Task SendRawAsync(string text, CancellationToken ct)
    {
        if (!Alive) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        await _writeLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(bytes, ct);
            await _stream.FlushAsync(ct);
        }
        catch
        {
            Alive = false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Close() => Alive = false;
}

/// <summary>Host 发出的一笔「文件发送请求」：访客确认后，凭 token 走 HTTP 取文件。</summary>
internal sealed class FileOffer
{
    public string Id { get; init; } = "";
    public string Token { get; init; } = "";
    public string GuestId { get; init; } = "";
    public string GuestName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public long Size { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    /// <summary>pending / accepted / rejected / expired / done</summary>
    public string State { get; set; } = "pending";
    public TaskCompletionSource<string> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>一次已获 Host 批准的上传：访客随后 PUT 内容，服务端流式写入 .part。</summary>
internal sealed class UploadSlot
{
    public string Id { get; init; } = "";
    public string GuestId { get; init; } = "";
    public string GuestName { get; init; } = "";
    public string FileName { get; init; } = "";
    public long Size { get; init; }
    public string SavePath { get; init; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// 网页模式的 Host 端服务：给没装 App 的朋友一个浏览器入口。
///
/// 设计要点（与 shared/WEB_MODE_UX.md 对齐）：
/// ① 自己实现 HTTP/1.1（TcpListener 双栈），不用 HttpListener——后者绑非回环地址要管理员权限/urlacl，
///    而这个程序一直是免安装、免提权运行的。
/// ② 信令用 SSE（EventSource）而不是 WebSocket：语义相同（服务端单向推送），客户端自动重连，
///    省掉一整层帧编解码，断线检测也更直接（TCP EOF 即断开）。
/// ③ 文件本体永远走普通 HTTP：下载流式返回、上传流式落盘，不会把大文件塞进内存。
/// ④ 上传是两阶段：/api/upload/offer 先问 Host（复用现有接收确认框），同意后才允许 PUT 内容。
/// </summary>
public sealed class WebHostService : IDisposable
{
    private const int BufferSize = 64 * 1024;
    private static readonly TimeSpan OfferTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan UploadConfirmTimeout = TimeSpan.FromSeconds(60);
    /// <summary>已加入但事件通道一直没建立的访客，超过这个时间视为无效会话。</summary>
    private static readonly TimeSpan OrphanGuestTtl = TimeSpan.FromSeconds(60);

    private readonly AppConfig _config;
    private readonly object _lock = new();
    private readonly Dictionary<string, WebGuest> _guests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileOffer> _offers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UploadSlot> _uploads = new(StringComparer.Ordinal);

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private System.Threading.Timer? _maintenance;
    private string _pin = "";
    private DateTime _lastRaise = DateTime.MinValue;
    /// <summary>连续输错 PIN 的次数，满 3 次自动换新码（换后清零）。</summary>
    private int _pinFailures;
    /// <summary>连续输错几次后自动更换 PIN。</summary>
    private const int PinFailureLimit = 3;

    public WebHostService(AppConfig config) => _config = config;

    /// <summary>实际监听端口（可能因占用而与配置值不同）。</summary>
    public int Port { get; private set; }

    public bool Running { get; private set; }

    public string Pin
    {
        get { lock (_lock) return _pin; }
    }

    /// <summary>访客列表变化（上线/离线/状态变更），UI 需切回自己的线程。</summary>
    public event Action? GuestsChanged;
    /// <summary>PIN 因连续输错被自动更换，参数是新 PIN；UI 应刷新显示并提示。</summary>
    public event Action<string>? PinRotated;
    public event Action<string>? Log;
    /// <summary>(方向, 文件名, 对端名, 已传, 总量)</summary>
    public event Action<string, string, string, long, long>? Progress;
    /// <summary>(方向, 文件名, 对端名, 大小, 是否成功, 说明)</summary>
    public event Action<string, string, string, long, bool, string>? Finished;
    /// <summary>访客上传：先问 Host 是否接收，并带回保存位置。无人订阅则自动接收。</summary>
    public event Func<IncomingRequest, Task<ReceiveDecision>>? ConfirmUpload;

    // ---------------- 生命周期 ----------------

    public (bool Ok, string Message) Start(int preferredPort)
    {
        if (Running) return (true, $"网页服务已在运行 · 端口 {Port}");

        var start = Math.Clamp(preferredPort, 1024, 65535);
        TcpListener? listener = null;
        var used = start;
        string lastError = "";
        for (var p = start; p < Math.Min(start + 12, 65536); p++)
        {
            if (TryBind(p, out var l, out var err))
            {
                listener = l;
                used = p;
                break;
            }
            lastError = err;
        }

        if (listener == null)
            return (false, $"端口 {start}–{start + 11} 都不可用：{lastError}");

        _listener = listener;
        _cts = new CancellationTokenSource();
        Port = used;
        Running = true;
        lock (_lock)
        {
            _pin = NewPin();
            _pinFailures = 0;
        }

        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _maintenance = new System.Threading.Timer(_ => Sweep(), null, 10_000, 10_000);

        return (true, used == start
            ? $"网页服务已启动 · 端口 {used}（局域网内浏览器可访问）"
            : $"端口 {start} 被占用，已改用 {used}");
    }

    private static bool TryBind(int port, out TcpListener? listener, out string error)
    {
        // 双栈：一个端口同时接受 IPv4 与 IPv6，地址族切换只影响显示的 URL
        try
        {
            var l = new TcpListener(IPAddress.IPv6Any, port);
            l.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
            l.Start();
            listener = l;
            error = "";
            return true;
        }
        catch
        {
            try
            {
                var l = new TcpListener(IPAddress.Any, port);
                l.Start();
                listener = l;
                error = "";
                return true;
            }
            catch (Exception ex)
            {
                listener = null;
                error = ex.Message;
                return false;
            }
        }
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _maintenance?.Dispose();
        _maintenance = null;

        List<WebGuest> guests;
        lock (_lock)
        {
            guests = _guests.Values.ToList();
            _guests.Clear();
            foreach (var offer in _offers.Values) offer.Decision.TrySetResult("stop");
            _offers.Clear();
            _uploads.Clear();
        }
        foreach (var g in guests)
        {
            try { _ = g.Channel?.SendRawAsync("data: {\"type\":\"shutdown\"}\n\n", CancellationToken.None); } catch { }
            g.Channel?.Close();
        }
        if (guests.Count > 0) Log?.Invoke($"网页服务已停止，{guests.Count} 位访客已断开");
        else Log?.Invoke("网页服务已停止");
        RaiseGuestsChanged(force: true);
    }

    /// <summary>刷新 PIN：只挡新访客，已在线的访客不受影响（决策1：不踢人）。</summary>
    public string RefreshPin()
    {
        lock (_lock)
        {
            _pin = NewPin();
            _pinFailures = 0;
        }
        Log?.Invoke($"访问 PIN 已刷新为 {Pin}（已在线的访客不受影响）");
        return Pin;
    }

    private static string NewPin() => RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

    public IReadOnlyList<WebGuest> SnapshotGuests()
    {
        lock (_lock) return _guests.Values.OrderBy(g => g.JoinedAt).ToList();
    }

    public bool DisconnectGuest(string guestId)
    {
        WebGuest? g;
        lock (_lock) _guests.TryGetValue(guestId, out g);
        if (g == null) return false;
        g.Channel?.Close();
        RemoveGuest(g, "已被主机断开");
        return true;
    }

    // ---------------- 发送文件给访客 ----------------

    /// <summary>给指定访客逐个发文件；多个访客并行，各自独立确认、互不影响。</summary>
    public async Task SendFilesAsync(IReadOnlyList<string> guestIds, IReadOnlyList<string> files)
    {
        var tasks = guestIds
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .Select(id => SendToGuestAsync(id, files))
            .ToArray();
        await Task.WhenAll(tasks);
    }

    private async Task SendToGuestAsync(string guestId, IReadOnlyList<string> files)
    {
        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                Log?.Invoke($"文件不存在：{file}");
                continue;
            }

            WebGuest? guest;
            lock (_lock) _guests.TryGetValue(guestId, out guest);
            if (guest == null)
            {
                Log?.Invoke("访客已离线，发送已取消");
                return;
            }

            var offer = new FileOffer
            {
                Id = NewId(8),
                Token = NewId(24),
                GuestId = guest.Id,
                GuestName = guest.Nickname,
                FilePath = file,
                FileName = Path.GetFileName(file),
                Size = new FileInfo(file).Length
            };
            lock (_lock) _offers[offer.Id] = offer;

            guest.Status = $"待确认 · {offer.FileName}";
            RaiseGuestsChanged(force: true);
            Log?.Invoke($"已向 {guest.Nickname} 发出「{offer.FileName}」（{TransferService.FormatSize(offer.Size)}），等待对方确认…");

            await PushAsync(guest, new
            {
                type = "file_offer",
                id = offer.Id,
                token = offer.Token,
                name = offer.FileName,
                size = offer.Size,
                ttl = (int)OfferTtl.TotalSeconds
            });

            var decision = await WaitAsync(offer.Decision.Task, OfferTtl);
            if (decision != "accept")
            {
                offer.State = decision == "reject" ? "rejected" : "expired";
                var reason = decision == "reject" ? "对方拒绝接收" : "超时未确认，已自动取消";
                guest.Status = "空闲";
                RaiseGuestsChanged(force: true);
                await PushAsync(guest, new { type = "offer_cancel", id = offer.Id, reason });
                Log?.Invoke($"{offer.FileName} → {guest.Nickname}：{reason}");
                Finished?.Invoke("发送", offer.FileName, guest.Nickname, offer.Size, false, reason);
                lock (_lock) _offers.Remove(offer.Id);
                continue;
            }

            // 已确认：等访客来取文件（取完由 blob 处理函数置 done）
            guest.Status = $"传输中 · {offer.FileName}";
            RaiseGuestsChanged(force: true);
            var timeout = TimeSpan.FromSeconds(60 + offer.Size / (256.0 * 1024));
            var finished = await WaitAsync(offer.Done.Task, timeout);
            if (!finished)
            {
                offer.State = "expired";
                guest.Status = "空闲";
                RaiseGuestsChanged(force: true);
                Log?.Invoke($"{offer.FileName} → {guest.Nickname}：对方已确认但未完成下载（超时）");
                Finished?.Invoke("发送", offer.FileName, guest.Nickname, offer.Size, false, "对方未完成下载");
                lock (_lock) _offers.Remove(offer.Id);
            }
        }
    }

    private static async Task<T?> WaitAsync<T>(Task<T> task, TimeSpan timeout)
    {
        var done = await Task.WhenAny(task, Task.Delay(timeout));
        return done == task ? await task : default;
    }

    private async Task PushAsync(WebGuest guest, object payload)
    {
        var channel = guest.Channel;
        if (channel == null) return;
        await channel.SendAsync(JsonSerializer.Serialize(payload), CancellationToken.None);
    }

    // ---------------- HTTP 接入 ----------------

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener == null) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                Log?.Invoke($"网页服务连接异常：{ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var remote = NormalizeRemote(client.Client.RemoteEndPoint as IPEndPoint);
            var req = await ReadRequestAsync(stream, remote, ct);
            if (req == null) return;
            await DispatchAsync(req, ct);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            Log?.Invoke($"网页请求处理失败：{ex.Message}");
        }
        finally
        {
            try { client.Close(); } catch { }
        }
    }

    private static string NormalizeRemote(IPEndPoint? ep)
    {
        var a = ep?.Address;
        if (a == null) return "?";
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        return a.ToString();
    }

    private async Task DispatchAsync(HttpRequest req, CancellationToken ct)
    {
        var path = req.Path;

        if (req.Method == "GET" && (path == "/" || path == "/index.html"))
        {
            await WriteTextAsync(req, 200, "text/html; charset=utf-8", GuestPage.Html, ct);
            return;
        }

        if (path == "/favicon.ico")
        {
            await WriteStatusAsync(req, 204, ct);
            return;
        }

        // 大请求体前先应答 100-continue，否则部分浏览器会干等
        if (req.Headers.TryGetValue("expect", out var expect) &&
            expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
        {
            await req.Stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), ct);
            await req.Stream.FlushAsync(ct);
        }

        if (path == "/api/join" && req.Method == "POST")
        {
            await HandleJoinAsync(req, ct);
            return;
        }

        if (path == "/api/events" && req.Method == "GET")
        {
            await ServeEventsAsync(req, ct);
            return;
        }

        if (path.StartsWith("/api/offer/", StringComparison.Ordinal) && req.Method == "POST")
        {
            await HandleOfferDecisionAsync(req, path, ct);
            return;
        }

        if (path.StartsWith("/api/blob/", StringComparison.Ordinal) && req.Method == "GET")
        {
            await ServeBlobAsync(req, path["/api/blob/".Length..], ct);
            return;
        }

        if (path == "/api/upload/offer" && req.Method == "POST")
        {
            await HandleUploadOfferAsync(req, ct);
            return;
        }

        if (path.StartsWith("/api/upload/", StringComparison.Ordinal) && req.Method == "PUT")
        {
            await HandleUploadPutAsync(req, path["/api/upload/".Length..], ct);
            return;
        }

        await WriteJsonAsync(req, 404, new { ok = false, error = "接口不存在" }, ct);
    }

    // ----- 加入 -----

    private async Task HandleJoinAsync(HttpRequest req, CancellationToken ct)
    {
        var body = await ReadBodyStringAsync(req, 4096, ct);
        string nickname = "";
        string pin = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("nickname", out var n)) nickname = n.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("pin", out var p)) pin = p.GetString() ?? "";
        }
        catch
        {
            await WriteJsonAsync(req, 200, new { ok = false, error = "请求格式错误" }, ct);
            return;
        }

        if (!string.Equals(pin.Trim(), Pin, StringComparison.Ordinal))
        {
            // 连续输错满 3 次就把 PIN 换掉：熟人场景下大概率是记错了，
            // 换码比反复试错更省事，也顺手挡掉在门外逐个试的陌生人。
            string? rotated = null;
            int failures;
            lock (_lock)
            {
                failures = ++_pinFailures;
                if (failures >= PinFailureLimit)
                {
                    _pin = NewPin();
                    _pinFailures = 0;
                    rotated = _pin;
                }
            }

            if (rotated != null)
            {
                Log?.Invoke($"连续 {PinFailureLimit} 次 PIN 输错（来自 {req.RemoteIp}），已自动更换新 PIN：{rotated}");
                PinRotated?.Invoke(rotated);
                await WriteJsonAsync(req, 200, new
                {
                    ok = false,
                    error = "PIN 已因多次输错而更换，请向主机索取新的 PIN"
                }, ct);
                return;
            }

            Log?.Invoke($"访客加入被拒绝（PIN 不正确，来自 {req.RemoteIp}，第 {failures} 次）");
            await WriteJsonAsync(req, 200, new { ok = false, error = "PIN 不正确，请向主机确认" }, ct);
            return;
        }

        lock (_lock) _pinFailures = 0;
        nickname = SanitizeNickname(nickname);
        var guest = new WebGuest
        {
            Id = NewId(6),
            Nickname = UniqueNickname(nickname),
            RemoteIp = req.RemoteIp
        };
        lock (_lock) _guests[guest.Id] = guest;

        Log?.Invoke($"{guest.Nickname} 已通过浏览器加入（{guest.RemoteIp}）");
        RaiseGuestsChanged(force: true);
        await WriteJsonAsync(req, 200, new
        {
            ok = true,
            clientId = guest.Id,
            nickname = guest.Nickname,
            host = _config.DeviceName
        }, ct);
    }

    private static string SanitizeNickname(string raw)
    {
        var sb = new StringBuilder();
        foreach (var c in raw.Trim())
            if (!char.IsControl(c)) sb.Append(c);
        var s = sb.ToString().Trim();
        if (s.Length == 0) s = "访客";
        return s.Length > 16 ? s[..16] : s;
    }

    private string UniqueNickname(string nickname)
    {
        lock (_lock)
        {
            if (!_guests.Values.Any(g => string.Equals(g.Nickname, nickname, StringComparison.Ordinal)))
                return nickname;
            for (var i = 2; i < 100; i++)
            {
                var candidate = $"{nickname} ({i})";
                if (!_guests.Values.Any(g => string.Equals(g.Nickname, candidate, StringComparison.Ordinal)))
                    return candidate;
            }
            return nickname + " " + NewId(2);
        }
    }

    // ----- 信令通道 -----

    private async Task ServeEventsAsync(HttpRequest req, CancellationToken ct)
    {
        var id = req.Query.TryGetValue("c", out var c) ? c : "";
        WebGuest? guest;
        lock (_lock) _guests.TryGetValue(id, out guest);
        if (guest == null)
        {
            await WriteJsonAsync(req, 200, new { ok = false, error = "会话已失效，请重新加入" }, ct);
            return;
        }

        var head =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/event-stream; charset=utf-8\r\n" +
            "Cache-Control: no-cache, no-store\r\n" +
            "Connection: keep-alive\r\n" +
            "X-Accel-Buffering: no\r\n\r\n";
        await req.Stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await req.Stream.FlushAsync(ct);

        var channel = new SseChannel(req.Stream);
        SseChannel? previous;
        lock (_lock)
        {
            // 先登记新通道再关旧的：否则旧通道的收尾会认为"当前通道还是我"，
            // 把刚重连上来的访客从列表里删掉（网络抖动后 EventSource 会带同一个 id 重连）
            previous = guest.Channel;
            guest.Channel = channel;
        }
        previous?.Close();

        // 首帧快照：刷新页面后能恢复身份，也能看到还没处理完的发送请求
        await channel.SendAsync(JsonSerializer.Serialize(new
        {
            type = "snapshot",
            nickname = guest.Nickname,
            host = _config.DeviceName,
            online = SnapshotGuests().Count
        }), ct);

        List<object> pending = new();
        lock (_lock)
        {
            foreach (var o in _offers.Values.Where(o => o.GuestId == guest.Id && o.State == "pending"))
            {
                pending.Add(new
                {
                    type = "file_offer",
                    id = o.Id,
                    token = o.Token,
                    name = o.FileName,
                    size = o.Size,
                    ttl = Math.Max(1, (int)(OfferTtl - (DateTime.UtcNow - o.CreatedAt)).TotalSeconds)
                });
            }
        }
        foreach (var p in pending) await channel.SendAsync(JsonSerializer.Serialize(p), ct);

        guest.Status = "空闲";
        RaiseGuestsChanged(force: true);

        try
        {
            // 客户端只会发心跳/关闭，这里读到 EOF 就说明页面关了
            var readTask = Task.Run(async () =>
            {
                var buf = new byte[512];
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var n = await req.Stream.ReadAsync(buf, ct);
                        if (n == 0) break;
                    }
                }
                catch { }
            }, ct);

            while (!ct.IsCancellationRequested && channel.Alive)
            {
                var done = await Task.WhenAny(readTask, Task.Delay(15_000, ct));
                if (done == readTask) break;
                await channel.SendRawAsync(": ping\n\n", ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            channel.Close();
            bool current;
            lock (_lock)
            {
                current = ReferenceEquals(guest.Channel, channel);
                if (current) guest.Channel = null;
            }
            // 只有"当前这条"通道断开才算访客离线；被新连接顶掉的旧通道不算
            if (current) RemoveGuest(guest, "已断开");
        }
    }

    private void RemoveGuest(WebGuest guest, string reason)
    {
        lock (_lock)
        {
            if (!_guests.Remove(guest.Id)) return;
            foreach (var offer in _offers.Values.Where(o => o.GuestId == guest.Id).ToList())
            {
                offer.Decision.TrySetResult("reject");
                offer.Done.TrySetResult(false);   // 让还在等下载完成的 Host 侧立刻收尾，不必等到超时
                _offers.Remove(offer.Id);
            }
        }
        Log?.Invoke($"{guest.Nickname} {reason}（{guest.RemoteIp}）");
        RaiseGuestsChanged(force: true);
    }

    // ----- 发送请求的应答 -----

    private async Task HandleOfferDecisionAsync(HttpRequest req, string path, CancellationToken ct)
    {
        var rest = path["/api/offer/".Length..];
        var accept = rest.EndsWith("/accept", StringComparison.Ordinal);
        var reject = rest.EndsWith("/reject", StringComparison.Ordinal);
        if (!accept && !reject)
        {
            await WriteJsonAsync(req, 404, new { ok = false, error = "接口不存在" }, ct);
            return;
        }

        var id = rest[..^7];
        FileOffer? offer;
        lock (_lock) _offers.TryGetValue(id, out offer);
        if (offer == null || offer.State != "pending")
        {
            await WriteJsonAsync(req, 200, new { ok = false, error = "该请求已失效" }, ct);
            return;
        }

        if (accept)
        {
            offer.State = "accepted";
            offer.Decision.TrySetResult("accept");
            Log?.Invoke($"{offer.GuestName} 已确认接收「{offer.FileName}」");
            await WriteJsonAsync(req, 200, new { ok = true, token = offer.Token }, ct);
        }
        else
        {
            offer.State = "rejected";
            offer.Decision.TrySetResult("reject");
            await WriteJsonAsync(req, 200, new { ok = true }, ct);
        }
    }

    // ----- 下载（Host → 访客）-----

    private async Task ServeBlobAsync(HttpRequest req, string token, CancellationToken ct)
    {
        FileOffer? offer;
        lock (_lock) offer = _offers.Values.FirstOrDefault(o => o.Token == token);
        if (offer == null || offer.State != "accepted")
        {
            await WriteJsonAsync(req, 404, new { ok = false, error = "下载链接已失效" }, ct);
            return;
        }

        var info = new FileInfo(offer.FilePath);
        if (!info.Exists)
        {
            await WriteJsonAsync(req, 404, new { ok = false, error = "文件已被移动或删除" }, ct);
            return;
        }

        var head =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: application/octet-stream\r\n" +
            $"Content-Length: {info.Length}\r\n" +
            $"Content-Disposition: {ContentDisposition(offer.FileName)}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";
        await req.Stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await req.Stream.FlushAsync(ct);

        long sent = 0;
        var lastTick = DateTime.UtcNow;
        var ok = false;
        try
        {
            using var fs = new FileStream(offer.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            var buffer = new byte[BufferSize];
            while (sent < info.Length)
            {
                var want = (int)Math.Min(buffer.Length, info.Length - sent);
                var read = await fs.ReadAsync(buffer.AsMemory(0, want), ct);
                if (read == 0) break;
                await req.Stream.WriteAsync(buffer.AsMemory(0, read), ct);
                sent += read;

                var now = DateTime.UtcNow;
                if ((now - lastTick).TotalMilliseconds >= 400)
                {
                    lastTick = now;
                    Progress?.Invoke("发送", offer.FileName, offer.GuestName, sent, info.Length);
                    UpdateGuestTransferStatus(offer.GuestId, offer.FileName, sent, info.Length);
                }
            }
            await req.Stream.FlushAsync(ct);
            ok = sent >= info.Length;
        }
        catch (IOException)
        {
            // 访客中途取消下载：不是错误，按未完成处理
        }
        catch (OperationCanceledException) { }

        offer.State = ok ? "done" : "expired";
        offer.Done.TrySetResult(ok);
        lock (_lock) _offers.Remove(offer.Id);

        var guestStatusGuest = FindGuest(offer.GuestId);
        if (guestStatusGuest != null) guestStatusGuest.Status = "空闲";
        RaiseGuestsChanged(force: true);

        if (ok)
        {
            Log?.Invoke($"✓ 已发送「{offer.FileName}」给 {offer.GuestName}（{TransferService.FormatSize(info.Length)}）");
            Finished?.Invoke("发送", offer.FileName, offer.GuestName, info.Length, true, $"已发送给 {offer.GuestName}");
        }
        else
        {
            Log?.Invoke($"✗ 「{offer.FileName}」传输中断（{TransferService.FormatSize(sent)}/{TransferService.FormatSize(info.Length)}）");
            Finished?.Invoke("发送", offer.FileName, offer.GuestName, info.Length, false, "对方中断了下载");
        }
    }

    private WebGuest? FindGuest(string guestId)
    {
        lock (_lock) return _guests.TryGetValue(guestId, out var g) ? g : null;
    }

    private void UpdateGuestTransferStatus(string guestId, string fileName, long done, long total)
    {
        var guest = FindGuest(guestId);
        if (guest == null) return;
        var pct = total > 0 ? (int)(done * 100 / total) : 0;
        guest.Status = $"传输中 {pct}% · {fileName}";
        RaiseGuestsChanged();
    }

    private static string ContentDisposition(string fileName)
    {
        var ascii = new string(fileName.Select(ch =>
            ch < 128 && ch != '"' && ch != '\\' && ch != '\r' && ch != '\n' ? ch : '_').ToArray());
        return $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }

    // ----- 上传（访客 → Host）：两阶段 -----

    private async Task HandleUploadOfferAsync(HttpRequest req, CancellationToken ct)
    {
        var body = await ReadBodyStringAsync(req, 8192, ct);
        string clientId = "", fileName = "";
        long size = 0;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("clientId", out var c)) clientId = c.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("name", out var n)) fileName = n.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("size", out var s)) size = s.TryGetInt64(out var v) ? v : 0;
        }
        catch
        {
            await WriteJsonAsync(req, 200, new { ok = false, error = "请求格式错误" }, ct);
            return;
        }

        var guest = FindGuest(clientId);
        if (guest == null)
        {
            await WriteJsonAsync(req, 200, new { ok = false, error = "会话已失效，请刷新页面" }, ct);
            return;
        }

        // 只保留文件名，杜绝 ../ 目录穿越
        fileName = Path.GetFileName(fileName.Replace('\\', '/'));
        fileName = string.IsNullOrWhiteSpace(fileName) ? "未命名文件" : fileName;
        if (size <= 0)
        {
            await WriteJsonAsync(req, 200, new { ok = false, error = "文件为空" }, ct);
            return;
        }

        Log?.Invoke($"上传请求：{fileName}（{TransferService.FormatSize(size)}）来自 {guest.Nickname}，等待确认…");

        var decision = new ReceiveDecision(true);
        var ask = ConfirmUpload;
        if (ask != null)
        {
            try
            {
                var task = ask(new IncomingRequest(fileName, size, $"{guest.Nickname}（{guest.RemoteIp}）"));
                var finished = await Task.WhenAny(task, Task.Delay(UploadConfirmTimeout, ct));
                decision = finished == task ? await task : new ReceiveDecision(false);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"上传确认异常：{ex.Message}");
                decision = new ReceiveDecision(false);
            }
        }

        if (!decision.Accept)
        {
            guest.Status = "空闲";
            RaiseGuestsChanged(force: true);
            Log?.Invoke($"已拒绝 {guest.Nickname} 的上传：{fileName}");
            await WriteJsonAsync(req, 200, new { ok = false, error = "主机拒绝接收" }, ct);
            return;
        }

        var savePath = string.IsNullOrWhiteSpace(decision.SavePath)
            ? TransferService.NonOverwritePath(AppConfig.ReceiveDirectory, fileName)
            : decision.SavePath!;

        var slot = new UploadSlot
        {
            Id = NewId(8),
            GuestId = guest.Id,
            GuestName = guest.Nickname,
            FileName = fileName,
            Size = size,
            SavePath = savePath
        };
        lock (_lock) _uploads[slot.Id] = slot;

        guest.Status = $"上传中 · {fileName}";
        RaiseGuestsChanged(force: true);
        Log?.Invoke($"已同意接收，等待 {guest.Nickname} 上传内容…");
        await WriteJsonAsync(req, 200, new { ok = true, uploadId = slot.Id }, ct);
    }

    private async Task HandleUploadPutAsync(HttpRequest req, string uploadId, CancellationToken ct)
    {
        UploadSlot? slot;
        lock (_lock) _uploads.TryGetValue(uploadId, out slot);
        if (slot == null)
        {
            await WriteJsonAsync(req, 404, new { ok = false, error = "上传会话已失效" }, ct);
            return;
        }

        var part = slot.SavePath + ".part";
        var dir = Path.GetDirectoryName(slot.SavePath);
        long written = 0;
        var ok = false;
        try
        {
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                if (req.Prefix.Length > 0)
                {
                    await fs.WriteAsync(req.Prefix, ct);
                    written += req.Prefix.Length;
                }

                var remaining = req.ContentLength - req.Prefix.Length;
                var buffer = new byte[BufferSize];
                var lastTick = DateTime.UtcNow;
                while (remaining > 0)
                {
                    var want = (int)Math.Min(buffer.Length, remaining);
                    var read = await req.Stream.ReadAsync(buffer.AsMemory(0, want), ct);
                    if (read == 0) break;
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;
                    remaining -= read;

                    var now = DateTime.UtcNow;
                    if ((now - lastTick).TotalMilliseconds >= 400)
                    {
                        lastTick = now;
                        Progress?.Invoke("收到", slot.FileName, slot.GuestName, written, slot.Size);
                        var g = FindGuest(slot.GuestId);
                        if (g != null)
                        {
                            var pct = slot.Size > 0 ? (int)(written * 100 / slot.Size) : 0;
                            g.Status = $"上传中 {pct}% · {slot.FileName}";
                            RaiseGuestsChanged();
                        }
                    }
                }
                await fs.FlushAsync(ct);
            }

            if (slot.Size > 0 && written != slot.Size)
                throw new IOException($"字节数不符（期望 {slot.Size}，实收 {written}）");

            File.Move(part, slot.SavePath, overwrite: true);
            ok = true;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
            Log?.Invoke($"上传失败：{slot.FileName} — {ex.Message}");
            lock (_lock) _uploads.Remove(uploadId);
            var g = FindGuest(slot.GuestId);
            if (g != null) g.Status = "空闲";
            RaiseGuestsChanged(force: true);
            await WriteJsonAsync(req, 200, new { ok = false, error = ex.Message }, ct);
            return;
        }

        lock (_lock) _uploads.Remove(uploadId);
        var hostGuest = FindGuest(slot.GuestId);
        if (hostGuest != null) hostGuest.Status = "空闲";
        RaiseGuestsChanged(force: true);

        Log?.Invoke($"✓ 已接收 {slot.GuestName} 上传的「{slot.FileName}」→ {slot.SavePath}");
        Finished?.Invoke("收到", slot.FileName, slot.GuestName, written, true, slot.SavePath);
        await WriteJsonAsync(req, 200, new { ok = ok, bytes = written }, ct);
    }

    // ---------------- 维护 ----------------

    private void Sweep()
    {
        var now = DateTime.UtcNow;
        List<WebGuest> orphans = new();
        lock (_lock)
        {
            foreach (var g in _guests.Values)
            {
                if (g.Channel == null && (now - g.JoinedAt.ToUniversalTime()).TotalSeconds > OrphanGuestTtl.TotalSeconds)
                    orphans.Add(g);
            }
            foreach (var o in _offers.Values.Where(o => o.State == "pending" &&
                        (now - o.CreatedAt).TotalSeconds > OfferTtl.TotalSeconds + 30).ToList())
            {
                o.Decision.TrySetResult("expire");
                _offers.Remove(o.Id);
            }
        }
        foreach (var g in orphans) RemoveGuest(g, "加入后未建立连接，已移除");
    }

    private void RaiseGuestsChanged(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - _lastRaise).TotalMilliseconds < 350) return;
        _lastRaise = now;
        try { GuestsChanged?.Invoke(); } catch { }
    }

    private static string NewId(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    // ---------------- 极简 HTTP 读写 ----------------

    private sealed class HttpRequest
    {
        public string Method { get; init; } = "";
        public string Path { get; init; } = "";
        public Dictionary<string, string> Query { get; init; } = new();
        public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public byte[] Prefix { get; init; } = Array.Empty<byte>();
        public long ContentLength { get; init; }
        public NetworkStream Stream { get; init; } = null!;
        public string RemoteIp { get; init; } = "";
    }

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, string remoteIp, CancellationToken ct)
    {
        var acc = new MemoryStream();
        var buffer = new byte[8192];
        var headerEnd = -1;

        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) return null;
            acc.Write(buffer, 0, read);
            if (acc.Length > 65536) return null;  // 请求头异常大，直接拒绝

            var data = acc.GetBuffer();
            var len = (int)acc.Length;
            for (var i = 3; i < len; i++)
            {
                if (data[i] == (byte)'\n' && data[i - 1] == (byte)'\r' &&
                    data[i - 2] == (byte)'\n' && data[i - 3] == (byte)'\r')
                {
                    headerEnd = i + 1;
                    break;
                }
            }
        }

        var all = acc.GetBuffer();
        var text = Encoding.UTF8.GetString(all, 0, headerEnd);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;

        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return null;

        var target = parts[1];
        var qIndex = target.IndexOf('?');
        var path = qIndex >= 0 ? target[..qIndex] : target;
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (qIndex >= 0)
        {
            foreach (var pair in target[(qIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq < 0) query[Uri.UnescapeDataString(pair)] = "";
                else query[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        long contentLength = 0;
        if (headers.TryGetValue("content-length", out var cl) && !long.TryParse(cl, out contentLength))
            contentLength = 0;

        var prefixLen = (int)Math.Min(Math.Max(0, acc.Length - headerEnd), contentLength);
        var prefix = new byte[prefixLen];
        Array.Copy(all, headerEnd, prefix, 0, prefixLen);

        // 路径统一成小写比较用的规范形式（保留原始大小写用于 token）
        return new HttpRequest
        {
            Method = parts[0].ToUpperInvariant(),
            Path = path,
            Query = query,
            Headers = headers,
            Prefix = prefix,
            ContentLength = contentLength,
            Stream = stream,
            RemoteIp = remoteIp
        };
    }

    private static async Task<string> ReadBodyStringAsync(HttpRequest req, int max, CancellationToken ct)
    {
        if (req.ContentLength <= 0) return "";
        if (req.ContentLength > max) throw new InvalidOperationException("请求体过大");

        var buf = new byte[req.ContentLength];
        var offset = 0;
        if (req.Prefix.Length > 0)
        {
            offset = Math.Min(req.Prefix.Length, buf.Length);
            Array.Copy(req.Prefix, buf, offset);
        }
        while (offset < buf.Length)
        {
            var read = await req.Stream.ReadAsync(buf.AsMemory(offset), ct);
            if (read == 0) break;
            offset += read;
        }
        return Encoding.UTF8.GetString(buf, 0, offset);
    }

    private static async Task WriteTextAsync(HttpRequest req, int status, string contentType, string body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head =
            $"HTTP/1.1 {status} {StatusText(status)}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {bytes.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";
        await req.Stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await req.Stream.WriteAsync(bytes, ct);
        await req.Stream.FlushAsync(ct);
    }

    private static Task WriteJsonAsync(HttpRequest req, int status, object payload, CancellationToken ct)
        => WriteTextAsync(req, status, "application/json; charset=utf-8", JsonSerializer.Serialize(payload), ct);

    private static async Task WriteStatusAsync(HttpRequest req, int status, CancellationToken ct)
    {
        var head = $"HTTP/1.1 {status} {StatusText(status)}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await req.Stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await req.Stream.FlushAsync(ct);
    }

    private static string StatusText(int status) => status switch
    {
        200 => "OK",
        204 => "No Content",
        400 => "Bad Request",
        404 => "Not Found",
        413 => "Payload Too Large",
        _ => "OK"
    };

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _cts = null;
    }
}
