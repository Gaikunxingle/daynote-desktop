import XCTest
@testable import DaynoteCore

final class SyncTests: XCTestCase {
    private func task(_ title: String) -> TaskData { var d = TaskData(); d.title = title; return d }
    private func bound() -> NativeState { var s = NativeState(); s.accountID = "owner-a"; return s }
    private func response(_ mutation: Mutation, version: Int = 1, status: String = "applied", cloudData: TaskData? = nil) -> SyncResponse {
        SyncResponse(accountID: "owner-a", results: [MutationResult(mutation: mutation, status: status, version: version)],
                     items: [CloudRecord(id: mutation.id, version: version, deleted: mutation.deleted, data: cloudData ?? mutation.data)])
    }
    func testDatesRemainExplicitNullAndAllDayStaysCalendarDate() throws {
        let encoded = try JSONEncoder().encode(task("未安排"))
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: encoded) as? [String: Any])
        XCTAssertTrue(object["due_at"] is NSNull); XCTAssertTrue(object["end_at"] is NSNull)
        let date = try XCTUnwrap(DaynoteDates.decode("2026-11-03", hasTime: false))
        XCTAssertEqual(DaynoteDates.encode(date, hasTime: false), "2026-11-03")
        XCTAssertNotNil(DaynoteDates.decode("2026-11-03T14:30:00+08:00", hasTime: true))
    }
    func testNetworkRetryKeepsImmutableMutation() throws {
        var s = bound(); _ = s.edit(id: nil, data: task("第一次"))
        let first = try XCTUnwrap(s.prepareBatch().first)
        let restored = try JSONDecoder().decode(NativeState.self, from: JSONEncoder().encode(s))
        var next = restored
        _ = next.edit(id: first.id, data: task("离线再次编辑"))
        let retry = try XCTUnwrap(next.prepareBatch().first)
        XCTAssertEqual(first, retry)
        XCTAssertEqual(next.records[first.id]?.data.title, "离线再次编辑")
    }
    func testEditDuringHTTPIsRebasedWithoutLoss() throws {
        var s = bound(); _ = s.edit(id: nil, data: task("初稿"))
        let sent = try XCTUnwrap(s.prepareBatch().first)
        _ = s.edit(id: sent.id, data: task("发送中继续编辑"))
        try s.merge(response(sent), sent: [sent])
        XCTAssertEqual(s.records[sent.id]?.data.title, "发送中继续编辑")
        XCTAssertEqual(s.records[sent.id]?.version, 1)
        XCTAssertTrue(s.records[sent.id]?.dirty == true)
        let next = try XCTUnwrap(s.prepareBatch().first)
        XCTAssertEqual(next.baseVersion, 1); XCTAssertNotEqual(next.mutationID, sent.mutationID)
    }
    func testConflictPreservesLocalCopyAndCloudVersion() throws {
        var s = bound()
        let id = daynoteID()
        s.records[id] = LocalRecord(id: id, data: task("本机"), version: 1)
        let sent = try XCTUnwrap(s.prepareBatch().first)
        let count = try s.merge(response(sent, version: 2, status: "conflict", cloudData: task("另一设备")), sent: [sent])
        XCTAssertEqual(count, 1); XCTAssertEqual(s.records[id]?.data.title, "另一设备")
        let copy = try XCTUnwrap(s.visible.first { $0.id != id })
        XCTAssertEqual(copy.data.title, "本机（冲突副本）"); XCTAssertTrue(copy.dirty)
        XCTAssertEqual(copy.version, 0)
    }
    func testDeletePropagatesTombstoneAndDoesNotReappear() throws {
        var s = bound(); let id = daynoteID()
        s.records[id] = LocalRecord(id: id, data: task("删除"), version: 1, dirty: false)
        s.delete(id)
        let sent = try XCTUnwrap(s.prepareBatch().first)
        try s.merge(response(sent, version: 2), sent: [sent])
        XCTAssertEqual(s.visible.count, 0); XCTAssertEqual(s.records[id]?.version, 2)
        var other = bound()
        try other.merge(SyncResponse(accountID: "owner-a", items: [CloudRecord(id: id, version: 2, deleted: true, data: TaskData())]), sent: [])
        XCTAssertEqual(other.visible.count, 0); XCTAssertTrue(other.records[id]?.deleted == true)
    }
    func testDeleteConflictKeepsUpdatedCloudItem() throws {
        var s = bound(); let id = daynoteID()
        s.records[id] = LocalRecord(id: id, data: task("旧"), version: 1, dirty: false)
        s.delete(id); let sent = try XCTUnwrap(s.prepareBatch().first)
        var r = response(sent, version: 2, status: "conflict", cloudData: task("新的安排"))
        r.items[0].deleted = false
        XCTAssertEqual(try s.merge(r, sent: [sent]), 1)
        XCTAssertEqual(s.visible.count, 1); XCTAssertEqual(s.visible.first?.data.title, "新的安排")
    }
    func testAccountMismatchDoesNotMerge() throws {
        var s = bound(); _ = s.edit(id: nil, data: task("私密")); let before = s
        XCTAssertThrowsError(try s.merge(SyncResponse(accountID: "owner-b"), sent: []))
        XCTAssertEqual(s, before)
    }
    func testLostResponseThenRemoteEditUsesLatestCloudState() throws {
        var s = bound(); _ = s.edit(id: nil, data: task("A设备"))
        let sent = try XCTUnwrap(s.prepareBatch().first)
        var r = response(sent, version: 1)
        r.items = [CloudRecord(id: sent.id, version: 2, data: task("B设备后来编辑"))]
        try s.merge(r, sent: [sent])
        XCTAssertEqual(s.records[sent.id]?.data.title, "B设备后来编辑")
        XCTAssertFalse(s.records[sent.id]?.dirty ?? true)
    }
    func testNewLocalEditAndRemoteEditAfterLostResponseBothSurvive() throws {
        var s = bound(); _ = s.edit(id: nil, data: task("初稿"))
        let sent = try XCTUnwrap(s.prepareBatch().first)
        _ = s.edit(id: sent.id, data: task("本机后来修改"))
        var r = response(sent, version: 1)
        r.items = [CloudRecord(id: sent.id, version: 2, data: task("云端后来修改"))]
        XCTAssertEqual(try s.merge(r, sent: [sent]), 1)
        XCTAssertEqual(s.visible.count, 2)
    }
    func testStaleEditorCreatesVisibleCopy() throws {
        var s = bound(); _ = s.edit(id: nil, data: task("原内容"))
        let first = try XCTUnwrap(s.visible.first)
        _ = s.edit(id: first.id, data: task("其他更新"))
        XCTAssertTrue(s.edit(id: first.id, data: task("编辑窗口内容"), expectedRevision: first.revision))
        XCTAssertEqual(s.visible.count, 2)
        XCTAssertEqual(s.records[first.id]?.data.title, "其他更新")
    }
    func testByteBudgetIncludesLongChineseNotes() throws {
        var s = bound()
        for index in 0..<100 { var d = task("\(index)"); d.notes = String(repeating: "测", count: 20000); _ = s.edit(id: nil, data: d) }
        let batch = try s.prepareBatch()
        XCTAssertFalse(batch.isEmpty); XCTAssertLessThan(batch.count, 100)
        XCTAssertLessThan(try JSONEncoder().encode(batch).count, 900000)
        XCTAssertEqual(s.pending.count, 100)
    }
    func testEmptyCloudDoesNotEraseOfflineTasks() throws {
        var s = bound(); _ = s.edit(id: nil, data: task("离线保留"))
        try s.merge(SyncResponse(accountID: "owner-a"), sent: [])
        XCTAssertEqual(s.visible.count, 1); XCTAssertEqual(s.dirtyCount, 1)
    }
}
