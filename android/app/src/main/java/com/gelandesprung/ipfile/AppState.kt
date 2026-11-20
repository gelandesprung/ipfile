package com.gelandesprung.ipfile

import android.content.Context
import android.content.SharedPreferences
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * 局域网内的一台设备。两种来源可并存：
 * [lan] = UDP 广播自动发现（会超时下线）；[paired] = 扫码配对（会话内常驻）。
 */
data class Peer(
    val name: String,
    val ip: String,
    val tcpPort: Int,
    val justPaired: Boolean = false,
    val lan: Boolean = false,
    val paired: Boolean = false
) {
    /** IPv6 必须加方括号，否则地址里的冒号无法与端口区分。 */
    val address: String get() = if (ip.contains(':')) "[$ip]:$tcpPort" else "$ip:$tcpPort"
    val key: String get() = address

    /** 列表右侧的状态文案。 */
    val status: String get() = when {
        justPaired -> "刚配对"
        paired && lan -> "已配对·在线"
        paired -> "已配对"
        else -> "在线"
    }
}

/** 二维码配对会话：token 一次性，10 分钟有效，收起/刷新即作废。 */
data class PairSession(
    val token: String,
    val expiresAt: Long,
    val used: Boolean = false
) {
    fun remainingMs(now: Long = System.currentTimeMillis()): Long = expiresAt - now
    fun isValid(now: Long = System.currentTimeMillis()): Boolean = !used && remainingMs(now) > 0
}

enum class TransferDirection { SEND, RECEIVE }
data class TransferRecord(
    val direction: TransferDirection,
    val fileName: String,
    val peerName: String,
    val size: Long,
    val success: Boolean,
    val time: String,
    /** 可打开的文件 Uri（收到的文件为保存位置，发出的文件为原文件）。 */
    val uri: String? = null
)

/** 等待用户确认的接收请求（60 秒内不处理即视为拒绝）。 */
data class PendingRequest(
    val id: String,
    val fileName: String,
    val size: Long,
    val peerIp: String,
    val expireAt: Long
)

/** 等待用户确认的配对请求（30 秒内不处理即视为拒绝）。 */
data class PendingPair(
    val id: String,
    val peerName: String,
    val peerIp: String,
    val peerPort: Int,
    val expireAt: Long
)

data class ProgressInfo(val direction: TransferDirection, val fileName: String, val done: Long, val total: Long)

/** 全局单例状态：UI 与后台服务通过它共享数据。 */
object AppState {
    private lateinit var prefs: SharedPreferences

    fun init(context: Context) {
        prefs = context.applicationContext.getSharedPreferences("ipfile", Context.MODE_PRIVATE)
        _deviceName.value = prefs.getString("deviceName", "")!!.ifEmpty { defaultName() }
    }

    private fun defaultName(): String = android.os.Build.MODEL.ifEmpty { "Android" }

    private val _deviceName = MutableStateFlow("Android")
    val deviceName: StateFlow<String> = _deviceName.asStateFlow()

    fun setDeviceName(name: String) {
        val n = name.trim().ifEmpty { defaultName() }
        _deviceName.value = n
        prefs.edit().putString("deviceName", n).apply()
    }

    private val _localIp = MutableStateFlow("")
    val localIp: StateFlow<String> = _localIp.asStateFlow()
    fun setLocalIp(ip: String) { _localIp.value = ip }

    private val _peers = MutableStateFlow<List<Peer>>(emptyList())
    val peers: StateFlow<List<Peer>> = _peers.asStateFlow()

    private val _logs = MutableStateFlow<List<String>>(emptyList())
    val logs: StateFlow<List<String>> = _logs.asStateFlow()

    private val _records = MutableStateFlow<List<TransferRecord>>(emptyList())
    val records: StateFlow<List<TransferRecord>> = _records.asStateFlow()

    private val _progress = MutableStateFlow<ProgressInfo?>(null)
    val progress: StateFlow<ProgressInfo?> = _progress.asStateFlow()

    private val _serviceRunning = MutableStateFlow(false)
    val serviceRunning: StateFlow<Boolean> = _serviceRunning.asStateFlow()
    fun setServiceRunning(v: Boolean) { _serviceRunning.value = v }

    /** 待用户确认的接收请求；非空时 UI 弹确认框、服务发通知。 */
    private val _pending = MutableStateFlow<PendingRequest?>(null)
    val pending: StateFlow<PendingRequest?> = _pending.asStateFlow()
    fun setPending(p: PendingRequest?) { _pending.value = p }

    /** 当前配对会话（二维码内容来源）；null 表示入口已收起。 */
    private val _pairSession = MutableStateFlow<PairSession?>(null)
    val pairSession: StateFlow<PairSession?> = _pairSession.asStateFlow()
    fun setPairSession(s: PairSession?) { _pairSession.value = s }

    /** 待用户确认的配对请求。 */
    private val _pendingPair = MutableStateFlow<PendingPair?>(null)
    val pendingPair: StateFlow<PendingPair?> = _pendingPair.asStateFlow()
    fun setPendingPair(p: PendingPair?) { _pendingPair.value = p }

    /** 配对成功时间戳：界面据此弹出提示并收起配对页。 */
    private val _pairSuccess = MutableStateFlow(0L)
    val pairSuccess: StateFlow<Long> = _pairSuccess.asStateFlow()
    fun notifyPairSuccess() { _pairSuccess.value = System.currentTimeMillis() }

    /** App 界面是否在前台：前台时用对话框确认，后台时用通知确认。 */
    private val _uiVisible = MutableStateFlow(false)
    val uiVisible: StateFlow<Boolean> = _uiVisible.asStateFlow()
    fun setUiVisible(v: Boolean) { _uiVisible.value = v }

    /**
     * 设备列表固定顺序：先按设备名（忽略大小写），同名再按地址。
     * 只要名字不变，位置就不会变；每 2 秒一次的广播不会把设备挪来挪去。
     */
    private fun List<Peer>.ordered(): List<Peer> =
        sortedWith(compareBy<Peer> { it.name.lowercase() }.thenBy { it.key })

    /** 局域网设备最后一次被听到的时间，用于超时下线；配对设备不进这张表。 */
    private val peerTimes = mutableMapOf<String, Long>()

    /**
     * 局域网广播到达。[byLocal] 为 true 表示这是本机自己发的包，直接丢弃。
     * 同一台设备可能既配对过又在广播，这时合并成一行（沿用配对时的地址），不能出现两行。
     */
    @Synchronized
    fun upsertLanPeer(peer: Peer, now: Long = System.currentTimeMillis()): Boolean {
        val list = _peers.value
        val twin = findPairedTwin(list, peer)
        val target = twin ?: list.firstOrNull { it.key == peer.key }
        peerTimes[peer.key] = now
        if (twin != null) peerTimes[twin.key] = now

        if (target == null) {
            _peers.value = (list + peer.copy(lan = true)).ordered()
            return true
        }
        if (twin != null) {
            // 配对行保留原地址（IPv6 优先），只更新在线标记
            val wasOnline = target.lan
            val updated = target.copy(lan = true)
            if (target == updated) return false
            _peers.value = list.toMutableList().also { it[list.indexOf(target)] = updated }.ordered()
            return !wasOnline
        }
        val wasOnline = target.lan
        val updated = target.copy(
            name = peer.name, ip = peer.ip, tcpPort = peer.tcpPort, lan = true
        )
        if (target == updated) return false
        _peers.value = list.toMutableList().also { it[list.indexOf(target)] = updated }.ordered()
        return !wasOnline
    }

    /** 收到 BYE 或超时：配对过的行保留并降级为「已配对」，纯局域网来源才删行。 */
    @Synchronized
    fun lanPeerGone(key: String, name: String = "", tcpPort: Int = 0) {
        peerTimes.remove(key)
        val list = _peers.value
        val target = list.firstOrNull { it.key == key }
            ?: list.firstOrNull {
                it.paired && it.tcpPort == tcpPort && it.name.trim().equals(name.trim(), ignoreCase = true)
            }
        if (target == null) return
        if (target.paired) {
            if (!target.lan) return
            _peers.value = list.toMutableList().also { it[list.indexOf(target)] = target.copy(lan = false) }.ordered()
            log("设备离线：${target.name}（仍保留在已配对列表）")
        } else {
            _peers.value = list.filterNot { it.key == target.key }
            log("设备离线：${target.name}")
        }
    }

    /**
     * 只有一台同名的已配对设备时才合并——两台同名手机（同型号默认名）不会被错误并成一行。
     */
    private fun findPairedTwin(list: List<Peer>, other: Peer): Peer? {
        val name = other.name.trim()
        val matches = list.filter {
            it.paired && it.tcpPort == other.tcpPort && it.name.trim().equals(name, ignoreCase = true)
        }
        return matches.singleOrNull()
    }

    /** 扫码配对成功：入列，并把同名的局域网行吸收进来。 */
    @Synchronized
    fun mergePairedPeer(peer: Peer) {
        val list = _peers.value
        val lanTwin = list.firstOrNull {
            !it.paired && it.tcpPort == peer.tcpPort &&
                it.name.trim().equals(peer.name.trim(), ignoreCase = true)
        }
        val merged = peer.copy(paired = true, justPaired = true, lan = lanTwin?.lan ?: peer.lan)
        val withoutTwin = if (lanTwin != null) list.filterNot { it.key == lanTwin.key } else list
        val index = withoutTwin.indexOfFirst { it.key == merged.key }
        _peers.value = if (index >= 0)
            withoutTwin.toMutableList().also { it[index] = merged }.ordered()
        else
            (withoutTwin + merged).ordered()
    }

    /** 「刚配对」只是短暂标记，几秒后清掉，避免状态列一直停在刚配对。 */
    @Synchronized
    fun clearJustPaired() {
        if (_peers.value.none { it.justPaired }) return
        _peers.value = _peers.value.map { it.copy(justPaired = false) }
    }

    /** 保留若干秒未收到广播的局域网设备下线；配对设备不受影响。 */
    @Synchronized
    fun pruneLanPeers(ttlMs: Long = 12_000) {
        val now = System.currentTimeMillis()
        val dead = peerTimes.filter { now - it.value > ttlMs }.keys.toList()
        if (dead.isEmpty()) return
        dead.forEach { peerTimes.remove(it) }
        val list = _peers.value
        _peers.value = list.mapNotNull { p ->
            when {
                p.paired -> if (p.lan) p.copy(lan = false) else p
                p.key in dead -> null
                else -> p
            }
        }
    }

    @Synchronized
    fun removePeer(key: String) {
        peerTimes.remove(key)
        _peers.value = _peers.value.filter { it.key != key }
    }

    fun log(msg: String) {
        _logs.value = (_logs.value + msg).takeLast(200)
    }

    fun addRecord(rec: TransferRecord) {
        _records.value = (listOf(rec) + _records.value).take(20)
    }

    fun setProgress(p: ProgressInfo?) { _progress.value = p }
}
