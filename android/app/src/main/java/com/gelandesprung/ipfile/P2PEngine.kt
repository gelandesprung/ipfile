package com.gelandesprung.ipfile

import android.content.Context
import android.net.Uri
import kotlinx.coroutines.*
import java.io.BufferedReader
import java.io.InputStreamReader
import java.io.OutputStream
import java.net.*
import java.nio.charset.StandardCharsets

/**
 * P2P 核心引擎：UDP 发现 + TCP 接收/发送。
 * 由前台 ReceiveService 持有其生命周期，Activity 通过 sendFile() 发起发送。
 */
class P2PEngine(private val appContext: Context) {
    companion object {
        const val UDP_PORT = 2425
        const val TCP_PORT = 2425
        private const val BUF = 64 * 1024
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var udp: DatagramSocket? = null
    private var server: ServerSocket? = null
    @Volatile private var running = false

    fun start() {
        if (running) return
        running = true
        val ip = NetUtils.localIp()
        AppState.setLocalIp(ip)
        AppState.log("已启动 · ${AppState.deviceName.value} ($ip:$TCP_PORT)")
        startUdp(ip)
        startTcpServer()
    }

    fun stop() {
        running = false
        udp?.close(); udp = null
        try { server?.close() } catch (e: Exception) { }
        server = null
        scope.cancel()
    }

    /** 改名后立即广播一次。 */
    fun announceName() {
        val ip = AppState.localIp.value.ifEmpty { NetUtils.localIp() }
        broadcast(Protocol.buildDiscovery(AppState.deviceName.value, ip, TCP_PORT, false), ip)
    }

    // ---------------- UDP 发现 ----------------
    private fun startUdp(ip: String) {
        try {
            val sock = DatagramSocket(null).apply {
                reuseAddress = true
                broadcast = true
                bind(InetSocketAddress(UDP_PORT))
            }
            udp = sock
        } catch (e: Exception) {
            AppState.log("UDP 端口占用：$UDP_PORT — ${e.message}")
            return
        }
        // 发送循环
        scope.launch {
            announceName()
            while (isActive && running) {
                delay(2000)
                announceName()
            }
        }
        // 接收循环
        scope.launch {
            val buf = ByteArray(4096)
            while (isActive && running) {
                try {
                    val pkt = DatagramPacket(buf, buf.size)
                    udp?.receive(pkt)
                    val text = String(pkt.data, pkt.offset, pkt.length, StandardCharsets.UTF_8)
                    val d = Protocol.parseDiscovery(text) ?: continue
                    if (d.peer.ip == AppState.localIp.value) continue
                    if (d.bye) {
                        AppState.removePeer(d.peer.key)
                        AppState.log("设备离线：${d.peer.name}")
                    } else {
                        val isNew = AppState.updatePeer(d.peer)
                        if (isNew) AppState.log("发现设备：${d.peer.name} (${d.peer.ip})")
                    }
                } catch (e: Exception) {
                    if (!running) break
                }
            }
        }
        // 清理离线设备
        scope.launch {
            while (isActive && running) {
                delay(1000)
                AppState.prunePeers(6000)
            }
        }
    }

    private fun broadcast(text: String, ip: String) {
        try {
            val bytes = text.toByteArray(StandardCharsets.UTF_8)
            val target = try {
                // 优先按本机网段广播，跨网段/受限网络回退到全局广播
                val lastDot = ip.lastIndexOf('.')
                if (lastDot > 0) "${ip.substring(0, lastDot)}.255" else "255.255.255.255"
            } catch (e: Exception) { "255.255.255.255" }
            val pkt = DatagramPacket(bytes, bytes.size, InetAddress.getByName(target), UDP_PORT)
            udp?.send(pkt)
        } catch (e: Exception) { }
    }

    // ---------------- TCP 接收服务 ----------------
    private fun startTcpServer() {
        try {
            server = ServerSocket(TCP_PORT).apply { reuseAddress = true }
        } catch (e: Exception) {
            AppState.log("TCP 端口占用：$TCP_PORT — ${e.message}")
            return
        }
        scope.launch {
            while (isActive && running) {
                try {
                    val client = server?.accept() ?: break
                    launch { handleIncoming(client) }
                } catch (e: Exception) {
                    if (!running) break
                }
            }
        }
    }

    private suspend fun handleIncoming(client: Socket) = withContext(Dispatchers.IO) {
        var pending = false
        try {
            client.soTimeout = 10000
            val reader = BufferedReader(InputStreamReader(client.getInputStream(), StandardCharsets.UTF_8))
            val headerLine = withTimeout(10_000) { reader.readLine() }
            if (headerLine == null) { AppState.log("接收：未收到请求头"); return@withContext }
            val header = Protocol.parseFileRequest(headerLine)
            if (header == null) {
                writeLine(client, Protocol.buildAckReject("请求头格式错误"))
                AppState.log("接收：请求头非法，已拒绝")
                return@withContext
            }

            val outStream: OutputStream
            val displayPath: String
            try {
                val (out, path) = NetUtils.createReceiveStream(appContext, header.fileName)
                outStream = out; displayPath = path
                pending = true
            } catch (e: Exception) {
                writeLine(client, Protocol.buildAckReject("无法创建文件：${e.message}"))
                AppState.log("接收：创建文件失败 — ${e.message}")
                return@withContext
            }

            AppState.log("收到发送请求：${header.fileName} (${NetUtils.formatSize(header.size)})")
            writeLine(client, Protocol.buildAckOk())

            client.soTimeout = 60000
            outStream.use { out ->
                val input = client.getInputStream()
                val buf = ByteArray(BUF)
                var received = 0L
                while (received < header.size) {
                    val want = minOf(buf.size.toLong(), header.size - received).toInt()
                    val n = input.read(buf, 0, want)
                    if (n <= 0) break
                    out.write(buf, 0, n)
                    received += n
                    AppState.setProgress(ProgressInfo(TransferDirection.RECEIVE, header.fileName, received, header.size))
                }
                out.flush()
                if (received != header.size) throw java.io.IOException("数据不完整：$received/${header.size}")
                writeLine(client, Protocol.buildDone(received))
                pending = false
                AppState.addRecord(TransferRecord(TransferDirection.RECEIVE, header.fileName, "对方", header.size, true, nowTime()))
                AppState.log("接收完成：${header.fileName} → $displayPath")
            }
        } catch (e: Exception) {
            AppState.log("接收失败：${e.message}")
            AppState.addRecord(TransferRecord(TransferDirection.RECEIVE, "未知", "对方", 0, false, nowTime()))
        } finally {
            if (pending) AppState.log("接收中断，已清理未完成文件")
            try { client.close() } catch (e: Exception) { }
            AppState.setProgress(null)
        }
    }

    // ---------------- TCP 发送 ----------------
    suspend fun sendFile(target: Peer, uri: Uri): Pair<Boolean, String> = withContext(Dispatchers.IO) {
        val cr = appContext.contentResolver
        val displayName = queryDisplayName(cr, uri) ?: "文件"
        val size = try {
            cr.openFileDescriptor(uri, "r")?.use { it.statSize } ?: -1
        } catch (e: Exception) { -1 }

        var client: Socket? = null
        try {
            AppState.log("开始发送：$displayName → ${target.name}")
            client = Socket()
            client.soTimeout = 10000
            withTimeout(10_000) { client.connect(InetSocketAddress(target.ip, target.tcpPort), 10_000) }
            writeLine(client, Protocol.buildFileRequest(displayName, size.coerceAtLeast(0)))

            val reader = BufferedReader(InputStreamReader(client.getInputStream(), StandardCharsets.UTF_8))
            val ackLine = withTimeout(10_000) { reader.readLine() }
            if (ackLine == null) return@withContext false to "等待对方应答超时"
            val ack = Protocol.parseAck(ackLine)
            if (!ack.first) return@withContext false to "对方拒绝：${ack.second}"

            val input = cr.openInputStream(uri) ?: return@withContext false to "无法打开文件"
            input.use { ins ->
                val out = client.getOutputStream()
                val buf = ByteArray(BUF)
                var sent = 0L
                while (true) {
                    val n = ins.read(buf)
                    if (n <= 0) break
                    out.write(buf, 0, n)
                    sent += n
                    AppState.setProgress(ProgressInfo(TransferDirection.SEND, displayName, sent, size.coerceAtLeast(sent)))
                }
                out.flush()
                val doneLine = withTimeout(10_000) { reader.readLine() }
                val recv = doneLine?.let { Protocol.parseDone(it) }
                if (recv == null || recv != sent) return@withContext false to "对方确认数不符（期望 $sent，实收 $recv）"
            }
            AppState.addRecord(TransferRecord(TransferDirection.SEND, displayName, target.name, size.coerceAtLeast(0), true, nowTime()))
            AppState.log("发送完成：$displayName → ${target.name}")
            true to "已发送 $displayName"
        } catch (e: Exception) {
            AppState.log("发送失败：${e.message}")
            AppState.addRecord(TransferRecord(TransferDirection.SEND, displayName, target.name, 0, false, nowTime()))
            false to (e.message ?: "未知错误")
        } finally {
            try { client?.close() } catch (e: Exception) { }
            AppState.setProgress(null)
        }
    }

    private fun queryDisplayName(cr: android.content.ContentResolver, uri: Uri): String? {
        val projection = arrayOf(android.provider.OpenableColumns.DISPLAY_NAME)
        val cursor = try { cr.query(uri, projection, null, null, null) } catch (e: Exception) { null }
        cursor?.use {
            if (it.moveToFirst()) {
                val idx = it.getColumnIndex(android.provider.OpenableColumns.DISPLAY_NAME)
                if (idx >= 0) return it.getString(idx)
            }
        }
        return uri.lastPathSegment
    }

    private fun writeLine(socket: Socket, text: String) {
        socket.getOutputStream().write(text.toByteArray(StandardCharsets.UTF_8))
        socket.getOutputStream().flush()
    }

    private fun nowTime(): String = java.text.SimpleDateFormat("HH:mm:ss", java.util.Locale.getDefault()).format(java.util.Date())
}
