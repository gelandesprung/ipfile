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
    data class ReceiveTarget(val stream: OutputStream, val displayPath: String, val uri: Uri)

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
     * 取本机最可能被对端访问的地址：全局 IPv6 → 唯一本地 IPv6（fdxx）→ IPv4。
     * 跳过回环、链路本地 fe80::（对方无法凭 scope 选网卡）与虚拟网卡。
     */
    fun localIp(): String {
        val v6 = mutableListOf<Pair<Int, String>>()
        val v4 = mutableListOf<String>()
        try {
            val ifaces = NetworkInterface.getNetworkInterfaces() ?: return "127.0.0.1"
            for (ni in ifaces) {
                if (!ni.isUp || ni.isLoopback || ni.isVirtual) continue
                val n = ni.name.lowercase()
                val preferredIface = n.contains("wlan") || n.contains("wifi") ||
                    n.contains("eth") || n.contains("ap") || n.contains("rmnet")

                val addrs = ni.inetAddresses
                while (addrs.hasMoreElements()) {
                    val a = addrs.nextElement()
                    if (a.isLoopbackAddress || a.isAnyLocalAddress || a.isMulticastAddress) continue

                    if (a is Inet6Address) {
                        if (a.isLinkLocalAddress || a.isSiteLocalAddress) continue
                        val b = a.address
                        val global = (b[0].toInt() and 0xE0) == 0x20   // 2000::/3
                        val ula = (b[0].toInt() and 0xFE) == 0xFC      // fc00::/7
                        if (!global && !ula) continue
                        val ip = a.hostAddress ?: continue
                        var score = if (global) 100 else 50
                        if (preferredIface) score += 10
                        v6.add(score to ip)
                    } else if (a is Inet4Address) {
                        val ip = a.hostAddress ?: continue
                        if (preferredIface) v4.add(0, ip) else v4.add(ip)
                    }
                }
            }
        } catch (e: Exception) { }

        if (v6.isNotEmpty()) return v6.sortedByDescending { it.first }.first().second
        return v4.firstOrNull() ?: "127.0.0.1"
    }

    /**
     * 本机 IPv4 地址（UDP 广播用）。广播只能走 IPv4，所以这里必须跳过 IPv6，
     * 否则对端拿到无法回连的地址。
     */
    fun localIpV4(): String {
        val v4 = mutableListOf<String>()
        try {
            val ifaces = NetworkInterface.getNetworkInterfaces() ?: return "127.0.0.1"
            for (ni in ifaces) {
                if (!ni.isUp || ni.isLoopback || ni.isVirtual) continue
                val n = ni.name.lowercase()
                val preferred = n.contains("wlan") || n.contains("wifi") ||
                    n.contains("eth") || n.contains("ap") || n.contains("rmnet")
                val addrs = ni.inetAddresses
                while (addrs.hasMoreElements()) {
                    val a = addrs.nextElement()
                    if (a !is Inet4Address) continue
                    if (a.isLoopbackAddress || a.isAnyLocalAddress) continue
                    val ip = a.hostAddress ?: continue
                    if (preferred) v4.add(0, ip) else v4.add(ip)
                }
            }
        } catch (e: Exception) { }
        return v4.firstOrNull() ?: "127.0.0.1"
    }

    /** 在共享 Download/飞鸽收件/ 下创建输出流；同名自动加 (1)(2)。 */
    fun createReceiveStream(context: Context, fileName: String): ReceiveTarget {
        val base = fileName.substringAfterLast('/').substringAfterLast('\\').ifEmpty { "未命名文件" }
        val mime = mimeOf(base)
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            // MediaStore：无需存储权限。先查出重名后生成唯一名，避免覆盖。
            val finalName = uniqueNameMediaStore(context, base)
            val values = ContentValues().apply {
                put(MediaStore.Downloads.DISPLAY_NAME, finalName)
                put(MediaStore.Downloads.MIME_TYPE, mime)
                put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/飞鸽收件")
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
            ReceiveTarget(finalOut, "Download/飞鸽收件/$finalName", uri)
        } else {
            // Android 9 及以下：公共 Download 目录，经 FileProvider 暴露以便「点击打开」
            val dir = File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS), "飞鸽收件")
            dir.mkdirs()
            val file = File(dir, uniqueNameFile(dir, base))
            val uri = FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", file)
            ReceiveTarget(file.outputStream(), "Download/飞鸽收件/${file.name}", uri)
        }
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
            val f = File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS), "飞鸽收件/$displayName")
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
