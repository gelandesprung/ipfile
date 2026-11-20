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
    val time: String
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

    private val peerTimes = mutableMapOf<String, Long>()

    @Synchronized
    fun updatePeer(peer: Peer, now: Long = System.currentTimeMillis()): Boolean {
        val isNew = !peerTimes.containsKey(peer.key)
        peerTimes[peer.key] = now
        _peers.value = _peers.value.filter { it.key != peer.key } + peer
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
