package com.gelandesprung.ipfile

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.launch

/** 前台服务：承载 P2PEngine 与网页模式，保证退到后台/锁屏仍能收发文件。 */
class ReceiveService : Service() {
    private var engine: P2PEngine? = null
    /** 网页模式服务：在这里持一份强引用，Activity 销毁后仍由前台服务托着。 */
    private var web: WebHost? = null
    private var job: Job? = null
    private val uiScope = CoroutineScope(SupervisorJob() + Dispatchers.Main)

    companion object {
        const val CHANNEL_ID = "ipfile_receive"
        const val ALERT_CHANNEL_ID = "ipfile_confirm"
        const val NOTI_ID = 1001
        const val ALERT_NOTI_ID = 1002
        const val PAIR_NOTI_ID = 1003
        const val WEB_NOTI_ID = 1004
        const val ACTION_START = "start"
        const val ACTION_STOP = "stop"
        const val ACTION_ACCEPT = "accept"
        const val ACTION_REJECT = "reject"
        const val ACTION_PAIR_ACCEPT = "pair_accept"
        const val ACTION_PAIR_REJECT = "pair_reject"
        const val ACTION_WEB_ACCEPT = "web_accept"
        const val ACTION_WEB_REJECT = "web_reject"
        const val EXTRA_REQUEST_ID = "request_id"
        const val EXTRA_PAIR_ID = "pair_id"
        const val EXTRA_UPLOAD_ID = "upload_id"

        fun start(context: Context) {
            val i = Intent(context, ReceiveService::class.java).apply { action = ACTION_START }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) context.startForegroundService(i)
            else context.startService(i)
        }
    }

    override fun onCreate() {
        super.onCreate()
        createChannel()
        startForeground(NOTI_ID, buildNotification("接收服务已启动"))
        // 这里只是持有实例；监听端口要等用户点「网页模式」才发生
        web = WebHostHolder.get(applicationContext)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> {
                stopSelf()
                return START_NOT_STICKY
            }
            ACTION_ACCEPT, ACTION_REJECT -> {
                val id = intent.getStringExtra(EXTRA_REQUEST_ID) ?: ""
                val accept = intent.action == ACTION_ACCEPT
                (engine ?: P2PEngineHolder.get(applicationContext)).respond(id, accept)
                getSystemService(NotificationManager::class.java)?.cancel(ALERT_NOTI_ID)
                return START_NOT_STICKY
            }
            ACTION_PAIR_ACCEPT, ACTION_PAIR_REJECT -> {
                val id = intent.getStringExtra(EXTRA_PAIR_ID) ?: ""
                val accept = intent.action == ACTION_PAIR_ACCEPT
                (engine ?: P2PEngineHolder.get(applicationContext)).respondPair(id, accept)
                getSystemService(NotificationManager::class.java)?.cancel(PAIR_NOTI_ID)
                return START_NOT_STICKY
            }
            ACTION_WEB_ACCEPT, ACTION_WEB_REJECT -> {
                val id = intent.getStringExtra(EXTRA_UPLOAD_ID) ?: ""
                (web ?: WebHostHolder.get(applicationContext))
                    .respondUpload(id, intent.action == ACTION_WEB_ACCEPT)
                getSystemService(NotificationManager::class.java)?.cancel(WEB_NOTI_ID)
                return START_NOT_STICKY
            }
            else -> {
                if (engine == null) {
                    engine = P2PEngineHolder.get(applicationContext).also { it.start() }
                    AppState.setServiceRunning(true)
                    observeProgress()
                    observePending()
                    observePendingPair()
                    observePendingUpload()
                }
            }
        }
        return START_STICKY
    }

    private fun observeProgress() {
        job = uiScope.launch {
            AppState.progress.collectLatest { p ->
                val text = if (p == null) {
                    "接收服务运行中 · ${AppState.deviceName.value}"
                } else {
                    val pct = if (p.total > 0) (p.done * 100 / p.total) else 0
                    "${if (p.direction == TransferDirection.RECEIVE) "正在接收" else "正在发送"} ${p.fileName} $pct%"
                }
                val nm = getSystemService(NotificationManager::class.java)
                nm?.notify(NOTI_ID, buildNotification(text))
            }
        }
    }

    /** 有待确认请求时发通知（App 在前台时由界面弹框，不重复打扰）。 */
    private fun observePending() {
        uiScope.launch {
            combine(AppState.pending, AppState.uiVisible) { pending, visible -> pending to visible }
                .collectLatest { (pending, visible) ->
                    val nm = getSystemService(NotificationManager::class.java) ?: return@collectLatest
                    if (pending == null || visible) {
                        nm.cancel(ALERT_NOTI_ID)
                    } else {
                        nm.notify(ALERT_NOTI_ID, buildConfirmNotification(pending))
                    }
                }
        }
    }

    /** 有配对请求时发通知（App 在前台时由界面弹框，不重复打扰）。 */
    private fun observePendingPair() {
        uiScope.launch {
            combine(AppState.pendingPair, AppState.uiVisible) { pair, visible -> pair to visible }
                .collectLatest { (pair, visible) ->
                    val nm = getSystemService(NotificationManager::class.java) ?: return@collectLatest
                    if (pair == null || visible) {
                        nm.cancel(PAIR_NOTI_ID)
                    } else {
                        nm.notify(PAIR_NOTI_ID, buildPairNotification(pair))
                    }
                }
        }
    }

    /** 有浏览器上传请求时发通知（App 在前台时由界面弹框，不重复打扰）。 */
    private fun observePendingUpload() {
        uiScope.launch {
            combine(AppState.pendingUpload, AppState.uiVisible) { p, visible -> p to visible }
                .collectLatest { (p, visible) ->
                    val nm = getSystemService(NotificationManager::class.java) ?: return@collectLatest
                    if (p == null || visible) {
                        nm.cancel(WEB_NOTI_ID)
                    } else {
                        nm.notify(WEB_NOTI_ID, buildUploadNotification(p))
                    }
                }
        }
    }

    private fun createChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val nm = getSystemService(NotificationManager::class.java)
            nm.createNotificationChannel(
                NotificationChannel(CHANNEL_ID, getString(R.string.notification_channel), NotificationManager.IMPORTANCE_LOW)
            )
            val alert = NotificationChannel(
                ALERT_CHANNEL_ID, "接收确认", NotificationManager.IMPORTANCE_HIGH
            ).apply {
                description = "有人发送文件时提醒你确认"
                enableVibration(true)
            }
            nm.createNotificationChannel(alert)
        }
    }

    private fun pendingFlags(): Int =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M)
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        else PendingIntent.FLAG_UPDATE_CURRENT

    private fun buildNotification(text: String): Notification {
        val pi = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), pendingFlags())
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.stat_sys_download)
            .setContentTitle("飞书")
            .setContentText(text)
            .setContentIntent(pi)
            .setOngoing(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .build()
    }

    private fun buildConfirmNotification(p: PendingRequest): Notification {
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), pendingFlags())
        val accept = PendingIntent.getService(this, 1, Intent(this, ReceiveService::class.java).apply {
            action = ACTION_ACCEPT
            putExtra(EXTRA_REQUEST_ID, p.id)
        }, pendingFlags())
        val reject = PendingIntent.getService(this, 2, Intent(this, ReceiveService::class.java).apply {
            action = ACTION_REJECT
            putExtra(EXTRA_REQUEST_ID, p.id)
        }, pendingFlags())

        val detail = "${p.fileName} · ${NetUtils.formatSize(p.size)}"
        return NotificationCompat.Builder(this, ALERT_CHANNEL_ID)
            .setSmallIcon(android.R.drawable.stat_sys_download_done)
            .setContentTitle("${p.peerIp} 想发送文件")
            .setContentText(detail)
            .setStyle(NotificationCompat.BigTextStyle().bigText("$detail\n点「接收」开始传输，60 秒内未选择将自动拒绝"))
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_MESSAGE)
            .setContentIntent(open)
            .setAutoCancel(true)
            .addAction(0, "接收", accept)
            .addAction(0, "拒绝", reject)
            .build()
    }

    private fun buildPairNotification(p: PendingPair): Notification {
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), pendingFlags())
        val accept = PendingIntent.getService(this, 3, Intent(this, ReceiveService::class.java).apply {
            action = ACTION_PAIR_ACCEPT
            putExtra(EXTRA_PAIR_ID, p.id)
        }, pendingFlags())
        val reject = PendingIntent.getService(this, 4, Intent(this, ReceiveService::class.java).apply {
            action = ACTION_PAIR_REJECT
            putExtra(EXTRA_PAIR_ID, p.id)
        }, pendingFlags())

        return NotificationCompat.Builder(this, ALERT_CHANNEL_ID)
            .setSmallIcon(android.R.drawable.stat_sys_download_done)
            .setContentTitle("${p.peerName} 请求配对")
            .setContentText("${p.peerIp} · 配对后可互相发送文件")
            .setStyle(NotificationCompat.BigTextStyle().bigText("${p.peerName}（${p.peerIp}）请求配对\n允许后对方会出现在设备列表，30 秒内未选择将自动拒绝"))
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_MESSAGE)
            .setContentIntent(open)
            .setAutoCancel(true)
            .addAction(0, "允许配对", accept)
            .addAction(0, "拒绝", reject)
            .build()
    }

    private fun buildUploadNotification(p: PendingUpload): Notification {
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), pendingFlags())
        val accept = PendingIntent.getService(this, 5, Intent(this, ReceiveService::class.java).apply {
            action = ACTION_WEB_ACCEPT
            putExtra(EXTRA_UPLOAD_ID, p.id)
        }, pendingFlags())
        val reject = PendingIntent.getService(this, 6, Intent(this, ReceiveService::class.java).apply {
            action = ACTION_WEB_REJECT
            putExtra(EXTRA_UPLOAD_ID, p.id)
        }, pendingFlags())

        val detail = "${p.fileName} · ${NetUtils.formatSize(p.size)}"
        return NotificationCompat.Builder(this, ALERT_CHANNEL_ID)
            .setSmallIcon(android.R.drawable.stat_sys_upload_done)
            .setContentTitle("${p.guestName}（浏览器）想上传文件")
            .setContentText(detail)
            .setStyle(
                NotificationCompat.BigTextStyle()
                    .bigText("$detail\n接收后保存到 Download/飞鸽收件/，60 秒内未选择将自动拒绝")
            )
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_MESSAGE)
            .setContentIntent(open)
            .setAutoCancel(true)
            .addAction(0, "接收", accept)
            .addAction(0, "拒绝", reject)
            .build()
    }

    override fun onDestroy() {
        job?.cancel()
        uiScope.launch {
            AppState.setPending(null)
            AppState.setPendingPair(null)
        }
        getSystemService(NotificationManager::class.java)?.cancel(ALERT_NOTI_ID)
        getSystemService(NotificationManager::class.java)?.cancel(PAIR_NOTI_ID)
        getSystemService(NotificationManager::class.java)?.cancel(WEB_NOTI_ID)
        web?.stop()
        web = null
        engine?.stop()
        engine = null
        P2PEngineHolder.release()
        AppState.setServiceRunning(false)
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null
}
