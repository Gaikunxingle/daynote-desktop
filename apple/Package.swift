// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "DaynoteCore",
    platforms: [.macOS(.v13), .iOS(.v16)],
    products: [.library(name: "DaynoteCore", targets: ["DaynoteCore"])],
    targets: [
        .target(name: "DaynoteCore", path: "Daynote",
                exclude: ["AppStore.swift", "DaynoteApp.swift", "CalendarScreen.swift", "TaskEditorScreen.swift", "Assets.xcassets", "Daynote-macOS.entitlements"],
                sources: ["SyncCore.swift"]),
        .testTarget(name: "DaynoteCoreTests", dependencies: ["DaynoteCore"], path: "Tests/DaynoteCoreTests")
    ]
)
