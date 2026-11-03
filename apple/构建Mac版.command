#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
trap 'status=$?; if [ "$status" -ne 0 ]; then printf "\n构建未完成，请把上方错误保存后反馈。\n"; fi; read -r -p "按回车关闭窗口…" ignored' EXIT
if ! xcodebuild -version >/dev/null 2>&1; then
  printf '请先从 Mac App Store 安装 Xcode，打开一次并完成组件安装，然后重试。\n'
  exit 1
fi
printf '正在检查同步核心…\n'
swift test
printf '\n正在构建原生 Mac 应用…\n'
xcodebuild -project Daynote.xcodeproj -scheme Daynote -configuration Release \
  -destination 'platform=macOS' -derivedDataPath build/MacDerivedData \
  CODE_SIGNING_ALLOWED=YES CODE_SIGN_IDENTITY=- CODE_SIGN_STYLE=Manual DEVELOPMENT_TEAM= build
app_path="$PWD/build/MacDerivedData/Build/Products/Release/朝夕日历.app"
if [ ! -d "$app_path" ]; then printf '没有找到生成的应用。\n'; exit 1; fi
printf '\n构建成功。Finder 将显示“朝夕日历”，可拖入“应用程序”。\n'
open -R "$app_path"
