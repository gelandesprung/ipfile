package com.gelandesprung.ipfile

import android.util.Base64
import java.nio.charset.StandardCharsets

/**
 * 飞鸽 P2P 协议 v1.0 的 Kotlin 实现，与 shared/PROTOCOL.md 严格对应，
 * 与 PC 端 Protocol.cs 逐字节互通。
 */
object Protocol {
    const val MAGIC = "FG_P2P"
    const val FILE_MAGIC = "FG_FILE"
    const val ACK_MAGIC = "FG_ACK"
    const val DONE_MAGIC = "FG_DONE"
    const val VERSION = 1

    private fun b64(s: String): String =
        Base64.encodeToString(s.toByteArray(StandardCharsets.UTF_8), Base64.NO_WRAP)
    private fun unb64(s: String): String =
        String(Base64.decode(s, Base64.DEFAULT), StandardCharsets.UTF_8)

    // ---------- UDP 发现 ----------
    fun buildDiscovery(name: String, ip: String, tcpPort: Int, bye: Boolean = false): String {
        var line = "$MAGIC|$VERSION|${b64(name)}|$ip|$tcpPort"
        if (bye) line += "|BYE"
        return line + "\n"
    }

    data class Discovery(val peer: Peer, val bye: Boolean)

    fun parseDiscovery(line: String): Discovery? = try {
        val p = line.trimEnd('\r', '\n').split('|')
        if (p.size < 5 || p[0] != MAGIC || p[1] != VERSION.toString()) null
        else {
            val name = unb64(p[2])
            val ip = p[3]
            val port = p[4].toInt()
            val bye = p.size >= 6 && p[5] == "BYE"
            Discovery(Peer(name, ip, port), bye)
        }
    } catch (e: Exception) { null }

    // ---------- TCP 文件传输 ----------
    fun buildFileRequest(fileName: String, size: Long, fileCount: Int = 1): String =
        "$FILE_MAGIC|$VERSION|${b64(fileName)}|$size|$fileCount\n"

    data class FileHeader(val fileName: String, val size: Long, val fileCount: Int)

    fun parseFileRequest(line: String): FileHeader? = try {
        val p = line.trimEnd('\r', '\n').split('|')
        if (p.size < 5 || p[0] != FILE_MAGIC || p[1] != VERSION.toString()) null
        else {
            val name = unb64(p[2])
            val size = p[3].toLong()
            if (size < 0 || name.isEmpty()) null
            else FileHeader(name, size, p[4].toInt())
        }
    } catch (e: Exception) { null }

    fun buildAckOk() = "$ACK_MAGIC|$VERSION|OK\n"
    fun buildAckReject(reason: String) = "$ACK_MAGIC|$VERSION|REJECT|${b64(reason)}\n"

    /** 返回 true=OK；false 时 reason 为原因（用 Pair 返回）。 */
    fun parseAck(line: String): Pair<Boolean, String> = try {
        val p = line.trimEnd('\r', '\n').split('|')
        when {
            p.size < 3 || p[0] != ACK_MAGIC || p[1] != VERSION.toString() -> false to "应答格式错误"
            p[2] == "OK" -> true to ""
            p[2] == "REJECT" && p.size >= 4 -> false to unb64(p[3])
            else -> false to "未知应答"
        }
    } catch (e: Exception) { false to "应答解析失败" }

    fun buildDone(received: Long) = "$DONE_MAGIC|$VERSION|$received\n"

    fun parseDone(line: String): Long? = try {
        val p = line.trimEnd('\r', '\n').split('|')
        if (p.size < 3 || p[0] != DONE_MAGIC || p[1] != VERSION.toString()) null else p[2].toLong()
    } catch (e: Exception) { null }
}
