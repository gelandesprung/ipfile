package com.gelandesprung.ipfile

import android.Manifest
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.ManagedActivityResultLauncher
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
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

    var showSettings by remember { mutableStateOf(false) }
    var sheetPeer by remember { mutableStateOf<Peer?>(null) }
    val scope = rememberCoroutineScope()

    val filePicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri: Uri? ->
        if (uri != null) sheetPeer?.let { target ->
            scope.launch {
                val (ok, msg) = P2PEngineHolder.get(ctx).sendFile(target, uri)
                Toast.makeText(ctx, msg, Toast.LENGTH_SHORT).show()
            }
        }
    }

    if (showSettings) {
        SettingsScreen(onBack = { showSettings = false })
        return
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("飞鸽 P2P", fontWeight = FontWeight.Bold) },
                actions = {
                    IconButton(onClick = { P2PEngineHolder.get(ctx).announceName(); Toast.makeText(ctx, "已刷新", Toast.LENGTH_SHORT).show() }) {
                        Icon(Icons.Default.Refresh, "刷新")
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

            // 在线设备
            item {
                Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 4.dp)) {
                    Text("在线设备", fontSize = 14.sp, fontWeight = FontWeight.Bold)
                    Spacer(Modifier.width(8.dp))
                    Text("${peers.size} 台 · 长按发送", fontSize = 11.sp, color = C.Gray)
                }
            }
            if (peers.isEmpty()) {
                item {
                    Card(colors = CardDefaults.cardColors(containerColor = C.Card), shape = RoundedCornerShape(12.dp), modifier = Modifier.fillMaxWidth()) {
                        Box(Modifier.fillMaxWidth().padding(28.dp), contentAlignment = Alignment.Center) {
                            Text("未发现设备\n确保对方也打开了本应用且在同一局域网", fontSize = 12.sp, color = C.Gray)
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
            items(records.take(5)) { rec -> RecordRow(rec) }
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
                    modifier = Modifier.clickable { filePicker.launch(arrayOf("*/*")); sheetPeer = null }
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
                Text(peer.name, fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
                Text(peer.ip, fontSize = 11.sp, color = C.Gray)
            }
            Icon(Icons.Default.ChevronRight, null, tint = C.Gray)
        }
    }
}

@Composable
fun RecordRow(rec: TransferRecord) {
    Card(colors = CardDefaults.cardColors(containerColor = C.Card), shape = RoundedCornerShape(10.dp), modifier = Modifier.fillMaxWidth()) {
        Row(Modifier.padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(
                if (rec.direction == TransferDirection.RECEIVE) Icons.Default.Download else Icons.Default.Upload,
                null, tint = if (rec.direction == TransferDirection.RECEIVE) C.Green else C.Blue,
                modifier = Modifier.size(20.dp)
            )
            Spacer(Modifier.width(10.dp))
            Column(Modifier.weight(1f)) {
                Text("${if (rec.direction == TransferDirection.RECEIVE) "收到" else "发送"} ${rec.fileName}", fontSize = 13.sp)
                Text("${rec.peerName} · ${rec.time}", fontSize = 10.sp, color = C.Gray)
            }
            Text(if (rec.success) "完成" else "失败", fontSize = 11.sp, color = if (rec.success) C.Green else C.Red)
        }
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
            Text("就绪 · 长按设备发送文件", fontSize = 11.sp, color = C.Gray)
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
                        P2PEngineHolder.get(ctx).announceName()
                        Toast.makeText(ctx, "设备名已保存", Toast.LENGTH_SHORT).show()
                        onBack()
                    }, modifier = Modifier.fillMaxWidth()) { Text("保存") }
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
