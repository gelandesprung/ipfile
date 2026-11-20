package com.gelandesprung.ipfile

import android.content.Context
import android.content.SharedPreferences
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

data class Peer(val name: String, val ip: String, val tcpPort: Int) {
    val key: String get() = "$ip:$tcpPort"
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

    /** App 界面是否在前台：前台时用对话框确认，后台时用通知确认。 */
    private val _uiVisible = MutableStateFlow(false)
    val uiVisible: StateFlow<Boolean> = _uiVisible.asStateFlow()
    fun setUiVisible(v: Boolean) { _uiVisible.value = v }

    private val peerTimes = mutableMapOf<String, Long>()

    /**
     * 设备列表的固定顺序：先按设备名（忽略大小写），同名再按地址。
     * 只要名字不变，位置就不会变；每 2 秒一次的广播不会把设备挪来挪去。
     */
    private fun List<Peer>.ordered(): List<Peer> =
        sortedWith(compareBy<Peer> { it.name.lowercase() }.thenBy { it.key })

    @Synchronized
    fun updatePeer(peer: Peer, now: Long = System.currentTimeMillis()): Boolean {
        val isNew = !peerTimes.containsKey(peer.key)
        peerTimes[peer.key] = now

        val list = _peers.value
        val index = list.indexOfFirst { it.key == peer.key }
        // 设备信息没变（只是又收到一次广播）就不碰列表，省掉一次无意义的重组
        if (!isNew && index >= 0 && list[index] == peer) return false

        _peers.value = if (index >= 0)
            list.toMutableList().also { it[index] = peer }.ordered()
        else
            (list + peer).ordered()
        return isNew
    }

    @Synchronized
    fun removePeer(key: String) {
        peerTimes.remove(key)
        _peers.value = _peers.value.filter { it.key != key }
    }

    @Synchronized
    fun prunePeers(ttlMs: Long = 6000) {
        val now = System.currentTimeMillis()
        val dead = peerTimes.filter { now - it.value > ttlMs }.keys
        if (dead.isNotEmpty()) {
            dead.forEach { peerTimes.remove(it) }
            _peers.value = _peers.value.filterNot { it.key in dead }
        }
    }

    fun log(msg: String) {
        _logs.value = (_logs.value + msg).takeLast(200)
    }

    fun addRecord(rec: TransferRecord) {
        _records.value = (listOf(rec) + _records.value).take(20)
    }

    fun setProgress(p: ProgressInfo?) { _progress.value = p }
}
