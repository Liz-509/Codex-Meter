import Foundation

@main
enum CodexAnalyticsTests {
    static func main() throws {
        try testNinetyDaySessionAggregation()
        try testContextHealthAggregation()
        try testLiveContextTailReading()
        try testNonProjectAggregation()
        try testSameNameGitProjects()
        try testForecastAndEvents()
        try testReportFormats()
        print("CodexAnalyticsTests passed")
    }

    private static func testContextHealthAggregation() throws {
        let temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("codex-meter-context-health-tests-\(UUID().uuidString)", isDirectory: true)
        let sessions = temporary.appendingPathComponent("sessions", isDirectory: true)
        let project = temporary.appendingPathComponent("ContextProject", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: project.appendingPathComponent(".git"), withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: temporary) }

        let userLines = [
            "{\"timestamp\":\"2033-05-13T10:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"thread-health\",\"cwd\":\"\(project.path)\",\"thread_source\":\"user\",\"context_window\":{\"window_id\":\"window-1\"}}}",
            "{\"timestamp\":\"2033-05-13T10:01:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"turn-health\"}}",
            "{\"timestamp\":\"2033-05-13T10:01:10Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"实现上下文健康度\"}}",
            "{\"timestamp\":\"2033-05-13T10:02:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"model_context_window\":100000,\"last_token_usage\":{\"total_tokens\":85000},\"total_token_usage\":{\"total_tokens\":85000}}}}",
            "{\"timestamp\":\"2033-05-13T10:03:00Z\",\"type\":\"compacted\",\"payload\":{\"window_id\":\"window-2\",\"window_number\":2}}",
            "{\"timestamp\":\"2033-05-13T10:04:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"model_context_window\":100000,\"last_token_usage\":{\"total_tokens\":25000},\"total_token_usage\":{\"total_tokens\":90000}}}}"
        ]
        let internalLines = [
            "{\"timestamp\":\"2033-05-13T10:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"thread-internal-health\",\"cwd\":\"\(project.path)\",\"source\":{\"subagent\":{\"name\":\"helper\"}}}}",
            "{\"timestamp\":\"2033-05-13T10:02:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"model_context_window\":100000,\"last_token_usage\":{\"total_tokens\":95000},\"total_token_usage\":{\"total_tokens\":95000}}}}"
        ]
        let olderDuplicateLines = [
            "{\"timestamp\":\"2033-05-13T09:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"thread-health\",\"cwd\":\"\(project.path)\",\"thread_source\":\"user\",\"context_window\":{\"window_id\":\"window-old\"}}}",
            "{\"timestamp\":\"2033-05-13T09:01:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"model_context_window\":100000,\"last_token_usage\":{\"total_tokens\":95000},\"total_token_usage\":{\"total_tokens\":95000}}}}"
        ]
        let now = ISO8601DateFormatter().date(from: "2033-05-13T12:00:00Z")!
        for (name, lines) in [("user.jsonl", userLines), ("internal.jsonl", internalLines), ("older.jsonl", olderDuplicateLines)] {
            let file = sessions.appendingPathComponent(name)
            try lines.joined(separator: "\n").write(to: file, atomically: true, encoding: .utf8)
            try FileManager.default.setAttributes([.modificationDate: now], ofItemAtPath: file.path)
        }

        let service = CodexUsageService()
        let payload = service.localPayloadForTesting(sessionsDirectory: sessions, now: now)
        let health = payload["contextHealth"] as? [String: Any]
        let rows = health?["sessions"] as? [[String: Any]] ?? []
        expect(rows.count == 1, "上下文健康度只应包含用户任务")
        expect(rows.first?["threadId"] as? String == "thread-health", "上下文健康度应关联任务")
        expect(rows.first?["contextWindowId"] as? String == "window-2", "压缩后应使用最新上下文窗口")
        expect((rows.first?["usedTokens"] as? NSNumber)?.intValue == 25_000, "应使用最新窗口 Token，而非累计 Token")
        expect((rows.first?["remainingPercent"] as? NSNumber)?.doubleValue == 75, "应计算上下文剩余比例")
        expect(rows.first?["status"] as? String == "healthy", "压缩后应按新窗口恢复健康状态")
        expect((rows.first?["compactions"] as? NSNumber)?.intValue == 1, "应记录上下文压缩次数")
        service.shutdown()
    }

    private static func testLiveContextTailReading() throws {
        let temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("codex-meter-live-context-tests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: temporary, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: temporary) }
        let file = temporary.appendingPathComponent("current.jsonl")
        let lines = [
            "{\"timestamp\":\"2033-05-13T10:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"model_context_window\":200000,\"last_token_usage\":{\"total_tokens\":50000}}}}",
            "{\"timestamp\":\"2033-05-13T10:01:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"model_context_window\":200000,\"last_token_usage\":{\"total_tokens\":75000}}}}"
        ]
        try lines.joined(separator: "\n").write(to: file, atomically: true, encoding: .utf8)

        let service = CodexUsageService()
        let first = service.contextMeasurementForTesting(file: file)
        expect((first?["usedTokens"] as? NSNumber)?.intValue == 75_000, "实时上下文应读取日志尾部的最新窗口用量")
        expect((first?["maxTokens"] as? NSNumber)?.intValue == 200_000, "实时上下文应读取模型窗口上限")

        let appended = "\n{\"timestamp\":\"2033-05-13T10:02:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"model_context_window\":200000,\"last_token_usage\":{\"total_tokens\":90000}}}}"
        let handle = try FileHandle(forWritingTo: file)
        try handle.seekToEnd()
        try handle.write(contentsOf: Data(appended.utf8))
        try handle.close()
        let second = service.contextMeasurementForTesting(file: file)
        expect((second?["usedTokens"] as? NSNumber)?.intValue == 90_000, "会话文件增长后应立即更新上下文用量")
        service.shutdown()
    }

    private static func testNonProjectAggregation() throws {
        let temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("codex-meter-non-project-tests-\(UUID().uuidString)", isDirectory: true)
        let sessions = temporary.appendingPathComponent("sessions", isDirectory: true)
        let firstDirectory = temporary.appendingPathComponent("LooseConversationA", isDirectory: true)
        let secondDirectory = temporary.appendingPathComponent("LooseConversationB", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: firstDirectory, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: secondDirectory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: temporary) }

        let fixtures = [
            ("first.jsonl", "thread-loose-a", "turn-loose-a", firstDirectory.path, "整理零散对话", 25),
            ("second.jsonl", "thread-loose-b", "turn-loose-b", secondDirectory.path, "检查临时任务", 35)
        ]
        let now = ISO8601DateFormatter().date(from: "2033-05-13T12:00:00Z")!
        for (fileName, threadID, turnID, cwd, prompt, tokens) in fixtures {
            let lines = [
                "{\"timestamp\":\"2033-05-13T10:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"\(threadID)\",\"cwd\":\"\(cwd)\",\"thread_source\":\"user\"}}",
                "{\"timestamp\":\"2033-05-13T10:01:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"\(turnID)\"}}",
                "{\"timestamp\":\"2033-05-13T10:01:10Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"\(prompt)\"}}",
                "{\"timestamp\":\"2033-05-13T10:02:00Z\",\"type\":\"token_usage_record\",\"payload\":{\"turn_id\":\"\(turnID)\",\"usage\":{\"total_tokens\":\(tokens)},\"turn_token_usage\":{\"total_tokens\":\(tokens)}}}"
            ]
            let file = sessions.appendingPathComponent(fileName)
            try lines.joined(separator: "\n").write(to: file, atomically: true, encoding: .utf8)
            try FileManager.default.setAttributes([.modificationDate: now], ofItemAtPath: file.path)
        }

        let service = CodexUsageService()
        let payload = service.localPayloadForTesting(sessionsDirectory: sessions, now: now)
        let insights = payload["insights"] as? [String: Any]
        let projects = insights?["projects"] as? [[String: Any]] ?? []
        let tasks = insights?["tasks"] as? [[String: Any]] ?? []
        expect(projects.count == 1, "不同非 Git 目录应合并为同一非项目分组")
        expect(projects.first?["key"] as? String == "__non_project__", "非项目分组应使用稳定键")
        expect(projects.first?["name"] as? String == "非项目中对话", "非项目分组应使用统一名称")
        expect(projects.first?["projectKind"] as? String == "non_project", "非项目分组应标注类型")
        expect(tasks.count == 2 && tasks.allSatisfy { $0["projectKey"] as? String == "__non_project__" }, "非项目任务应保留并归入统一分组")
        expect(tasks.reduce(0) { $0 + (($1["tokens"] as? NSNumber)?.intValue ?? 0) } == 60, "非项目 Token 总量应保持可解释")
        service.shutdown()
    }

    private static func testSameNameGitProjects() throws {
        let temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("codex-meter-same-name-tests-\(UUID().uuidString)", isDirectory: true)
        let sessions = temporary.appendingPathComponent("sessions", isDirectory: true)
        let firstProject = temporary.appendingPathComponent("ParentA/Shared", isDirectory: true)
        let secondProject = temporary.appendingPathComponent("ParentB/Shared", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: firstProject.appendingPathComponent(".git"), withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: secondProject.appendingPathComponent(".git"), withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: temporary) }

        let now = ISO8601DateFormatter().date(from: "2033-05-13T12:00:00Z")!
        for (index, project) in [firstProject, secondProject].enumerated() {
            let lines = [
                "{\"timestamp\":\"2033-05-13T10:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"thread-\(index)\",\"cwd\":\"\(project.path)\",\"thread_source\":\"user\"}}",
                "{\"timestamp\":\"2033-05-13T10:01:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"turn-\(index)\"}}",
                "{\"timestamp\":\"2033-05-13T10:02:00Z\",\"type\":\"token_usage_record\",\"payload\":{\"turn_id\":\"turn-\(index)\",\"usage\":{\"total_tokens\":10},\"turn_token_usage\":{\"total_tokens\":10}}}"
            ]
            let file = sessions.appendingPathComponent("project-\(index).jsonl")
            try lines.joined(separator: "\n").write(to: file, atomically: true, encoding: .utf8)
            try FileManager.default.setAttributes([.modificationDate: now], ofItemAtPath: file.path)
        }

        let service = CodexUsageService()
        let payload = service.localPayloadForTesting(sessionsDirectory: sessions, now: now)
        let insights = payload["insights"] as? [String: Any]
        let projects = insights?["projects"] as? [[String: Any]] ?? []
        let names = Set(projects.compactMap { $0["name"] as? String })
        expect(projects.count == 2, "两个 Git 根目录应保持独立")
        expect(names == Set(["Shared — ParentA", "Shared — ParentB"]), "同名项目应使用父目录消歧")
        expect(projects.allSatisfy { $0["projectKind"] as? String == "project" }, "Git 根目录应标注为项目")
        service.shutdown()
    }

    private static func testNinetyDaySessionAggregation() throws {
        let temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("codex-meter-session-tests-\(UUID().uuidString)", isDirectory: true)
        let sessions = temporary.appendingPathComponent("sessions", isDirectory: true)
        let project = temporary.appendingPathComponent("ExampleProject", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: project.appendingPathComponent(".git"), withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: temporary) }

        let userLines = [
            "{\"timestamp\":\"2033-05-13T10:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"thread-user\",\"cwd\":\"\(project.path)\",\"thread_source\":\"user\"}}",
            "{\"timestamp\":\"2033-05-13T10:01:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"turn-user\"}}",
            "{\"timestamp\":\"2033-05-13T10:01:10Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"实现项目统计\"}}",
            "{\"timestamp\":\"2033-05-13T10:02:00Z\",\"type\":\"token_usage_record\",\"payload\":{\"turn_id\":\"turn-user\",\"usage\":{\"total_tokens\":50},\"turn_token_usage\":{\"total_tokens\":50}}}"
        ]
        let internalLines = [
            "{\"timestamp\":\"2033-05-13T10:03:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"thread-agent\",\"cwd\":\"\(project.path)\",\"source\":{\"subagent\":{\"name\":\"helper\"}}}}",
            "{\"timestamp\":\"2033-05-13T10:04:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"turn-agent\"}}",
            "{\"timestamp\":\"2033-05-13T10:05:00Z\",\"type\":\"token_usage_record\",\"payload\":{\"turn_id\":\"turn-agent\",\"usage\":{\"total_tokens\":30},\"turn_token_usage\":{\"total_tokens\":30}}}"
        ]
        let userFile = sessions.appendingPathComponent("user.jsonl")
        let internalFile = sessions.appendingPathComponent("internal.jsonl")
        try userLines.joined(separator: "\n").write(to: userFile, atomically: true, encoding: .utf8)
        try internalLines.joined(separator: "\n").write(to: internalFile, atomically: true, encoding: .utf8)
        let now = ISO8601DateFormatter().date(from: "2033-05-13T12:00:00Z")!
        try FileManager.default.setAttributes([.modificationDate: now], ofItemAtPath: userFile.path)
        try FileManager.default.setAttributes([.modificationDate: now], ofItemAtPath: internalFile.path)

        let service = CodexUsageService()
        let payload = service.localPayloadForTesting(sessionsDirectory: sessions, now: now)
        let history = payload["history"] as? [String: Any]
        let days = history?["dailyTokens"] as? [[String: Any]] ?? []
        expect(days.count == 90, "本地历史应包含 90 天")
        expect((days.last?["tokens"] as? NSNumber)?.intValue == 80, "用户与子代理 Token 应计入当天总量")
        let today = payload["today"] as? [String: Any]
        expect((today?["questions"] as? NSNumber)?.intValue == 1, "子代理不应计入用户对话轮次")
        let insights = payload["insights"] as? [String: Any]
        let tasks = insights?["tasks"] as? [[String: Any]] ?? []
        expect(tasks.contains { $0["name"] as? String == "实现项目统计" && ($0["tokens"] as? NSNumber)?.intValue == 50 }, "应生成用户任务统计")
        expect(tasks.contains { $0["name"] as? String == "系统/子代理活动" && ($0["tokens"] as? NSNumber)?.intValue == 30 }, "应归集子代理消耗")
        service.shutdown()
    }

    private static func testForecastAndEvents() throws {
        let temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("codex-meter-tests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: temporary, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: temporary) }
        guard let defaults = UserDefaults(suiteName: "CodexMeterTests.\(UUID().uuidString)") else {
            throw TestFailure("无法创建测试 UserDefaults")
        }
        let monitor = CodexQuotaMonitor(
            defaults: defaults,
            fileURL: temporary.appendingPathComponent("samples.json")
        )
        let base = Date(timeIntervalSince1970: 2_000_000_000)
        let reset = base.addingTimeInterval(10 * 3_600).timeIntervalSince1970

        let initial = monitor.process(payload(remaining: 100, resetsAt: reset), now: base)
        expect(initial.events.isEmpty, "首次同步不应通知")
        expect((initial.forecast["primary"] as? [String: Any])?["status"] as? String == "insufficient", "首次预测应为数据不足")

        _ = monitor.process(payload(remaining: 98, resetsAt: reset), now: base.addingTimeInterval(5 * 60))
        _ = monitor.process(payload(remaining: 96, resetsAt: reset), now: base.addingTimeInterval(10 * 60))
        let forecast = monitor.process(payload(remaining: 94, resetsAt: reset), now: base.addingTimeInterval(20 * 60))
        let primary = forecast.forecast["primary"] as? [String: Any]
        expect(primary?["status"] as? String == "will_deplete", "持续下降应预测提前耗尽")
        expect((primary?["ratePerHour"] as? NSNumber)?.doubleValue ?? 0 > 0, "预测应包含正消耗速率")

        let threshold = monitor.process(payload(remaining: 9, resetsAt: reset), now: base.addingTimeInterval(25 * 60))
        expect(threshold.events.count == 1, "一次跨越多个阈值只应发送最严重的一条")
        expect(threshold.events.first?.kind == .threshold && threshold.events.first?.threshold == 10, "阈值事件应为 10%")

        let duplicate = monitor.process(payload(remaining: 8, resetsAt: reset), now: base.addingTimeInterval(30 * 60))
        expect(duplicate.events.isEmpty, "同一周期不得重复发送阈值通知")

        let exhausted = monitor.process(payload(remaining: 0, resetsAt: reset), now: base.addingTimeInterval(35 * 60))
        expect(exhausted.events.contains { $0.kind == .exhausted }, "降至零应发送耗尽事件")

        let nextReset = reset + 7 * 86_400
        let restored = monitor.process(payload(remaining: 85, resetsAt: nextReset), now: base.addingTimeInterval(40 * 60))
        expect(restored.events.contains { $0.kind == .restored }, "新周期回升应发送恢复事件")
    }

    private static func testReportFormats() throws {
        let payload: [String: Any] = [
            "history": [
                "dailyTokens": [
                    ["date": "2033-05-12", "tokens": 120, "source": "account"],
                    ["date": "2033-05-13", "tokens": 80, "source": "local"]
                ]
            ],
            "insights": [
                "tasks": [
                    [
                        "date": "2033-05-13",
                        "projectName": "项目,甲",
                        "projectKind": "project",
                        "name": "修复 \"导出\" | 测试",
                        "turns": 2,
                        "tokens": 80,
                        "lastActive": "2033-05-13T10:00:00Z"
                    ],
                    [
                        "date": "2033-05-13",
                        "projectName": "非项目中对话",
                        "projectKind": "non_project",
                        "name": "临时问答",
                        "turns": 1,
                        "tokens": 20,
                        "lastActive": "2033-05-13T11:00:00Z"
                    ]
                ]
            ]
        ]
        let now = ISO8601DateFormatter().date(from: "2033-05-13T12:00:00Z")!
        let markdown = CodexReportGenerator.markdown(from: payload, now: now)
        expect(markdown.contains("最近 7 天报告"), "Markdown 应包含标题")
        expect(markdown.contains("\\|"), "Markdown 表格内容应转义竖线")
        expect(markdown.contains("活跃项目：1"), "活跃项目数不应包含非项目分组")
        expect(markdown.contains("非项目中对话"), "项目排行应保留非项目分组")
        let csv = CodexReportGenerator.csv(from: payload, now: now)
        expect(Array(csv.prefix(3)) == [0xEF, 0xBB, 0xBF], "CSV 应包含 UTF-8 BOM")
        let text = String(data: csv.dropFirst(3), encoding: .utf8) ?? ""
        expect(text.contains("\"项目,甲\""), "CSV 应转义逗号")
        expect(text.contains("\"修复 \"\"导出\"\" | 测试\""), "CSV 应转义双引号")
        expect(text.contains("非项目中对话,临时问答"), "CSV 应使用统一非项目名称")
    }

    private static func payload(remaining: Double, resetsAt: TimeInterval) -> [String: Any] {
        [
            "rateLimits": [
                "primary": ["remainingPercent": remaining, "resetsAt": resetsAt]
            ]
        ]
    }

    private static func expect(_ condition: @autoclosure () -> Bool, _ message: String) {
        if !condition() { fatalError(message) }
    }

    private struct TestFailure: Error {
        let message: String
        init(_ message: String) { self.message = message }
    }
}
