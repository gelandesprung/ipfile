package com.gelandesprung.ipfile

import android.content.Context
import android.net.Uri
import kotlinx.coroutines.*
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.InputStream
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
        /** 等待用户确认接收的最长时间，超时按拒绝处理。 */
        const val CONFIRM_TIMEOUT_MS = 60_000L
        /** 发送方等待对方确认的时间，必须长于接收方确认窗口。 */
        private const val ACK_TIMEOUT_MS = 90_000L
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var udp: DatagramSocket? = null
    private var server: ServerSocket? = null
    /** 每个待确认请求对应一个 CompletableDeferred，用户选择后 complete。 */
    private val pendingDecisions = java.util.concurrent.ConcurrentHashMap<String, CompletableDeferred<Boolean>>()
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
        // 所有待确认请求按拒绝处理，别让对方一直等到超时
        pendingDecisions.keys.toList().forEach { key ->
            pendingDecisions.remove(key)?.complete(false)
        }
        AppState.setPending(null)
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
        // 清理离线设备（与 PC 端一致：12 秒，容忍 WiFi 丢包）
        scope.launch {
            while (isActive && running) {
                delay(1000)
                AppState.prunePeers(12_000)
            }
        }
    }

    private fun broadcast(text: String, ip: String) {
        val bytes = text.toByteArray(StandardCharsets.UTF_8)
        // 与 PC 端保持一致：主用全局广播 255.255.255.255；
        // 同时发一份网段广播，覆盖部分路由器丢弃全局广播的情况。
        val targets = mutableSetOf("255.255.255.255")
        try {
            val lastDot = ip.lastIndexOf('.')
            if (lastDot > 0) targets.add("${ip.substring(0, lastDot)}.255")
        } catch (e: Exception) { }
        for (t in targets) {
            try {
                val pkt = DatagramPacket(bytes, bytes.size, InetAddress.getByName(t), UDP_PORT)
                udp?.send(pkt)
            } catch (e: Exception) { }
        }
    }

    // ---------------- TCP 接收服务 ----------------
    private fun startTcpServer() {
        try {
            // 必须先设 reuseAddress 再 bind：用无参构造，否则端口 TIME_WAIT 时绑定失败
            val ss = ServerSocket()
            ss.reuseAddress = true
            ss.bind(InetSocketAddress(TCP_PORT))
            server = ss
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
            val inputStream = client.getInputStream()
            val headerLine = withTimeout(10_000) { readLineRaw(inputStream) }
            if (headerLine == null) { AppState.log("接收：未收到请求头"); return@withContext }
            val header = Protocol.parseFileRequest(headerLine)
            if (header == null) {
                writeLine(client, Protocol.buildAckReject("请求头格式错误"))
                AppState.log("接收：请求头非法，已拒绝")
                return@withContext
            }

            val remoteIp = try { client.inetAddress?.hostAddress ?: "对方" } catch (e: Exception) { "对方" }

            // － 接收确认：先问用户，同意才建文件、才回 ACK OK －
            val requestId = java.util.UUID.randomUUID().toString()
            val decision = CompletableDeferred<Boolean>()
            pendingDecisions[requestId] = decision
            AppState.setPending(PendingRequest(requestId, header.fileName, header.size, remoteIp,
                System.currentTimeMillis() + CONFIRM_TIMEOUT_MS))
            AppState.log("收到发送请求：${header.fileName} (${NetUtils.formatSize(header.size)}) 来自 $remoteIp，等待确认…")

            val accepted = try {
                withTimeoutOrNull(CONFIRM_TIMEOUT_MS) { decision.await() } ?: false
            } catch (e: Exception) {
                false
            } finally {
                pendingDecisions.remove(requestId)
                // 并发请求时只清掉属于自己的那条，别把别人的确认框关了
                if (AppState.pending.value?.id == requestId) AppState.setPending(null)
            }

            if (!accepted) {
                writeLine(client, Protocol.buildAckReject("对方拒绝接收"))
                AppState.log("已拒绝接收：${header.fileName}（已通知对方）")
                AppState.addRecord(TransferRecord(TransferDirection.RECEIVE, header.fileName, remoteIp,
                    header.size, false, nowTime()))
                return@withContext
            }

            val target: NetUtils.ReceiveTarget
            try {
                target = NetUtils.createReceiveStream(appContext, header.fileName)
                pending = true
            } catch (e: Exception) {
                writeLine(client, Protocol.buildAckReject("无法创建文件：${e.message}"))
                AppState.log("接收：创建文件失败 — ${e.message}")
                return@withContext
            }

            writeLine(client, Protocol.buildAckOk())

            client.soTimeout = 60000
            var received = 0L
            // use 块结束后输出流才关闭：MediaStore 的 IS_PENDING 到这时才清零，文件才对其他 App 可见
            target.stream.use { out ->
                val buf = ByteArray(BUF)
                while (received < header.size) {
                    val want = minOf(buf.size.toLong(), header.size - received).toInt()
                    val n = inputStream.read(buf, 0, want)
                    if (n <= 0) break
                    out.write(buf, 0, n)
                    received += n
                    AppState.setProgress(ProgressInfo(TransferDirection.RECEIVE, header.fileName, received, header.size))
                }
                out.flush()
            }

            if (received != header.size) throw java.io.IOException("数据不完整：$received/${header.size}")

            // 先落盘再回 DONE：让对方看到的「完成」意味着文件确实已就位
            writeLine(client, Protocol.buildDone(received))
            pending = false
            AppState.addRecord(TransferRecord(TransferDirection.RECEIVE, header.fileName, remoteIp,
                header.size, true, nowTime(), target.uri.toString()))
            AppState.log("接收完成：${header.fileName} → ${target.displayPath}")
        } catch (e: Exception) {
            AppState.log("接收失败：${e.message}")
            AppState.addRecord(TransferRecord(TransferDirection.RECEIVE, "未知", "对方", 0, false, nowTime()))
        } finally {
            if (pending) AppState.log("接收中断，已清理未完成文件")
            try { client.close() } catch (e: Exception) { }
            AppState.setProgress(null)
        }
    }

    /** 用户对接收请求的决定。可由界面对话框或通知按钮调用。 */
    fun respond(requestId: String, accept: Boolean) {
        val d = pendingDecisions.remove(requestId)
        if (AppState.pending.value?.id == requestId) AppState.setPending(null)
        d?.complete(accept)
    }

    // ---------------- TCP 发送 ----------------
    suspend fun sendFile(target: Peer, uri: Uri): Pair<Boolean, String> = withContext(Dispatchers.IO) {
        val cr = appContext.contentResolver
        val displayName = queryDisplayName(cr, uri) ?: "文件"
        // 尽量拿到长期读权限，这样「最近传输」里点开已发送的文件仍可打开
        try { cr.takePersistableUriPermission(uri, android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION) } catch (e: Exception) { }

        // 部分 Provider 查不到文件大小（statSize=-1）。若直接按 0 发送，
        // 对方会立刻按「0 字节」收完并生成空文件——所以先落到缓存文件量出真实长度。
        var tmpFile: File? = null
        var size = resolveSize(cr, uri)
        var openInput: () -> InputStream? = { cr.openInputStream(uri) }
        if (size < 0) {
            val t = File(appContext.cacheDir, "feige_send_${System.nanoTime()}.tmp")
            try {
                val ins = cr.openInputStream(uri) ?: return@withContext false to "无法读取所选文件"
                ins.use { input -> t.outputStream().use { input.copyTo(it) } }
            } catch (e: Exception) {
                try { t.delete() } catch (_: Exception) { }
                return@withContext false to "无法读取所选文件：${e.message}"
            }
            tmpFile = t
            size = t.length()
            openInput = { t.inputStream() }
        }

        var client: Socket? = null
        try {
            AppState.log("开始发送：$displayName → ${target.name}")
            client = Socket()
            client.soTimeout = 10000
            withTimeout(10_000) { client.connect(InetSocketAddress(target.ip, target.tcpPort), 10_000) }
            writeLine(client, Protocol.buildFileRequest(displayName, size))

            // 对方要先弹窗请你确认，等待时间必须覆盖它的 60 秒确认窗口
            val replyStream = client.getInputStream()
            AppState.log("已发送请求，等待 ${target.name} 确认接收…")
            val ackLine = withTimeout(ACK_TIMEOUT_MS) { readLineRaw(replyStream) }
            if (ackLine == null) return@withContext false to "对方未在规定时间内确认"
            val ack = Protocol.parseAck(ackLine)
            if (!ack.first) return@withContext false to (ack.second.ifBlank { "对方拒绝了本次传输" })

            val input = openInput() ?: return@withContext false to "无法打开文件"
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
                val doneLine = withTimeout(10_000) { readLineRaw(replyStream) }
                val recv = doneLine?.let { Protocol.parseDone(it) }
                if (recv == null || recv != sent) return@withContext false to "对方确认数不符（期望 $sent，实收 $recv）"
            }
            AppState.addRecord(TransferRecord(TransferDirection.SEND, displayName, target.name,
                size.coerceAtLeast(0), true, nowTime(), uri.toString()))
            AppState.log("发送完成：$displayName → ${target.name}")
            true to "已发送 $displayName"
        } catch (e: Exception) {
            AppState.log("发送失败：${e.message}")
            AppState.addRecord(TransferRecord(TransferDirection.SEND, displayName, target.name, 0, false, nowTime()))
            false to (e.message ?: "未知错误")
        } finally {
            try { client?.close() } catch (e: Exception) { }
            try { tmpFile?.delete() } catch (e: Exception) { }
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

    /** 尽量查出文件长度；查不到返回 -1（调用方会退回缓存文件方式量长度）。 */
    private fun resolveSize(cr: android.content.ContentResolver, uri: Uri): Long {
        try {
            cr.openFileDescriptor(uri, "r")?.use { fd ->
                if (fd.statSize >= 0) return fd.statSize
            }
        } catch (e: Exception) { }
        try {
            cr.openAssetFileDescriptor(uri, "r")?.use { afd ->
                if (afd.length >= 0) return afd.length
            }
        } catch (e: Exception) { }
        return -1
    }

    /** 逐字节读一行（以 \n 结尾），不使用 BufferedReader，避免预读缓冲吞掉随后的文件二进制数据。 */
    private fun readLineRaw(input: InputStream): String? {
        val buf = ByteArrayOutputStream()
        while (true) {
            val b = input.read()
            if (b == -1) break
            if (b == '\n'.code) break
            if (b != '\r'.code) buf.write(b)
            if (buf.size() > 8192) break
        }
        return if (buf.size() == 0) null else String(buf.toByteArray(), StandardCharsets.UTF_8)
    }

    private fun writeLine(socket: Socket, text: String) {
        socket.getOutputStream().write(text.toByteArray(StandardCharsets.UTF_8))
        socket.getOutputStream().flush()
    }

    private fun nowTime(): String = java.text.SimpleDateFormat("HH:mm:ss", java.util.Locale.getDefault()).format(java.util.Date())
}
