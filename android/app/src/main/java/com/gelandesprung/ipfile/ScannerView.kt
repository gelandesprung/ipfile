package com.gelandesprung.ipfile

import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.ImageDecoder
import android.net.Uri
import android.os.Build
import android.provider.MediaStore
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.PhotoLibrary
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.VideocamOff
import androidx.compose.material3.Button
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.MultiFormatReader
import com.google.zxing.RGBLuminanceSource
import com.google.zxing.common.HybridBinarizer
import com.journeyapps.barcodescanner.BarcodeCallback
import com.journeyapps.barcodescanner.BarcodeResult
import com.journeyapps.barcodescanner.BarcodeView
import com.journeyapps.barcodescanner.DefaultDecoderFactory
import kotlinx.coroutines.delay

/**
 * 页内扫码取景：真实相机预览 + 四角取景框 + 扫描线。
 * 替代原来那个只显示图标的深色占位框，也不再跳 ZXing 的横屏 Activity。
 */
@Composable
fun ScannerPane(
    modifier: Modifier = Modifier,
    active: Boolean,
    errorSignal: Int,
    onText: (String) -> Unit
) {
    val ctx = LocalContext.current
    var granted by remember { mutableStateOf(hasCameraPermission(ctx)) }
    var camera by remember { mutableStateOf<BarcodeView?>(null) }
    var torch by remember { mutableStateOf(false) }
    var flash by remember { mutableStateOf(false) }

    val activeState = rememberUpdatedState(active)
    val onTextState = rememberUpdatedState(onText)

    // 扫到不是配对码时，取景框红一下再恢复
    var lastError by remember { mutableIntStateOf(errorSignal) }
    LaunchedEffect(errorSignal) {
        if (errorSignal != lastError) {
            lastError = errorSignal
            flash = true
            delay(650)
            flash = false
        }
    }

    val permLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { ok ->
        granted = ok
        if (ok) runCatching { camera?.resume() }
    }

    // 从相册选一张二维码照片再识别（没有相机权限时也能用）
    val galleryLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.GetContent()
    ) { uri: Uri? ->
        if (uri != null) {
            val text = runCatching { decodeQrFromUri(ctx, uri) }.getOrNull()
            onTextState.value(text.orEmpty())
        }
    }

    // 从系统设置里授权后回到 App，要立刻重开相机，不能还停在「无法使用相机」
    val permOwner = LocalLifecycleOwner.current
    DisposableEffect(permOwner) {
        val obs = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) granted = hasCameraPermission(ctx)
        }
        permOwner.lifecycle.addObserver(obs)
        onDispose { permOwner.lifecycle.removeObserver(obs) }
    }

    if (!granted) {
        NoCameraCard(
            modifier = modifier,
            onRequest = { permLauncher.launch(android.Manifest.permission.CAMERA) },
            onSettings = {
                ctx.startActivity(
                    Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.fromParts("package", ctx.packageName, null))
                        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                )
            },
            onGallery = { galleryLauncher.launch("image/*") }
        )
        return
    }

    BoxWithConstraints(modifier = modifier.clip(RoundedCornerShape(20.dp))) {
        AndroidView(
            modifier = Modifier.fillMaxSize(),
            factory = { context ->
                BarcodeView(context).apply {
                    setDecoderFactory(DefaultDecoderFactory(listOf(BarcodeFormat.QR_CODE)))
                    decodeContinuous(object : BarcodeCallback {
                        override fun barcodeResult(result: BarcodeResult?) {
                            val text = result?.text ?: return
                            if (!activeState.value) return
                            onTextState.value(text)
                        }
                    })
                    camera = this
                    resume()
                }
            },
            onRelease = {
                runCatching { it.pause() }
                camera = null
            }
        )

        // 页面在处理结果时靠 active 静音解码，不释放相机，避免确认卡背后黑屏
        LaunchedEffect(active) { if (active) runCatching { camera?.resume() } }
        LaunchedEffect(torch) { runCatching { camera?.setTorch(torch) } }

        val owner = LocalLifecycleOwner.current
        DisposableEffect(owner) {
            val observer = LifecycleEventObserver { _, event ->
                when (event) {
                    Lifecycle.Event.ON_RESUME -> if (active) runCatching { camera?.resume() }
                    Lifecycle.Event.ON_PAUSE -> runCatching { camera?.pause() }
                    else -> Unit
                }
            }
            owner.lifecycle.addObserver(observer)
            onDispose {
                owner.lifecycle.removeObserver(observer)
                runCatching { camera?.pause() }
            }
        }

        ScanOverlay(
            corner = if (flash) C.Red else Color(0xFFF2F8FF),
            sideFraction = 0.64f,
            modifier = Modifier.fillMaxSize()
        )

        // 右上角手电筒
        Box(
            modifier = Modifier
                .align(Alignment.TopEnd)
                .padding(12.dp)
                .size(40.dp)
                .clip(RoundedCornerShape(20.dp))
                .background(if (torch) Color(0xCC1E6FD9) else Color(0x8C0F141C))
                .clickable { torch = !torch },
            contentAlignment = Alignment.Center
        ) {
            Text(if (torch) "🔆" else "☀", fontSize = 17.sp, color = Color.White)
        }

        Text(
            "自动识别 · 无需按快门",
            color = Color(0xFFDFE8F3),
            fontSize = 10.sp,
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .padding(bottom = 14.dp)
        )
    }
}

/** 四角取景框 + 外部压暗 + 上下往复的扫描线 */
@Composable
private fun ScanOverlay(
    corner: Color,
    sideFraction: Float,
    modifier: Modifier = Modifier
) {
    val transition = rememberInfiniteTransition(label = "scan")
    val t by transition.animateFloat(
        initialValue = 0f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(tween(2200, easing = LinearEasing), RepeatMode.Reverse),
        label = "line"
    )

    Canvas(modifier = modifier) {
        val s = minOf(size.width, size.height) * sideFraction
        val left = (size.width - s) / 2f
        val top = (size.height - s) / 2f - size.height * 0.03f
        val dim = Color(0x8C080C12)

        // 取景框外压暗：四条矩形，避免半透明叠色
        drawRect(dim, Offset(0f, 0f), Size(size.width, top))
        drawRect(dim, Offset(0f, top + s), Size(size.width, size.height - top - s))
        drawRect(dim, Offset(0f, top), Size(left, s))
        drawRect(dim, Offset(left + s, top), Size(size.width - left - s, s))

        val len = 26.dp.toPx()
        val stroke = 3.5.dp.toPx()

        fun bracket(x: Float, y: Float, dx: Float, dy: Float) {
            drawLine(corner, Offset(x, y), Offset(x + dx * len, y), stroke, StrokeCap.Round)
            drawLine(corner, Offset(x, y), Offset(x, y + dy * len), stroke, StrokeCap.Round)
        }

        bracket(left, top, 1f, 1f)
        bracket(left + s, top, -1f, 1f)
        bracket(left, top + s, 1f, -1f)
        bracket(left + s, top + s, -1f, -1f)

        val y = top + 14.dp.toPx() + t * (s - 28.dp.toPx())
        drawLine(
            color = Color(0xFF4DA3FF).copy(alpha = 0.9f),
            start = Offset(left + 8.dp.toPx(), y),
            end = Offset(left + s - 8.dp.toPx(), y),
            strokeWidth = 2.dp.toPx(),
            cap = StrokeCap.Round
        )
    }
}

/** 相机权限被拒 / 无相机：说明 + 两个出口，不留白屏 */
@Composable
private fun NoCameraCard(
    modifier: Modifier,
    onRequest: () -> Unit,
    onSettings: () -> Unit,
    onGallery: () -> Unit
) {
    Box(
        modifier = modifier
            .clip(RoundedCornerShape(20.dp))
            .background(Color(0xFF15181D)),
        contentAlignment = Alignment.Center
    ) {
        Column(
            Modifier.padding(24.dp),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Icon(Icons.Default.VideocamOff, null, tint = Color(0xFF9FB0C4), modifier = Modifier.size(38.dp))
            Spacer(Modifier.height(14.dp))
            Text("无法使用相机", color = Color.White, fontSize = 15.sp, fontWeight = FontWeight.Bold)
            Spacer(Modifier.height(8.dp))
            Text(
                "扫码需要用相机读取对方的配对二维码。\n也可以直接从相册选一张二维码照片。",
                color = Color(0xFF9FB0C4), fontSize = 11.5.sp, textAlign = TextAlign.Center, lineHeight = 18.sp
            )
            Spacer(Modifier.height(18.dp))
            Button(onClick = onRequest, modifier = Modifier.fillMaxWidth().height(46.dp)) {
                Text("授予相机权限")
            }
            Spacer(Modifier.height(6.dp))
            Row(verticalAlignment = Alignment.CenterVertically) {
                TextButton(onClick = onGallery) {
                    Icon(Icons.Default.PhotoLibrary, null, modifier = Modifier.size(16.dp))
                    Spacer(Modifier.width(6.dp))
                    Text("从相册选择", fontSize = 12.sp)
                }
                TextButton(onClick = onSettings) {
                    Icon(Icons.Default.Settings, null, modifier = Modifier.size(16.dp))
                    Spacer(Modifier.width(6.dp))
                    Text("去设置", fontSize = 12.sp)
                }
            }
        }
    }
}

private fun hasCameraPermission(ctx: Context): Boolean =
    ContextCompat.checkSelfPermission(ctx, android.Manifest.permission.CAMERA) ==
        android.content.pm.PackageManager.PERMISSION_GRANTED

/** 从相册图片里解二维码；解不出返回 null */
private fun decodeQrFromUri(ctx: Context, uri: Uri): String? {
    val bmp: Bitmap = try {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            val src = ImageDecoder.createSource(ctx.contentResolver, uri)
            ImageDecoder.decodeBitmap(src) { decoder, info, _ ->
                val max = 1600
                val w = info.size.width
                val h = info.size.height
                if (w > max || h > max) {
                    val scale = max.toFloat() / maxOf(w, h)
                    decoder.setTargetSize(
                        (w * scale).toInt().coerceAtLeast(1),
                        (h * scale).toInt().coerceAtLeast(1)
                    )
                }
                decoder.allocator = ImageDecoder.ALLOCATOR_SOFTWARE
            }
        } else {
            @Suppress("DEPRECATION")
            MediaStore.Images.Media.getBitmap(ctx.contentResolver, uri)
        }
    } catch (e: Exception) {
        return null
    }

    val w = bmp.width
    val h = bmp.height
    if (w <= 0 || h <= 0) return null
    val pixels = IntArray(w * h)
    bmp.getPixels(pixels, 0, w, 0, 0, w, h)
    val binary = BinaryBitmap(HybridBinarizer(RGBLuminanceSource(w, h, pixels)))
    val reader = MultiFormatReader().apply {
        setHints(
            mapOf(
                DecodeHintType.POSSIBLE_FORMATS to listOf(BarcodeFormat.QR_CODE),
                DecodeHintType.TRY_HARDER to true
            )
        )
    }
    return try {
        reader.decode(binary).text
    } catch (e: Exception) {
        null
    }
}
