import SwiftUI

@MainActor struct TaskEditorScreen: View {
    @EnvironmentObject private var store: AppStore
    @Environment(\.dismiss) private var dismiss
    let record: LocalRecord?
    @State private var data: TaskData
    @State private var scheduled: Bool
    @State private var startDate: Date
    @State private var endDate: Date
    @State private var withEnd: Bool
    @State private var localError: String?
    private let colors = ["blue", "teal", "purple", "orange", "green", "red", "pink", "gray"]
    private let colorNames = ["blue": "蓝", "teal": "青", "purple": "紫", "orange": "橙", "green": "绿", "red": "红", "pink": "粉", "gray": "灰"]
    init(record: LocalRecord?, initialDate: Date? = nil) {
        self.record = record
        var value = record?.data ?? TaskData()
        let start = record?.data.dueDate ?? initialDate ?? Date()
        if record == nil { value.dueAt = DaynoteDates.encode(start, hasTime: true) }
        _data = State(initialValue: value)
        _scheduled = State(initialValue: record == nil ? initialDate != nil : value.dueAt != nil)
        _startDate = State(initialValue: start)
        _endDate = State(initialValue: value.endDate ?? start.addingTimeInterval(3600))
        _withEnd = State(initialValue: value.endAt != nil)
    }
    var body: some View {
        NavigationStack {
            Form {
                Section("日程") {
                    TextField("标题", text: $data.title)
                    TextField("备注（可选）", text: $data.notes, axis: .vertical).lineLimit(3...8)
                    Toggle("截止事项", isOn: $data.isDeadline)
                    if record != nil { Toggle("已完成", isOn: $data.completed) }
                }
                Section("时间") {
                    Toggle("设置日期", isOn: $scheduled)
                    if scheduled {
                        Toggle("全天", isOn: Binding(get: { !data.hasTime }, set: { data.hasTime = !$0 }))
                        DatePicker(data.isDeadline ? "截止时间" : "开始", selection: $startDate,
                                   displayedComponents: data.hasTime ? [.date, .hourAndMinute] : [.date])
                        Toggle("设置结束时间", isOn: $withEnd)
                        if withEnd {
                            DatePicker("结束", selection: $endDate,
                                       displayedComponents: data.hasTime ? [.date, .hourAndMinute] : [.date])
                        }
                        Picker("提醒", selection: $data.reminderMinutes) {
                            Text("不提醒").tag(-1)
                            Text("到时提醒").tag(0)
                            Text("提前 5 分钟").tag(5)
                            Text("提前 10 分钟").tag(10)
                            Text("提前 15 分钟").tag(15)
                            Text("提前 30 分钟").tag(30)
                            Text("提前 1 小时").tag(60)
                            Text("提前 1 天").tag(1440)
                            if ![-1, 0, 5, 10, 15, 30, 60, 1440].contains(data.reminderMinutes) {
                                Text("提前 \(data.reminderMinutes) 分钟").tag(data.reminderMinutes)
                            }
                        }
                        if !data.hasTime, data.reminderMinutes >= 0 {
                            Text("全天事项以当天 00:00 计算提醒时间。").font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }
                Section("颜色") {
                    HStack(spacing: 14) {
                        ForEach(colors, id: \.self) { id in
                            Button { data.colorID = id } label: {
                                Image(systemName: data.colorID == id ? "checkmark.circle.fill" : "circle.fill")
                                    .font(.title2).foregroundStyle(taskColor(id))
                            }.buttonStyle(.plain).accessibilityLabel(colorNames[id] ?? id)
                        }
                    }.padding(.vertical, 4)
                }
                if let localError { Section { Text(localError).foregroundStyle(.red) } }
            }
            .formStyle(.grouped)
            .navigationTitle(record == nil ? "新日程" : "编辑日程")
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("取消") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) { Button("保存", action: save).font(.body.weight(.semibold)) }
            }
            #if os(macOS)
            .frame(width: 540, height: 650)
            #endif
        }
    }
    private func save() {
        var value = data
        value.dueAt = scheduled ? DaynoteDates.encode(startDate, hasTime: value.hasTime) : nil
        value.endAt = scheduled && withEnd ? DaynoteDates.encode(endDate, hasTime: value.hasTime) : nil
        if !scheduled { value.reminderMinutes = -1 }
        if store.save(id: record?.id, data: value, expectedRevision: record?.revision) { dismiss() }
        else { localError = store.errorMessage; store.errorMessage = nil }
    }
}

@MainActor struct SettingsScreen: View {
    @EnvironmentObject private var store: AppStore
    @Environment(\.dismiss) private var dismiss
    @State private var syncCode = ""
    @State private var confirmConnect = false
    @State private var confirmDisconnect = false
    let onExport: () -> Void
    var body: some View {
        NavigationStack {
            Form {
                Section("我的设备") {
                    LabeledContent("日历", value: store.state.accountID == nil ? "尚未配对" : store.state.accountLabel)
                    LabeledContent("待上传", value: "\(store.state.dirtyCount) 项")
                    if let last = store.state.lastSync {
                        LabeledContent("上次同步", value: last.formatted(date: .abbreviated, time: .shortened))
                    }
                    Text(store.status).font(.caption).foregroundStyle(.secondary)
                    if store.connected {
                        Button("立即同步") { Task { await store.sync() } }.disabled(store.busy)
                        Button("断开这台设备", role: .destructive) { confirmDisconnect = true }.disabled(store.busy)
                    } else {
                        SecureField("粘贴你的个人同步码", text: $syncCode)
                            #if os(iOS)
                            .textInputAutocapitalization(.never).autocorrectionDisabled()
                            #endif
                        Button(store.busy ? "正在连接…" : "连接我的日历") { confirmConnect = true }
                            .disabled(store.busy || syncCode.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || !store.canWrite)
                        Text("在自己的 iPhone、Mac 和 Windows 输入同一份同步码。朋友使用另一份同步码。同步码相当于密码，请勿公开或放入截图。")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
                Section("提醒与备份") {
                    LabeledContent("通知", value: store.notificationsEnabled ? "已允许" : "未允许")
                    Button("允许日程提醒") { Task { await store.requestNotifications() } }
                    Button("导出日程备份", action: onExport).disabled(!store.canWrite)
                    Text("设备会安排已同步日程中最近的 60 条未来提醒。iPhone 关闭应用后不会持续同步；打开应用会获取其他设备的新更改，再更新本机提醒。")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Section("关于") {
                    Text("朝夕日历 · 原生互通预览版 2.5.0")
                    Text("离线时仍可编辑，联网后同步。两人各自独立，未设置共享日历。")
                        .font(.caption).foregroundStyle(.secondary)
                    Text("同步期间仍可编辑；发生冲突时保留副本，避免覆盖更改。")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }.formStyle(.grouped)
            .navigationTitle("设备同步")
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("完成") { dismiss() } } }
            .confirmationDialog("连接个人日历？", isPresented: $confirmConnect, titleVisibility: .visible) {
                Button("确认连接并同步") {
                    let code = syncCode; syncCode = ""
                    Task { await store.connect(code) }
                }
                Button("取消", role: .cancel) {}
            } message: {
                Text(store.state.visible.isEmpty ? "连接后将下载该同步码对应的个人日程。" : "本机现有 \(store.state.visible.count) 项日程将加入该同步码对应的个人日历，并同步到你的其他设备。请确认这是你自己的同步码。")
            }
            .confirmationDialog("断开同步？", isPresented: $confirmDisconnect, titleVisibility: .visible) {
                Button("断开", role: .destructive) { store.disconnect() }
                Button("取消", role: .cancel) {}
            } message: { Text("只移除本机保存的同步码，日程保留。本机资料仍属于原账号，重新连接时请使用原来的同步码。") }
            .alert("朝夕日历", isPresented: Binding(get: { store.errorMessage != nil || store.infoMessage != nil }, set: { if !$0 { store.errorMessage = nil; store.infoMessage = nil } })) {
                Button("知道了", role: .cancel) { store.errorMessage = nil; store.infoMessage = nil }
            } message: { Text(store.errorMessage ?? store.infoMessage ?? "") }
            #if os(macOS)
            .frame(width: 540, height: 610)
            #endif
        }
    }
}
