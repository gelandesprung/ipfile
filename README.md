# 飞鸽 P2P · 局域网互传

一个纯点对点（P2P）的局域网文件传输工具，灵感来自 2005 年前后的「飞鸽传书」。PC（Windows）与 Android 手机安装本应用后，在同一局域网内互相发现、直接互传文件，**不经过任何服务器、不加密、无需联网**。支持 1GB 以内文件。

- **PC 端**：C# + WinForms，.NET 8，独立 exe
- **Android 端**：Kotlin + Jetpack Compose，minSdk 24（Android 7.0+）
- **通信**：UDP 广播发现（端口 2425）+ TCP 点对点传输（端口 2425），协议见 [`shared/PROTOCOL.md`](shared/PROTOCOL.md)

---

## 功能

- 自动发现同一局域网内的在线设备（2 秒广播 / 12 秒无广播才判定离线）
- PC 右键设备、手机长按设备 → 直接选文件发送
- 接收方先确认再接收：PC 上点「接收…」即弹出保存位置选择，选好才开始传输；拒绝会立刻通知对方
- 同名文件不覆盖，自动加 `(1)`、`(2)`
- 设备名可改，改后立即广播，对方列表同步刷新
- PC 右侧「最近传输」最多留 5 条，点一条即可在资源管理器中定位文件；下面是传输日志
- PC 关闭窗口只收进托盘（仍可接收文件），需从托盘菜单「退出」才真正结束
- 传输进度与日志；手机端以前台服务常驻，退后台/锁屏仍能收文件
- 跨网段可手动填 IP 直连（见 FAQ）

---

## 目录结构

```
.
├── shared/PROTOCOL.md      两端共用通信协议 v1.0
├── UI-DESIGN.md            界面设计说明
├── pc/                     PC 端 C# 工程（FeigeP2P.csproj），app.ico 为程序图标
├── android/                Android 工程（Gradle / Kotlin）
├── tools/icongen/          由「飞」字轮廓生成 ico 与 Android vector 的小工具
└── test/FeigeTest/         协议闭环测试（C#）
```

---

## 编译产物（已放在根目录）

| 文件 | 说明 |
|---|---|
| `FeigeP2P.exe` | PC 端单文件程序，约 480 KB，双击即可运行（需 .NET 8 桌面运行时，本机已具备） |
| `飞书.apk` | Android 安装包，约 16 MB，支持 Android 7.0+；装好后在手机上显示为「飞书」 |

两端图标一致：蓝色圆底 + 白色「飞」，字形取自微软雅黑粗体的真实轮廓。PC 的 `pc/app.ico` 与 Android 的 `ic_launcher_foreground.xml` 都由 `tools/icongen` 从同一份轮廓生成，改颜色或大小可重新跑一遍：

```bash
cd tools/icongen
dotnet run -c Release -- ../../pc/app.ico ../pathdata.txt   # 换 logo 色只需改 IconGen.Blue
```

Android 端安装后名称为「飞书」，包名仍是 `com.gelandesprung.ipfile`。

这两个文件由构建脚本生成，已加入 `.gitignore`（二进制不进版本库）。

重新生成：

```bash
# PC 端 → 根目录单文件
dotnet publish pc/FeigeP2P.csproj -c Release -r win-x64 \
  --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o .

# Android 端 → 复制到根目录
cd android && ./gradlew assembleDebug
cp app/build/outputs/apk/debug/app-debug.apk ../飞书.apk
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
- **收文件**：有人发来时会弹确认框，显示文件名/大小/来源 IP。
  - 点 **接收…** → 立刻弹出「选择保存位置」，选好目录/文件名后开始接收（默认收件夹是程序目录 `received/`）
  - 在选择框里点取消不算拒绝，会退回确认框继续等
  - 点「拒绝」则不接收，并**立刻通知对方被拒绝**；60 秒不操作也会自动拒绝
- **最近传输**：右上区域最多显示 5 条收/发记录（`↓` 收、`↑` 发），**点一下**就在资源管理器中选中该文件；文件已被移走时改为打开其所在目录
- **关闭窗口 = 收进托盘**：程序继续在后台接收文件，双击托盘图标可恢复窗口；**真正退出**请右键托盘图标 → 「退出飞鸽 P2P」
- **改设备名**：点顶部自己的名字 → 变成输入框 → 回车保存
- **复制 IP**：右键设备 → 「复制 IP 地址」
- **打开收件夹**：右键设备列表空白处 → 「打开收件夹」

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

- 首次打开会请求「通知」权限（用于接收服务的常驻通知与接收确认），请允许
- **发文件**：在「在线设备」里**长按**目标 → 底部面板 → 「发送文件…」→ 选文件
- **收文件**：有人发来时会请你确认
  - App 在前台：弹「收到文件」对话框，点「接收」或「拒绝」
  - App 在后台/锁屏：通知栏出现带「接收 / 拒绝」按钮的高优先级通知
  - 拒绝会立刻通知对方；60 秒未选择按拒绝处理
  - 同意后保存到公共下载目录 `飞鸽收件/`，通知栏显示进度
- **打开收到的文件**：在「最近传输」里点任意一条记录，用系统默认应用打开（发出的记录也可点开原文件）
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

`test/FeigeTest` 用 PC 端的真实协议类做本机闭环测试（编解码 + TCP 传输 + 中文名 + 重名不覆盖 + 拒绝/同意/另存 + 150MB 大文件 + 设备事件语义）：

```bash
cd test/FeigeTest
dotnet run -c Release
# 期望：21 通过 / 0 失败
```
