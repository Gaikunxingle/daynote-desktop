import SwiftUI
import UniformTypeIdentifiers

enum TaskFilter: String, CaseIterable, Identifiable {
    case day = "当天", all = "全部", unscheduled = "待安排", deadlines = "截止", completed = "已完成"
    var id: String { rawValue }
}
struct EditSelection: Identifiable {
    let id = UUID()
    var record: LocalRecord?
    var date: Date?
}

@MainActor struct CalendarScreen: View {
    @EnvironmentObject private var store: AppStore
    @Environment(\.scenePhase) private var scenePhase
    @State private var selectedDate = Date()
    @State private var displayedMonth = Date()
    @State private var filter = TaskFilter.day
    @State private var editor: EditSelection?
    @State private var showSettings = false
    @State private var deleting: LocalRecord?
    @State private var exportDocument = CalendarBackup(data: Data())
    @State private var exporting = false
    @State private var pendingExport = false
    private let calendar = Calendar.current
    private let refresh = Timer.publish(every: 30, on: .main, in: .common).autoconnect()
    private var visibleItems: [LocalRecord] {
        store.state.visible.filter { r in
            switch filter {
            case .day: return r.data.dueDate.map { calendar.isDate($0, inSameDayAs: selectedDate) } ?? false
            case .all: return !r.data.completed
            case .unscheduled: return r.data.dueAt == nil && !r.data.completed
            case .deadlines: return r.data.isDeadline && !r.data.completed
            case .completed: return r.data.completed
            }
        }.sorted {
            if $0.data.completed != $1.data.completed { return !$0.data.completed }
            let left = $0.data.dueDate ?? .distantFuture, right = $1.data.dueDate ?? .distantFuture
            return left == right ? $0.data.title.localizedStandardCompare($1.data.title) == .orderedAscending : left < right
        }
    }
    var body: some View {
        NavigationStack {
            GeometryReader { geometry in
                Group {
                    if geometry.size.width >= 760 {
                        HStack(alignment: .top, spacing: 24) {
                            calendarPanel.frame(width: min(420, geometry.size.width * 0.45))
                            Divider()
                            agendaPanel
                        }.padding(24)
                    } else {
                        ScrollView {
                            VStack(spacing: 20) { calendarPanel; agendaPanel }
                                .padding(.horizontal, 18).padding(.vertical, 16)
                        }
                    }
                }
            }
            .navigationTitle("朝夕日历")
            .toolbar {
                ToolbarItemGroup(placement: .primaryAction) {
                    Button { selectedDate = Date(); displayedMonth = Date(); filter = .day } label: { Text("今天") }
                    Button { editor = EditSelection(date: selectedDate) } label: { Image(systemName: "plus") }
                        .accessibilityLabel("添加日程").disabled(!store.canWrite)
                    Button { showSettings = true } label: { Image(systemName: "person.crop.circle") }
                        .accessibilityLabel("设备同步与设置")
                }
            }
            .safeAreaInset(edge: .bottom) { statusBar }
            .sheet(item: $editor) { selection in
                TaskEditorScreen(record: selection.record, initialDate: selection.date).environmentObject(store)
            }
            .sheet(isPresented: $showSettings, onDismiss: {
                if pendingExport { pendingExport = false; exporting = true }
            }) {
                SettingsScreen(onExport: {
                    do { exportDocument = CalendarBackup(data: try store.exportData()); pendingExport = true; showSettings = false }
                    catch { store.errorMessage = error.localizedDescription }
                }).environmentObject(store)
            }
            .confirmationDialog("删除这项日程？", isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } }), titleVisibility: .visible) {
                Button("删除日程", role: .destructive) { if let r = deleting { store.delete(r.id) }; deleting = nil }
                Button("取消", role: .cancel) { deleting = nil }
            } message: { Text(deleting?.data.title ?? "") }
            .alert("朝夕日历", isPresented: Binding(get: { !showSettings && editor == nil && (store.errorMessage != nil || store.infoMessage != nil) }, set: { if !$0 { store.errorMessage = nil; store.infoMessage = nil } })) {
                Button("知道了", role: .cancel) { store.errorMessage = nil; store.infoMessage = nil }
            } message: { Text(store.errorMessage ?? store.infoMessage ?? "") }
            .fileExporter(isPresented: $exporting, document: exportDocument, contentType: .json,
                          defaultFilename: "朝夕日历备份_" + Date().formatted(.dateTime.year().month().day())) { result in
                if case .failure(let error) = result { store.errorMessage = error.localizedDescription }
            }
            .onAppear { store.setActive(scenePhase == .active) }
            .onChange(of: scenePhase) { store.setActive($0 == .active) }
            .onReceive(refresh) { _ in if scenePhase == .active { Task { await store.sync() } } }
        }
    }
    private var calendarPanel: some View {
        VStack(spacing: 18) {
            HStack {
                VStack(alignment: .leading, spacing: 3) {
                    Text(displayedMonth.formatted(.dateTime.year().month(.wide))).font(.title2.bold())
                    Text("把时间留给重要的事").font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Button { moveMonth(-1) } label: { Image(systemName: "chevron.left").frame(width: 28, height: 28) }.accessibilityLabel("上个月")
                Button { moveMonth(1) } label: { Image(systemName: "chevron.right").frame(width: 28, height: 28) }.accessibilityLabel("下个月")
            }
            LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 4), count: 7), spacing: 9) {
                ForEach(Array(["一", "二", "三", "四", "五", "六", "日"].enumerated()), id: \.offset) { _, label in
                    Text(label).font(.caption).foregroundStyle(.secondary).frame(maxWidth: .infinity)
                }
                ForEach(Array(monthDates.enumerated()), id: \.offset) { _, date in dayCell(date) }
            }
            Divider()
            HStack(spacing: 18) {
                Label("\(store.state.visible.filter { !$0.data.completed }.count) 项待办", systemImage: "circle")
                Label("\(store.state.visible.filter { $0.data.completed }.count) 项完成", systemImage: "checkmark.circle.fill")
            }.font(.caption).foregroundStyle(.secondary).frame(maxWidth: .infinity, alignment: .leading)
        }
    }
    private var monthDates: [Date] {
        let start = calendar.date(from: calendar.dateComponents([.year, .month], from: displayedMonth))!
        let weekday = (calendar.component(.weekday, from: start) + 5) % 7
        let count = calendar.range(of: .day, in: .month, for: start)!.count
        let slots = ((weekday + count + 6) / 7) * 7
        return (0..<slots).map { calendar.date(byAdding: .day, value: $0 - weekday, to: start)! }
    }
    private func dayCell(_ date: Date) -> some View {
        let selected = calendar.isDate(date, inSameDayAs: selectedDate)
        let thisMonth = calendar.isDate(date, equalTo: displayedMonth, toGranularity: .month)
        let hasTasks = store.state.visible.contains { $0.data.dueDate.map { calendar.isDate($0, inSameDayAs: date) } ?? false }
        return Button {
            selectedDate = date; filter = .day
            if !thisMonth { displayedMonth = date }
        } label: {
            VStack(spacing: 4) {
                Text("\(calendar.component(.day, from: date))").font(.body.weight(calendar.isDateInToday(date) ? .bold : .regular))
                Circle().fill(hasTasks ? (selected ? Color.white : Color.red) : Color.clear).frame(width: 4, height: 4)
            }
            .frame(maxWidth: .infinity).frame(height: 40)
            .foregroundStyle(selected ? Color.white : (thisMonth ? Color.primary : Color.secondary.opacity(0.5)))
            .background(selected ? Color.red : Color.clear, in: RoundedRectangle(cornerRadius: 11))
            .overlay(RoundedRectangle(cornerRadius: 11).stroke(calendar.isDateInToday(date) && !selected ? Color.red.opacity(0.4) : Color.clear))
        }.buttonStyle(.plain).accessibilityLabel(date.formatted(date: .complete, time: .omitted) + (hasTasks ? "，有日程" : ""))
    }
    private var agendaPanel: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                Text(filter == .day ? selectedDate.formatted(.dateTime.month().day().weekday()) : filter.rawValue).font(.headline)
                Spacer()
                Text("\(visibleItems.count) 项").font(.caption).foregroundStyle(.secondary)
            }
            Picker("日程范围", selection: $filter) {
                ForEach(TaskFilter.allCases) { Text($0.rawValue).tag($0) }
            }.pickerStyle(.segmented)
            if visibleItems.isEmpty {
                VStack(spacing: 10) {
                    Image(systemName: "calendar.badge.checkmark").font(.system(size: 32)).foregroundStyle(.red.opacity(0.7))
                    Text("这里还没有日程").font(.headline)
                    Text("点右上角 +，记下下一件事。").font(.subheadline).foregroundStyle(.secondary)
                }.frame(maxWidth: .infinity).padding(.vertical, 42)
            } else {
                ScrollView {
                    LazyVStack(spacing: 10) {
                        ForEach(visibleItems) { record in
                            taskRow(record).contextMenu {
                                Button("编辑") { editor = EditSelection(record: record) }
                                Button(record.data.completed ? "恢复待办" : "标记完成") { store.toggle(record.id) }
                                Button("删除", role: .destructive) { deleting = record }
                            }
                        }
                    }
                }.frame(minHeight: min(CGFloat(visibleItems.count) * 90, 300))
            }
        }.frame(maxWidth: .infinity, alignment: .topLeading)
    }
    private func taskRow(_ r: LocalRecord) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Button { store.toggle(r.id) } label: {
                Image(systemName: r.data.completed ? "checkmark.circle.fill" : "circle")
                    .font(.title3).foregroundStyle(r.data.completed ? Color.secondary : taskColor(r.data.colorID))
            }.buttonStyle(.plain).accessibilityLabel(r.data.completed ? "恢复待办" : "完成事项")
            VStack(alignment: .leading, spacing: 5) {
                Text(r.data.title).font(.body.weight(.medium)).strikethrough(r.data.completed).foregroundStyle(r.data.completed ? .secondary : .primary)
                HStack(spacing: 6) {
                    if r.data.isDeadline { Text("截止").foregroundStyle(.orange) }
                    Text(timeLabel(r.data)).foregroundStyle(.secondary)
                    if r.data.reminderMinutes >= 0 { Image(systemName: "bell").foregroundStyle(.secondary) }
                }.font(.caption)
                if !r.data.notes.isEmpty { Text(r.data.notes).font(.caption).foregroundStyle(.secondary).lineLimit(2) }
            }.frame(maxWidth: .infinity, alignment: .leading)
            Image(systemName: "chevron.right").font(.caption2).foregroundStyle(.tertiary)
        }
        .padding(14).background(taskColor(r.data.colorID).opacity(0.065), in: RoundedRectangle(cornerRadius: 14))
        .contentShape(Rectangle()).onTapGesture { editor = EditSelection(record: r) }
    }
    private var statusBar: some View {
        HStack(spacing: 8) {
            if store.busy { ProgressView().controlSize(.small) }
            else { Image(systemName: store.connected ? "arrow.triangle.2.circlepath.cloud" : "internaldrive") }
            Text(store.status).lineLimit(2)
            Spacer(minLength: 4)
            if store.connected {
                Button("同步") { Task { await store.sync() } }.disabled(store.busy)
            } else { Button("连接设备") { showSettings = true } }
        }.font(.caption).padding(.horizontal, 18).padding(.vertical, 10).background(.regularMaterial)
    }
    private func moveMonth(_ delta: Int) { displayedMonth = calendar.date(byAdding: .month, value: delta, to: displayedMonth)! }
}

func taskColor(_ id: String) -> Color {
    switch id { case "teal": return .teal; case "purple": return .purple; case "orange": return .orange
    case "green": return .green; case "red": return .red; case "pink": return .pink; case "gray": return .gray; default: return .blue }
}
func timeLabel(_ data: TaskData) -> String {
    guard let due = data.dueDate else { return "未安排日期" }
    if !data.hasTime { return due.formatted(.dateTime.month().day()) + " · 全天" }
    var text = due.formatted(.dateTime.month().day().hour().minute())
    if let end = data.endDate { text += " – " + end.formatted(.dateTime.hour().minute()) }
    return text
}
struct CalendarBackup: FileDocument {
    static var readableContentTypes: [UTType] { [.json] }
    var data: Data
    init(data: Data) { self.data = data }
    init(configuration: ReadConfiguration) throws { data = configuration.file.regularFileContents ?? Data() }
    func fileWrapper(configuration: WriteConfiguration) throws -> FileWrapper { FileWrapper(regularFileWithContents: data) }
}
