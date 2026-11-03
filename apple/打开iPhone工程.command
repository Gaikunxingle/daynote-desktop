#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
if ! xcodebuild -version >/dev/null 2>&1; then
  printf '请先安装并打开 Xcode，完成首次组件安装。\n'
  read -r -p '按回车关闭窗口…' ignored
  exit 1
fi
printf '请在 Xcode 中选择 Daynote → Signing & Capabilities → Team → 你的 Personal Team。\n'
printf '连接 iPhone，选为运行设备，点击运行。免费签名有效期为 7 天。\n'
open Daynote.xcodeproj
