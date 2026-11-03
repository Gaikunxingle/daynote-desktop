import Foundation

public func daynoteID() -> String { UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased() }

private func copyTitle(_ original: String, suffix: String) -> String {
    var prefix = ""
    var count = 0
    let limit = 200 - suffix.unicodeScalars.count
    for character in original {
        let next = character.unicodeScalars.count
        if count + next > limit { break }
        prefix.append(character); count += next
    }
    return prefix + suffix
}

public struct TaskData: Codable, Equatable {
    public var title = ""
    public var notes = ""
    public var dueAt: String?
    public var hasTime = true
    public var reminderMinutes = -1
    public var completed = false
    public var endAt: String?
    public var colorID = "blue"
    public var isDeadline = false
    public init() {}
    enum CodingKeys: String, CodingKey {
        case title, notes, completed
        case dueAt = "due_at", hasTime = "has_time", reminderMinutes = "reminder_minutes"
        case endAt = "end_at", colorID = "color_id", isDeadline = "is_deadline"
    }
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        title = try c.decodeIfPresent(String.self, forKey: .title) ?? ""
        notes = try c.decodeIfPresent(String.self, forKey: .notes) ?? ""
        dueAt = try c.decodeIfPresent(String.self, forKey: .dueAt)
        hasTime = try c.decodeIfPresent(Bool.self, forKey: .hasTime) ?? true
        reminderMinutes = try c.decodeIfPresent(Int.self, forKey: .reminderMinutes) ?? -1
        completed = try c.decodeIfPresent(Bool.self, forKey: .completed) ?? false
        endAt = try c.decodeIfPresent(String.self, forKey: .endAt)
        colorID = try c.decodeIfPresent(String.self, forKey: .colorID) ?? "blue"
        isDeadline = try c.decodeIfPresent(Bool.self, forKey: .isDeadline) ?? false
    }
    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(title, forKey: .title); try c.encode(notes, forKey: .notes)
        if let dueAt { try c.encode(dueAt, forKey: .dueAt) } else { try c.encodeNil(forKey: .dueAt) }
        if let endAt { try c.encode(endAt, forKey: .endAt) } else { try c.encodeNil(forKey: .endAt) }
        try c.encode(hasTime, forKey: .hasTime); try c.encode(reminderMinutes, forKey: .reminderMinutes)
        try c.encode(completed, forKey: .completed); try c.encode(colorID, forKey: .colorID)
        try c.encode(isDeadline, forKey: .isDeadline)
    }
    public var dueDate: Date? { DaynoteDates.decode(dueAt, hasTime: hasTime) }
    public var endDate: Date? { DaynoteDates.decode(endAt, hasTime: hasTime) }
    public func reminderFireDate() -> Date? {
        guard !completed, reminderMinutes >= 0, let due = dueDate else { return nil }
        // Match the Windows ReminderEngine: all-day reminders are anchored at local 09:00.
        let effectiveDue = hasTime ? due : Calendar.current.date(bySettingHour: 9, minute: 0, second: 0, of: due)
        return effectiveDue?.addingTimeInterval(-Double(reminderMinutes) * 60)
    }
}

public enum DaynoteDates {
    public static func encode(_ date: Date?, hasTime: Bool) -> String? {
        guard let date else { return nil }
        if hasTime { return ISO8601DateFormatter().string(from: date) }
        let f = DateFormatter(); f.locale = Locale(identifier: "en_US_POSIX"); f.calendar = Calendar(identifier: .gregorian)
        f.timeZone = .current; f.dateFormat = "yyyy-MM-dd"
        return f.string(from: date)
    }
    public static func decode(_ value: String?, hasTime: Bool) -> Date? {
        guard let value else { return nil }
        if hasTime {
            let f = ISO8601DateFormatter()
            if let d = f.date(from: value) { return d }
            f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
            return f.date(from: value)
        }
        let f = DateFormatter(); f.locale = Locale(identifier: "en_US_POSIX"); f.calendar = Calendar(identifier: .gregorian)
        f.timeZone = .current; f.dateFormat = "yyyy-MM-dd"; f.isLenient = false
        return f.date(from: value)
    }
}

public struct LocalRecord: Codable, Identifiable, Equatable {
    public var id: String
    public var data: TaskData
    public var deleted: Bool
    public var version: Int
    public var revision: Int
    public var dirty: Bool
    public init(id: String = daynoteID(), data: TaskData, deleted: Bool = false, version: Int = 0, revision: Int = 1, dirty: Bool = true) {
        self.id = id; self.data = data; self.deleted = deleted; self.version = version; self.revision = revision; self.dirty = dirty
    }
}

public struct CloudRecord: Codable, Equatable {
    public var id: String
    public var version: Int
    public var deleted: Bool
    public var data: TaskData
    public var updatedAt: String?
    enum CodingKeys: String, CodingKey { case id, version, deleted, data; case updatedAt = "updated_at" }
    public init(id: String, version: Int, deleted: Bool = false, data: TaskData) {
        self.id = id; self.version = version; self.deleted = deleted; self.data = data
    }
}

public struct Mutation: Codable, Equatable {
    public var id: String
    public var mutationID: String
    public var baseVersion: Int
    public var deleted: Bool
    public var data: TaskData
    enum CodingKeys: String, CodingKey { case id, deleted, data; case mutationID = "mutation_id", baseVersion = "base_version" }
    public init(record: LocalRecord) {
        id = record.id; mutationID = daynoteID(); baseVersion = record.version; deleted = record.deleted; data = record.data
    }
}
public struct PendingMutation: Codable, Equatable {
    public var mutation: Mutation
    public var localRevision: Int
}

public struct MutationResult: Codable {
    public var id: String
    public var mutationID: String
    public var status: String
    public var version: Int
    enum CodingKeys: String, CodingKey { case id, status, version; case mutationID = "mutation_id" }
    public init(mutation: Mutation, status: String, version: Int) {
        id = mutation.id; mutationID = mutation.mutationID; self.status = status; self.version = version
    }
}

public struct SyncResponse: Codable {
    public var accountID: String
    public var accountLabel: String
    public var serverTime: String
    public var results: [MutationResult]
    public var items: [CloudRecord]
    enum CodingKeys: String, CodingKey {
        case results, items
        case accountID = "account_id", accountLabel = "account_label", serverTime = "server_time"
    }
    public init(accountID: String, results: [MutationResult] = [], items: [CloudRecord] = []) {
        self.accountID = accountID; accountLabel = "我的日历"; serverTime = ""; self.results = results; self.items = items
    }
}

public enum SyncError: LocalizedError {
    case accountMismatch, invalidResponse, disk(String), invalidKey, serviceUnavailable
    public var errorDescription: String? {
        switch self {
        case .accountMismatch: return "这份本地日历已绑定另一个同步账号。请使用原来的同步码，避免混入他人的日程。"
        case .invalidResponse: return "云端返回的数据不完整，本机日程已保留。"
        case .disk(let message): return "本机保存失败：\(message)"
        case .invalidKey: return "同步码无效或已停用，请核对后重试。本机日程不会删除。"
        case .serviceUnavailable: return "暂时无法连接云端。更改已留在本机，联网后可再次同步。"
        }
    }
}

public struct NativeState: Codable, Equatable {
    public var format = 1
    public var deviceID = daynoteID()
    public var accountID: String?
    public var accountLabel = "未连接"
    public var records: [String: LocalRecord] = [:]
    public var pending: [PendingMutation] = []
    public var lastSync: Date?
    public init() {}
    public var visible: [LocalRecord] { records.values.filter { !$0.deleted } }
    public var dirtyCount: Int { records.values.filter(\.dirty).count }

    public mutating func edit(id: String?, data: TaskData, expectedRevision: Int? = nil) -> Bool {
        if let id, var old = records[id] {
            if let expectedRevision, expectedRevision != old.revision {
                var copy = data; copy.title = copyTitle(copy.title, suffix: "（编辑副本）")
                let r = LocalRecord(data: copy); records[r.id] = r; return true
            }
            guard old.data != data || old.deleted else { return false }
            old.data = data; old.deleted = false; old.dirty = true; old.revision += 1; records[id] = old
        } else {
            let r = LocalRecord(data: data); records[r.id] = r
        }
        return false
    }
    public mutating func delete(_ id: String) {
        guard var r = records[id], !r.deleted else { return }
        r.deleted = true; r.dirty = true; r.revision += 1; records[id] = r
    }
    public mutating func prepareBatch() throws -> [Mutation] {
        let queuedIDs = Set(pending.map { $0.mutation.id })
        for r in records.values.sorted(by: { $0.id < $1.id }) where r.dirty && !queuedIDs.contains(r.id) {
            pending.append(PendingMutation(mutation: Mutation(record: r), localRevision: r.revision))
        }
        // Persist the returned mutations before sending. Never mutate an unacknowledged request.
        var batch: [Mutation] = []
        var bytes = 1024
        for item in pending.prefix(100) {
            let count = try JSONEncoder().encode(item.mutation).count + 1
            if bytes + count > 850_000 { break }
            batch.append(item.mutation); bytes += count
        }
        return batch
    }

    @discardableResult public mutating func merge(_ response: SyncResponse, sent: [Mutation]) throws -> Int {
        guard accountID == nil || accountID == response.accountID else { throw SyncError.accountMismatch }
        guard Set(response.items.map(\.id)).count == response.items.count,
              response.items.allSatisfy({ $0.version > 0 }),
              Set(response.results.map(\.mutationID)).count == response.results.count else { throw SyncError.invalidResponse }
        let remote = Dictionary(uniqueKeysWithValues: response.items.map { ($0.id, $0) })
        let sentIDs = Set(sent.map(\.mutationID))
        guard response.results.count == sent.count,
              response.results.allSatisfy({ sentIDs.contains($0.mutationID) && ["applied", "conflict"].contains($0.status) }) else {
            throw SyncError.invalidResponse
        }
        var conflicts = 0
        var handled = Set<String>()
        for result in response.results {
            guard let p = pending.first(where: { $0.mutation.mutationID == result.mutationID }),
                  p.mutation.id == result.id else { throw SyncError.invalidResponse }
            if result.status == "applied" {
                guard let server = remote[result.id], server.version >= result.version, result.version > 0 else { throw SyncError.invalidResponse }
                if var local = records[result.id] {
                    if local.revision == p.localRevision {
                        adopt(server)
                    } else if server.version == result.version {
                        // New local changes made during HTTP stay dirty and are based on the accepted version.
                        local.version = server.version; local.dirty = true; records[local.id] = local
                    } else {
                        conflicts += preserveConflict(local, remote: server)
                    }
                } else { adopt(server) }
            } else {
                if let local = records[result.id] { conflicts += preserveConflict(local, remote: remote[result.id]) }
                else if let server = remote[result.id] { adopt(server) }
            }
            pending.removeAll { $0.mutation.mutationID == result.mutationID }
            handled.insert(result.id)
        }
        let stillPending = Set(pending.map { $0.mutation.id })
        for server in response.items where !handled.contains(server.id) {
            guard !stillPending.contains(server.id) else { continue }
            if let local = records[server.id], local.dirty {
                if server.version > local.version { conflicts += preserveConflict(local, remote: server) }
            } else if (records[server.id]?.version ?? 0) <= server.version { adopt(server) }
        }
        accountID = response.accountID; accountLabel = response.accountLabel; lastSync = Date()
        return conflicts
    }
    private mutating func adopt(_ server: CloudRecord) {
        let old = records[server.id]
        let changed = old?.data != server.data || old?.deleted != server.deleted
        records[server.id] = LocalRecord(id: server.id, data: server.data, deleted: server.deleted,
            version: server.version, revision: (old?.revision ?? 0) + (changed ? 1 : 0), dirty: false)
    }
    private mutating func preserveConflict(_ local: LocalRecord, remote: CloudRecord?) -> Int {
        if !local.deleted {
            var data = local.data; data.title = copyTitle(data.title, suffix: "（冲突副本）")
            let copy = LocalRecord(data: data); records[copy.id] = copy
        }
        if let remote { adopt(remote) } else { records.removeValue(forKey: local.id) }
        return 1
    }
}
