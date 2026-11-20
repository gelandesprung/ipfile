package com.gelandesprung.ipfile

import android.util.Base64
import java.nio.charset.StandardCharsets

/**
 * 飞鸽 P2P 协议的 Kotlin 实现，与 shared/PROTOCOL.md 严格对应，
 * 与 PC 端 Protocol.cs 逐字节互通。
 * 设备发现有两条并行通道：UDP 广播（FG_P2P，局域网自动发现）与二维码配对（FG_QR/FG_HELLO）；
 * 文件传输格式两侧共用。
 */
object Protocol {
    const val DISCOVERY_MAGIC = "FG_P2P"
    const val QR_MAGIC = "FG_QR"
    const val HELLO_MAGIC = "FG_HELLO"
    const val HELLO_ACK_MAGIC = "FG_HELLO_ACK"
    const val FILE_MAGIC = "FG_FILE"
    const val ACK_MAGIC = "FG_ACK"
    const val DONE_MAGIC = "FG_DONE"
    const val VERSION = 1

    private fun b64(s: String): String =
        Base64.encodeToString(s.toByteArray(StandardCharsets.UTF_8), Base64.NO_WRAP)
    private fun unb64(s: String): String =
        String(Base64.decode(s, Base64.DEFAULT), StandardCharsets.UTF_8)

    // ---------- UDP 局域网发现 ----------

    /** 广播包（不含 BYE）：FG_P2P|1|name_b64|ip|port */
    fun buildDiscovery(name: String, ip: String, tcpPort: Int, bye: Boolean): String {
        val line = "$DISCOVERY_MAGIC|$VERSION|${b64(name)}|$ip|$tcpPort"
        return if (bye) "$line|BYE\n" else "$line\n"
    }

    data class DiscoveryPacket(val peer: Peer, val bye: Boolean)

    fun parseDiscovery(line: String): DiscoveryPacket? = try {
        val p = line.trimEnd('\r', '\n').split('|')
        if (p.size < 5 || p[0] != DISCOVERY_MAGIC || p[1] != VERSION.toString()) null
        else {
            val name = unb64(p[2]).ifBlank { "未命名设备" }
            val ip = p[3].trim().trim('[', ']')
            val port = p[4].toInt()
            val bye = p.size >= 6 && p[5] == "BYE"
            if (!isIpLiteral(ip) || port !in 1..65535) null
            else DiscoveryPacket(Peer(name, ip, port, lan = true), bye)
        }
    } catch (e: Exception) { null }

    // ---------- 二维码载荷 ----------

    /** 二维码内容（不含换行）：FG_QR|1|name_b64|ip|port|token。 */
    fun buildQrPayload(name: String, ip: String, tcpPort: Int, token: String): String =
        "$QR_MAGIC|$VERSION|${b64(name)}|$ip|$tcpPort|$token"

    data class QrPayload(val name: String, val ip: String, val tcpPort: Int, val token: String)

    fun parseQrPayload(text: String): QrPayload? = try {
        val p = text.trim().trimEnd('\r', '\n').split('|')
        if (p.size != 6 || p[0] != QR_MAGIC || p[1] != VERSION.toString()) null
        else {
            val name = unb64(p[2]).ifBlank { "未命名设备" }
            val ip = p[3].trim().trim('[', ']')
            val port = p[4].toInt()
            val token = p[5].lowercase()
            if (!isIpLiteral(ip) || port !in 1..65535 || !isToken(token)) null
            else QrPayload(name, ip, port, token)
        }
    } catch (e: Exception) { null }

    // ---------- 配对握手 ----------

    /** FG_HELLO|1|token|name_b64|tcp_port */
    fun buildHello(token: String, name: String, tcpPort: Int): String =
        "$HELLO_MAGIC|$VERSION|$token|${b64(name)}|$tcpPort\n"

    data class HelloRequest(val token: String, val name: String, val tcpPort: Int)

    fun parseHello(line: String): HelloRequest? = try {
        val p = line.trimEnd('\r', '\n').split('|')
        if (p.size < 4 || p[0] != HELLO_MAGIC || p[1] != VERSION.toString()) null
        else {
            val token = p[2].lowercase()
            if (!isToken(token)) null
            else {
                val name = unb64(p[3]).ifBlank { "未命名设备" }
                val port = if (p.size >= 5) p[4].toInt() else 2425
                if (port !in 1..65535) null else HelloRequest(token, name, port)
            }
        }
    } catch (e: Exception) { null }

    /** FG_HELLO_ACK|1|OK|name_b64|ip|tcp_port */
    fun buildHelloAckOk(name: String, ip: String, tcpPort: Int): String =
        "$HELLO_ACK_MAGIC|$VERSION|OK|${b64(name)}|$ip|$tcpPort\n"

    fun buildHelloAckReject(reason: String): String =
        "$HELLO_ACK_MAGIC|$VERSION|REJECT|${b64(reason)}\n"

    data class HelloAck(
        val ok: Boolean,
        val reason: String = "",
        val name: String = "",
        val ip: String = "",
        val tcpPort: Int = 0
    )

    fun parseHelloAck(line: String): HelloAck = try {
        val p = line.trimEnd('\r', '\n').split('|')
        when {
            p.size < 3 || p[0] != HELLO_ACK_MAGIC || p[1] != VERSION.toString() ->
                HelloAck(false, "应答格式错误")
            p[2] == "REJECT" ->
                HelloAck(false, if (p.size >= 4) unb64(p[3]) else "对方拒绝配对")
            p[2] != "OK" -> HelloAck(false, "未知应答")
            p.size < 6 -> HelloAck(false, "应答缺少设备信息")
            else -> {
                val ip = p[4].trim().trim('[', ']')
                val port = p[5].toIntOrNull() ?: 0
                if (!isIpLiteral(ip) || port !in 1..65535) HelloAck(false, "应答地址非法")
                else HelloAck(true, "", unb64(p[3]).ifBlank { "对方" }, ip, port)
            }
        }
    } catch (e: Exception) { HelloAck(false, "应答解析失败") }

    // ---------- TCP 文件传输（与 v1 一致） ----------

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

    // ---------- 校验 ----------

    /** token 为 32 位十六进制（16 字节随机数），拒绝分隔符与空白。 */
    private fun isToken(s: String): Boolean =
        s.length in 8..128 && s.all { it in '0'..'9' || it in 'a'..'f' || it in 'A'..'F' }

    /**
     * 只接受 IP 字面量，避免 InetAddress.getByName 触发 DNS 解析。
     * IPv6 允许 %scope（如 fe80::1%wlan0），但扫码配对已尽量避开链路本地地址。
     */
    fun isIpLiteral(s: String): Boolean {
        if (s.isBlank()) return false
        val core = s.substringBefore('%')
        val zone = s.substringAfter('%', "")
        if (zone.isNotEmpty() && !zone.all { it.isLetterOrDigit() || it == '_' || it == '.' }) return false
        if (core.contains(':')) {
            return core.count { it == ':' } in 2..8 &&
                core.all { it.isDigit() || it in 'a'..'f' || it in 'A'..'F' || it == ':' || it == '.' }
        }
        val parts = core.split('.')
        if (parts.size != 4) return false
        return parts.all { part ->
            val n = part.toIntOrNull()
            n != null && n in 0..255 && part.all { it.isDigit() }
        }
    }
}
