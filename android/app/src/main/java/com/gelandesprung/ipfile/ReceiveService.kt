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
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch

/** 前台服务：承载 P2PEngine，保证退到后台/锁屏仍能接收文件。 */
class ReceiveService : Service() {
    private var engine: P2PEngine? = null
    private var job: kotlinx.coroutines.Job? = null

    companion object {
        const val CHANNEL_ID = "ipfile_receive"
        const val NOTI_ID = 1001
        const val ACTION_START = "start"
        const val ACTION_STOP = "stop"

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
            ACTION_STOP -> stopSelf()
            else -> {
                if (engine == null) {
                    engine = P2PEngineHolder.get(applicationContext).also { it.start() }
                    AppState.setServiceRunning(true)
                    observeProgress()
                }
            }
        }
        return START_STICKY
    }

    private fun observeProgress() {
        job = kotlinx.coroutines.CoroutineScope(kotlinx.coroutines.SupervisorJob() + kotlinx.coroutines.Dispatchers.Main).launch {
            AppState.progress.collectLatest { p ->
                val text = if (p == null) {
                    "接收服务运行中 · ${AppState.deviceName.value}"
                } else {
                    val pct = if (p.total > 0) (p.done * 100 / p.total) else 0
                    "${if (p.direction == TransferDirection.RECEIVE) "正在接收" else "正在发送"} ${p.fileName} $pct%"
                }
                val nm = getSystemService(NotificationManager::class.java)
                nm.notify(NOTI_ID, buildNotification(text))
            }
        }
    }

    private fun createChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val ch = NotificationChannel(CHANNEL_ID, getString(R.string.notification_channel), NotificationManager.IMPORTANCE_LOW)
            getSystemService(NotificationManager::class.java).createNotificationChannel(ch)
        }
    }

    private fun buildNotification(text: String): Notification {
        val pi = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java),
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M)
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
            else PendingIntent.FLAG_UPDATE_CURRENT
        )
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.stat_sys_download)
            .setContentTitle("飞鸽 P2P")
            .setContentText(text)
            .setContentIntent(pi)
            .setOngoing(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .build()
    }

    override fun onDestroy() {
        job?.cancel()
        engine?.stop()
        engine = null
        P2PEngineHolder.release()
        AppState.setServiceRunning(false)
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null
}
