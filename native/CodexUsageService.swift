import Foundation

private final class JSONLineResponseCollector: @unchecked Sendable {
    private let condition = NSCondition()
    private var buffer = Data()
    private var responses: [Int: [String: Any]] = [:]

    func append(_ data: Data) {
        guard !data.isEmpty else { return }
        condition.lock()
        buffer.append(data)

        while let newline = buffer.firstIndex(of: 0x0A) {
            let line = Data(buffer[..<newline])
            buffer.removeSubrange(...newline)
            guard !line.isEmpty,
                  let object = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
                  let number = object["id"] as? NSNumber else { continue }
            responses[number.intValue] = object
        }

        condition.broadcast()
        condition.unlock()
    }

    func wait(for ids: Set<Int>, timeout: TimeInterval) -> [Int: [String: Any]]? {
        let deadline = Date().addingTimeInterval(timeout)
        condition.lock()
        defer { condition.unlock() }

        while !ids.allSatisfy({ responses[$0] != nil }) {
            if !condition.wait(until: deadline) {
                return nil
            }
        }
        return responses
    }
}

final class CodexUsageService {
    private struct DailyTokenStats {
        let date: String
        let tokens: Int
    }

    private struct ConversationStats {
        let turnId: String
        let threadId: String?
        let contextWindowId: String?
        let startedAt: Date
        var preview: String
        var tokens: Int?
    }

    private struct LocalStats {
        let dailyTokens: [DailyTokenStats]
        let conversations: [ConversationStats]

        var questions: Int { conversations.count }
        var tokens: Int { dailyTokens.last?.tokens ?? 0 }
    }

    private enum ServiceError: LocalizedError {
        case binaryMissing
        case timedOut
        case responseMissing(String)
        case server(String)

        var errorDescription: String? {
            switch self {
            case .binaryMissing:
                return "未找到 Codex App Server"
            case .timedOut:
                return "Codex 数据请求超时"
            case .responseMissing(let method):
                return "未收到 \(method) 数据"
            case .server(let message):
                return message
            }
        }
    }

    func fetch(completion: @escaping ([String: Any]) -> Void) {
        DispatchQueue.global(qos: .userInitiated).async {
            let localStats = self.localStats()
            var payload = self.localPayload(from: localStats)

            var partialPayload = payload
            partialPayload["partial"] = true
            partialPayload["syncMessage"] = "正在同步额度"
            DispatchQueue.main.async {
                completion(partialPayload)
            }

            do {
                let responses = try self.readAccountData()
                let limits = try self.result(for: 1, method: "额度", in: responses)
                let usage = try self.result(for: 2, method: "Token", in: responses)
                payload = self.localPayload(
                    from: localStats,
                    threadNames: self.threadNames(from: responses)
                )

                for (key, value) in limits {
                    payload[key] = value
                }

                if let buckets = usage["dailyUsageBuckets"] as? [[String: Any]],
                   let accountHistory = self.accountHistory(
                       from: buckets,
                       localDays: localStats.dailyTokens
                   ) {
                    payload["history"] = [
                        "source": "account",
                        "localFallback": accountHistory.localFallback,
                        "dailyTokens": accountHistory.days.map { ["date": $0.date, "tokens": $0.tokens] }
                    ]
                    var today = payload["today"] as? [String: Any] ?? [:]
                    today["tokens"] = accountHistory.days.last?.tokens ?? localStats.tokens
                    today["tokenSource"] = accountHistory.todayFromAccount ? "account" : "local"
                    payload["today"] = today
                }
                payload["source"] = "Codex App Server"
            } catch {
                payload["error"] = error.localizedDescription
            }

            DispatchQueue.main.async {
                completion(payload)
            }
        }
    }

    func consumeResetCredit(idempotencyKey: String, completion: @escaping ([String: Any]) -> Void) {
        DispatchQueue.global(qos: .userInitiated).async {
            let payload: [String: Any]
            do {
                let responses = try self.performRequests([
                    [
                        "method": "account/rateLimitResetCredit/consume",
                        "id": 1,
                        "params": ["idempotencyKey": idempotencyKey]
                    ]
                ], waitingFor: [1])
                let result = try self.result(for: 1, method: "重置", in: responses)
                guard let outcome = result["outcome"] as? String else {
                    throw ServiceError.responseMissing("重置结果")
                }
                payload = ["outcome": outcome]
            } catch {
                payload = ["error": error.localizedDescription]
            }

            DispatchQueue.main.async {
                completion(payload)
            }
        }
    }

    private func readAccountData() throws -> [Int: [String: Any]] {
        try performRequests([
            ["method": "account/rateLimits/read", "id": 1],
            ["method": "account/usage/read", "id": 2],
            [
                "method": "thread/list",
                "id": 3,
                "params": ["limit": 100, "sortKey": "updated_at"]
            ]
        ], waitingFor: [1, 2, 3])
    }

    private func performRequests(
        _ requests: [[String: Any]],
        waitingFor responseIDs: Set<Int>
    ) throws -> [Int: [String: Any]] {
        guard let executable = codexExecutable else {
            throw ServiceError.binaryMissing
        }

        let process = Process()
        let input = Pipe()
        let output = Pipe()
        let errors = Pipe()
        process.executableURL = executable
        process.arguments = ["app-server"]
        process.standardInput = input
        process.standardOutput = output
        process.standardError = errors
        process.currentDirectoryURL = FileManager.default.homeDirectoryForCurrentUser

        var environment = ProcessInfo.processInfo.environment
        environment["PATH"] = "/usr/local/bin:/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin"
        process.environment = environment

        let collector = JSONLineResponseCollector()
        output.fileHandleForReading.readabilityHandler = { handle in
            collector.append(handle.availableData)
        }

        do {
            try process.run()
            defer {
                output.fileHandleForReading.readabilityHandler = nil
                try? input.fileHandleForWriting.close()
                if process.isRunning { process.terminate() }
            }

            try send([
                "method": "initialize",
                "id": 0,
                "params": [
                    "clientInfo": [
                        "name": "codex_usage_widget",
                        "title": "Codex Meter",
                        "version": "1.3.0"
                    ]
                ]
            ], to: input.fileHandleForWriting)

            guard let initialized = collector.wait(for: [0], timeout: 10),
                  let initializeResponse = initialized[0] else {
                throw ServiceError.server("Codex 初始化超时")
            }
            if let error = initializeResponse["error"] as? [String: Any] {
                throw ServiceError.server(error["message"] as? String ?? "Codex 初始化失败")
            }

            try send(["method": "initialized", "params": [:]], to: input.fileHandleForWriting)
            for request in requests {
                try send(request, to: input.fileHandleForWriting)
            }

            guard let responses = collector.wait(for: responseIDs, timeout: 20) else {
                throw ServiceError.timedOut
            }
            return responses
        } catch {
            output.fileHandleForReading.readabilityHandler = nil
            try? input.fileHandleForWriting.close()
            if process.isRunning { process.terminate() }
            throw error
        }
    }

    private func send(_ message: [String: Any], to handle: FileHandle) throws {
        let data = try JSONSerialization.data(withJSONObject: message)
        try handle.write(contentsOf: data)
        try handle.write(contentsOf: Data([0x0A]))
    }

    private func result(
        for id: Int,
        method: String,
        in responses: [Int: [String: Any]]
    ) throws -> [String: Any] {
        guard let response = responses[id] else {
            throw ServiceError.responseMissing(method)
        }
        if let error = response["error"] as? [String: Any] {
            throw ServiceError.server(error["message"] as? String ?? "Codex 返回未知错误")
        }
        guard let result = response["result"] as? [String: Any] else {
            throw ServiceError.responseMissing(method)
        }
        return result
    }

    private var codexExecutable: URL? {
        let home = FileManager.default.homeDirectoryForCurrentUser
        var candidates: [URL] = []
        for variable in ["CODEX_BINARY", "CODEX_CLI_PATH"] {
            if let customPath = ProcessInfo.processInfo.environment[variable],
               !customPath.isEmpty {
                candidates.append(URL(fileURLWithPath: customPath))
            }
        }
        candidates.append(contentsOf: [
            URL(fileURLWithPath: "/Applications/ChatGPT.app/Contents/Resources/codex"),
            home.appendingPathComponent("Applications/ChatGPT.app/Contents/Resources/codex"),
            home.appendingPathComponent(".codex/plugins/.plugin-appserver/codex"),
            URL(fileURLWithPath: "/opt/homebrew/bin/codex"),
            URL(fileURLWithPath: "/usr/local/bin/codex")
        ])
        return candidates.first(where: {
            FileManager.default.isExecutableFile(atPath: $0.path)
        })
    }

    private func dateFormatter() -> DateFormatter {
        let formatter = DateFormatter()
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = .current
        formatter.dateFormat = "yyyy-MM-dd"
        return formatter
    }

    private func localPayload(
        from stats: LocalStats,
        threadNames: [String: String] = [:]
    ) -> [String: Any] {
        [
            "today": [
                "questions": stats.questions,
                "tokens": stats.tokens,
                "tokenSource": "local",
                "conversations": stats.conversations.map { conversation in
                    var item: [String: Any] = [
                        "turnId": conversation.turnId,
                        "startedAt": ISO8601DateFormatter().string(from: conversation.startedAt),
                        "preview": conversation.preview
                    ]
                    if let threadId = conversation.threadId { item["threadId"] = threadId }
                    if let threadId = conversation.threadId,
                       let threadName = threadNames[threadId] {
                        item["threadName"] = threadName
                    }
                    if let contextWindowId = conversation.contextWindowId {
                        item["contextWindowId"] = contextWindowId
                    }
                    if let tokens = conversation.tokens { item["tokens"] = tokens }
                    return item
                }
            ],
            "history": [
                "source": "local",
                "dailyTokens": stats.dailyTokens.map { ["date": $0.date, "tokens": $0.tokens] }
            ]
        ]
    }

    private func threadNames(from responses: [Int: [String: Any]]) -> [String: String] {
        guard let response = responses[3],
              response["error"] == nil,
              let result = response["result"] as? [String: Any],
              let threads = result["data"] as? [[String: Any]] else { return [:] }

        var names: [String: String] = [:]
        for thread in threads {
            guard let name = thread["name"] as? String,
                  !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { continue }
            for key in [thread["id"] as? String, thread["sessionId"] as? String].compactMap({ $0 }) {
                if names[key] == nil { names[key] = name }
            }
        }
        return names
    }

    private func accountHistory(
        from buckets: [[String: Any]],
        localDays: [DailyTokenStats]
    ) -> (days: [DailyTokenStats], localFallback: Bool, todayFromAccount: Bool)? {
        var values: [String: Int] = [:]
        for bucket in buckets {
            guard let date = bucket["startDate"] as? String,
                  let tokens = (bucket["tokens"] as? NSNumber)?.intValue else { continue }
            values[date] = tokens
        }
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = .current
        let formatter = dateFormatter()
        let today = calendar.startOfDay(for: Date())
        let dates = (0..<7).compactMap { offset in
            calendar.date(byAdding: .day, value: offset - 6, to: today)
        }
        let keys = dates.map(formatter.string(from:))
        guard keys.contains(where: { values[$0] != nil }) else { return nil }

        let localValues = Dictionary(uniqueKeysWithValues: localDays.map { ($0.date, $0.tokens) })
        var localFallback = false
        let days = dates.map { date in
            let key = formatter.string(from: date)
            if let tokens = values[key] {
                return DailyTokenStats(date: key, tokens: tokens)
            }
            let tokens = localValues[key] ?? 0
            if tokens > 0 { localFallback = true }
            return DailyTokenStats(date: key, tokens: tokens)
        }
        guard let todayKey = keys.last else { return nil }
        return (
            days: days,
            localFallback: localFallback,
            todayFromAccount: values[todayKey] != nil
        )
    }

    private func localStats() -> LocalStats {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = .current
        let today = calendar.startOfDay(for: Date())
        let historyStart = calendar.date(byAdding: .day, value: -6, to: today) ?? today
        let formatter = dateFormatter()
        let dayKeys = (0..<7).compactMap { offset -> String? in
            guard let date = calendar.date(byAdding: .day, value: offset, to: historyStart) else { return nil }
            return formatter.string(from: date)
        }
        var dailyTokens = Dictionary(uniqueKeysWithValues: dayKeys.map { ($0, 0) })
        let todayKey = formatter.string(from: today)

        let environment = ProcessInfo.processInfo.environment
        let codexHome: URL
        if let configuredHome = environment["CODEX_HOME"], !configuredHome.isEmpty {
            codexHome = URL(fileURLWithPath: configuredHome)
        } else {
            codexHome = FileManager.default.homeDirectoryForCurrentUser
                .appendingPathComponent(".codex")
        }
        let directory = codexHome.appendingPathComponent("sessions")

        guard let enumerator = FileManager.default.enumerator(
            at: directory,
            includingPropertiesForKeys: [.contentModificationDateKey],
            options: [.skipsHiddenFiles]
        ) else {
            return LocalStats(
                dailyTokens: dayKeys.map { DailyTokenStats(date: $0, tokens: 0) },
                conversations: []
            )
        }

        var conversations: [String: ConversationStats] = [:]
        for case let file as URL in enumerator where file.pathExtension == "jsonl" {
            guard let values = try? file.resourceValues(forKeys: [.contentModificationDateKey]),
                  let modifiedAt = values.contentModificationDate,
                  modifiedAt >= historyStart else { continue }
            guard let contents = try? String(contentsOf: file, encoding: .utf8) else { continue }
            var threadId: String?
            var contextWindowId: String?
            var currentTurnId: String?
            var previousTotal = 0
            var usageRecordSinceTokenCount = false

            contents.enumerateLines { line, _ in
                guard let data = line.data(using: .utf8),
                      let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                      let rootType = object["type"] as? String,
                      let payload = object["payload"] as? [String: Any] else { return }

                if rootType == "session_meta" {
                    threadId = payload["id"] as? String ?? threadId
                    if let contextWindow = payload["context_window"] as? [String: Any] {
                        contextWindowId = contextWindow["window_id"] as? String ?? contextWindowId
                    }
                    return
                }

                if rootType == "event_msg" {
                    let eventType = payload["type"] as? String
                    if eventType == "task_started" {
                        let turnId = payload["turn_id"] as? String ?? UUID().uuidString.lowercased()
                        currentTurnId = turnId
                        guard let startedAt = self.parseTimestamp(object["timestamp"] as? String),
                              formatter.string(from: startedAt) == todayKey else { return }
                        conversations[turnId] = ConversationStats(
                            turnId: turnId,
                            threadId: threadId,
                            contextWindowId: contextWindowId,
                            startedAt: startedAt,
                            preview: "未命名对话",
                            tokens: nil
                        )
                        return
                    }

                    if eventType == "user_message",
                       let turnId = currentTurnId,
                       var conversation = conversations[turnId],
                       let message = payload["message"] as? String,
                       !message.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                        conversation.preview = self.normalizePreview(message)
                        conversations[turnId] = conversation
                        return
                    }

                    guard eventType == "token_count",
                          let info = payload["info"] as? [String: Any],
                          let totalUsage = info["total_token_usage"] as? [String: Any],
                          let total = (totalUsage["total_tokens"] as? NSNumber)?.intValue else { return }

                    let delta = total >= previousTotal ? total - previousTotal : total
                    previousTotal = total
                    if usageRecordSinceTokenCount {
                        usageRecordSinceTokenCount = false
                        return
                    }
                    guard let timestamp = self.parseTimestamp(object["timestamp"] as? String) else { return }
                    let dateKey = formatter.string(from: timestamp)
                    if dailyTokens[dateKey] != nil { dailyTokens[dateKey, default: 0] += max(0, delta) }
                    if let turnId = currentTurnId, var conversation = conversations[turnId] {
                        conversation.tokens = (conversation.tokens ?? 0) + max(0, delta)
                        conversations[turnId] = conversation
                    }
                    return
                }

                if rootType == "token_usage_record" {
                    let turnId = payload["turn_id"] as? String ?? currentTurnId
                    var countedUsageRecord = false
                    if let usage = payload["usage"] as? [String: Any],
                       let responseTokens = (usage["total_tokens"] as? NSNumber)?.intValue,
                       let timestamp = self.parseTimestamp(object["timestamp"] as? String) {
                        let dateKey = formatter.string(from: timestamp)
                        if dailyTokens[dateKey] != nil {
                            dailyTokens[dateKey, default: 0] += max(0, responseTokens)
                        }
                        countedUsageRecord = true
                    }
                    if let turnId,
                       var conversation = conversations[turnId],
                       let turnUsage = payload["turn_token_usage"] as? [String: Any],
                       let turnTokens = (turnUsage["total_tokens"] as? NSNumber)?.intValue {
                        conversation.tokens = max(0, turnTokens)
                        conversations[turnId] = conversation
                    }
                    usageRecordSinceTokenCount = countedUsageRecord
                    return
                }

                guard rootType == "response_item",
                      payload["type"] as? String == "message",
                      payload["role"] as? String == "user",
                      let metadata = payload["internal_chat_message_metadata_passthrough"] as? [String: Any],
                      let kinds = metadata["content_item_kinds"] as? [String],
                      kinds.contains("user.text"),
                      let turnId = metadata["turn_id"] as? String ?? currentTurnId,
                      var conversation = conversations[turnId],
                      let content = payload["content"] as? [[String: Any]] else { return }
                let text = content.compactMap { item -> String? in
                    guard let type = item["type"] as? String,
                          type == "input_text" || type == "text" else { return nil }
                    return item["text"] as? String
                }.joined(separator: " ")
                if !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    conversation.preview = self.normalizePreview(text)
                    conversations[turnId] = conversation
                }
            }
        }

        return LocalStats(
            dailyTokens: dayKeys.map { DailyTokenStats(date: $0, tokens: dailyTokens[$0] ?? 0) },
            conversations: conversations.values.sorted { $0.startedAt > $1.startedAt }
        )
    }

    private func parseTimestamp(_ value: String?) -> Date? {
        guard let value else { return nil }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = formatter.date(from: value) { return date }
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.date(from: value)
    }

    private func normalizePreview(_ value: String) -> String {
        let normalized = value
            .components(separatedBy: .whitespacesAndNewlines)
            .filter { !$0.isEmpty }
            .joined(separator: " ")
        guard normalized.count > 160 else { return normalized }
        let end = normalized.index(normalized.startIndex, offsetBy: 160)
        return normalized[..<end].trimmingCharacters(in: .whitespacesAndNewlines) + "…"
    }
}
