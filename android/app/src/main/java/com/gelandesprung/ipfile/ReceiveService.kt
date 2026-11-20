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

/** 前台服务：承载 P2PEngine，保证退到后台/锁屏仍能接收文件。 */
class ReceiveService : Service() {
    private var engine: P2PEngine? = null
    private var job: Job? = null
    private val uiScope = CoroutineScope(SupervisorJob() + Dispatchers.Main)

    companion object {
        const val CHANNEL_ID = "ipfile_receive"
        const val ALERT_CHANNEL_ID = "ipfile_confirm"
        const val NOTI_ID = 1001
        const val ALERT_NOTI_ID = 1002
        const val ACTION_START = "start"
        const val ACTION_STOP = "stop"
        const val ACTION_ACCEPT = "accept"
        const val ACTION_REJECT = "reject"
        const val EXTRA_REQUEST_ID = "request_id"

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
            else -> {
                if (engine == null) {
                    engine = P2PEngineHolder.get(applicationContext).also { it.start() }
                    AppState.setServiceRunning(true)
                    observeProgress()
                    observePending()
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
            .setContentTitle("飞鸽 P2P")
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

    override fun onDestroy() {
        job?.cancel()
        uiScope.launch { AppState.setPending(null) }
        getSystemService(NotificationManager::class.java)?.cancel(ALERT_NOTI_ID)
        engine?.stop()
        engine = null
        P2PEngineHolder.release()
        AppState.setServiceRunning(false)
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null
}
