package com.gelandesprung.ipfile

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.graphics.Bitmap
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Language
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Send
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * 网页模式页（Host = 这台手机）。
 *
 * 与 PC 的「网页模式」窗口同构：访问地址（可切 IPv4/IPv6）+ 二维码 + PIN + 在线访客 + 群发。
 * 差异只在平台能力：
 * - 安卓不选保存位置，访客上传直接进公共 Download/飞鸽收件/（与 P2P 接收一致）
 * - 服务由前台服务持有，退出这个页面不会停服（要停请点「停止服务」）
 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalFoundationApi::class)
@Composable
fun WebModeScreen(onBack: () -> Unit) {
    val ctx = LocalContext.current
    val web = WebHostHolder.get(ctx)

    val running by AppState.webRunning.collectAsState()
    val port by AppState.webPort.collectAsState()
    val pin by AppState.webPin.collectAsState()
    val guests by AppState.webGuests.collectAsState()
    val pinRotatedAt by AppState.pinRotatedAt.collectAsState()

    var family by rememberSaveable { mutableStateOf("v4") }
    var selecting by remember { mutableStateOf(false) }
    val selected = remember { mutableStateMapOf<String, Boolean>() }
    var sheetGuest by remember { mutableStateOf<WebGuest?>(null) }
    var sendTargets by remember { mutableStateOf<List<String>>(emptyList()) }
    var pinFlash by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()

    // 地址每次进入本页、或服务重启时重取（换了网络就会变）
    var addrTick by remember { mutableIntStateOf(0) }
    val v4 = remember(addrTick) { NetUtils.localIpV4() }
    val v6 = remember(addrTick) { NetUtils.localIpV6() }
    val hasV4 = v4.isNotEmpty() && v4 != "127.0.0.1"
    val hasV6 = v6.isNotEmpty()

    // 选中的地址族突然没地址了（换网、关了 v6），退回 IPv4 而不是显示死链
    LaunchedEffect(hasV6) { if (!hasV6 && family == "v6") family = "v4" }

    val live = running && port > 0
    val url = if (family == "v6" && hasV6)
        "http://[$v6]:$port/"
    else
        "http://$v4:$port/"

    // 进页面才启动服务（与二维码"展开才生成"同哲学）；副作用放这里，不在组合期做
    LaunchedEffect(Unit) {
        addrTick++
        if (!AppState.webRunning.value) {
            val (ok, msg) = withContext(Dispatchers.IO) { web.start() }
            Toast.makeText(ctx, if (ok) msg else "启动失败：$msg", Toast.LENGTH_LONG).show()
        }
    }

    LaunchedEffect(pinRotatedAt) {
        if (pinRotatedAt > 0) {
            pinFlash = true
            delay(2400)
            pinFlash = false
        }
    }

    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        val targets = sendTargets
        sendTargets = emptyList()
        selecting = false
        selected.clear()
        if (uris.isNullOrEmpty()) return@rememberLauncherForActivityResult
        if (targets.isEmpty()) return@rememberLauncherForActivityResult
        web.sendFiles(targets, uris)
        val who = if (targets.size == 1)
            guests.firstOrNull { it.id == targets.first() }?.nickname ?: "访客"
        else
            "${targets.size} 位访客"
        Toast.makeText(ctx, "已发送 ${uris.size} 个文件给 $who，等待对方确认", Toast.LENGTH_SHORT).show()
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(if (selecting) "已选 ${selected.count { it.value }} 人" else "网页模式", fontWeight = FontWeight.Bold) },
                navigationIcon = {
                    IconButton(onClick = {
                        if (selecting) { selecting = false; selected.clear() } else onBack()
                    }) { Icon(Icons.Default.ArrowBack, "返回") }
                },
                actions = {
                    if (selecting) {
                        TextButton(
                            enabled = selected.any { it.value },
                            onClick = {
                                sendTargets = selected.filter { it.value }.keys.toList()
                                picker.launch(arrayOf("*/*"))
                            }
                        ) { Text("发送文件") }
                    } else if (running) {
                        TextButton(onClick = {
                            web.stop()
                            Toast.makeText(ctx, "网页服务已停止", Toast.LENGTH_SHORT).show()
                        }) { Text("停止服务", color = C.Red) }
                    }
                },
                colors = TopAppBarDefaults.topAppBarColors(containerColor = Color.White)
            )
        }
    ) { padding ->
        Column(
            Modifier.fillMaxSize().padding(padding).verticalScroll(rememberScrollState()).padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            // ---------- 访问信息 ----------
            Card(colors = CardDefaults.cardColors(containerColor = C.Card), shape = RoundedCornerShape(12.dp)) {
                Column(Modifier.padding(16.dp)) {
                    if (!live) {
                        // 停服后的空态：别显示 :0 这种死链，直接给启动入口
                        Text("网页服务已停止", fontSize = 15.sp, fontWeight = FontWeight.Bold)
                        Spacer(Modifier.height(6.dp))
                        Text(
                            "启动后本机在局域网内提供一个网页地址，朋友用浏览器就能和你互传文件，不用装 App。",
                            fontSize = 11.sp, color = C.Gray, lineHeight = 17.sp
                        )
                        Spacer(Modifier.height(14.dp))
                        Button(
                            onClick = {
                                scope.launch {
                                    val (ok, msg) = withContext(Dispatchers.IO) { web.start() }
                                    Toast.makeText(
                                        ctx, if (ok) msg else "启动失败：$msg", Toast.LENGTH_LONG
                                    ).show()
                                }
                            },
                            modifier = Modifier.fillMaxWidth().height(46.dp)
                        ) { Text("启动网页服务") }
                        return@Column
                    }

                    Text("访问地址", fontSize = 11.sp, color = C.Gray)
                    Spacer(Modifier.height(10.dp))

                    if (hasV4 && hasV6) {
                        AddressTabs(family, onPick = { family = it })
                        Spacer(Modifier.height(12.dp))
                    }

                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(
                            url,
                            fontSize = 12.sp,
                            fontFamily = FontFamily.Monospace,
                            color = C.Blue,
                            modifier = Modifier.weight(1f)
                        )
                        IconButton(
                            onClick = {
                                val cb = ctx.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                                cb.setPrimaryClip(ClipData.newPlainText("url", url))
                                Toast.makeText(ctx, "已复制访问地址", Toast.LENGTH_SHORT).show()
                            },
                            modifier = Modifier.size(32.dp)
                        ) { Icon(Icons.Default.ContentCopy, "复制地址", tint = C.Blue, modifier = Modifier.size(18.dp)) }
                    }

                    if (!hasV4) {
                        Text("本机没有可用 IPv4，请让朋友扫码或用手输 IPv6 地址", fontSize = 11.sp, color = C.Amber)
                        Spacer(Modifier.height(6.dp))
                    }

                    Spacer(Modifier.height(6.dp))
                    QrBlock(url, family)

                    Spacer(Modifier.height(14.dp))
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text("访问 PIN", fontSize = 11.sp, color = C.Gray)
                        Spacer(Modifier.width(12.dp))
                        Text(
                            pin.ifEmpty { "—" },
                            fontSize = 20.sp,
                            fontWeight = FontWeight.Bold,
                            color = if (pinFlash) C.Blue else C.Ink,
                            fontFamily = FontFamily.Monospace
                        )
                        if (pinFlash) {
                            Spacer(Modifier.width(6.dp))
                            Text("已更换", fontSize = 10.sp, color = C.Blue)
                        }
                        Spacer(Modifier.weight(1f))
                        TextButton(onClick = { web.refreshPin() }) {
                            Icon(Icons.Default.Refresh, null, modifier = Modifier.size(15.dp))
                            Spacer(Modifier.width(4.dp))
                            Text("刷新", fontSize = 12.sp)
                        }
                    }
                    Text(
                        "朋友连同一 WiFi / 热点，用浏览器扫码或手输地址 + PIN 加入。" +
                            "切换地址族只影响新扫码的人：PIN 不变、在线访客不受影响。",
                        fontSize = 11.sp, color = C.Gray, lineHeight = 17.sp
                    )
                }
            }

            // ---------- 在线访客 ----------
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("在线访客", fontSize = 14.sp, fontWeight = FontWeight.Bold)
                Spacer(Modifier.width(8.dp))
                Text("${guests.size} 人 · 长按操作", fontSize = 11.sp, color = C.Gray)
                Spacer(Modifier.weight(1f))
                if (!selecting && guests.isNotEmpty()) {
                    TextButton(onClick = { selecting = true; selected.clear() }) {
                        Icon(Icons.Default.Send, null, modifier = Modifier.size(14.dp))
                        Spacer(Modifier.width(4.dp))
                        Text("群发", fontSize = 12.sp)
                    }
                }
            }

            if (guests.isEmpty()) {
                Card(colors = CardDefaults.cardColors(containerColor = C.Card), shape = RoundedCornerShape(12.dp), modifier = Modifier.fillMaxWidth()) {
                    Box(Modifier.fillMaxWidth().padding(28.dp), contentAlignment = Alignment.Center) {
                        Text(
                            "还没有访客\n让朋友连你的热点或同一 WiFi，用浏览器打开上面的地址，输入 PIN 加入",
                            fontSize = 12.sp, color = C.Gray, textAlign = TextAlign.Center, lineHeight = 18.sp
                        )
                    }
                }
            }

            guests.forEach { g ->
                GuestRow(
                    guest = g,
                    selecting = selecting,
                    checked = selected[g.id] == true,
                    onCheck = { selected[g.id] = it },
                    onLongClick = { if (!selecting) sheetGuest = g },
                    onClick = { if (selecting) selected[g.id] = !(selected[g.id] ?: false) }
                )
            }

            Spacer(Modifier.height(4.dp))
            Text(
                "关掉这个页面服务仍在运行 · 访客上传时会弹确认框，与 P2P 接收走同一套确认与落盘逻辑",
                fontSize = 11.sp, color = C.Gray, lineHeight = 17.sp
            )
            Spacer(Modifier.height(16.dp))
        }
    }

    sheetGuest?.let { g ->
        ModalBottomSheet(onDismissRequest = { sheetGuest = null }) {
            Column(Modifier.padding(horizontal = 20.dp, vertical = 8.dp).navigationBarsPadding()) {
                Text(g.nickname, fontSize = 16.sp, fontWeight = FontWeight.Bold)
                Text("${g.remoteIp} · ${g.status}", fontSize = 12.sp, color = C.Gray)
                Spacer(Modifier.height(16.dp))
                ListItem(
                    headlineContent = { Text("发送文件…") },
                    supportingContent = { Text("可多选，逐个发送给对方确认", fontSize = 11.sp, color = C.Gray) },
                    leadingContent = { Icon(Icons.Default.Send, null, tint = C.Blue) },
                    modifier = Modifier.clickable {
                        sendTargets = listOf(g.id)
                        sheetGuest = null
                        picker.launch(arrayOf("*/*"))
                    }
                )
                ListItem(
                    headlineContent = { Text("复制 IP 地址") },
                    leadingContent = { Icon(Icons.Default.ContentCopy, null, tint = C.Blue) },
                    modifier = Modifier.clickable {
                        val cb = ctx.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                        cb.setPrimaryClip(ClipData.newPlainText("ip", g.remoteIp))
                        Toast.makeText(ctx, "已复制 ${g.remoteIp}", Toast.LENGTH_SHORT).show()
                        sheetGuest = null
                    }
                )
                ListItem(
                    headlineContent = { Text("断开此访客") },
                    leadingContent = { Icon(Icons.Default.Delete, null, tint = C.Red) },
                    modifier = Modifier.clickable {
                        web.disconnectGuest(g.id)
                        Toast.makeText(ctx, "已断开 ${g.nickname}", Toast.LENGTH_SHORT).show()
                        sheetGuest = null
                    }
                )
                Spacer(Modifier.height(16.dp))
            }
        }
    }
}

/** 地址族分段控件：选中态蓝底白字，未选灰字（不只靠颜色区分，WCAG 2.2 AA）。 */
@Composable
private fun AddressTabs(family: String, onPick: (String) -> Unit) {
    Row {
        SegmentButton("IPv4", family == "v4", Modifier.weight(1f)) { onPick("v4") }
        Spacer(Modifier.width(8.dp))
        SegmentButton("IPv6", family == "v6", Modifier.weight(1f)) { onPick("v6") }
    }
}

@Composable
private fun SegmentButton(text: String, on: Boolean, modifier: Modifier = Modifier, onClick: () -> Unit) {
    Box(
        modifier
            .height(34.dp)
            .background(if (on) C.Blue else Color.White, RoundedCornerShape(8.dp))
            .border(1.dp, if (on) C.Blue else C.Divider, RoundedCornerShape(8.dp))
            .clickable { onClick() },
        contentAlignment = Alignment.Center
    ) {
        Text(
            text,
            fontSize = 13.sp,
            fontWeight = if (on) FontWeight.SemiBold else FontWeight.Normal,
            color = if (on) Color.White else C.Gray
        )
    }
}

/** 二维码：载荷变化时重绘（默认 220dp，扫描距离够用）。 */
@Composable
private fun QrBlock(payload: String, family: String) {
    var bmp by remember { mutableStateOf<Bitmap?>(null) }
    LaunchedEffect(payload) {
        bmp = withContext(Dispatchers.Default) { QrUtils.generate(payload, 640) }
    }
    Column(Modifier.fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally) {
        Card(
            colors = CardDefaults.cardColors(containerColor = Color.White),
            shape = RoundedCornerShape(14.dp),
            border = androidx.compose.foundation.BorderStroke(1.dp, C.Divider)
        ) {
            Box(Modifier.size(212.dp).padding(10.dp), contentAlignment = Alignment.Center) {
                val b = bmp
                if (b != null) {
                    Image(b.asImageBitmap(), "网页访问二维码", Modifier.fillMaxSize())
                } else {
                    Text("正在生成二维码…", fontSize = 12.sp, color = C.Gray)
                }
            }
        }
        Spacer(Modifier.height(8.dp))
        Text(
            if (family == "v6") "让朋友用手机相机扫（IPv6 需其网络也支持）" else "让朋友用手机相机或扫一扫打开",
            fontSize = 11.sp, color = C.Gray, textAlign = TextAlign.Center
        )
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun GuestRow(
    guest: WebGuest,
    selecting: Boolean,
    checked: Boolean,
    onCheck: (Boolean) -> Unit,
    onLongClick: () -> Unit,
    onClick: () -> Unit
) {
    Card(
        colors = CardDefaults.cardColors(containerColor = C.Card),
        shape = RoundedCornerShape(12.dp),
        modifier = Modifier
            .fillMaxWidth()
            .combinedClickable(onClick = onClick, onLongClick = onLongClick)
    ) {
        Row(Modifier.padding(14.dp), verticalAlignment = Alignment.CenterVertically) {
            if (selecting) {
                Checkbox(checked = checked, onCheckedChange = onCheck)
                Spacer(Modifier.width(4.dp))
            } else {
                Box(
                    Modifier.size(36.dp).background(C.Blue.copy(alpha = 0.1f), RoundedCornerShape(9.dp)),
                    contentAlignment = Alignment.Center
                ) { Icon(Icons.Default.Language, null, tint = C.Blue, modifier = Modifier.size(20.dp)) }
                Spacer(Modifier.width(12.dp))
            }
            Column(Modifier.weight(1f)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(guest.nickname, fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
                    if (guest.justJoined) {
                        Spacer(Modifier.width(6.dp))
                        Text("刚加入", fontSize = 10.sp, color = C.Blue)
                    }
                }
                Text("${guest.remoteIp} · ${guest.status}", fontSize = 11.sp, color = C.Gray)
            }
            if (!selecting) {
                Text(
                    if (guest.status.startsWith("空闲")) "空闲" else "传输中",
                    fontSize = 10.sp,
                    color = if (guest.status.startsWith("空闲")) C.Green else C.Blue
                )
            }
        }
    }
}

/**
 * 访客上传的确认框。放在这里是为了让「网页模式页」与「主界面」都能弹——
 * 用户在哪个界面都得能当场决定，不能只在一处等。
 */
@Composable
fun UploadConfirmDialog() {
    val ctx = LocalContext.current
    val pending by AppState.pendingUpload.collectAsState()
    val web = WebHostHolder.get(ctx)

    pending?.let { p ->
        AlertDialog(
            onDismissRequest = { web.respondUpload(p.id, false) },
            title = { Text("浏览器访客要上传文件") },
            text = {
                Column {
                    Text(p.fileName, fontSize = 15.sp, fontWeight = FontWeight.Bold)
                    Spacer(Modifier.height(6.dp))
                    Text("${NetUtils.formatSize(p.size)} · 来自 ${p.guestName}", fontSize = 12.sp, color = C.Gray)
                    Spacer(Modifier.height(10.dp))
                    Text("接收后保存到 Download/飞鸽收件/", fontSize = 11.sp, color = C.Gray)
                    Spacer(Modifier.height(6.dp))
                    Text("60 秒内未选择将自动拒绝", fontSize = 11.sp, color = C.Amber)
                }
            },
            confirmButton = {
                TextButton(onClick = { web.respondUpload(p.id, true) }) { Text("接收") }
            },
            dismissButton = {
                TextButton(onClick = { web.respondUpload(p.id, false) }) { Text("拒绝") }
            }
        )
    }
}

/** 与前台服务共享同一个 WebHost 实例（同一进程单例）。 */
object WebHostHolder {
    @Volatile
    private var instance: WebHost? = null

    @Synchronized
    fun get(ctx: Context): WebHost =
        instance ?: WebHost(ctx.applicationContext).also { instance = it }
}
