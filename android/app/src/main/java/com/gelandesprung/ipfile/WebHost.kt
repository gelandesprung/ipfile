package com.gelandesprung.ipfile

import android.content.Context
import android.content.Intent
import android.net.Uri
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketTimeoutException
import java.security.SecureRandom
import java.util.concurrent.ConcurrentHashMap

/**
 * 网页模式的 Host 端服务（Android）：给没装 App 的朋友一个浏览器入口。
 *
 * 与 PC 的 `WebHostService.cs` 逐接口对齐，只是传输层换成 Android 的东西：
 * - 地址与端口：ServerSocket 先试 `::`（双栈，同时接受 IPv4/IPv6），失败退回 `0.0.0.0`
 * - 信令：SSE（EventSource），语义等同 WebSocket，但免去帧编解码、断线靠 TCP EOF 判定
 * - 下载（Host → 访客）：从 SAF 的 content:// 流式读取，长度未知时先落缓存量长度
 * - 上传（访客 → Host）：两阶段 offer → PUT；落盘走 MediaStore 的 IS_PENDING
 *   （这就是安卓版的 `.part`：收齐才发布，中途失败直接删行，不留残缺文件）
 *
 * 生命周期由前台服务 [ReceiveService] 持有，退后台/锁屏仍在运行。
 */
class WebHost(private val appContext: Context) {

    companion object {
        /** 随机端口区间：避开系统端口，也避开 8080/3000 这类被各方抢占的热门端口。 */
        const val PORT_MIN = 20000
        const val PORT_MAX = 61000
        private val SECURE_RANDOM = SecureRandom()
        private const val BUF = 64 * 1024
        private const val OFFER_TTL_MS = 60_000L
        private const val UPLOAD_CONFIRM_TIMEOUT_MS = 60_000L
        private const val ORPHAN_GUEST_TTL_MS = 60_000L
        private const val PIN_FAILURE_LIMIT = 3
        private const val MAX_HEADER = 64 * 1024
    }

    /** 待发送给访客的一个文件；size 为 -1 表示长度未知。 */
    data class OutFile(val uri: Uri, val name: String, val size: Long)

    private class Guest(
        val id: String,
        val nickname: String,
        val remoteIp: String,
        val joinedAt: Long = System.currentTimeMillis()
    ) {
        @Volatile var status: String = "空闲"
        @Volatile var channel: SseChannel? = null
        @Volatile var justJoined: Boolean = true
    }

    /** 一条 SSE 通道：写失败即判定断开。 */
    private class SseChannel(private val socket: Socket, private val out: OutputStream) {
        private val lock = Any()

        @Volatile
        var alive: Boolean = true
            private set

        fun send(json: String) = sendRaw("data: $json\n\n")

        fun sendRaw(text: String) {
            if (!alive) return
            synchronized(lock) {
                if (!alive) return
                try {
                    out.write(text.toByteArray(Charsets.UTF_8))
                    out.flush()
                } catch (e: Exception) {
                    alive = false
                }
            }
        }

        /** 关通道：连 socket 一起关，否则阻塞在读上的协程要等超时才能退出。 */
        fun close() {
            alive = false
            runCatching { socket.close() }
        }
    }

    /** Host 发出的一笔「文件发送请求」：访客确认后凭 token 走 HTTP 取文件。 */
    private class Offer(
        val id: String,
        val token: String,
        val guestId: String,
        val guestName: String,
        val file: OutFile
    ) {
        @Volatile var state: String = "pending"
        val decision = CompletableDeferred<String>()
        val done = CompletableDeferred<Boolean>()
    }

    /** 一次已获 Host 批准的上传：访客随后 PUT 内容。 */
    private class UploadSlot(
        val id: String,
        val guestId: String,
        val guestName: String,
        val fileName: String,
        val size: Long,
        val createdAt: Long = System.currentTimeMillis()
    ) {
        @Volatile var target: NetUtils.ReceiveTarget? = null
        val decision = CompletableDeferred<Boolean>()
    }

    // ---------------- 状态 ----------------

    private var scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var server: ServerSocket? = null
    private var acceptJob: Job? = null

    private val guests = ConcurrentHashMap<String, Guest>()
    private val offers = ConcurrentHashMap<String, Offer>()
    private val uploads = ConcurrentHashMap<String, UploadSlot>()

    private val pinLock = Any()
    @Volatile private var pinValue: String = ""
    private var pinFailures = 0

    @Volatile
    var running: Boolean = false
        private set

    /** 实际监听端口（可能因占用而顺延）。 */
    @Volatile
    var port: Int = 0
        private set

    val pin: String get() = pinValue

    private val guestHtml: String by lazy { loadGuestHtml() }

    // ---------------- 生命周期 ----------------

    /**
     * 启动服务。[preferredPort] 传 0（默认）表示每次启动都随机换一个端口，不记忆上次用过的——
     * 固定端口容易被别的程序占，也容易被猜到。端口被占用时：随机模式会再抽几个随机端口，
     * 最后顺延兜底；指定端口则只往后顺延。
     */
    fun start(preferredPort: Int = 0): Pair<Boolean, String> {
        if (running) return true to "网页服务已在运行 · 端口 $port"

        val random = preferredPort <= 0
        val start = if (random) randomPort() else preferredPort.coerceIn(1024, 65535)
        var socket: ServerSocket? = null
        var used = start
        var lastError = ""
        for (p in portCandidates(start, random)) {
            if (p < 1024 || p > 65535) continue
            try {
                socket = openSocket(p)
                used = p
                break
            } catch (e: Exception) {
                lastError = e.message ?: e.javaClass.simpleName
            }
        }
        if (socket == null) return false to if (random) {
            "随机端口都没绑上（最后一个 $used）：$lastError"
        } else {
            "端口 $start–${start + 11} 都不可用：$lastError"
        }

        server = socket
        port = used
        running = true
        synchronized(pinLock) {
            pinValue = newPin()
            pinFailures = 0
        }
        AppState.setWebPin(pinValue)
        AppState.setWebPort(used)
        AppState.setWebRunning(true)

        scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        acceptJob = scope.launch { acceptLoop(socket) }
        scope.launch { sweepLoop() }

        log("网页服务已启动 · 端口 $used（局域网内浏览器可访问）")
        return true to when {
            used == start -> "网页服务已启动 · 端口 $used"
            random -> "随机端口 $start 被占用，已改用 $used"
            else -> "端口 $start 被占用，已改用 $used"
        }
    }

    /** 随机挑一个端口（只挑数值，能不能用由 [start] 试绑决定）。 */
    private fun randomPort(): Int = PORT_MIN + SECURE_RANDOM.nextInt(PORT_MAX - PORT_MIN + 1)

    /** 候选端口序列：随机模式先抽 5 个随机端口，再顺延 8 个兜底。 */
    private fun portCandidates(start: Int, random: Boolean): List<Int> {
        val seen = LinkedHashSet<Int>()
        seen.add(start)
        if (!random) {
            for (i in 1 until 12) seen.add(start + i)
            return seen.toList()
        }
        repeat(4) { seen.add(randomPort()) }
        for (i in 1..8) seen.add(start + i)
        return seen.toList()
    }

    /**
     * 双栈优先：绑 `::` 时 Android 默认 net.ipv6.bindv6only=0，同一个端口
     * 既接受 IPv6 也接受 v4-mapped 的 IPv4 连接，不用为两个地址族开两个端口。
     */
    private fun openSocket(p: Int): ServerSocket {
        try {
            return ServerSocket().apply {
                reuseAddress = true
                bind(InetSocketAddress(InetAddress.getByName("::"), p), 50)
            }
        } catch (e: Exception) {
            // 设备关掉了 IPv6 / 内核只允许 v4：退回 IPv4 单栈
        }
        return ServerSocket().apply {
            reuseAddress = true
            bind(InetSocketAddress(p), 50)
        }
    }

    fun stop() {
        if (!running) return
        running = false
        try { acceptJob?.cancel() } catch (e: Exception) { }
        try { server?.close() } catch (e: Exception) { }
        server = null
        acceptJob = null

        val snapshot = guests.values.toList()
        guests.clear()
        offers.values.forEach { it.decision.complete("stop") }
        offers.clear()
        uploads.values.forEach {
            it.decision.complete(false)
            it.target?.let { t -> runCatching { t.abort() } }
        }
        uploads.clear()
        snapshot.forEach { g ->
            runCatching { g.channel?.sendRaw("data: {\"type\":\"shutdown\"}\n\n") }
            g.channel?.close()
        }
        AppState.setPendingUpload(null)
        AppState.setWebGuests(emptyList())
        AppState.setWebRunning(false)
        AppState.setWebPort(0)
        AppState.setWebPin("")
        if (snapshot.isNotEmpty()) log("网页服务已停止，${snapshot.size} 位访客已断开") else log("网页服务已停止")
        scope.cancel()
    }

    /** 刷新 PIN：只挡新访客，已在线的访客不受影响。 */
    fun refreshPin(): String {
        synchronized(pinLock) {
            pinValue = newPin()
            pinFailures = 0
        }
        AppState.setWebPin(pinValue)
        log("访问 PIN 已刷新为 $pinValue（已在线的访客不受影响）")
        return pinValue
    }

    fun snapshotGuests(): List<WebGuest> =
        guests.values.sortedBy { it.joinedAt }.map {
            WebGuest(it.id, it.nickname, it.remoteIp, it.status, it.justJoined)
        }

    fun disconnectGuest(guestId: String): Boolean {
        val g = guests[guestId] ?: return false
        g.channel?.close()
        removeGuest(g, "已被主机断开")
        return true
    }

    /** 用户对上传请求的决定（界面弹框或通知按钮调用）。 */
    fun respondUpload(id: String, accept: Boolean) {
        val slot = uploads[id] ?: return
        if (AppState.pendingUpload.value?.id == id) AppState.setPendingUpload(null)
        slot.decision.complete(accept)
    }

    private fun newPin(): String = (100000 + SecureRandom().nextInt(900000)).toString()

    private fun newId(bytes: Int): String {
        val b = ByteArray(bytes)
        SecureRandom().nextBytes(b)
        return b.joinToString("") { "%02x".format(it) }
    }

    // ---------------- 发送文件给访客 ----------------

    /** 给选中访客逐个发文件；多个访客并行，各自独立确认、互不影响。 */
    fun sendFiles(guestIds: List<String>, uris: List<Uri>) {
        if (!running) return
        scope.launch {
            val files = uris.mapNotNull { toOutFile(it) }
            if (files.isEmpty()) {
                log("没有可发送的文件")
                return@launch
            }
            guestIds.distinct()
                .filter { it.isNotEmpty() }
                .map { id -> async { sendToGuest(id, files) } }
                .awaitAll()
        }
    }

    private fun toOutFile(uri: Uri): OutFile? = try {
        // 尽量拿到长期读权限，传输过程中 Activity 被回收也不会失效
        runCatching {
            appContext.contentResolver.takePersistableUriPermission(
                uri, Intent.FLAG_GRANT_READ_URI_PERMISSION
            )
        }
        OutFile(uri, NetUtils.displayName(appContext, uri), NetUtils.sizeOf(appContext, uri))
    } catch (e: Exception) {
        log("无法读取所选文件：${e.message}")
        null
    }

    private suspend fun sendToGuest(guestId: String, files: List<OutFile>) {
        for (f in files) {
            val guest = guests[guestId]
            if (guest == null) {
                log("访客已离线，发送已取消")
                return
            }

            val offer = Offer(newId(8), newId(24), guest.id, guest.nickname, f)
            offers[offer.id] = offer
            guest.status = "待确认 · ${f.name}"
            publishGuests(true)
            log("已向 ${guest.nickname} 发出「${f.name}」（${NetUtils.formatSize(f.size.coerceAtLeast(0))}），等待对方确认…")
            push(
                guest, jsonOf(
                    "type" to "file_offer",
                    "id" to offer.id,
                    "token" to offer.token,
                    "name" to f.name,
                    "size" to f.size,
                    "ttl" to (OFFER_TTL_MS / 1000).toInt()
                )
            )

            val decision = withTimeoutOrNull(OFFER_TTL_MS) { offer.decision.await() } ?: "expire"
            if (decision != "accept") {
                val reason = when (decision) {
                    "reject" -> "对方拒绝接收"
                    "stop" -> "服务已停止"
                    else -> "超时未确认，已自动取消"
                }
                guest.status = "空闲"
                publishGuests(true)
                push(guest, jsonOf("type" to "offer_cancel", "id" to offer.id, "reason" to reason))
                log("${f.name} → ${guest.nickname}：$reason")
                AppState.addRecord(
                    TransferRecord(
                        TransferDirection.SEND, f.name, guest.nickname,
                        f.size.coerceAtLeast(0), false, nowTime()
                    )
                )
                offers.remove(offer.id)
                continue
            }

            // 已确认：等访客来取（取完由 serveBlob 置 done）
            guest.status = "传输中 · ${f.name}"
            publishGuests(true)
            val timeout = 60_000L + f.size.coerceAtLeast(0) / 256
            val finished = withTimeoutOrNull(timeout) { offer.done.await() } ?: false
            if (!finished) {
                guest.status = "空闲"
                publishGuests(true)
                log("${f.name} → ${guest.nickname}：对方已确认但未完成下载（超时）")
                AppState.addRecord(
                    TransferRecord(
                        TransferDirection.SEND, f.name, guest.nickname,
                        f.size.coerceAtLeast(0), false, nowTime()
                    )
                )
                offers.remove(offer.id)
            }
        }
    }

    private fun push(guest: Guest, payload: String) {
        guest.channel?.send(payload)
    }

    // ---------------- 连接与分发 ----------------

    private suspend fun acceptLoop(sock: ServerSocket) {
        while (running && scope.isActive) {
            val client = try {
                sock.accept()
            } catch (e: Exception) {
                if (!running) break else continue
            }
            scope.launch { handleSocket(client) }
        }
    }

    private suspend fun handleSocket(socket: Socket) {
        try {
            socket.tcpNoDelay = true
            socket.soTimeout = 30_000
            val input = socket.getInputStream()
            val output = socket.getOutputStream()
            val req = readRequest(socket, input, output) ?: return
            dispatch(req)
        } catch (e: SocketTimeoutException) {
            // 慢客户端 / SSE 心跳，不算错误
        } catch (e: IOException) {
            // 访客关页、下载中途取消
        } catch (e: Exception) {
            if (running) log("网页请求处理失败：${e.message}")
        } finally {
            try { socket.close() } catch (e: Exception) { }
        }
    }

    private suspend fun dispatch(req: Request) {
        val path = req.path

        if (req.method == "GET" && (path == "/" || path == "/index.html")) {
            writeText(req, 200, "text/html; charset=utf-8", guestHtml)
            return
        }
        if (path == "/favicon.ico") {
            writeStatus(req, 204)
            return
        }
        // 大请求体前先应答 100-continue，否则部分浏览器会干等
        if (req.headers["expect"]?.contains("100-continue", true) == true) {
            req.output.write("HTTP/1.1 100 Continue\r\n\r\n".toByteArray())
            req.output.flush()
        }

        when {
            path == "/api/join" && req.method == "POST" -> handleJoin(req)
            path == "/api/events" && req.method == "GET" -> serveEvents(req)
            path.startsWith("/api/offer/") && req.method == "POST" ->
                handleOfferDecision(req, path.removePrefix("/api/offer/"))
            path.startsWith("/api/blob/") && req.method == "GET" ->
                serveBlob(req, path.removePrefix("/api/blob/"))
            path == "/api/upload/offer" && req.method == "POST" -> handleUploadOffer(req)
            path.startsWith("/api/upload/") && req.method == "PUT" ->
                handleUploadPut(req, path.removePrefix("/api/upload/"))
            else -> writeJson(req, 404, mapOf("ok" to false, "error" to "接口不存在"))
        }
    }

    // ---------------- 加入 ----------------

    private fun handleJoin(req: Request) {
        val obj = runCatching { JSONObject(readBodyString(req, 4096)) }.getOrNull()
        if (obj == null) {
            writeJson(req, 200, mapOf("ok" to false, "error" to "请求格式错误"))
            return
        }
        val nickname = obj.optString("nickname", "")
        val inputPin = obj.optString("pin", "")

        if (inputPin.trim() != pinValue) {
            // 连续输错满 3 次就换码：熟人场景大概率是记错了，换码比反复试错更省事
            var rotated: String? = null
            var failures = 0
            synchronized(pinLock) {
                pinFailures++
                failures = pinFailures
                if (failures >= PIN_FAILURE_LIMIT) {
                    pinValue = newPin()
                    pinFailures = 0
                    rotated = pinValue
                }
            }
            if (rotated != null) {
                AppState.setWebPin(rotated!!)
                AppState.notifyPinRotated()
                log("连续 $PIN_FAILURE_LIMIT 次 PIN 输错（来自 ${req.remoteIp}），已自动更换新 PIN：$rotated")
                writeJson(
                    req, 200,
                    mapOf("ok" to false, "error" to "PIN 已因多次输错而更换，请向主机索取新的 PIN")
                )
                return
            }
            log("访客加入被拒绝（PIN 不正确，来自 ${req.remoteIp}，第 $failures 次）")
            writeJson(req, 200, mapOf("ok" to false, "error" to "PIN 不正确，请向主机确认"))
            return
        }

        synchronized(pinLock) { pinFailures = 0 }

        val guest = Guest(
            id = newId(6),
            nickname = uniqueNickname(sanitizeNickname(nickname)),
            remoteIp = req.remoteIp
        )
        guests[guest.id] = guest
        log("${guest.nickname} 已通过浏览器加入（${guest.remoteIp}）")
        publishGuests(true)

        // 「刚加入」只是 5 秒提示
        scope.launch {
            delay(5000)
            val g = guests[guest.id] ?: return@launch
            g.justJoined = false
            publishGuests(true)
        }

        writeJson(
            req, 200,
            mapOf(
                "ok" to true,
                "clientId" to guest.id,
                "nickname" to guest.nickname,
                "host" to AppState.deviceName.value
            )
        )
    }

    private fun sanitizeNickname(raw: String): String {
        val cleaned = raw.trim().filter { !it.isISOControl() }.trim()
        val s = cleaned.ifEmpty { "访客" }
        return if (s.length > 16) s.substring(0, 16) else s
    }

    private fun uniqueNickname(nickname: String): String {
        val taken = guests.values.map { it.nickname }
        if (!taken.contains(nickname)) return nickname
        for (i in 2 until 100) {
            val candidate = "$nickname ($i)"
            if (!taken.contains(candidate)) return candidate
        }
        return "$nickname ${newId(2)}"
    }

    // ---------------- 信令通道 ----------------

    private fun serveEvents(req: Request) {
        val id = req.query["c"] ?: ""
        val guest = guests[id]
        if (guest == null) {
            writeJson(req, 200, mapOf("ok" to false, "error" to "会话已失效，请重新加入"))
            return
        }

        val head =
            "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/event-stream; charset=utf-8\r\n" +
                "Cache-Control: no-cache, no-store\r\n" +
                "Connection: keep-alive\r\n" +
                "X-Accel-Buffering: no\r\n\r\n"
        req.output.write(head.toByteArray())
        req.output.flush()

        val channel = SseChannel(req.socket, req.output)
        // 先登记新通道再关旧的：否则旧协程的收尾会看到"通道还是我"，把刚重连的访客删掉
        val previous = guest.channel
        guest.channel = channel
        previous?.close()

        // 首帧快照：刷新页面后能恢复身份，也能看到还没处理完的发送请求
        channel.send(
            jsonOf(
                "type" to "snapshot",
                "nickname" to guest.nickname,
                "host" to AppState.deviceName.value,
                "online" to guests.size
            )
        )
        offers.values
            .filter { it.guestId == guest.id && it.state == "pending" }
            .forEach { o ->
                channel.send(
                    jsonOf(
                        "type" to "file_offer", "id" to o.id, "token" to o.token,
                        "name" to o.file.name, "size" to o.file.size,
                        "ttl" to (OFFER_TTL_MS / 1000).toInt()
                    )
                )
            }

        guest.status = "空闲"
        publishGuests(true)

        // 读超时当心跳：15 秒没读到任何东西就发一行注释保活；读到 EOF 说明页面关了
        try {
            req.socket.soTimeout = 15_000
            val probe = ByteArray(256)
            while (channel.alive) {
                val n = try {
                    req.input.read(probe)
                } catch (e: SocketTimeoutException) {
                    channel.sendRaw(": ping\n\n")
                    continue
                }
                if (n < 0) break
            }
        } catch (e: Exception) {
            // 断开
        } finally {
            channel.close()
            // 只有"当前这条"通道断开才算访客离线；被新连接顶掉的旧通道不算
            if (guest.channel === channel) {
                guest.channel = null
                removeGuest(guest, "已断开")
            }
        }
    }

    private fun removeGuest(guest: Guest, reason: String) {
        if (guests.remove(guest.id) == null) return
        offers.values.filter { it.guestId == guest.id }.forEach {
            it.decision.complete("reject")
            it.done.complete(false)
            offers.remove(it.id)
        }
        uploads.values.filter { it.guestId == guest.id && it.target == null }.forEach {
            it.decision.complete(false)
            uploads.remove(it.id)
        }
        if (AppState.pendingUpload.value?.guestName == guest.nickname) AppState.setPendingUpload(null)
        log("${guest.nickname} $reason（${guest.remoteIp}）")
        publishGuests(true)
    }

    // ---------------- 访客对发送请求的应答 ----------------

    private fun handleOfferDecision(req: Request, rest: String) {
        val accept = rest.endsWith("/accept")
        val reject = rest.endsWith("/reject")
        if (!accept && !reject) {
            writeJson(req, 404, mapOf("ok" to false, "error" to "接口不存在"))
            return
        }
        val id = rest.removeSuffix("/accept").removeSuffix("/reject")
        val offer = offers[id]
        if (offer == null || offer.state != "pending") {
            writeJson(req, 200, mapOf("ok" to false, "error" to "该请求已失效"))
            return
        }
        if (accept) {
            offer.state = "accepted"
            offer.decision.complete("accept")
            log("${offer.guestName} 已确认接收「${offer.file.name}」")
            writeJson(req, 200, mapOf("ok" to true, "token" to offer.token))
        } else {
            offer.state = "rejected"
            offer.decision.complete("reject")
            writeJson(req, 200, mapOf("ok" to true))
        }
    }

    // ---------------- 下载：Host → 访客 ----------------

    private fun serveBlob(req: Request, token: String) {
        val offer = offers.values.firstOrNull { it.token == token }
        if (offer == null || offer.state != "accepted") {
            writeJson(req, 404, mapOf("ok" to false, "error" to "下载链接已失效"))
            return
        }

        val cr = appContext.contentResolver
        var size = offer.file.size
        var temp: File? = null
        var opener: () -> InputStream? = { runCatching { cr.openInputStream(offer.file.uri) }.getOrNull() }

        // 长度未知（部分 Provider statSize=-1）：先落到缓存量出真实长度，
        // 否则没法给浏览器 Content-Length，进度条也就无从谈起
        if (size < 0) {
            val t = File(appContext.cacheDir, "feige_web_${System.nanoTime()}.tmp")
            try {
                val ins = opener() ?: throw IOException("无法读取文件")
                ins.use { input -> t.outputStream().use { input.copyTo(it) } }
                temp = t
                size = t.length()
                opener = { t.inputStream() }
            } catch (e: Exception) {
                runCatching { t.delete() }
                writeJson(req, 404, mapOf("ok" to false, "error" to "文件已被移动或删除"))
                return
            }
        }

        val head =
            "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/octet-stream\r\n" +
                "Content-Length: $size\r\n" +
                "Content-Disposition: ${WebWire.contentDisposition(offer.file.name)}\r\n" +
                "Cache-Control: no-store\r\n" +
                "Connection: close\r\n\r\n"
        req.output.write(head.toByteArray())
        req.output.flush()

        var sent = 0L
        var ok = false
        var lastTick = System.currentTimeMillis()
        try {
            val ins = opener() ?: throw IOException("无法读取文件")
            ins.use { input ->
                val buf = ByteArray(BUF)
                while (sent < size) {
                    val want = minOf(buf.size.toLong(), size - sent).toInt()
                    val n = input.read(buf, 0, want)
                    if (n <= 0) break
                    req.output.write(buf, 0, n)
                    sent += n
                    val now = System.currentTimeMillis()
                    if (now - lastTick >= 400) {
                        lastTick = now
                        AppState.setProgress(
                            ProgressInfo(TransferDirection.SEND, offer.file.name, sent, size)
                        )
                        guests[offer.guestId]?.let { g ->
                            g.status = "传输中 ${pct(sent, size)}% · ${offer.file.name}"
                            publishGuests()
                        }
                    }
                }
                req.output.flush()
            }
            ok = sent >= size
        } catch (e: Exception) {
            // 访客中途取消下载：不是错误，按未完成处理
        } finally {
            runCatching { temp?.delete() }
            AppState.setProgress(null)
        }

        offer.state = if (ok) "done" else "expired"
        offer.done.complete(ok)
        offers.remove(offer.id)
        guests[offer.guestId]?.let { it.status = "空闲" }
        publishGuests(true)

        if (ok) {
            log("✓ 已发送「${offer.file.name}」给 ${offer.guestName}（${NetUtils.formatSize(size)}）")
            AppState.addRecord(
                TransferRecord(
                    TransferDirection.SEND, offer.file.name, offer.guestName,
                    size, true, nowTime(), offer.file.uri.toString()
                )
            )
        } else {
            log("✗ 「${offer.file.name}」传输中断（${NetUtils.formatSize(sent)}/${NetUtils.formatSize(size)}）")
            AppState.addRecord(
                TransferRecord(
                    TransferDirection.SEND, offer.file.name, offer.guestName,
                    size, false, nowTime()
                )
            )
        }
    }

    // ---------------- 上传：访客 → Host（两阶段） ----------------

    private suspend fun handleUploadOffer(req: Request) {
        val obj = runCatching { JSONObject(readBodyString(req, 8192)) }.getOrNull()
        if (obj == null) {
            writeJson(req, 200, mapOf("ok" to false, "error" to "请求格式错误"))
            return
        }
        val clientId = obj.optString("clientId", "")
        val rawName = obj.optString("name", "")
        val size = obj.optLong("size", 0)

        val guest = guests[clientId]
        if (guest == null) {
            writeJson(req, 200, mapOf("ok" to false, "error" to "会话已失效，请刷新页面"))
            return
        }
        val fileName = WebWire.sanitizeFileName(rawName)
        if (size <= 0) {
            writeJson(req, 200, mapOf("ok" to false, "error" to "文件为空"))
            return
        }

        log("上传请求：$fileName（${NetUtils.formatSize(size)}）来自 ${guest.nickname}，等待确认…")

        val slot = UploadSlot(newId(8), guest.id, guest.nickname, fileName, size)
        uploads[slot.id] = slot
        AppState.setPendingUpload(
            PendingUpload(
                slot.id, fileName, size, guest.nickname, guest.remoteIp,
                System.currentTimeMillis() + UPLOAD_CONFIRM_TIMEOUT_MS
            )
        )

        // 等用户点「接收」：界面弹框或通知按钮都会走到 respondUpload
        val accepted = awaitDecision(slot, UPLOAD_CONFIRM_TIMEOUT_MS)
        if (AppState.pendingUpload.value?.id == slot.id) AppState.setPendingUpload(null)

        if (!accepted) {
            uploads.remove(slot.id)
            guest.status = "空闲"
            publishGuests(true)
            log("已拒绝 ${guest.nickname} 的上传：$fileName")
            writeJson(req, 200, mapOf("ok" to false, "error" to "主机拒绝接收"))
            return
        }

        // 安卓端不选保存位置：直接进公共 Download/数据线收件/，与 P2P 接收同一套落盘
        val target = try {
            NetUtils.createReceiveStream(appContext, fileName)
        } catch (e: Exception) {
            uploads.remove(slot.id)
            guest.status = "空闲"
            publishGuests(true)
            log("上传：创建文件失败 — ${e.message}")
            writeJson(req, 200, mapOf("ok" to false, "error" to "无法创建文件：${e.message}"))
            return
        }
        slot.target = target
        guest.status = "上传中 · $fileName"
        publishGuests(true)
        log("已同意接收，等待 ${guest.nickname} 上传内容…")
        writeJson(req, 200, mapOf("ok" to true, "uploadId" to slot.id))
    }

    /** 等用户决定；超时或访客断线都按拒绝处理。 */
    private suspend fun awaitDecision(slot: UploadSlot, timeoutMs: Long): Boolean =
        try {
            withTimeoutOrNull(timeoutMs) { slot.decision.await() } ?: false
        } catch (e: Exception) {
            false
        }

    private fun handleUploadPut(req: Request, uploadId: String) {
        val slot = uploads[uploadId]
        val target = slot?.target
        if (slot == null || target == null) {
            writeJson(req, 404, mapOf("ok" to false, "error" to "上传会话已失效"))
            return
        }

        var written = 0L
        var ok = false
        try {
            // 上传期间放宽读超时：慢速网络下也别把连接掐了（但保留上限，防挂死）
            req.socket.soTimeout = 120_000
            if (req.prefix.isNotEmpty()) {
                target.stream.write(req.prefix)
                written += req.prefix.size
            }
            var remaining = req.contentLength - req.prefix.size
            val buf = ByteArray(BUF)
            var lastTick = System.currentTimeMillis()
            while (remaining > 0) {
                val want = minOf(buf.size.toLong(), remaining).toInt()
                val n = req.input.read(buf, 0, want)
                if (n <= 0) break
                target.stream.write(buf, 0, n)
                written += n
                remaining -= n

                val now = System.currentTimeMillis()
                if (now - lastTick >= 400) {
                    lastTick = now
                    AppState.setProgress(
                        ProgressInfo(TransferDirection.RECEIVE, slot.fileName, written, slot.size)
                    )
                    guests[slot.guestId]?.let { g ->
                        g.status = "上传中 ${pct(written, slot.size)}% · ${slot.fileName}"
                        publishGuests()
                    }
                }
            }
            target.stream.flush()
            if (slot.size > 0 && written != slot.size) {
                throw IOException("字节数不符（期望 ${slot.size}，实收 $written）")
            }
            // 关闭即发布：MediaStore 的 IS_PENDING 清零，文件才对其他 App 可见
            target.stream.close()
            ok = true
        } catch (e: Exception) {
            runCatching { target.abort() }
            log("上传失败：${slot.fileName} — ${e.message}")
        } finally {
            uploads.remove(uploadId)
            AppState.setProgress(null)
            guests[slot.guestId]?.status = "空闲"
            publishGuests(true)
        }

        if (ok) {
            log("✓ 已接收 ${slot.guestName} 上传的「${slot.fileName}」→ ${target.displayPath}")
            AppState.addRecord(
                TransferRecord(
                    TransferDirection.RECEIVE, slot.fileName, slot.guestName,
                    written, true, nowTime(), target.uri.toString()
                )
            )
        } else {
            AppState.addRecord(
                TransferRecord(
                    TransferDirection.RECEIVE, slot.fileName, slot.guestName,
                    written, false, nowTime()
                )
            )
        }
        writeJson(req, 200, mapOf("ok" to ok, "bytes" to written))
    }

    // ---------------- 维护 ----------------

    private suspend fun sweepLoop() {
        while (running && scope.isActive) {
            delay(10_000)
            val now = System.currentTimeMillis()
            try {
                guests.values
                    .filter { it.channel == null && now - it.joinedAt > ORPHAN_GUEST_TTL_MS }
                    .forEach { removeGuest(it, "加入后未建立连接，已移除") }

                uploads.values
                    .filter { it.target == null && now - it.createdAt > UPLOAD_CONFIRM_TIMEOUT_MS + 30_000 }
                    .forEach {
                        it.decision.complete(false)
                        uploads.remove(it.id)
                    }
            } catch (e: Exception) {
                if (running) log("网页模式维护任务异常：${e.message}")
            }
        }
    }

    private var lastPublish = 0L

    private fun publishGuests(force: Boolean = false) {
        val now = System.currentTimeMillis()
        if (!force && now - lastPublish < 350) return
        lastPublish = now
        AppState.setWebGuests(snapshotGuests())
    }

    private fun pct(done: Long, total: Long): Int =
        if (total > 0) ((done * 100) / total).toInt().coerceIn(0, 100) else 0

    private fun log(msg: String) = AppState.log(msg)

    private fun nowTime(): String =
        java.text.SimpleDateFormat("HH:mm:ss", java.util.Locale.getDefault()).format(java.util.Date())

    private fun loadGuestHtml(): String = try {
        appContext.assets.open("guest.html").bufferedReader().use { it.readText() }
    } catch (e: Exception) {
        log("访客页资源缺失：${e.message}")
        "<!DOCTYPE html><meta charset=\"utf-8\"><title>数据线</title>" +
            "<p style=\"font-family:sans-serif;padding:24px\">访客页资源缺失，请重新安装应用。</p>"
    }

    // ---------------- 极简 HTTP 读写 ----------------

    private class Request(
        val method: String,
        val path: String,
        val query: Map<String, String>,
        val headers: Map<String, String>,
        val prefix: ByteArray,
        val contentLength: Long,
        val socket: Socket,
        val input: InputStream,
        val output: OutputStream,
        val remoteIp: String
    )

    private fun readRequest(socket: Socket, input: InputStream, output: OutputStream): Request? {
        val acc = ByteArrayOutputStream()
        val buf = ByteArray(8192)
        var headerEnd = -1

        while (headerEnd < 0) {
            val n = input.read(buf)
            if (n <= 0) return null
            acc.write(buf, 0, n)
            if (acc.size() > MAX_HEADER) return null
            headerEnd = WebWire.findHeaderEnd(acc.toByteArray(), acc.size())
        }

        val all = acc.toByteArray()
        val head = WebWire.parseHead(String(all, 0, headerEnd, Charsets.UTF_8)) ?: return null

        val contentLength = head.headers["content-length"]?.toLongOrNull() ?: 0L
        val prefixLen = minOf(maxOf(0, all.size - headerEnd).toLong(), contentLength).toInt()
        val prefix = ByteArray(prefixLen)
        if (prefixLen > 0) System.arraycopy(all, headerEnd, prefix, 0, prefixLen)

        return Request(
            method = head.method,
            path = head.path,
            query = head.query,
            headers = head.headers,
            prefix = prefix,
            contentLength = contentLength,
            socket = socket,
            input = input,
            output = output,
            remoteIp = normalizeRemote(socket)
        )
    }

    /** IPv4 走 v4-mapped 时是 ::ffff:192.168.1.7，去掉前缀便于显示。 */
    private fun normalizeRemote(socket: Socket): String {
        val host = try { socket.inetAddress?.hostAddress } catch (e: Exception) { null } ?: return "?"
        return host.removePrefix("::ffff:").substringBefore('%')
    }

    private fun readBodyString(req: Request, max: Int): String {
        if (req.contentLength <= 0) return ""
        if (req.contentLength > max) throw IllegalStateException("请求体过大")
        val buf = ByteArray(req.contentLength.toInt())
        var offset = 0
        if (req.prefix.isNotEmpty()) {
            offset = minOf(req.prefix.size, buf.size)
            System.arraycopy(req.prefix, 0, buf, 0, offset)
        }
        while (offset < buf.size) {
            val n = req.input.read(buf, offset, buf.size - offset)
            if (n <= 0) break
            offset += n
        }
        return String(buf, 0, offset, Charsets.UTF_8)
    }

    private fun writeText(req: Request, status: Int, contentType: String, body: String) {
        val bytes = body.toByteArray(Charsets.UTF_8)
        val head =
            "HTTP/1.1 $status ${WebWire.statusText(status)}\r\n" +
                "Content-Type: $contentType\r\n" +
                "Content-Length: ${bytes.size}\r\n" +
                "Cache-Control: no-store\r\n" +
                "Connection: close\r\n\r\n"
        req.output.write(head.toByteArray())
        req.output.write(bytes)
        req.output.flush()
    }

    private fun writeJson(req: Request, status: Int, payload: Map<String, Any?>) {
        writeText(req, status, "application/json; charset=utf-8", jsonOfMap(payload))
    }

    private fun writeStatus(req: Request, status: Int) {
        val head = "HTTP/1.1 $status ${WebWire.statusText(status)}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
        req.output.write(head.toByteArray())
        req.output.flush()
    }

    private fun jsonOf(vararg pairs: Pair<String, Any?>): String {
        val o = JSONObject()
        pairs.forEach { (k, v) -> o.put(k, v ?: JSONObject.NULL) }
        return o.toString()
    }

    private fun jsonOfMap(map: Map<String, Any?>): String {
        val o = JSONObject()
        map.forEach { (k, v) -> o.put(k, v ?: JSONObject.NULL) }
        return o.toString()
    }
}
