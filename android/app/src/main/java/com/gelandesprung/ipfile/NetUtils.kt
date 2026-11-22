package com.gelandesprung.ipfile

import android.content.ContentValues
import android.content.Context
import android.media.MediaScannerConnection
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import androidx.core.content.FileProvider
import java.io.File
import java.io.OutputStream
import java.net.Inet4Address
import java.net.Inet6Address
import java.net.NetworkInterface

object NetUtils {
    /** 接收目标：输出流 + 展示路径 + 可打开该文件的 Uri。 */
    data class ReceiveTarget(
        val stream: OutputStream,
        val displayPath: String,
        val uri: Uri,
        /** 半途失败时调用：丢弃已写入的半成品，不留下 0 字节或残缺文件。 */
        val abort: () -> Unit = {}
    )

    /** 按扩展名推断 MIME，供保存与「点击打开」使用。 */
    fun mimeOf(name: String): String = when (name.substringAfterLast('.', "").lowercase()) {
        "jpg", "jpeg" -> "image/jpeg"
        "png" -> "image/png"
        "gif" -> "image/gif"
        "webp" -> "image/webp"
        "bmp" -> "image/bmp"
        "pdf" -> "application/pdf"
        "txt", "log", "md" -> "text/plain"
        "mp4" -> "video/mp4"
        "mkv" -> "video/x-matroska"
        "mp3" -> "audio/mpeg"
        "wav" -> "audio/wav"
        "apk" -> "application/vnd.android.package-archive"
        "zip" -> "application/zip"
        "doc" -> "application/msword"
        "docx" -> "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
        "xls" -> "application/vnd.ms-excel"
        "xlsx" -> "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        "ppt" -> "application/vnd.ms-powerpoint"
        "pptx" -> "application/vnd.openxmlformats-officedocument.presentationml.presentation"
        else -> "application/octet-stream"
    }

    /**
     * 网卡偏好：手机同时连 WiFi 和移动数据时，朋友只能连前者。
     * 蜂窝网卡（rmnet/ccmni/clat）给负分，避免把 4G 的地址出示给别人。
     */
    private fun ifaceScore(name: String): Int {
        val n = name.lowercase()
        return when {
            n.contains("wlan") || n.contains("wifi") || n.startsWith("ap") || n.contains("softap") -> 60
            n.contains("eth") -> 40
            n.contains("rmnet") || n.contains("ccmni") || n.contains("clat") || n.contains("pdp") -> -80
            else -> 0
        }
    }

    /**
     * 收集本机全局/ULA IPv6，按"最适合给访客"排序。
     *
     * 一块网卡常同时挂多个 IPv6：RA/SLAAC 的 /64（同链路设备凭前缀直接邻居发现，最兼容）、
     * DHCPv6 单独下发的 /128（租期到了会变）、以及隐私临时地址。只按"是否全局"打分会让
     * /64 与 /128 同分、谁先枚举到就用谁——所以前缀长度必须是评分维度。
     */
    private fun v6Candidates(): List<String> {
        val scored = mutableListOf<Pair<Int, String>>()
        try {
            val ifaces = NetworkInterface.getNetworkInterfaces() ?: return emptyList()
            for (ni in ifaces) {
                // 单个网卡枚举失败不该让整台机器没有地址（如已关掉的蜂窝接口）
                try {
                    if (!ni.isUp || ni.isLoopback || ni.isVirtual) continue
                    val iface = ifaceScore(ni.name)
                    for (ia in ni.interfaceAddresses) {
                        val a = ia.address
                        if (a !is Inet6Address) continue
                        if (a.isLoopbackAddress || a.isAnyLocalAddress || a.isMulticastAddress) continue
                        if (a.isLinkLocalAddress || a.isSiteLocalAddress) continue
                        val b = a.address
                        val global = (b[0].toInt() and 0xE0) == 0x20   // 2000::/3
                        val ula = (b[0].toInt() and 0xFE) == 0xFC      // fc00::/7
                        if (!global && !ula) continue
                        val ip = a.hostAddress ?: continue
                        var score = if (global) 100 else 50
                        score += iface
                        // 前缀长度是区分 SLAAC /64 与 DHCPv6 /128 的唯一线索
                        score += when (ia.networkPrefixLength.toInt()) {
                            64 -> 30
                            128 -> -20
                            else -> 0
                        }
                        // EUI-64 接口标识里含 ff:fe，比随机标识更稳定
                        if (isEui64(b)) score += 5
                        scored.add(score to ip)
                    }
                } catch (e: Exception) { }
            }
        } catch (e: Exception) { }
        return scored.sortedByDescending { it.first }.map { it.second }
    }

    private fun isEui64(b: ByteArray): Boolean =
        b.size == 16 && b[11] == 0xFF.toByte() && b[12] == 0xFE.toByte()

    /**
     * 取本机最可能被对端访问的地址：全局 IPv6 → 唯一本地 IPv6（fdxx）→ IPv4。
     * 跳过回环、链路本地 fe80::（对方无法凭 scope 选网卡）与虚拟网卡。
     */
    fun localIp(): String = v6Candidates().firstOrNull() ?: localIpV4()

    /** 仅 IPv6：没有全局/ULA 地址时返回空串（网页模式据此判断能否出示 v6 地址）。 */
    fun localIpV6(): String = v6Candidates().firstOrNull() ?: ""

    /**
     * 本机 IPv4 地址（UDP 广播用）。广播只能走 IPv4，所以这里必须跳过 IPv6，
     * 否则对端拿到无法回连的地址。WiFi/以太网优先于蜂窝。
     */
    fun localIpV4(): String {
        var best: String? = null
        var bestScore = Int.MIN_VALUE
        try {
            val ifaces = NetworkInterface.getNetworkInterfaces() ?: return "127.0.0.1"
            for (ni in ifaces) {
                try {
                    if (!ni.isUp || ni.isLoopback || ni.isVirtual) continue
                    val iface = ifaceScore(ni.name)
                    val addrs = ni.inetAddresses
                    while (addrs.hasMoreElements()) {
                        val a = addrs.nextElement()
                        if (a !is Inet4Address) continue
                        if (a.isLoopbackAddress || a.isAnyLocalAddress) continue
                        val ip = a.hostAddress ?: continue
                        if (iface > bestScore) { bestScore = iface; best = ip }
                    }
                } catch (e: Exception) { }
            }
        } catch (e: Exception) { }
        return best ?: "127.0.0.1"
    }

    /** 在共享 Download/数据线收件/ 下创建输出流；同名自动加 (1)(2)。 */
    fun createReceiveStream(context: Context, fileName: String): ReceiveTarget {
        val base = fileName.substringAfterLast('/').substringAfterLast('\\').ifEmpty { "未命名文件" }
        val mime = mimeOf(base)
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            // MediaStore：无需存储权限。先查出重名后生成唯一名，避免覆盖。
            val finalName = uniqueNameMediaStore(context, base)
            val values = ContentValues().apply {
                put(MediaStore.Downloads.DISPLAY_NAME, finalName)
                put(MediaStore.Downloads.MIME_TYPE, mime)
                put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/数据线收件")
                put(MediaStore.Downloads.IS_PENDING, 1)
            }
            val uri: Uri = context.contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)
                ?: throw IllegalStateException("无法创建下载文件")
            val out = context.contentResolver.openOutputStream(uri)
                ?: throw IllegalStateException("无法打开文件流")
            val finalOut = object : OutputStream() {
                override fun write(b: Int) { out.write(b) }
                override fun write(b: ByteArray, off: Int, len: Int) { out.write(b, off, len) }
                override fun close() {
                    out.close()
                    val v = ContentValues().apply { put(MediaStore.Downloads.IS_PENDING, 0) }
                    context.contentResolver.update(uri, v, null, null)
                }
            }
            val discard: () -> Unit = {
                try { out.close() } catch (e: Exception) { }
                try { context.contentResolver.delete(uri, null, null) } catch (e: Exception) { }
            }
            ReceiveTarget(finalOut, "Download/数据线收件/$finalName", uri, discard)
        } else {
            // Android 9 及以下：公共 Download 目录，经 FileProvider 暴露以便「点击打开」
            val dir = File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS), "数据线收件")
            dir.mkdirs()
            val file = File(dir, uniqueNameFile(dir, base))
            val uri = FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", file)
            val discard: () -> Unit = {
                try { file.delete() } catch (e: Exception) { }
            }
            ReceiveTarget(file.outputStream(), "Download/数据线收件/${file.name}", uri, discard)
        }
    }

    /** 文件名：优先 OpenableColumns.DISPLAY_NAME，取不到再退回 Uri 末段。 */
    fun displayName(context: Context, uri: Uri): String {
        try {
            context.contentResolver.query(uri, arrayOf(android.provider.OpenableColumns.DISPLAY_NAME), null, null, null)
                ?.use { c ->
                    if (c.moveToFirst()) {
                        val i = c.getColumnIndex(android.provider.OpenableColumns.DISPLAY_NAME)
                        if (i >= 0) c.getString(i)?.takeIf { it.isNotBlank() }?.let { return it }
                    }
                }
        } catch (e: Exception) { }
        return uri.lastPathSegment?.substringAfterLast('/')?.takeIf { it.isNotBlank() } ?: "文件"
    }

    /** 文件长度；查不到返回 -1（调用方需要落到缓存文件量真实长度）。 */
    fun sizeOf(context: Context, uri: Uri): Long {
        try {
            context.contentResolver.openFileDescriptor(uri, "r")?.use { fd ->
                if (fd.statSize >= 0) return fd.statSize
            }
        } catch (e: Exception) { }
        try {
            context.contentResolver.openAssetFileDescriptor(uri, "r")?.use { afd ->
                if (afd.length >= 0) return afd.length
            }
        } catch (e: Exception) { }
        return -1
    }

    private fun splitName(name: String): Pair<String, String> {
        val dot = name.lastIndexOf('.')
        return if (dot > 0) name.substring(0, dot) to name.substring(dot) else name to ""
    }

    private fun uniqueNameFile(dir: File, name: String): String {
        if (!File(dir, name).exists()) return name
        val (stem, ext) = splitName(name)
        var i = 1
        while (File(dir, "$stem ($i)$ext").exists()) i++
        return "$stem ($i)$ext"
    }

    private fun uniqueNameMediaStore(context: Context, name: String): String {
        val cr = context.contentResolver
        fun exists(n: String): Boolean {
            val sel = "${MediaStore.MediaColumns.DISPLAY_NAME}=?"
            val cur = cr.query(MediaStore.Downloads.EXTERNAL_CONTENT_URI,
                arrayOf(MediaStore.MediaColumns._ID), sel, arrayOf(n), null) ?: return false
            return cur.use { it.count > 0 }
        }
        if (!exists(name)) return name
        val (stem, ext) = splitName(name)
        var i = 1
        while (exists("$stem ($i)$ext")) i++
        return "$stem ($i)$ext"
    }

    fun notifyScan(context: Context, displayName: String) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) {
            val f = File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS), "数据线收件/$displayName")
            MediaScannerConnection.scanFile(context, arrayOf(f.absolutePath), null, null)
        }
    }

    fun formatSize(bytes: Long): String = when {
        bytes < 1024 -> "$bytes B"
        bytes < 1024 * 1024 -> String.format("%.1f KB", bytes / 1024.0)
        bytes < 1024 * 1024 * 1024 -> String.format("%.1f MB", bytes / 1024.0 / 1024.0)
        else -> String.format("%.2f GB", bytes / 1024.0 / 1024.0 / 1024.0)
    }
}
