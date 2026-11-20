package com.gelandesprung.ipfile

import android.content.ContentValues
import android.content.Context
import android.media.MediaScannerConnection
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import java.io.File
import java.io.OutputStream
import java.net.Inet4Address
import java.net.NetworkInterface

object NetUtils {
    /** 取本机局域网 IPv4（跳过回环/虚拟网卡）。 */
    fun localIp(): String {
        try {
            val ifaces = NetworkInterface.getNetworkInterfaces() ?: return "127.0.0.1"
            for (ni in ifaces) {
                if (!ni.isUp || ni.isLoopback || ni.isVirtual) continue
                val n = ni.name.lowercase()
                if (n.contains("wlan") || n.contains("eth") || n.contains("ap")) {
                    val addrs = ni.inetAddresses
                    while (addrs.hasMoreElements()) {
                        val a = addrs.nextElement()
                        if (a is Inet4Address && !a.isLoopbackAddress) return a.hostAddress ?: continue
                    }
                }
            }
            // 兜底：任意非回环 IPv4
            for (ni in NetworkInterface.getNetworkInterfaces()) {
                if (!ni.isUp || ni.isLoopback) continue
                val addrs = ni.inetAddresses
                while (addrs.hasMoreElements()) {
                    val a = addrs.nextElement()
                    if (a is Inet4Address && !a.isLoopbackAddress) return a.hostAddress ?: continue
                }
            }
        } catch (e: Exception) { }
        return "127.0.0.1"
    }

    /** 在共享 Download/飞鸽收件/ 下创建输出流；同名自动加 (1)(2)。 */
    fun createReceiveStream(context: Context, fileName: String): Pair<OutputStream, String> {
        val base = fileName.substringAfterLast('/').substringAfterLast('\\').ifEmpty { "未命名文件" }
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            // MediaStore：无需存储权限。先查出重名后生成唯一名，避免覆盖。
            val finalName = uniqueNameMediaStore(context, base)
            val values = ContentValues().apply {
                put(MediaStore.Downloads.DISPLAY_NAME, finalName)
                put(MediaStore.Downloads.MIME_TYPE, "application/octet-stream")
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
            finalOut to "Download/飞鸽收件/$finalName"
        } else {
            // Android 9 及以下：公共 Download 目录
            val dir = File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS), "飞鸽收件")
            dir.mkdirs()
            val file = File(dir, uniqueNameFile(dir, base))
            file.outputStream() to ("Download/飞鸽收件/${file.name}")
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
