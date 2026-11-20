#!/usr/bin/env bash
# 生成 PC 端安装包：飞鸽P2P-Setup-x64.msi
#
# 前置：
#   1) .NET 8 SDK（本机在 tools/dotnet-sdk-8.0.428-win-x64）
#   2) WiX v5 作为 .NET 全局工具安装：
#        dotnet tool install --global wix --version 5.0.2
#        wix extension add -g WixToolset.UI.wixext/5.0.2
#
# 产物：仓库根目录 飞鸽P2P-Setup-x64.msi
# 说明：payload 是「自包含单文件」exe（约 72MB），安装后无需另装 .NET 运行时。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DOTNET_ROOT="${DOTNET_ROOT:-$ROOT/../todo/tools/dotnet-sdk-8.0.428-win-x64}"
DOTNET="$DOTNET_ROOT/dotnet.exe"
PAYLOAD="$ROOT/build/payload"
OUT="$ROOT/飞鸽P2P-Setup-x64.msi"

echo "==> 发布自包含单文件 exe"
rm -rf "$PAYLOAD"
"$DOTNET" publish "$ROOT/pc/FeigeP2P.csproj" -c Release -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none \
  -p:EnableCompressionInSingleFile=true \
  -o "$PAYLOAD" --nologo -v q

echo "==> 构建 MSI（中文向导，当前用户安装）"
cd "$ROOT/pc/installer"
rm -f "$OUT"
wix build Product.wxs -arch x64 -culture zh-CN \
  -ext WixToolset.UI.wixext \
  -o "$OUT"

echo "==> 完成：$OUT"
ls -lh "$OUT"
