package com.gelandesprung.ipfile

import android.Manifest
import android.app.NotificationManager
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
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
import androidx.core.content.ContextCompat
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

private object C {
    val Blue = Color(0xFF1E6FD9)
    val Green = Color(0xFF1E9E4A)
    val Red = Color(0xFFC62828)
    val Amber = Color(0xFFB8860B)
    val Bg = Color(0xFFFAFAFA)
    val Card = Color(0xFFFFFFFF)
    val Divider = Color(0xFFE8EAED)
    val Gray = Color(0xFF888888)
}

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        AppState.init(this)
        requestPermissionsIfNeeded()
        ReceiveService.start(this)

        setContent {
            MaterialTheme(colorScheme = lightColorScheme(primary = C.Blue)) {
                Surface(modifier = Modifier.fillMaxSize(), color = C.Bg) {
                    AppScreen()
                }
            }
        }
    }

    override fun onResume() {
        super.onResume()
        AppState.setUiVisible(true)
        // 界面已经能弹确认框了，就不必再用通知打扰
        getSystemService(NotificationManager::class.java)?.cancel(ReceiveService.ALERT_NOTI_ID)
    }

    override fun onPause() {
        AppState.setUiVisible(false)
        super.onPause()
    }

    private fun requestPermissionsIfNeeded() {
        val need = mutableListOf<String>()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            need.add(Manifest.permission.POST_NOTIFICATIONS)
        if (Build.VERSION.SDK_INT <= Build.VERSION_CODES.P &&
            checkSelfPermission(Manifest.permission.WRITE_EXTERNAL_STORAGE) != PackageManager.PERMISSION_GRANTED)
            need.add(Manifest.permission.WRITE_EXTERNAL_STORAGE)
        if (need.isNotEmpty())
            requestPermissions(need.toTypedArray(), 100)
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AppScreen() {
    val ctx = LocalContext.current
    val deviceName by AppState.deviceName.collectAsState()
    val localIp by AppState.localIp.collectAsState()
    val peers by AppState.peers.collectAsState()
    val records by AppState.records.collectAsState()
    val progress by AppState.progress.collectAsState()
    val running by AppState.serviceRunning.collectAsState()
    val pendingReq by AppState.pending.collectAsState()
    val pendingPair by AppState.pendingPair.collectAsState()

    var showPair by remember { mutableStateOf(false) }
    var showSettings by remember { mutableStateOf(false) }
    var showLogs by remember { mutableStateOf(false) }
    var sheetPeer by remember { mutableStateOf<Peer?>(null) }
    var showManualDialog by remember { mutableStateOf(false) }
    var manualIp by remember { mutableStateOf("") }
    // 发送目标：优先长按选中的设备，其次手动输入的 IP
    var sendTarget by remember { mutableStateOf<Peer?>(null) }
    val scope = rememberCoroutineScope()

    // 「刚配对」只是短暂提示，5 秒后清掉，避免状态一直挂着
    LaunchedEffect(peers.any { it.justPaired }) {
        if (peers.any { it.justPaired }) {
            kotlinx.coroutines.delay(5000)
            AppState.clearJustPaired()
        }
    }

    val filePicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri: Uri? ->
        if (uri != null) sendTarget?.let { target ->
            scope.launch {
                val (ok, msg) = P2PEngineHolder.get(ctx).sendFile(target, uri)
                Toast.makeText(ctx, if (ok) msg else "发送失败：$msg", Toast.LENGTH_LONG).show()
            }
        }
    }

    if (showManualDialog) {
        val ipOk = manualIp.trim().let { runCatching { java.net.InetAddress.getByName(it) }.isSuccess }
        AlertDialog(
            onDismissRequest = { showManualDialog = false },
            title = { Text("手动直连发送") },
            text = {
                Column {
                    Text("输入对方电脑/手机的局域网 IP，直接发送文件（绕开自动发现）。", fontSize = 12.sp, color = C.Gray)
                    Spacer(Modifier.height(10.dp))
                    OutlinedTextField(
                        value = manualIp, onValueChange = { manualIp = it },
                        singleLine = true, label = { Text("IP 地址，如 192.168.2.196") },
                        modifier = Modifier.fillMaxWidth(), isError = manualIp.isNotEmpty() && !ipOk
                    )
                }
            },
            confirmButton = {
                TextButton(onClick = {
                    val ip = manualIp.trim()
                    if (ipOk) {
                        sendTarget = Peer(ip, ip, P2PEngine.TCP_PORT)
                        showManualDialog = false
                        filePicker.launch(arrayOf("*/*"))
                    }
                }) { Text("选择文件") }
            },
            dismissButton = { TextButton(onClick = { showManualDialog = false }) { Text("取消") } }
        )
    }

    // 有人发来文件 → 弹确认框，用户不同意就回绝对方
    pendingReq?.let { p ->
        AlertDialog(
            onDismissRequest = { P2PEngineHolder.get(ctx).respond(p.id, false) },
            title = { Text("收到文件") },
            text = {
                Column {
                    Text(p.fileName, fontSize = 15.sp, fontWeight = FontWeight.Bold)
                    Spacer(Modifier.height(6.dp))
                    Text("${NetUtils.formatSize(p.size)} · 来自 ${p.peerIp}", fontSize = 12.sp, color = C.Gray)
                    Spacer(Modifier.height(10.dp))
                    Text("60 秒内未选择将自动拒绝", fontSize = 11.sp, color = C.Amber)
                }
            },
            confirmButton = {
                TextButton(onClick = { P2PEngineHolder.get(ctx).respond(p.id, true) }) { Text("接收") }
            },
            dismissButton = {
                TextButton(onClick = { P2PEngineHolder.get(ctx).respond(p.id, false) }) { Text("拒绝") }
            }
        )
    }

    // 对方请求配对 → 本机确认（第一层确认，和「是否接收文件」互不影响）
    pendingPair?.let { p ->
        AlertDialog(
            onDismissRequest = { P2PEngineHolder.get(ctx).respondPair(p.id, false) },
            title = { Text("设备请求配对") },
            text = {
                Column {
                    Text(p.peerName, fontSize = 15.sp, fontWeight = FontWeight.Bold)
                    Spacer(Modifier.height(6.dp))
                    Text("${p.peerIp} · 端口 ${p.peerPort}", fontSize = 11.sp, color = C.Gray)
                    Spacer(Modifier.height(10.dp))
                    Text("允许后对方会加入你的设备列表；30 秒内未选择将自动拒绝。", fontSize = 12.sp, color = C.Gray)
                }
            },
            confirmButton = {
                TextButton(onClick = { P2PEngineHolder.get(ctx).respondPair(p.id, true) }) { Text("允许配对") }
            },
            dismissButton = {
                TextButton(onClick = { P2PEngineHolder.get(ctx).respondPair(p.id, false) }) { Text("拒绝") }
            }
        )
    }

    if (showPair) {
        PairScreen(onBack = { showPair = false })
        return
    }
    if (showSettings) {
        SettingsScreen(onBack = { showSettings = false })
        return
    }
    if (showLogs) {
        LogsScreen(onBack = { showLogs = false })
        return
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("飞书", fontWeight = FontWeight.Bold) },
                actions = {
                    IconButton(onClick = { showLogs = true }) {
                        Icon(Icons.Default.Article, "日志")
                    }
                    IconButton(onClick = { showSettings = true }) {
                        Icon(Icons.Default.Settings, "设置")
                    }
                },
                colors = TopAppBarDefaults.topAppBarColors(containerColor = Color.White)
            )
        },
        bottomBar = { ProgressBar(progress) }
    ) { padding ->
        LazyColumn(
            modifier = Modifier.fillMaxSize().padding(padding).padding(horizontal = 16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            item { Spacer(Modifier.height(4.dp)) }

            // 本机状态卡
            item {
                Card(colors = CardDefaults.cardColors(containerColor = C.Card), shape = RoundedCornerShape(12.dp), elevation = CardDefaults.cardElevation(1.dp)) {
                    Column(Modifier.padding(16.dp)) {
                        Text("本机", fontSize = 11.sp, color = C.Gray)
                        Spacer(Modifier.height(4.dp))
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(deviceName, fontSize = 17.sp, fontWeight = FontWeight.Bold)
                            Spacer(Modifier.width(8.dp))
                            StatusDot(running)
                        }
                        Spacer(Modifier.height(4.dp))
                        Text("$localIp : ${P2PEngine.TCP_PORT}  ·  在设置中修改设备名", fontSize = 11.sp, color = C.Gray)
                    }
                }
            }

            // 唯一的配对入口：默认只显示这一个按钮
            item {
                OutlinedButton(
                    onClick = { showPair = true },
                    modifier = Modifier.fillMaxWidth().height(48.dp),
                    shape = RoundedCornerShape(10.dp)
                ) {
                    Icon(Icons.Default.Add, null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(6.dp))
                    Text("添加设备", fontSize = 14.sp)
                }
            }

            // 已发现/已配对设备（发送入口）
            item {
                Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 4.dp)) {
                    Text("设备", fontSize = 14.sp, fontWeight = FontWeight.Bold)
                    Spacer(Modifier.width(8.dp))
                    Text("${peers.size} 台 · 长按操作", fontSize = 11.sp, color = C.Gray)
                    Spacer(Modifier.weight(1f))
                    Text(
                        "刷新",
                        fontSize = 12.sp,
                        color = C.Blue,
                        modifier = Modifier.clickable {
                            P2PEngineHolder.get(ctx).announceName()
                            AppState.log("已重新广播本机信息")
                        }
                    )
                }
            }
            if (peers.isEmpty()) {
                item {
                    Card(colors = CardDefaults.cardColors(containerColor = C.Card), shape = RoundedCornerShape(12.dp), modifier = Modifier.fillMaxWidth()) {
                        Box(Modifier.fillMaxWidth().padding(28.dp), contentAlignment = Alignment.Center) {
                            Text(
                                "暂无设备\n同网段设备会自动出现；跨网段点「添加设备」扫码配对",
                                fontSize = 12.sp, color = C.Gray, textAlign = TextAlign.Center
                            )
                        }
                    }
                }
            }
            items(peers, key = { it.key }) { peer ->
                DeviceCard(peer = peer, onLongClick = { sheetPeer = it })
            }

            // 最近传输
            item { Spacer(Modifier.height(4.dp)) }
            item { Text("最近传输", fontSize = 14.sp, fontWeight = FontWeight.Bold) }
            if (records.isEmpty()) {
                item { Text("暂无记录", fontSize = 12.sp, color = C.Gray, modifier = Modifier.padding(8.dp)) }
            }
            items(records.take(5)) { rec -> RecordRow(rec, onOpen = { openRecord(ctx, rec) }) }
            item { Spacer(Modifier.height(24.dp)) }
        }
    }

    sheetPeer?.let { peer ->
        ModalBottomSheet(onDismissRequest = { sheetPeer = null }) {
            Column(Modifier.padding(horizontal = 20.dp, vertical = 8.dp).navigationBarsPadding()) {
                Text("发送到 ${peer.name}", fontSize = 16.sp, fontWeight = FontWeight.Bold)
                Text("${peer.ip}:${peer.tcpPort}", fontSize = 12.sp, color = C.Gray)
                Spacer(Modifier.height(16.dp))
                ListItem(
                    headlineContent = { Text("发送文件…") },
                    leadingContent = { Icon(Icons.Default.Send, null, tint = C.Blue) },
                    modifier = Modifier.clickable { sendTarget = peer; filePicker.launch(arrayOf("*/*")); sheetPeer = null }
                )
                ListItem(
                    headlineContent = { Text("复制 IP 地址") },
                    leadingContent = { Icon(Icons.Default.ContentCopy, null, tint = C.Blue) },
                    modifier = Modifier.clickable {
                        val cb = ctx.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                        cb.setPrimaryClip(ClipData.newPlainText("ip", peer.ip))
                        Toast.makeText(ctx, "已复制 ${peer.ip}", Toast.LENGTH_SHORT).show()
                        sheetPeer = null
                    }
                )
                ListItem(
                    headlineContent = { Text("手动输入地址发送…") },
                    supportingContent = { Text("对方没出示二维码时的兜底", fontSize = 11.sp, color = C.Gray) },
                    leadingContent = { Icon(Icons.Default.Keyboard, null, tint = C.Blue) },
                    modifier = Modifier.clickable {
                        manualIp = peer.ip
                        showManualDialog = true
                        sheetPeer = null
                    }
                )
                ListItem(
                    headlineContent = { Text("从列表移除") },
                    leadingContent = { Icon(Icons.Default.Delete, null, tint = C.Red) },
                    modifier = Modifier.clickable {
                        AppState.removePeer(peer.key)
                        Toast.makeText(ctx, "已移除 ${peer.name}", Toast.LENGTH_SHORT).show()
                        sheetPeer = null
                    }
                )
                Spacer(Modifier.height(16.dp))
            }
        }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
fun DeviceCard(peer: Peer, onLongClick: (Peer) -> Unit) {
    Card(
        colors = CardDefaults.cardColors(containerColor = C.Card),
        shape = RoundedCornerShape(12.dp),
        elevation = CardDefaults.cardElevation(1.dp),
        modifier = Modifier
            .fillMaxWidth()
            .combinedClickable(onClick = {}, onLongClick = { onLongClick(peer) })
    ) {
        Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(40.dp).background(C.Blue.copy(alpha = 0.1f), RoundedCornerShape(10.dp)), contentAlignment = Alignment.Center) {
                Icon(Icons.Default.Computer, null, tint = C.Blue, modifier = Modifier.size(22.dp))
            }
            Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(peer.name, fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
                    if (peer.justPaired) {
                        Spacer(Modifier.width(6.dp))
                        Text("刚配对", fontSize = 10.sp, color = C.Blue)
                    }
                }
                Text(peer.address, fontSize = 11.sp, color = C.Gray)
            }
            Text(
                peer.status,
                fontSize = 10.sp,
                color = if (peer.paired) C.Blue else C.Green
            )
            Icon(Icons.Default.ChevronRight, null, tint = C.Gray)
        }
    }
}

@Composable
fun RecordRow(rec: TransferRecord, onOpen: () -> Unit) {
    val canOpen = !rec.uri.isNullOrEmpty() && rec.success
    Card(
        colors = CardDefaults.cardColors(containerColor = C.Card),
        shape = RoundedCornerShape(10.dp),
        modifier = Modifier.fillMaxWidth().clickable { if (canOpen) onOpen() }
    ) {
        Row(Modifier.padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(
                if (rec.direction == TransferDirection.RECEIVE) Icons.Default.Download else Icons.Default.Upload,
                null, tint = if (rec.direction == TransferDirection.RECEIVE) C.Green else C.Blue,
                modifier = Modifier.size(20.dp)
            )
            Spacer(Modifier.width(10.dp))
            Column(Modifier.weight(1f)) {
                Text("${if (rec.direction == TransferDirection.RECEIVE) "收到" else "发送"} ${rec.fileName}", fontSize = 13.sp)
                Text(
                    "${rec.peerName} · ${rec.time}" + if (canOpen) " · 点击打开" else "",
                    fontSize = 10.sp, color = C.Gray
                )
            }
            Text(
                if (rec.success) "完成" else "失败",
                fontSize = 11.sp,
                color = if (rec.success) C.Green else C.Red
            )
        }
    }
}

/** 用系统默认应用打开这条记录对应的文件。 */
fun openRecord(ctx: Context, rec: TransferRecord) {
    val uriStr = rec.uri
    if (uriStr.isNullOrEmpty()) {
        Toast.makeText(ctx, "该记录没有可打开的文件", Toast.LENGTH_SHORT).show()
        return
    }
    try {
        val uri = Uri.parse(uriStr)
        // 优先按扩展名给类型：接收时保存的 MIME 可能已被写成通用二进制
        val guessed = NetUtils.mimeOf(rec.fileName)
        val mime = if (guessed != "application/octet-stream") guessed
        else (ctx.contentResolver.getType(uri) ?: "application/octet-stream")
        val intent = Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, mime)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        ctx.startActivity(intent)
    } catch (e: Exception) {
        Toast.makeText(ctx, "没有能打开该文件的应用", Toast.LENGTH_SHORT).show()
    }
}

@Composable
fun StatusDot(running: Boolean) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(8.dp).background(if (running) C.Green else C.Gray, RoundedCornerShape(50)))
        Spacer(Modifier.width(4.dp))
        Text(if (running) "接收中" else "未运行", fontSize = 11.sp, color = if (running) C.Green else C.Gray)
    }
}

@Composable
fun ProgressBar(progress: ProgressInfo?) {
    if (progress == null) {
        Box(Modifier.fillMaxWidth().height(56.dp).background(Color.White), contentAlignment = Alignment.Center) {
            Text("就绪 · 同网段设备自动发现，长按发送文件", fontSize = 11.sp, color = C.Gray)
        }
        return
    }
    val pct = if (progress.total > 0) (progress.done * 100f / progress.total) / 100f else 0f
    Column(Modifier.fillMaxWidth().background(Color.White).padding(horizontal = 16.dp, vertical = 10.dp)) {
        Text("${progress.fileName}  ${NetUtils.formatSize(progress.done)}/${NetUtils.formatSize(progress.total)}  ${(pct * 100).toInt()}%", fontSize = 11.sp)
        Spacer(Modifier.height(6.dp))
        LinearProgressIndicator(progress = pct, modifier = Modifier.fillMaxWidth(), color = C.Blue)
    }
}

// ---------------- 二维码配对页 ----------------

private fun scanOptions(): ScanOptions = ScanOptions().apply {
    setDesiredBarcodeFormats(ScanOptions.QR_CODE)
    setPrompt("对准对方的配对二维码")
    setBeepEnabled(false)
    setOrientationLocked(false)
}

private fun formatRemaining(ms: Long): String {
    val total = (ms / 1000).coerceAtLeast(0)
    return String.format("%02d:%02d", total / 60, total % 60)
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PairScreen(onBack: () -> Unit) {
    val ctx = LocalContext.current
    val engine = P2PEngineHolder.get(ctx)
    var tab by rememberSaveable { mutableIntStateOf(0) }
    val session by AppState.pairSession.collectAsState()
    val localIp by AppState.localIp.collectAsState()
    val successTick by AppState.pairSuccess.collectAsState()
    var remaining by remember { mutableLongStateOf(0L) }
    var qr by remember { mutableStateOf<Bitmap?>(null) }
    var scanned by remember { mutableStateOf<Protocol.QrPayload?>(null) }
    var pairing by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    val openedAt by remember { mutableLongStateOf(successTick) }

    // 进入配对页才生成 token；离开即作废
    LaunchedEffect(Unit) { engine.beginPairing() }
    DisposableEffect(Unit) {
        onDispose { engine.cancelPairing() }
    }

    LaunchedEffect(session?.token) {
        while (true) {
            val s = session ?: break
            remaining = s.remainingMs()
            if (remaining <= 0) break
            delay(1000)
        }
    }

    LaunchedEffect(session?.token) {
        val s = session ?: return@LaunchedEffect
        val payload = Protocol.buildQrPayload(
            AppState.deviceName.value, localIp, P2PEngine.TCP_PORT, s.token
        )
        qr = QrUtils.generate(payload)
    }

    LaunchedEffect(successTick) {
        if (successTick > openedAt) {
            Toast.makeText(ctx, "配对成功，设备已加入列表", Toast.LENGTH_SHORT).show()
            onBack()
        }
    }

    val scanLauncher = rememberLauncherForActivityResult(ScanContract()) { result ->
        val contents = result.contents
        if (!contents.isNullOrBlank()) {
            val payload = Protocol.parseQrPayload(contents)
            if (payload == null) {
                Toast.makeText(ctx, "这不是飞鸽的配对二维码", Toast.LENGTH_LONG).show()
            } else {
                scanned = payload
            }
        }
    }
    val cameraPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) scanLauncher.launch(scanOptions())
        else Toast.makeText(ctx, "需要相机权限才能扫码", Toast.LENGTH_LONG).show()
    }

    fun startScan() {
        val granted = ContextCompat.checkSelfPermission(ctx, Manifest.permission.CAMERA) ==
            PackageManager.PERMISSION_GRANTED
        if (granted) scanLauncher.launch(scanOptions())
        else cameraPermission.launch(Manifest.permission.CAMERA)
    }

    // 扫到码后先确认对方身份，用户点「确认连接」才发 FG_HELLO
    scanned?.let { payload ->
        AlertDialog(
            onDismissRequest = { if (!pairing) scanned = null },
            title = { Text("确认连接到这台设备？") },
            text = {
                Column {
                    Text(payload.name, fontSize = 16.sp, fontWeight = FontWeight.Bold)
                    Spacer(Modifier.height(4.dp))
                    Text(
                        "${payload.ip}:${payload.tcpPort}",
                        fontSize = 11.sp, color = C.Gray, fontFamily = FontFamily.Monospace
                    )
                    Spacer(Modifier.height(10.dp))
                    Text(
                        "确认后还需要对方在设备上允许配对；成功后对方会加入你的设备列表，不会自动发送文件。",
                        fontSize = 12.sp, color = C.Gray
                    )
                }
            },
            confirmButton = {
                TextButton(
                    enabled = !pairing,
                    onClick = {
                        pairing = true
                        scope.launch {
                            val (ok, msg) = engine.pairWith(payload.ip, payload.tcpPort, payload.token)
                            pairing = false
                            scanned = null
                            Toast.makeText(ctx, if (ok) msg else "配对失败：$msg", Toast.LENGTH_LONG).show()
                        }
                    }
                ) { Text(if (pairing) "配对中…" else "确认连接") }
            },
            dismissButton = {
                TextButton(enabled = !pairing, onClick = { scanned = null }) { Text("取消") }
            }
        )
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("添加设备", fontWeight = FontWeight.Bold) },
                navigationIcon = {
                    IconButton(onClick = onBack) { Icon(Icons.Default.ArrowBack, "返回") }
                },
                colors = TopAppBarDefaults.topAppBarColors(containerColor = Color.White)
            )
        }
    ) { padding ->
        Column(Modifier.fillMaxSize().padding(padding)) {
            TabRow(selectedTabIndex = tab, containerColor = Color.White) {
                Tab(selected = tab == 0, onClick = { tab = 0 }, text = { Text("出示二维码") })
                Tab(selected = tab == 1, onClick = { tab = 1 }, text = { Text("扫码配对") })
            }

            Column(
                Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(20.dp),
                horizontalAlignment = Alignment.CenterHorizontally
            ) {
                if (tab == 0) {
                    Text("让对方用「扫码配对」扫描此码", fontSize = 12.sp, color = C.Gray)
                    Spacer(Modifier.height(16.dp))

                    val bitmap = qr
                    Card(
                        colors = CardDefaults.cardColors(containerColor = C.Card),
                        shape = RoundedCornerShape(16.dp),
                        elevation = CardDefaults.cardElevation(1.dp)
                    ) {
                        Box(Modifier.size(260.dp).padding(14.dp), contentAlignment = Alignment.Center) {
                            if (bitmap != null) {
                                Image(bitmap.asImageBitmap(), "配对二维码", Modifier.fillMaxSize())
                            } else {
                                Text("正在生成二维码…", fontSize = 12.sp, color = C.Gray)
                            }
                        }
                    }

                    Spacer(Modifier.height(14.dp))
                    val expired = remaining <= 0
                    Text(
                        if (expired) "二维码已过期" else "剩余 ${formatRemaining(remaining)}",
                        fontSize = 16.sp,
                        fontWeight = FontWeight.Bold,
                        color = when {
                            expired -> C.Red
                            remaining <= 60_000 -> C.Amber
                            else -> C.Blue
                        }
                    )
                    Spacer(Modifier.height(6.dp))
                    Text(
                        "$localIp:${P2PEngine.TCP_PORT}",
                        fontSize = 11.sp, color = C.Gray,
                        fontFamily = FontFamily.Monospace, textAlign = TextAlign.Center
                    )
                    Spacer(Modifier.height(16.dp))
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        OutlinedButton(onClick = { engine.beginPairing() }) {
                            Text(if (expired) "重新生成" else "刷新二维码")
                        }
                        Spacer(Modifier.width(8.dp))
                        TextButton(onClick = onBack) { Text("收起") }
                    }
                    Spacer(Modifier.height(10.dp))
                    Text("刷新或收起后旧码立即失效", fontSize = 11.sp, color = C.Gray)
                } else {
                    Text("扫描对方出示的二维码", fontSize = 12.sp, color = C.Gray)
                    Spacer(Modifier.height(16.dp))
                    Box(
                        Modifier.fillMaxWidth().height(260.dp)
                            .background(Color(0xFF15181D), RoundedCornerShape(14.dp)),
                        contentAlignment = Alignment.Center
                    ) {
                        Icon(
                            Icons.Default.QrCodeScanner, null,
                            tint = Color.White, modifier = Modifier.size(76.dp)
                        )
                    }
                    Spacer(Modifier.height(20.dp))
                    Button(onClick = { startScan() }, modifier = Modifier.fillMaxWidth().height(48.dp)) {
                        Icon(Icons.Default.PhotoCamera, null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(8.dp))
                        Text("打开相机扫码")
                    }
                    Spacer(Modifier.height(12.dp))
                    Text(
                        "扫码后先确认对方身份，再由对方允许配对；\n配对只建立设备信任，不会自动发文件。",
                        fontSize = 11.sp, color = C.Gray, textAlign = TextAlign.Center
                    )
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun LogsScreen(onBack: () -> Unit) {
    val logs by AppState.logs.collectAsState()
    Scaffold(topBar = {
        TopAppBar(title = { Text("运行日志") }, navigationIcon = {
            IconButton(onClick = onBack) { Icon(Icons.Default.ArrowBack, "返回") }
        })
    }) { padding ->
        if (logs.isEmpty()) {
            Box(Modifier.fillMaxSize().padding(padding), contentAlignment = Alignment.Center) {
                Text("暂无日志", fontSize = 13.sp, color = C.Gray)
            }
        } else {
            LazyColumn(Modifier.fillMaxSize().padding(padding).padding(16.dp)) {
                items(logs) { line ->
                    Text(line, fontSize = 12.sp, color = Color(0xFF333333),
                        modifier = Modifier.padding(vertical = 4.dp))
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScreen(onBack: () -> Unit) {
    val ctx = LocalContext.current
    var name by rememberSaveable { mutableStateOf(AppState.deviceName.value) }
    Scaffold(topBar = {
        TopAppBar(title = { Text("设置") }, navigationIcon = {
            IconButton(onClick = onBack) { Icon(Icons.Default.ArrowBack, "返回") }
        })
    }) { padding ->
        Column(Modifier.fillMaxSize().padding(padding).padding(16.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
            Card(colors = CardDefaults.cardColors(containerColor = C.Card), shape = RoundedCornerShape(12.dp)) {
                Column(Modifier.padding(16.dp)) {
                    Text("设备名（其他设备看到的名字）", fontSize = 12.sp, color = C.Gray)
                    Spacer(Modifier.height(8.dp))
                    OutlinedTextField(value = name, onValueChange = { name = it }, singleLine = true, modifier = Modifier.fillMaxWidth())
                    Spacer(Modifier.height(8.dp))
                    Button(onClick = {
                        AppState.setDeviceName(name)
                        Toast.makeText(ctx, "设备名已保存，下次出示二维码时生效", Toast.LENGTH_SHORT).show()
                        onBack()
                    }, modifier = Modifier.fillMaxWidth()) { Text("保存") }
                    Spacer(Modifier.height(8.dp))
                    Text(
                        "同网段设备会自动被发现（离开局域网后从列表消失）；扫码配对的设备只保留在本次运行中，重启 App 后需重新配对。",
                        fontSize = 11.sp, color = C.Gray
                    )
                }
            }
            Card(colors = CardDefaults.cardColors(containerColor = C.Card), shape = RoundedCornerShape(12.dp)) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text("接收服务", fontSize = 14.sp, fontWeight = FontWeight.Bold)
                    Text("保持开启以接收他人发送的文件（前台服务常驻）", fontSize = 11.sp, color = C.Gray)
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        Button(onClick = { ReceiveService.start(ctx) }) { Text("启动服务") }
                        OutlinedButton(onClick = { ctx.stopService(Intent(ctx, ReceiveService::class.java)) }) { Text("停止服务") }
                    }
                }
            }
            Card(colors = CardDefaults.cardColors(containerColor = C.Card), shape = RoundedCornerShape(12.dp)) {
                Column(Modifier.padding(16.dp)) {
                    Text("接收目录", fontSize = 14.sp, fontWeight = FontWeight.Bold)
                    Spacer(Modifier.height(4.dp))
                    Text("公共下载目录 / 飞鸽收件/", fontSize = 12.sp, color = C.Gray)
                }
            }
            Card(colors = CardDefaults.cardColors(containerColor = C.Card), shape = RoundedCornerShape(12.dp)) {
                Column(Modifier.padding(16.dp)) {
                    Text("关于", fontSize = 14.sp, fontWeight = FontWeight.Bold)
                    Spacer(Modifier.height(4.dp))
                    val ver = try {
                        ctx.packageManager.getPackageInfo(ctx.packageName, 0).versionName
                    } catch (e: Exception) { "?" }
                    Text("飞书 · v$ver", fontSize = 12.sp, color = C.Gray)
                    Text("build 2026-11-21 · 广播发现 + 二维码配对 · 接收需确认", fontSize = 11.sp, color = C.Gray)
                }
            }
        }
    }
}

/** 获取与前台服务共享的引擎实例（同一进程单例）。 */
object P2PEngineHolder {
    @Volatile private var instance: P2PEngine? = null
    @Synchronized fun get(ctx: Context): P2PEngine =
        instance ?: P2PEngine(ctx.applicationContext).also { instance = it }
    @Synchronized fun release() { instance = null }
}
