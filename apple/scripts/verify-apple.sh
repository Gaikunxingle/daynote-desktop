#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
swift test
xcodebuild -project Daynote.xcodeproj -scheme Daynote -configuration Debug \
  -destination 'generic/platform=iOS Simulator' -derivedDataPath build/Verify-iOS CODE_SIGNING_ALLOWED=NO build
xcodebuild -project Daynote.xcodeproj -scheme Daynote -configuration Debug \
  -destination 'platform=macOS' -derivedDataPath build/Verify-Mac CODE_SIGNING_ALLOWED=NO build
printf '\nApple 核心测试、iOS 模拟器与 macOS 编译均已通过。仍需真机检查通知与签名。\n'
