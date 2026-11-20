# 飞鸽 P2P · 局域网互传

一个纯点对点（P2P）的局域网文件传输工具，灵感来自 2005 年前后的「飞鸽传书」。PC（Windows）与 Android 手机安装本应用后，在同一局域网内互相发现、直接互传文件，**不经过任何服务器、不加密、无需联网**。支持 1GB 以内文件。

- **PC 端**：C# + WinForms，.NET 8，独立 exe
- **Android 端**：Kotlin + Jetpack Compose，minSdk 24（Android 7.0+）
- **通信**：UDP 广播发现（端口 2425）+ TCP 点对点传输（端口 2425），协议见 [`shared/PROTOCOL.md`](shared/PROTOCOL.md)

---

## 功能

- 自动发现同一局域网内的在线设备（2 秒广播 / 6 秒离线剔除）
- PC 右键设备、手机长按设备 → 直接选文件发送
- 接收方自动保存：PC 存到 `received/`，手机存到公共下载目录 `飞鸽收件/`
- 同名文件不覆盖，自动加 `(1)`、`(2)`
- 设备名可改，改后立即广播，对方列表同步刷新
- 传输进度与日志；手机端以前台服务常驻，退后台/锁屏仍能收文件
- 跨网段可手动填 IP 直连（见 FAQ）

---

## 目录结构

```
.
├── shared/PROTOCOL.md      两端共用通信协议 v1.0
├── UI-DESIGN.md            界面设计说明
├── pc/                     PC 端 C# 工程（FeigeP2P.csproj）
├── android/                Android 工程（Gradle / Kotlin）
└── test/FeigeTest/         协议闭环测试（C#）
```

---

## 编译产物（已放在根目录）

| 文件 | 说明 |
|---|---|
| `FeigeP2P.exe` | PC 端单文件程序，204 KB，双击即可运行（需 .NET 8 桌面运行时，本机已具备） |
| `FeigeP2P-android.apk` | Android 安装包，约 16 MB，支持 Android 7.0+ |

这两个文件由构建脚本生成，已加入 `.gitignore`（二进制不进版本库）。

重新生成：

```bash
# PC 端 → 根目录单文件
dotnet publish pc/FeigeP2P.csproj -c Release -r win-x64 \
  --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o .

# Android 端 → 复制到根目录
cd android && ./gradlew assembleDebug
cp app/build/outputs/apk/debug/app-debug.apk ../FeigeP2P-android.apk
```

---

## PC 端

### 编译

需要 .NET 8 SDK（本机构建环境：`D:\workbuddy\todo\tools\dotnet-sdk-8.0.428-win-x64`）。

```bash
cd pc
dotnet build -c Release
# 产物：pc/bin/Release/net8.0-windows/
```

日常运行请直接用根目录的 `FeigeP2P.exe`（单文件）。若目标机器**没有安装 .NET 8 运行时**，改用自包含发布：`dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`。

### 使用

- **发文件**：在左侧「在线设备」列表右键目标 → 「发送文件…」→ 选文件（可多选，逐个发送）
- **收文件**：自动接收，保存到程序目录 `received/`；点左下「打开收件夹」查看
- **改设备名**：点顶部自己的名字 → 变成输入框 → 回车保存
- **复制 IP**：右键设备 → 「复制 IP 地址」

---

## Android 端

### 编译

需要 JDK 17 + Android SDK 35 + Gradle 8.9（本机环境在 `D:\workbuddy\todo\tools`）。

用 Android Studio 打开 `android/` 目录直接编译；或用命令行：

```bash
# 先设置 JAVA_HOME / ANDROID_HOME / GRADLE_USER_HOME 指向 tools 下对应目录
gradle -p android assembleDebug
# 产物：android/app/build/outputs/apk/debug/app-debug.apk
```

把 `app-debug.apk` 传到手机安装（需允许「安装未知来源应用」）。

### 使用

- 首次打开会请求「通知」权限（用于接收服务的常驻通知），请允许
- **发文件**：在「在线设备」里**长按**目标 → 底部面板 → 「发送文件…」→ 选文件
- **收文件**：自动接收，保存到公共下载目录 `飞鸽收件/`；通知栏显示进度
- **改设备名**：右上角「设置」→ 第 1 项「设备名」→ 保存
- **服务**：设置页可手动启动/停止接收服务；正常情况下打开 App 会自动启动

> 注意：Android 10+ 后台限制较严，若要长时间离开 App 也能收文件，请保持接收服务的前台通知常驻（不要划掉通知），并允许电池优化白名单。

---

## 防火墙 / 网络

两端必须：
1. 在同一个局域网（同一 Wi-Fi / 同一网段）
2. 放行 **TCP 2425** 与 **UDP 2425**（Windows 首次运行会弹防火墙提示，选「允许访问专用网络」）

如果刷不出设备：
- 确认双方都打开了应用、且在同一 Wi-Fi（不要连访客网络 / 公司隔离网）
- 关闭 Windows 防火墙临时测试
- 手机确认已连接 Wi-Fi 且未开「随机 MAC / 隐私网络隔离」

---

## FAQ

**Q：能跨网段/不同 Wi-Fi 传吗？**
发现（广播）只在同网段生效，但传输是纯 TCP，只要 IP 可达就能连。跨网段时如此处理：一端的设备名后带 IP，手动用文件传输连接该 IP:2425 即可。这是预留能力，当前 UI 的主路径是同网段自动发现。

**Q：支持多大文件？**
协议用 64KB 分块流式传输，不整体加载内存，实测 150MB 正常，1GB 无问题。再大也可以，只是耗时更长。

**Q：传输中断会留下半截文件吗？**
不会。接收时写 `.part` 临时文件，收齐校验后才改名；中断则删除临时文件。

**Q：为什么叫「P2P」？**
没有中心服务器，每台设备既能发也能收，互为对端——和当年的飞鸽传书一样。

---

## 一致性测试

`test/FeigeTest` 用 PC 端的真实协议类做本机闭环测试（编解码 + TCP 传输 + 中文名 + 重名不覆盖 + 150MB 大文件）：

```bash
cd test/FeigeTest
dotnet run -c Release
# 期望：11 通过 / 0 失败
```
