import SwiftUI

@main @MainActor struct DaynoteApp: App {
    @StateObject private var store = AppStore()
    var body: some Scene {
        WindowGroup("朝夕日历") {
            CalendarScreen().environmentObject(store)
                .tint(.red)
                #if os(macOS)
                .frame(minWidth: 760, minHeight: 640)
                #endif
        }
        #if os(macOS)
        .defaultSize(width: 980, height: 760)
        #endif
    }
}
