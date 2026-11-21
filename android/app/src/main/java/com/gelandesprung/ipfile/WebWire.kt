package com.gelandesprung.ipfile

/**
 * HTTP 线上那点纯字节逻辑：找请求头结束位置、解析请求行/请求头/查询串、
 * 清洗文件名、生成 Content-Disposition。
 *
 * 刻意不依赖任何 Android API——这些解析最容易写错（IPv6 方括号、路径穿越、
 * 非 ASCII 文件名），放在这里就能在 JVM 上直接跑用例验证，不必等真机。
 */
internal object WebWire {

    class Head(
        val method: String,
        val path: String,
        val query: Map<String, String>,
        val headers: Map<String, String>
    )

    private val CR = '\r'.code.toByte()
    private val NL = '\n'.code.toByte()

    /** 返回 `\r\n\r\n` 之后的下标（即正文起点）；找不到返回 -1。 */
    fun findHeaderEnd(data: ByteArray, length: Int = data.size): Int {
        if (length < 4) return -1
        for (i in 3 until length) {
            if (data[i] == NL && data[i - 1] == CR && data[i - 2] == NL && data[i - 3] == CR) {
                return i + 1
            }
        }
        return -1
    }

    /** 解析请求头文本（含首行）。头部键统一小写，值去首尾空白。 */
    fun parseHead(text: String): Head? {
        val lines = text.split("\r\n").filter { it.isNotEmpty() }
        if (lines.isEmpty()) return null
        val parts = lines[0].split(" ")
        if (parts.size < 2) return null

        val target = parts[1]
        val q = target.indexOf('?')
        val path = if (q >= 0) target.substring(0, q) else target
        val query = LinkedHashMap<String, String>()
        if (q >= 0) {
            target.substring(q + 1).split("&").filter { it.isNotEmpty() }.forEach { pair ->
                val eq = pair.indexOf('=')
                if (eq < 0) query[decode(pair)] = ""
                else query[decode(pair.substring(0, eq))] = decode(pair.substring(eq + 1))
            }
        }

        val headers = LinkedHashMap<String, String>()
        for (i in 1 until lines.size) {
            val colon = lines[i].indexOf(':')
            if (colon <= 0) continue
            headers[lines[i].substring(0, colon).trim().lowercase()] =
                lines[i].substring(colon + 1).trim()
        }

        return Head(parts[0].uppercase(), path, query, headers)
    }

    fun decode(s: String): String = try {
        java.net.URLDecoder.decode(s, "UTF-8")
    } catch (e: Exception) {
        s
    }

    /**
     * 只保留文件名：去掉目录部分（含 Windows 反斜杠）与控制字符。
     * 访客可以把 name 传成 `../../databases/x.db`，这里是唯一的拦截点。
     */
    fun sanitizeFileName(raw: String): String {
        val base = raw.replace('\\', '/').substringAfterLast('/')
        val cleaned = base.filter { !it.isISOControl() }.trim()
        return if (cleaned.isEmpty()) "未命名文件" else cleaned.take(120)
    }

    /** 中文名的文件下载：ASCII 兜底 + RFC 5987 的 UTF-8 编码，两种浏览器都能取对名字。 */
    fun contentDisposition(fileName: String): String {
        val ascii = fileName.map { ch ->
            if (ch.code in 32..127 && ch != '"' && ch != '\\') ch else '_'
        }.joinToString("")
        val encoded = java.net.URLEncoder.encode(fileName, "UTF-8").replace("+", "%20")
        return "attachment; filename=\"$ascii\"; filename*=UTF-8''$encoded"
    }

    fun statusText(status: Int): String = when (status) {
        200 -> "OK"
        204 -> "No Content"
        400 -> "Bad Request"
        404 -> "Not Found"
        413 -> "Payload Too Large"
        else -> "OK"
    }
}
