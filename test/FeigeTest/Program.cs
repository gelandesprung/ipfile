using System.Security.Cryptography;
using System.Text;
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

static string Fmt(long b) => b < (1<<20) ? $"{b/1024.0:F0}KB" : $"{b/1024.0/1024.0:F0}MB";

int passed = 0, failed = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) { Console.WriteLine($"  PASS  {name} {detail}"); passed++; }
    else { Console.WriteLine($"  FAIL  {name} {detail}"); failed++; }
}

Console.WriteLine("========== 飞鸽 P2P 协议闭环测试 ==========\n");

// ---------- 1. 协议编解码 ----------
Console.WriteLine("[1] 协议编解码");
var name = "测试-张三";
var pkt = Protocol.BuildDiscovery(name, "192.168.1.10", 2425);
var parsed = Protocol.ParseDiscovery(pkt);
Check("发现包普通", parsed != null && !parsed.Bye && parsed.Peer.Name == name && parsed.Peer.Ip == "192.168.1.10" && parsed.Peer.TcpPort == 2425);
var bye = Protocol.BuildDiscovery(name, "192.168.1.10", 2425, true);
var parsedBye = Protocol.ParseDiscovery(bye);
Check("发现包 BYE", parsedBye != null && parsedBye.Bye);
Check("发现包无关行过滤", Protocol.ParseDiscovery("hello world") == null);
var fh = Protocol.BuildFileRequest("合同.pdf", 12345);
var okHead = Protocol.ParseFileRequest(fh, out var fn, out var fsz, out var fc);
Check("文件头编解码", okHead && fn == "合同.pdf" && fsz == 12345 && fc == 1);
Check("ACK OK", Protocol.ParseAck(Protocol.BuildAckOk(), out _) == true);
Check("ACK REJECT", Protocol.ParseAck(Protocol.BuildAckReject("空间不足"), out var reason) == false && reason == "空间不足");
Check("DONE", Protocol.ParseDone(Protocol.BuildDone(9999), out var got) && got == 9999);

// ---------- 2. TCP 传输 ----------
Console.WriteLine("\n[2] TCP 文件传输（本机双实例）");
var tmpSend = Path.Combine(Path.GetTempPath(), "feige_send_test");
var recvDir = AppConfig.ReceiveDirectory;
Directory.CreateDirectory(tmpSend);
foreach (var f in Directory.GetFiles(recvDir)) { try { File.Delete(f); } catch { } }

var recvConfig = new AppConfig { DeviceName = "Receiver", UdpPort = 25002, TcpPort = 25001 };
var receiver = new TransferService(recvConfig);
TaskCompletionSource<bool> done = new();
receiver.TransferFinished += (dir, n, peer, size, ok, msg) => { if (dir == "收到") done.TrySetResult(ok); };
receiver.Log += m => Console.WriteLine("    [recv] " + m);
receiver.Start();
await Task.Delay(400);

var sender = new TransferService(new AppConfig { DeviceName = "Sender", UdpPort = 25003, TcpPort = 25004 });
var target = new Peer { Name = "Receiver", Ip = "127.0.0.1", TcpPort = 25001 };

async Task<bool> SendAndWait(string file)
{
    done = new TaskCompletionSource<bool>();
    var t = Task.Run(async () => await sender.SendFileAsync(target, file));
    var recvOk = await Task.WhenAny(done.Task, Task.Delay(60000)) == done.Task && await done.Task;
    var sendResult = await t;
    return recvOk && sendResult.Item1;
}

// 小文件
var small = Path.Combine(tmpSend, "small.bin");
GenFile(small, 1);
var t0 = DateTime.Now;
var okSmall = await SendAndWait(small);
var smallRecv = Path.Combine(recvDir, "small.bin");
Check("小文件 1MB 传输", okSmall && File.Exists(smallRecv) && Md5(small) == Md5(smallRecv), $"({(DateTime.Now-t0).TotalSeconds:F1}s)");

// 大文件 150MB
var big = Path.Combine(tmpSend, "big.bin");
GenFile(big, 150);
t0 = DateTime.Now;
var okBig = await SendAndWait(big);
var bigRecv = Path.Combine(recvDir, "big.bin");
Check("大文件 150MB 传输", okBig && File.Exists(bigRecv) && Md5(big) == Md5(bigRecv), $"({(DateTime.Now-t0).TotalSeconds:F1}s)");

// 中文文件名
var cn = Path.Combine(tmpSend, "合同 2026.pdf");
GenFile(cn, 1);
var okCn = await SendAndWait(cn);
Check("中文文件名", okCn && File.Exists(Path.Combine(recvDir, "合同 2026.pdf")));

// 重名不覆盖
var okDup = await SendAndWait(small);
Check("重名不覆盖生成(1)", okDup && File.Exists(Path.Combine(recvDir, "small (1).bin")));

receiver.Dispose();
sender.Dispose();
try { Directory.Delete(tmpSend, true); } catch { }

// ---------- 3. UDP 发现服务（覆盖刚修复的绑定崩溃 bug） ----------
Console.WriteLine("\n[3] UDP 发现服务");
using var disc = new DiscoveryService(new AppConfig { DeviceName = "发现测试机", UdpPort = 25010, TcpPort = 25011 });
bool started;
try { disc.Start(); started = true; } catch (Exception ex) { started = false; Console.WriteLine("    Start 抛异常：" + ex.Message); }
Check("发现服务启动不崩溃", started);
await Task.Delay(500);
// 抓一个本机发出的广播包，验证格式可被协议解析
bool captured = false;
try {
    using var sniffer = new System.Net.Sockets.UdpClient();
    sniffer.Client.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.ReuseAddress, true);
    sniffer.Client.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, 25010));
    sniffer.Client.ReceiveTimeout = 3000;
    System.Net.IPEndPoint from = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
    var r = sniffer.Receive(ref from);
    var text = Encoding.UTF8.GetString(r);
    var p = Protocol.ParseDiscovery(text);
    captured = p != null && p.Peer.Name == "发现测试机" && !p.Bye;
} catch (Exception ex) { Console.WriteLine("    抓包失败：" + ex.Message); }
Check("广播包格式可被对端解析", captured);

// ---------- 4. 设备事件语义（回归：周期性广播不能把设备判成离线） ----------
Console.WriteLine("\n[4] 设备事件语义");
var seq = new List<(PeerChange change, int peerCount)>();
using var disc2 = new DiscoveryService(new AppConfig { DeviceName = "事件测试机", UdpPort = 25020, TcpPort = 25021 });
disc2.PeerChanged += (peer, change) => { lock (seq) seq.Add((change, disc2.Peers.Count)); };
disc2.Start();
await Task.Delay(300);

using (var tx = new System.Net.Sockets.UdpClient())
{
    async Task SendPkt(bool bye)
    {
        // 包内 IP 故意用一个非本机地址，绕过「忽略自己」的过滤
        var bytes = Encoding.UTF8.GetBytes(Protocol.BuildDiscovery("模拟手机", "10.9.8.7", 25021, bye));
        await tx.SendAsync(bytes, bytes.Length, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 25020));
        await Task.Delay(400);
    }
    await SendPkt(false); // 首次广播 → Added
    await SendPkt(false); // 同一设备再来一次 → 必须是 Updated，不能删行
    await SendPkt(true);  // BYE → Removed
}

List<(PeerChange change, int peerCount)> snap;
lock (seq) snap = new List<(PeerChange, int)>(seq);
var seqText = string.Join(" → ", snap.Select(s => $"{s.change}({s.peerCount})"));
Console.WriteLine($"    事件序列：{seqText}");

Check("首次广播判定为 Added", snap.Count >= 1 && snap[0].change == PeerChange.Added);
Check("重复广播只刷新不删行", snap.Count >= 2 && snap[1].change == PeerChange.Updated && snap[1].peerCount == 1, seqText);
Check("BYE 才判定为 Removed", snap.Count >= 3 && snap[2].change == PeerChange.Removed && snap[2].peerCount == 0, seqText);

Console.WriteLine($"\n========== 结果：{passed} 通过 / {failed} 失败 ==========");
Environment.Exit(failed == 0 ? 0 : 1);
