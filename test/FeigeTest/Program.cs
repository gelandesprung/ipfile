using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FeigeP2P;

static string Md5(string path)
{
    using var md5 = MD5.Create();
    using var fs = File.OpenRead(path);
    var buf = new byte[1 << 20];
    int n;
    while ((n = fs.Read(buf, 0, buf.Length)) > 0) md5.TransformBlock(buf, 0, n, null, 0);
    md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
    return Convert.ToHexString(md5.Hash!).ToLowerInvariant();
}

static void GenFile(string path, int mb)
{
    var rnd = new Random(42);
    var buf = new byte[1024 * 1024];
    using var fs = File.Create(path);
    for (int i = 0; i < mb; i++) { rnd.NextBytes(buf); fs.Write(buf); }
}

static async Task<JsonElement> PostJson(HttpClient http, string url, object payload)
{
    using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
    using var resp = await http.PostAsync(url, content);
    var text = await resp.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(text);
    return doc.RootElement.Clone();
}

/// <summary>从 SSE 流里读下一条 data: 事件（注释行与空行跳过）。</summary>
static async Task<JsonElement?> ReadSse(StreamReader reader, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        var line = await reader.ReadLineAsync(ct);
        if (line == null) return null;
        if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
        using var doc = JsonDocument.Parse(line[6..]);
        return doc.RootElement.Clone();
    }
    return null;
}

int passed = 0, failed = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok) { Console.WriteLine($"  PASS  {name} {detail}"); passed++; }
    else { Console.WriteLine($"  FAIL  {name} {detail}"); failed++; }
}

// ---------- IPv6 取址策略 ----------
// 同一块网卡上通常有三类地址：SLAAC 的 /64（稳定）、隐私临时地址（会轮换）、
// DHCPv6 下发的 /128。网页模式要把地址给别人的浏览器用，必须挑 /64 那个。
{
    var v6All = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                    n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback &&
                    n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        .Where(u => !u.Address.IsIPv6LinkLocal && !u.Address.IsIPv6SiteLocal &&
                    !u.Address.IsIPv6Multicast && !u.Address.IsIPv6Teredo)
        .ToList();

    var chosenV6 = AppConfig.GetLocalIpV6Only();
    if (chosenV6 == null)
    {
        Console.WriteLine("  SKIP  IPv6 取址策略（本机没有可用的全局 IPv6）");
    }
    else
    {
        var pick = v6All.FirstOrDefault(u => u.Address.ToString() == chosenV6);
        Check("IPv6 不选隐私临时地址", pick == null || !pick.IsTransient, chosenV6);
        if (v6All.Any(u => u.PrefixLength == 64))
            Check("有 /64 地址时优先选 /64（同链路直连）",
                pick != null && pick.PrefixLength == 64,
                $"{chosenV6} prefixlen={pick?.PrefixLength}");
    }
}

Console.WriteLine("========== 飞鸽 P2P v2.0 协议与配对测试 ==========\n");

// ---------- 1. 二维码 / 配对编解码 ----------
Console.WriteLine("[1] 二维码与配对握手编解码");
var token = PairSession.NewToken();
Check("token 为 32 位十六进制", token.Length == 32 && token.All(c => "0123456789abcdef".Contains(c)), token);

var qrText = Protocol.BuildQrPayload("客厅电脑", "2409:8a55:1234::5", 2425, token);
var qr = Protocol.ParseQrPayload(qrText);
Check("二维码载荷 IPv6 + 中文名",
    qr != null && qr.Name == "客厅电脑" && qr.Ip == "2409:8a55:1234::5" && qr.TcpPort == 2425 && qr.Token == token);
Check("二维码容忍首尾空白/换行", Protocol.ParseQrPayload("  " + qrText + "\r\n") != null);
Check("二维码拒绝非本协议内容", Protocol.ParseQrPayload("https://example.com") == null);
Check("二维码拒绝非法 IP", Protocol.ParseQrPayload(Protocol.BuildQrPayload("x", "999.1.1.1", 2425, token)) == null);
Check("二维码拒绝非法端口", Protocol.ParseQrPayload(Protocol.BuildQrPayload("x", "::1", 0, token)) == null);
Check("二维码拒绝含分隔符的 token", Protocol.ParseQrPayload(Protocol.BuildQrPayload("x", "::1", 2425, "abc|def")) == null);

var helloLine = Protocol.BuildHello(token, "手机-小美", 2425);
var hello = Protocol.ParseHello(helloLine);
Check("HELLO 编解码", hello != null && hello.Token == token && hello.Name == "手机-小美" && hello.TcpPort == 2425);
Check("HELLO 带换行也能解析", Protocol.ParseHello(helloLine.TrimEnd('\n')) != null);
Check("HELLO 拒绝非法 token", Protocol.ParseHello(Protocol.BuildHello("bad token", "x", 2425)) == null);

var ackOk = Protocol.ParseHelloAck(Protocol.BuildHelloAckOk("PC-书房", "2001:db8::2", 2425));
Check("HELLO_ACK OK 编解码",
    ackOk.Ok && ackOk.Name == "PC-书房" && ackOk.Ip == "2001:db8::2" && ackOk.TcpPort == 2425);
var ackRej = Protocol.ParseHelloAck(Protocol.BuildHelloAckReject("二维码已过期"));
Check("HELLO_ACK REJECT 编解码", !ackRej.Ok && ackRej.Reason == "二维码已过期");

// ---------- 2. 设备键与地址 ----------
Console.WriteLine("\n[2] 设备键与地址模型");
var v6Peer = new Peer { Name = "手机", Ip = "2409:8a55::5", TcpPort = 2425 };
var v4Peer = new Peer { Name = "电脑", Ip = "192.168.1.8", TcpPort = 2425 };
Check("IPv6 设备键加方括号", v6Peer.Key == "[2409:8a55::5]:2425", v6Peer.Key);
Check("IPv4 设备键保持原样", v4Peer.Key == "192.168.1.8:2425", v4Peer.Key);
var localCandidates = AppConfig.GetLocalIpCandidates();
Console.WriteLine("    本机候选地址：" + (localCandidates.Count == 0 ? "(无)" : string.Join(", ", localCandidates)));
Check("候选地址已过滤链路本地与回环",
    localCandidates.All(ip => !ip.StartsWith("fe80", StringComparison.OrdinalIgnoreCase) && ip != "::1" && ip != "127.0.0.1"));

// ---------- 2b. 局域网广播发现（原有能力，必须保留） ----------
Console.WriteLine("\n[2b] 局域网 UDP 自动发现");
var discText = Protocol.BuildDiscovery("客厅电脑", "192.168.1.8", 2425);
var disc = Protocol.ParseDiscovery(discText);
Check("广播包编解码",
    disc != null && !disc.Bye && disc.Peer.Name == "客厅电脑" &&
    disc.Peer.Ip == "192.168.1.8" && disc.Peer.TcpPort == 2425);
Check("广播解析出的设备标记为局域网来源", disc!.Peer.IsLan && !disc.Peer.IsPaired);
var byePkt = Protocol.ParseDiscovery(Protocol.BuildDiscovery("客厅电脑", "192.168.1.8", 2425, bye: true));
Check("BYE 包可识别", byePkt != null && byePkt.Bye);
Check("广播包拒绝非本协议内容", Protocol.ParseDiscovery("FG_QR|1|eA==|192.168.1.8|2425|deadbeef") == null);
Check("广播包拒绝字段不足", Protocol.ParseDiscovery("FG_P2P|1|eA==") == null);

var ipv4 = AppConfig.GetLocalIpV4();
Console.WriteLine($"    本机 IPv4（广播用）：{ipv4}");
Check("广播用地址一定是 IPv4", !ipv4.Contains(':') && System.Net.IPAddress.TryParse(ipv4, out _), ipv4);

// 直接往发现服务的端口投递一个 UDP 包，验证「收包 → 解析 → 事件」整条链路
const int UdpTestPort = 25150;
using var lan = new DiscoveryService(new AppConfig { DeviceName = "本机", UdpPort = UdpTestPort, TcpPort = 2425 });
var lanEvents = new List<(Peer Peer, PeerChange Change)>();
lan.PeerChanged += (p, c) => { lock (lanEvents) lanEvents.Add((p, c)); };
lan.Start();
await Task.Delay(150);   // 等接收循环就位

void SendUdp(string text)
{
    using var sock = new System.Net.Sockets.UdpClient();
    var bytes = System.Text.Encoding.UTF8.GetBytes(text);
    sock.Send(bytes, bytes.Length, "127.0.0.1", UdpTestPort);
}

SendUdp(Protocol.BuildDiscovery("手机-小美", "192.168.1.20", 2425));
await Task.Delay(400);
var first = lanEvents.FirstOrDefault();
Check("收到广播判定为 Added", first.Change == PeerChange.Added && first.Peer.Name == "手机-小美",
    lanEvents.Count > 0 ? first.Change.ToString() : "(无事件)");

SendUdp(Protocol.BuildDiscovery("手机-小美", "192.168.1.20", 2425));
await Task.Delay(400);
Check("重复广播只刷新不删行",
    lanEvents.Count >= 2 && lanEvents[1].Change == PeerChange.Updated &&
    lanEvents.All(e => e.Change != PeerChange.Removed));

SendUdp(Protocol.BuildDiscovery("手机-小美", "192.168.1.20", 2425, bye: true));
await Task.Delay(400);
Check("收到 BYE 才判定为 Removed",
    lanEvents.Count >= 3 && lanEvents[^1].Change == PeerChange.Removed);

// 自己发的广播必须被忽略，否则本机会把自己列进设备列表
var selfPkt = Protocol.BuildDiscovery("本机", ipv4, 2425);
SendUdp(selfPkt);
await Task.Delay(300);
Check("忽略本机自己的广播", lanEvents.All(e => e.Peer.Name != "本机"));

// ---------- 3. 文件传输（IPv6 回环） ----------
Console.WriteLine("\n[3] TCP 文件传输（IPv6 回环 ::1）");
var tmpSend = Path.Combine(Path.GetTempPath(), "feige_send_test");
var recvDir = AppConfig.ReceiveDirectory;
Directory.CreateDirectory(tmpSend);
foreach (var f in Directory.GetFiles(recvDir)) { try { File.Delete(f); } catch { } }

const int FilePort = 25101;
using var receiver = new TransferService(new AppConfig { DeviceName = "PC-接收方", TcpPort = FilePort });
receiver.Log += m => Console.WriteLine("    [recv] " + m);
receiver.Start();
await Task.Delay(400);

using var sender = new TransferService(new AppConfig { DeviceName = "安卓-发送方", TcpPort = 25102 });
var target = new Peer { Name = "PC-接收方", Ip = "::1", TcpPort = FilePort };

async Task<bool> SendAndWait(string file)
{
    var t = sender.SendFileAsync(target, file);
    var result = await t;
    return result.Item1;
}

var small = Path.Combine(tmpSend, "small.bin");
GenFile(small, 1);
var t0 = DateTime.Now;
var okSmall = await SendAndWait(small);
var smallRecv = Path.Combine(recvDir, "small.bin");
Check("小文件 1MB 经 IPv6 传输",
    okSmall && File.Exists(smallRecv) && Md5(small) == Md5(smallRecv),
    $"({(DateTime.Now - t0).TotalSeconds:F1}s)");

var big = Path.Combine(tmpSend, "big.bin");
GenFile(big, 150);
t0 = DateTime.Now;
var okBig = await SendAndWait(big);
var bigRecv = Path.Combine(recvDir, "big.bin");
Check("大文件 150MB 经 IPv6 传输",
    okBig && File.Exists(bigRecv) && Md5(big) == Md5(bigRecv),
    $"({(DateTime.Now - t0).TotalSeconds:F1}s)");

var cn = Path.Combine(tmpSend, "合同 2026.pdf");
GenFile(cn, 1);
var okCn = await SendAndWait(cn);
Check("中文文件名经 IPv6 传输", okCn && File.Exists(Path.Combine(recvDir, "合同 2026.pdf")));

var okDup = await SendAndWait(small);
Check("重名不覆盖生成(1)", okDup && File.Exists(Path.Combine(recvDir, "small (1).bin")));

// ---------- 4. 配对闭环 ----------
Console.WriteLine("\n[4] 二维码配对闭环");
const int PairPort = 25111;
using var pairReceiver = new TransferService(new AppConfig { DeviceName = "PC-书房", TcpPort = PairPort });
var paired = new List<Peer>();
pairReceiver.PeerPaired += p => { lock (paired) paired.Add(p); };
pairReceiver.Log += m => Console.WriteLine("    [pair] " + m);
pairReceiver.Start();
await Task.Delay(300);

var session = pairReceiver.BeginPairing();
var pairResult = await sender.PairWithAsync("::1", PairPort, session.Token, "安卓-小美");
Check("IPv6 配对成功", pairResult.Ok && pairResult.Peer != null, pairResult.Message);
Check("出示方收到 PeerPaired", paired.Count == 1 && paired[0].Name == "安卓-小美",
    paired.Count > 0 ? paired[0].Address : "无");
Check("出示方记录对端 ::1", paired.Count == 1 && paired[0].Ip == "::1",
    paired.Count > 0 ? paired[0].Ip : "无");
Check("配对后 token 标记已用", session.Used);

var replay = await sender.PairWithAsync("::1", PairPort, session.Token, "安卓-小美");
Check("同一 token 不可重放", !replay.Ok && replay.Message.Contains("使用"), replay.Message);

var afterPairTarget = new Peer { Name = "PC-书房", Ip = "::1", TcpPort = PairPort };
var pairedFile = Path.Combine(tmpSend, "after_pair.bin");
GenFile(pairedFile, 1);
var pairedSend = await sender.SendFileAsync(afterPairTarget, pairedFile);
Check("配对后按地址发送文件",
    pairedSend.Item1 && File.Exists(Path.Combine(recvDir, "after_pair.bin")) &&
    Md5(pairedFile) == Md5(Path.Combine(recvDir, "after_pair.bin")));

// ---------- 5. 刷新 / 过期 / 拒绝 ----------
Console.WriteLine("\n[5] token 生命周期");

const int RefreshPort = 25121;
using var refreshReceiver = new TransferService(new AppConfig { DeviceName = "刷新测试机", TcpPort = RefreshPort });
refreshReceiver.Start();
await Task.Delay(250);
var oldSession = refreshReceiver.BeginPairing();
var newSession = refreshReceiver.BeginPairing();
var oldTry = await sender.PairWithAsync("::1", RefreshPort, oldSession.Token, "安卓");
Check("刷新后旧 token 立即失效", !oldTry.Ok && oldTry.Message.Contains("配对码"), oldTry.Message);
var newTry = await sender.PairWithAsync("::1", RefreshPort, newSession.Token, "安卓");
Check("刷新后新 token 可用", newTry.Ok, newTry.Message);

const int ExpirePort = 25131;
using var expireReceiver = new TransferService(
    new AppConfig { DeviceName = "过期测试机", TcpPort = ExpirePort },
    TimeSpan.FromMilliseconds(700));
expireReceiver.Start();
await Task.Delay(250);
var expireSession = expireReceiver.BeginPairing();
await Task.Delay(900);
var expiredTry = await sender.PairWithAsync("::1", ExpirePort, expireSession.Token, "安卓");
Check("过期 token 被拒", !expiredTry.Ok && expiredTry.Message.Contains("过期"), expiredTry.Message);

const int RejectPort = 25141;
using var rejectReceiver = new TransferService(new AppConfig { DeviceName = "拒绝测试机", TcpPort = RejectPort });
Func<PairRequest, Task<bool>> deny = _ => Task.FromResult(false);
rejectReceiver.ConfirmPair += deny;
rejectReceiver.Start();
await Task.Delay(250);
var rejectSession = rejectReceiver.BeginPairing();
var rejected = await sender.PairWithAsync("::1", RejectPort, rejectSession.Token, "安卓");
Check("出示方拒绝时扫码方收到原因", !rejected.Ok && rejected.Message.Contains("拒绝"), rejected.Message);
Check("拒绝后 token 未被消费", !rejectSession.Used);

rejectReceiver.ConfirmPair -= deny;
var retry = await sender.PairWithAsync("::1", RejectPort, rejectSession.Token, "安卓");
Check("拒绝后可用同一 token 重试", retry.Ok, retry.Message);

receiver.Dispose();
sender.Dispose();
try { Directory.Delete(tmpSend, true); } catch { }

// ---------- 6. 网页模式（浏览器访客） ----------
Console.WriteLine("\n[6] 网页模式：加入 / 下行发送 / 上行接收");

const int WebPort = 25160;
var webCfg = new AppConfig { DeviceName = "网页测试机", TcpPort = 25162, WebPort = WebPort };
using var web = new WebHostService(webCfg);

var webDir = Path.Combine(Path.GetTempPath(), "feige_web_" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(webDir);
var uploadTarget = Path.Combine(webDir, "从浏览器上传.bin");
var rejectUploads = false;
web.ConfirmUpload += _ => Task.FromResult(
    rejectUploads ? new ReceiveDecision(false) : new ReceiveDecision(true, uploadTarget));

var finishedEvents = new List<string>();
web.Finished += (dir, name, peer, size, ok, msg) =>
{
    lock (finishedEvents) finishedEvents.Add($"{dir}|{name}|{ok}");
};

var webStart = web.Start(WebPort);
Check("网页服务启动并绑定指定端口", webStart.Ok && web.Port == WebPort, webStart.Message);
Check("启动即生成 6 位数字 PIN", web.Pin.Length == 6 && web.Pin.All(char.IsDigit), web.Pin);

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
var baseUrl = $"http://127.0.0.1:{web.Port}/";

var page = await http.GetStringAsync(baseUrl);
Check("访客页可直接打开（零安装）", page.Contains("加入飞鸽") && page.Contains("/api/join"), $"{page.Length} 字节");
Check("页面自包含、不依赖外部资源", !page.Contains("{{") && !page.Contains("cdn.") && !page.Contains("<script src"));

var badJoin = await PostJson(http, baseUrl + "api/join", new { nickname = "小美", pin = "000000" });
Check("PIN 错误被拒", !badJoin.GetProperty("ok").GetBoolean(), badJoin.GetProperty("error").GetString() ?? "");
Check("PIN 错误不产生访客", web.SnapshotGuests().Count == 0);

var join = await PostJson(http, baseUrl + "api/join", new { nickname = "小美", pin = web.Pin });
var clientId = join.GetProperty("ok").GetBoolean() ? join.GetProperty("clientId").GetString() ?? "" : "";
Check("正确 PIN 可加入", clientId.Length > 0, clientId);
Check("访客以填写的昵称出现在 Host 列表",
    web.SnapshotGuests().Count == 1 && web.SnapshotGuests()[0].Nickname.Contains("小美"));

using var sseCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
var sseResp = await http.SendAsync(
    new HttpRequestMessage(HttpMethod.Get, baseUrl + "api/events?c=" + clientId),
    HttpCompletionOption.ResponseHeadersRead, sseCts.Token);
using var sseReader = new StreamReader(
    await sseResp.Content.ReadAsStreamAsync(sseCts.Token), Encoding.UTF8);
var snapshot = await ReadSse(sseReader, sseCts.Token);
Check("信令通道首帧为身份快照",
    snapshot != null && snapshot.Value.GetProperty("type").GetString() == "snapshot");

// 下行：Host 发文件 → 访客确认 → 走普通 HTTP 取文件
var downFile = Path.Combine(webDir, "给朋友的照片.jpg");
await File.WriteAllBytesAsync(downFile, RandomNumberGenerator.GetBytes(256 * 1024));
var downSize = new FileInfo(downFile).Length;
var sendTask = web.SendFilesAsync(new[] { clientId }, new[] { downFile });

var offerEv = await ReadSse(sseReader, sseCts.Token);
Check("访客收到 file_offer 信令（文件本体不走信令）",
    offerEv != null && offerEv.Value.GetProperty("type").GetString() == "file_offer");
var offerId = offerEv!.Value.GetProperty("id").GetString() ?? "";
var offerToken = offerEv.Value.GetProperty("token").GetString() ?? "";

var beforeAccept = await http.GetAsync(baseUrl + "api/blob/" + offerToken);
Check("未确认前下载链接不可用",
    beforeAccept.StatusCode == System.Net.HttpStatusCode.NotFound);

var accept = await PostJson(http, $"{baseUrl}api/offer/{offerId}/accept", new { });
Check("访客确认后服务端才放行", accept.GetProperty("ok").GetBoolean());

var downloaded = await http.GetByteArrayAsync(baseUrl + "api/blob/" + offerToken);
Check("文件按字节完整送达",
    downloaded.LongLength == downSize &&
    Convert.ToHexString(MD5.HashData(downloaded)).ToLowerInvariant() == Md5(downFile));
await sendTask;
lock (finishedEvents)
    Check("Host 侧记录发送完成", finishedEvents.Any(e => e.StartsWith("发送|") && e.EndsWith("|True")));

// 上行：两阶段（先 offer 问 Host，同意后才 PUT 内容）
var upBytes = RandomNumberGenerator.GetBytes(180 * 1024);
var upOffer = await PostJson(http, baseUrl + "api/upload/offer",
    new { clientId, name = "contract.bin", size = upBytes.Length });
Check("上传第一阶段：请求 Host 确认", upOffer.GetProperty("ok").GetBoolean());

var uploadId = upOffer.GetProperty("uploadId").GetString() ?? "";
var putResp = await http.PutAsync(baseUrl + "api/upload/" + uploadId, new ByteArrayContent(upBytes));
var putText = await putResp.Content.ReadAsStringAsync();
Check("上传第二阶段：流式落盘成功",
    putText.Contains("\"ok\":true") && File.Exists(uploadTarget) &&
    new FileInfo(uploadTarget).Length == upBytes.Length, $"{upBytes.Length} 字节");
Check("落盘内容与上传一致",
    File.Exists(uploadTarget) &&
    Convert.ToHexString(MD5.HashData(upBytes)).ToLowerInvariant() == Md5(uploadTarget));
lock (finishedEvents)
    Check("Host 侧记录接收完成", finishedEvents.Any(e => e.StartsWith("收到|contract.bin|True")));

rejectUploads = true;
var rejectedUpload = await PostJson(http, baseUrl + "api/upload/offer",
    new { clientId, name = "不想要的.exe", size = 10 });
Check("Host 拒绝时上传不进入传输阶段", !rejectedUpload.GetProperty("ok").GetBoolean());
rejectUploads = false;

var oldPin = web.Pin;
var newPin = web.RefreshPin();
Check("刷新 PIN 生成新码", newPin.Length == 6 && newPin != oldPin);
Check("刷新 PIN 不踢在线访客（决策1）", web.SnapshotGuests().Count == 1);

var notFound = await http.GetAsync(baseUrl + "api/unknown");
Check("未知接口返回 404", notFound.StatusCode == System.Net.HttpStatusCode.NotFound);

// ---------- PIN 连续输错 3 次自动更换 ----------
// 用 000001：NewPin 生成的是 100000–999999，绝不会撞上，保证这次一定是错的
var wrongPin = "000001";
var pinBase = web.RefreshPin();   // 顺手把之前的失败计数清零
await PostJson(http, baseUrl + "api/join", new { nickname = "试错", pin = wrongPin });
await PostJson(http, baseUrl + "api/join", new { nickname = "试错", pin = wrongPin });
Check("连错 2 次 PIN 保持不变", web.Pin == pinBase, web.Pin);

var rotatedEvents = new List<string>();
web.PinRotated += p => { lock (rotatedEvents) rotatedEvents.Add(p); };

var thirdWrong = await PostJson(http, baseUrl + "api/join", new { nickname = "试错", pin = wrongPin });
var rotatedPin = web.Pin;
Check("第 3 次输错后 PIN 自动更换", rotatedPin != pinBase, $"{pinBase} -> {rotatedPin}");
Check("更换后的 PIN 仍是 6 位数字", rotatedPin.Length == 6 && rotatedPin.All(char.IsDigit), rotatedPin);
Check("第 3 次输错返回「已更换」提示",
    (thirdWrong.GetProperty("error").GetString() ?? "").Contains("更换"),
    thirdWrong.GetProperty("error").GetString() ?? "");
Check("PinRotated 事件把新 PIN 推给界面",
    rotatedEvents.Count == 1 && rotatedEvents[0] == rotatedPin, string.Join(",", rotatedEvents));

var oldPinJoin = await PostJson(http, baseUrl + "api/join", new { nickname = "老王", pin = pinBase });
Check("旧 PIN 立即失效", !oldPinJoin.GetProperty("ok").GetBoolean());

var newPinJoin = await PostJson(http, baseUrl + "api/join", new { nickname = "老王", pin = rotatedPin });
Check("新 PIN 可正常加入", newPinJoin.GetProperty("ok").GetBoolean());
Check("更换 PIN 不踢在线访客（决策1）", web.SnapshotGuests().Count == 2);

var pinAfterJoin = web.Pin;
await PostJson(http, baseUrl + "api/join", new { nickname = "试错", pin = wrongPin });
await PostJson(http, baseUrl + "api/join", new { nickname = "试错", pin = wrongPin });
Check("成功加入后失败计数已重置（再错 2 次不换码）", web.Pin == pinAfterJoin);

web.Stop();
Check("停止服务后访客全部清空", web.SnapshotGuests().Count == 0);
try { Directory.Delete(webDir, true); } catch { }

Console.WriteLine($"\n========== 结果：{passed} 通过 / {failed} 失败 ==========");
Environment.Exit(failed == 0 ? 0 : 1);
