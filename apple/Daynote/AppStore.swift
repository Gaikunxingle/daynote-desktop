import Foundation
import SwiftUI
import Security
import UserNotifications

enum CloudAPI {
    static let endpoint = URL(string: "https://c--ff542ec0-79c6-40d2-9610-34eb964ccd22-prod.lovable.cloud/rest/v1/rpc/daynote_sync_v1")!
    static let publishableKey = "sb_publishable_rlWdj3LzNurnQW9c--QvDA_udvomPd1"
    private struct Request: Encodable {
        let p_key: String
        let p_device: String
        let p_changes: [Mutation]
    }
    static func sync(key: String, device: String, changes: [Mutation]) async throws -> SyncResponse {
        var request = URLRequest(url: endpoint, timeoutInterval: 25)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue(publishableKey, forHTTPHeaderField: "apikey")
        request.httpBody = try JSONEncoder().encode(Request(p_key: key, p_device: device, p_changes: changes))
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw SyncError.serviceUnavailable }
        guard (200..<300).contains(http.statusCode) else {
            if [401, 403].contains(http.statusCode) { throw SyncError.invalidKey }
            throw SyncError.serviceUnavailable
        }
        guard data.count < 32 * 1024 * 1024 else { throw SyncError.invalidResponse }
        do { return try JSONDecoder().decode(SyncResponse.self, from: data) }
        catch { throw SyncError.invalidResponse }
    }
}

enum SyncKeychain {
    private static var service: String { (Bundle.main.bundleIdentifier ?? "app.youmu.daynote") + ".sync" }
    private static var query: [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: "private-sync-key"]
    }
    static func read() throws -> String? {
        var q = query; q[kSecReturnData as String] = true; q[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        let status = SecItemCopyMatching(q as CFDictionary, &item)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = item as? Data, let key = String(data: data, encoding: .utf8) else {
            throw NSError(domain: "DaynoteKeychain", code: Int(status), userInfo: [NSLocalizedDescriptionKey: "无法读取系统钥匙串。解锁设备后重试。"])
        }
        return key
    }
    static func save(_ key: String) throws {
        let data = Data(key.utf8)
        let update = SecItemUpdate(query as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        if update == errSecSuccess { return }
        guard update == errSecItemNotFound else { throw keychainError(update) }
        var q = query; q[kSecValueData as String] = data
        #if os(iOS)
        q[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        #endif
        let status = SecItemAdd(q as CFDictionary, nil)
        guard status == errSecSuccess else { throw keychainError(status) }
    }
    static func remove() throws {
        let status = SecItemDelete(query as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw keychainError(status) }
    }
    private static func keychainError(_ status: OSStatus) -> NSError {
        NSError(domain: "DaynoteKeychain", code: Int(status), userInfo: [NSLocalizedDescriptionKey: "系统钥匙串暂时不可用，请解锁设备后重试。"])
    }
}

final class NoticeDelegate: NSObject, UNUserNotificationCenterDelegate {
    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .sound, .list])
    }
}

@MainActor final class AppStore: ObservableObject {
    @Published private(set) var state = NativeState()
    @Published private(set) var connected = false
    @Published private(set) var busy = false
    @Published private(set) var status = "本机日历"
    @Published private(set) var canWrite = true
    @Published var errorMessage: String?
    @Published var infoMessage: String?
    @Published var notificationsEnabled = false
    private var key: String?
    private let fileURL: URL
    private let noticeDelegate = NoticeDelegate()
    private var debounce: Task<Void, Never>?
    private var active = false
    private var notificationGeneration = 0
    private var notificationWorkerRunning = false

    init() {
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        fileURL = support.appendingPathComponent("DaynoteNative", isDirectory: true).appendingPathComponent("calendar.json")
        do {
            try FileManager.default.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
            if FileManager.default.fileExists(atPath: fileURL.path) {
                do { state = try Self.readState(fileURL) }
                catch {
                    if let restored = try? Self.readState(fileURL.appendingPathExtension("bak")) {
                        state = restored
                        infoMessage = "主文件无法读取，已从本机备份恢复。请导出一份备份，并检查最近的事项。"
                    } else {
                        canWrite = false
                        errorMessage = "本机日历文件损坏，已停止写入以保护原文件。请从备份恢复，勿删除应用。"
                    }
                }
            }
        } catch { canWrite = false; errorMessage = error.localizedDescription }
        // Credential availability is independent of local-data integrity. A locked or
        // unavailable Keychain must not disable offline editing on either platform.
        if canWrite {
            do {
                key = try SyncKeychain.read(); connected = key != nil
                status = connected ? "等待同步" : "本机日历"
            } catch {
                key = nil; connected = false
                status = "暂时无法读取同步凭据，可先本机编辑；解锁设备后重新连接。"
            }
        } else {
            key = nil; connected = false
            status = "本机资料已保护，暂停写入与同步"
        }
        UNUserNotificationCenter.current().delegate = noticeDelegate
    }

    private static func readState(_ url: URL) throws -> NativeState {
        let data = try Data(contentsOf: url)
        guard data.count <= 32 * 1024 * 1024 else { throw SyncError.invalidResponse }
        let result = try JSONDecoder().decode(NativeState.self, from: data)
        guard result.format == 1,
              result.records.allSatisfy({ $0.key == $0.value.id }),
              Set(result.pending.map { $0.mutation.id }).count == result.pending.count else { throw SyncError.invalidResponse }
        return result
    }
    private func commit(_ next: NativeState) throws {
        guard canWrite else { throw SyncError.disk("本机日历处于保护状态，暂时不能保存。") }
        let encoded = try JSONEncoder().encode(next)
        guard encoded.count <= 32 * 1024 * 1024 else { throw SyncError.disk("本机资料已达到预览版容量限制，请先导出备份并减少较长备注。") }
        // Keep the previous valid state; do not overwrite a recovery backup with a corrupt file.
        if let old = try? Data(contentsOf: fileURL), (try? Self.readState(fileURL)) != nil {
            try old.write(to: fileURL.appendingPathExtension("bak"), options: .atomic)
        }
        try encoded.write(to: fileURL, options: .atomic)
        #if os(iOS)
        try? FileManager.default.setAttributes([.protectionKey: FileProtectionType.completeUntilFirstUserAuthentication], ofItemAtPath: fileURL.path)
        try? FileManager.default.setAttributes([.protectionKey: FileProtectionType.completeUntilFirstUserAuthentication], ofItemAtPath: fileURL.appendingPathExtension("bak").path)
        #endif
        state = next
    }

    @discardableResult func save(id: String?, data: TaskData, expectedRevision: Int?) -> Bool {
        var data = data; data.title = data.title.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !data.title.isEmpty, data.title.unicodeScalars.count <= 200, data.notes.unicodeScalars.count <= 20000 else {
            errorMessage = "请填写标题（最多 200 字），备注最多 20000 字。"; return false
        }
        if let start = data.dueDate, let end = data.endDate, end < start { errorMessage = "结束时间不能早于开始时间。"; return false }
        do {
            var next = state
            let copied = next.edit(id: id, data: data, expectedRevision: expectedRevision)
            try commit(next)
            if copied { infoMessage = "编辑期间该事项已有更新，你的内容已另存为“编辑副本”。" }
            changed(); return true
        } catch { errorMessage = error.localizedDescription; return false }
    }
    func toggle(_ id: String) {
        guard let r = state.records[id], !r.deleted else { return }
        var data = r.data; data.completed.toggle()
        _ = save(id: id, data: data, expectedRevision: r.revision)
    }
    func delete(_ id: String) {
        do { var next = state; next.delete(id); try commit(next); changed() }
        catch { errorMessage = error.localizedDescription }
    }
    private func changed() {
        status = connected ? "更改已保存，等待同步" : "已保存在本机"
        scheduleNotifications()
        scheduleSync()
    }
    func setActive(_ value: Bool) {
        active = value
        if value { Task { await refreshNotificationPermission(); await sync() } }
        else { debounce?.cancel() }
    }
    func scheduleSync() {
        guard connected, active else { return }
        debounce?.cancel()
        debounce = Task {
            try? await Task.sleep(nanoseconds: 900_000_000)
            guard !Task.isCancelled else { return }
            await sync()
        }
    }
    func connect(_ enteredKey: String) async {
        guard !busy, canWrite else { return }
        let proposed = enteredKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard proposed.count >= 32, proposed.count <= 256 else { errorMessage = "请粘贴完整的个人同步码。"; return }
        busy = true; status = "正在验证同步码"
        do {
            // Handshake contains no personal data and must succeed before any upload.
            let response = try await CloudAPI.sync(key: proposed, device: state.deviceID, changes: [])
            guard state.accountID == nil || state.accountID == response.accountID else { throw SyncError.accountMismatch }
            var next = state
            let conflicts = try next.merge(response, sent: [])
            try commit(next)
            try SyncKeychain.save(proposed)
            key = proposed; connected = true; status = "已连接：\(response.accountLabel)"
            if conflicts > 0 { infoMessage = "有 \(conflicts) 项更改发生冲突，本地编辑已保留为副本；删除冲突保留云端版本。" }
            scheduleNotifications()
        } catch { status = "连接未完成"; errorMessage = safeMessage(error) }
        busy = false
        if connected { await sync() }
    }
    func disconnect() {
        guard !busy else { return }
        do {
            try SyncKeychain.remove(); key = nil; connected = false; debounce?.cancel()
            status = "已断开同步，本机日程保留"
        } catch { errorMessage = error.localizedDescription }
    }
    func sync() async {
        guard let currentKey = key, connected, !busy, canWrite else { return }
        busy = true; status = "正在同步"
        do {
            var prepared = state
            let batch = try prepared.prepareBatch()
            try commit(prepared)
            let response = try await CloudAPI.sync(key: currentKey, device: state.deviceID, changes: batch)
            // Start from current state, not the pre-request snapshot: UI edits may have occurred meanwhile.
            var next = state
            let conflicts = try next.merge(response, sent: batch)
            try commit(next)
            status = next.dirtyCount == 0 ? "已同步" : "已同步，另有 \(next.dirtyCount) 项待上传"
            if conflicts > 0 { infoMessage = "有 \(conflicts) 项更改发生冲突。本地编辑已保留为“冲突副本”；删除冲突保留了云端新版本。" }
            scheduleNotifications()
        } catch { status = safeMessage(error) }
        busy = false
        if state.dirtyCount > 0, status.hasPrefix("已同步") { scheduleSync() }
    }
    private func safeMessage(_ error: Error) -> String {
        if error is URLError { return SyncError.serviceUnavailable.localizedDescription }
        return error.localizedDescription
    }

    func refreshNotificationPermission() async {
        let s = await UNUserNotificationCenter.current().notificationSettings()
        notificationsEnabled = s.authorizationStatus == .authorized || s.authorizationStatus == .provisional
        scheduleNotifications()
    }
    func requestNotifications() async {
        do {
            _ = try await UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound, .badge])
            await refreshNotificationPermission()
            if !notificationsEnabled { infoMessage = "通知尚未开启，可在系统设置中允许“朝夕日历”发送通知。" }
        } catch { errorMessage = "无法申请通知权限，请在系统设置中检查。" }
    }
    private func scheduleNotifications() {
        notificationGeneration += 1
        guard !notificationWorkerRunning else { return }
        notificationWorkerRunning = true
        Task {
            defer { notificationWorkerRunning = false }
            let center = UNUserNotificationCenter.current()
            var processedGeneration = -1
            while processedGeneration != notificationGeneration {
            let generation = notificationGeneration
            let snapshot = state.visible
            let permission = await center.notificationSettings()
            if generation != notificationGeneration { continue }
            guard permission.authorizationStatus == .authorized || permission.authorizationStatus == .provisional else { return }
            let now = Date()
            let upcoming = snapshot.compactMap { r -> (LocalRecord, Date)? in
                guard let fire = r.data.reminderFireDate() else { return nil }
                return fire > now ? (r, fire) : nil
            }.sorted { $0.1 < $1.1 }.prefix(60)
            center.removeAllPendingNotificationRequests()
            for (r, fire) in upcoming {
                if generation != notificationGeneration { break }
                let content = UNMutableNotificationContent()
                content.title = r.data.title
                content.body = r.data.notes.isEmpty ? "朝夕日历 · 日程提醒" : String(r.data.notes.prefix(160))
                content.sound = .default
                let components = Calendar.current.dateComponents([.year, .month, .day, .hour, .minute, .second], from: fire)
                let request = UNNotificationRequest(identifier: "daynote." + r.id, content: content,
                    trigger: UNCalendarNotificationTrigger(dateMatching: components, repeats: false))
                do { try await center.add(request) }
                catch { status = "日程已保存，但部分通知未能安排。请检查系统通知设置。" }
            }
            processedGeneration = generation
            }
        }
    }
    func exportData() throws -> Data {
        // A portable backup deliberately omits the sync key, account binding, and queued request IDs.
        let rows = state.visible.sorted { $0.id < $1.id }.map { CloudRecord(id: $0.id, version: 1, data: $0.data) }
        let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return try encoder.encode(rows)
    }
}
