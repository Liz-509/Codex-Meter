import Foundation

private final class JSONLineResponseCollector: @unchecked Sendable {
    private let condition = NSCondition()
    private var buffer = Data()
    private var responses: [Int: [String: Any]] = [:]
    private var closed = false

    func append(_ data: Data) {
        guard !data.isEmpty else {
            close()
            return
        }
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
            if closed { return nil }
            if !condition.wait(until: deadline) {
                return nil
            }
        }
        var result: [Int: [String: Any]] = [:]
        for id in ids {
            result[id] = responses.removeValue(forKey: id)
        }
        return result
    }

    func close() {
        condition.lock()
        closed = true
        condition.broadcast()
        condition.unlock()
    }
}

final class CodexUsageService {
    private static let nonProjectKey = "__non_project__"
    private static let nonProjectName = "非项目中对话"

    private struct DailyTokenStats {
        let date: String
        let tokens: Int
        let source: String
    }

    private struct ConversationStats {
        let turnId: String
        let threadId: String?
        let contextWindowId: String?
        let startedAt: Date
        let date: String
        let projectPath: String
        var preview: String
        var tokens: Int?
    }

    private struct ProjectDailyStats {
        let date: String
        let projectPath: String
        let tokens: Int
    }

    private struct ContextHealthStats {
        let threadId: String?
        var contextWindowId: String?
        var projectPath: String
        var preview: String
        var usedTokens: Int
        var maxTokens: Int
        var updatedAt: Date
        var compactions: Int
    }

    private struct LocalStats {
        let dailyTokens: [DailyTokenStats]
        let conversations: [ConversationStats]
        let projectDailyTokens: [ProjectDailyStats]
        let contextHealth: [ContextHealthStats]

        var tokens: Int { dailyTokens.last?.tokens ?? 0 }
    }

    private struct SessionFileSignature: Equatable {
        let size: Int
        let modifiedAt: Date
    }

    private struct CachedSessionFile {
        let signature: SessionFileSignature
        let stats: LocalStats
    }

    private struct ContextMeasurement {
        let usedTokens: Int
        let maxTokens: Int
        let updatedAt: Date
    }

    private struct CachedLiveContext {
        let signature: SessionFileSignature
        let measurement: ContextMeasurement?
    }

    private let workQueue = DispatchQueue(label: "com.local.codex-usage.service", qos: .userInitiated)
    private var serverProcess: Process?
    private var serverInput: Pipe?
    private var serverOutput: Pipe?
    private var serverErrors: Pipe?
    private var responseCollector: JSONLineResponseCollector?
    private let lifecycleLock = NSLock()
    private let errorLock = NSLock()
    private var errorBuffer = Data()
    private var cachedExecutable: URL?
    private var nextRequestID = 0
    private var isShutdown = false
    private var sessionFiles: [String: CachedSessionFile] = [:]
    private var sessionWindowKey: String?
    private var projectPathCache: [String: String] = [:]
    private var liveContexts: [String: CachedLiveContext] = [:]

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
        workQueue.async {
            let localStats = self.cachedLocalStats()
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
                        "dailyTokens": accountHistory.days.map {
                            ["date": $0.date, "tokens": $0.tokens, "source": $0.source]
                        }
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
        workQueue.async {
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

    func fetchCurrentContextHealth(completion: @escaping ([String: Any]?) -> Void) {
        workQueue.async {
            let payload: [String: Any]?
            do {
                let responses = try self.performRequests([
                    [
                        "method": "thread/list",
                        "id": 1,
                        "params": [
                            "limit": 8,
                            "sortKey": "recency_at",
                            "sortDirection": "desc",
                            "useStateDbOnly": true
                        ]
                    ]
                ], waitingFor: [1])
                let result = try self.result(for: 1, method: "当前对话", in: responses)
                let threads = result["data"] as? [[String: Any]] ?? []
                if let thread = threads.first(where: {
                    ($0["parentThreadId"] == nil || $0["parentThreadId"] is NSNull)
                        && ($0["ephemeral"] as? Bool != true)
                }) {
                    payload = self.currentContextPayload(from: thread)
                } else {
                    payload = nil
                }
            } catch {
                payload = nil
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
        for attempt in 0..<2 {
            do {
                try ensureServer()
                lifecycleLock.lock()
                let input = serverInput?.fileHandleForWriting
                let collector = responseCollector
                lifecycleLock.unlock()
                guard let input, let collector else {
                    throw ServiceError.server("Codex App Server 尚未启动")
                }

                var physicalIDs: Set<Int> = []
                var logicalToPhysical: [Int: Int] = [:]
                for request in requests {
                    guard let logicalID = (request["id"] as? NSNumber)?.intValue else { continue }
                    let physicalID = nextID()
                    var physicalRequest = request
                    physicalRequest["id"] = physicalID
                    logicalToPhysical[logicalID] = physicalID
                    physicalIDs.insert(physicalID)
                    try send(physicalRequest, to: input)
                }

                guard physicalIDs.count == responseIDs.count,
                      let physicalResponses = collector.wait(for: physicalIDs, timeout: 20) else {
                    if let message = serverErrorMessage(), !message.isEmpty {
                        throw ServiceError.server(message)
                    }
                    throw ServiceError.timedOut
                }
                var logicalResponses: [Int: [String: Any]] = [:]
                for logicalID in responseIDs {
                    guard let physicalID = logicalToPhysical[logicalID],
                          let response = physicalResponses[physicalID] else {
                        throw ServiceError.responseMissing("请求")
                    }
                    logicalResponses[logicalID] = response
                }
                return logicalResponses
            } catch {
                resetServer()
                cachedExecutable = nil
                if attempt == 1 { throw error }
            }
        }
        throw ServiceError.timedOut
    }

    private func ensureServer() throws {
        lifecycleLock.lock()
        let shutdownRequested = isShutdown
        let serverIsRunning = serverProcess?.isRunning == true
        lifecycleLock.unlock()
        if shutdownRequested { throw ServiceError.server("Codex Meter 正在退出") }
        if serverIsRunning { return }
        resetServer()

        guard let executable = cachedExecutable ?? codexExecutable else {
            throw ServiceError.binaryMissing
        }
        cachedExecutable = executable

        let process = Process()
        let input = Pipe()
        let output = Pipe()
        let errors = Pipe()
        let collector = JSONLineResponseCollector()
        process.executableURL = executable
        process.arguments = ["app-server"]
        process.standardInput = input
        process.standardOutput = output
        process.standardError = errors
        process.currentDirectoryURL = FileManager.default.homeDirectoryForCurrentUser

        var environment = ProcessInfo.processInfo.environment
        environment["PATH"] = "/usr/local/bin:/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin"
        process.environment = environment

        output.fileHandleForReading.readabilityHandler = { handle in
            collector.append(handle.availableData)
        }
        errors.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            guard !data.isEmpty, let self else { return }
            self.errorLock.lock()
            self.errorBuffer.append(data)
            if self.errorBuffer.count > 8192 {
                self.errorBuffer.removeFirst(self.errorBuffer.count - 8192)
            }
            self.errorLock.unlock()
        }
        process.terminationHandler = { _ in collector.close() }

        do {
            try process.run()
            lifecycleLock.lock()
            if isShutdown {
                lifecycleLock.unlock()
                if process.isRunning { process.terminate() }
                throw ServiceError.server("Codex Meter 正在退出")
            }
            serverProcess = process
            serverInput = input
            serverOutput = output
            serverErrors = errors
            responseCollector = collector
            lifecycleLock.unlock()
            errorLock.lock()
            errorBuffer.removeAll(keepingCapacity: true)
            errorLock.unlock()

            let initializeID = nextID()
            try send([
                "method": "initialize",
                "id": initializeID,
                "params": [
                    "clientInfo": [
                        "name": "codex_usage_widget",
                        "title": "Codex Meter",
                        "version": "1.4.1"
                    ]
                ]
            ], to: input.fileHandleForWriting)

            guard let initialized = collector.wait(for: [initializeID], timeout: 10),
                  let initializeResponse = initialized[initializeID] else {
                throw ServiceError.server("Codex 初始化超时")
            }
            if let error = initializeResponse["error"] as? [String: Any] {
                throw ServiceError.server(error["message"] as? String ?? "Codex 初始化失败")
            }
            try send(["method": "initialized", "params": [:]], to: input.fileHandleForWriting)
        } catch {
            resetServer()
            throw error
        }
    }

    private func nextID() -> Int {
        nextRequestID += 1
        return nextRequestID
    }

    private func serverErrorMessage() -> String? {
        errorLock.lock()
        defer { errorLock.unlock() }
        return String(data: errorBuffer, encoding: .utf8)?
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private func resetServer() {
        lifecycleLock.lock()
        let process = serverProcess
        let input = serverInput
        let output = serverOutput
        let errors = serverErrors
        let collector = responseCollector
        serverProcess = nil
        serverInput = nil
        serverOutput = nil
        serverErrors = nil
        responseCollector = nil
        lifecycleLock.unlock()

        output?.fileHandleForReading.readabilityHandler = nil
        errors?.fileHandleForReading.readabilityHandler = nil
        try? input?.fileHandleForWriting.close()
        collector?.close()
        if process?.isRunning == true { process?.terminate() }
    }

    func shutdown() {
        // App termination must not wait for an in-flight 20-second response wait.
        // Wake the active response wait before synchronizing with the work queue.
        lifecycleLock.lock()
        isShutdown = true
        let collector = responseCollector
        lifecycleLock.unlock()
        collector?.close()
        workQueue.sync {
            resetServer()
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
        let todayKey = stats.dailyTokens.last?.date ?? ""
        let todayConversations = stats.conversations.filter { $0.date == todayKey }
        let projectPaths = Set(stats.projectDailyTokens.map(\.projectPath) + stats.contextHealth.map(\.projectPath))
        let projectNames = displayNames(for: projectPaths)
        var taskRows: [String: [String: Any]] = [:]
        for conversation in stats.conversations {
            let taskID = conversation.threadId ?? conversation.contextWindowId ?? conversation.turnId
            let key = "\(conversation.date)\u{0}\(conversation.projectPath)\u{0}\(taskID)"
            var row = taskRows[key] ?? [
                "date": conversation.date,
                "projectKey": conversation.projectPath,
                "projectName": projectNames[conversation.projectPath] ?? "未识别项目",
                "projectKind": projectKind(for: conversation.projectPath),
                "taskId": taskID,
                "name": conversation.preview,
                "turns": 0,
                "tokens": 0,
                "lastActive": ISO8601DateFormatter().string(from: conversation.startedAt),
                "kind": "user"
            ]
            row["turns"] = ((row["turns"] as? NSNumber)?.intValue ?? 0) + 1
            row["tokens"] = ((row["tokens"] as? NSNumber)?.intValue ?? 0) + (conversation.tokens ?? 0)
            if let threadID = conversation.threadId,
               let threadName = threadNames[threadID] {
                row["name"] = threadName
            }
            if let lastValue = row["lastActive"] as? String,
               ISO8601DateFormatter().string(from: conversation.startedAt) > lastValue {
                row["lastActive"] = ISO8601DateFormatter().string(from: conversation.startedAt)
            }
            taskRows[key] = row
        }

        let projectDayTotals = Dictionary(uniqueKeysWithValues: stats.projectDailyTokens.map {
            ("\($0.date)\u{0}\($0.projectPath)", $0.tokens)
        })
        var attributed: [String: Int] = [:]
        for row in taskRows.values {
            guard let date = row["date"] as? String,
                  let project = row["projectKey"] as? String else { continue }
            attributed["\(date)\u{0}\(project)", default: 0] += (row["tokens"] as? NSNumber)?.intValue ?? 0
        }
        for (key, total) in projectDayTotals {
            let gap = max(0, total - (attributed[key] ?? 0))
            guard gap > 0 else { continue }
            let parts = key.split(separator: "\u{0}", maxSplits: 1).map(String.init)
            guard parts.count == 2 else { continue }
            taskRows["\(key)\u{0}system"] = [
                "date": parts[0],
                "projectKey": parts[1],
                "projectName": projectNames[parts[1]] ?? "未识别项目",
                "projectKind": projectKind(for: parts[1]),
                "taskId": "system",
                "name": "系统/子代理活动",
                "turns": 0,
                "tokens": gap,
                "lastActive": "",
                "kind": "system"
            ]
        }

        let projects: [[String: Any]] = projectNames.map { path, name in
            var project: [String: Any] = [
                "key": path,
                "name": name,
                "projectKind": projectKind(for: path)
            ]
            if path != Self.nonProjectKey { project["path"] = path }
            return project
        }.sorted { ($0["name"] as? String ?? "") < ($1["name"] as? String ?? "") }
        let contextRows: [[String: Any]] = stats.contextHealth
            .sorted { $0.updatedAt > $1.updatedAt }
            .prefix(20)
            .map { context in
                let usedPercent = min(100, max(0, Double(context.usedTokens) / Double(context.maxTokens) * 100))
                let taskID = context.threadId ?? context.contextWindowId ?? "context-\(Int(context.updatedAt.timeIntervalSince1970))"
                var row: [String: Any] = [
                    "taskId": taskID,
                    "name": context.threadId.flatMap { threadNames[$0] } ?? context.preview,
                    "projectKey": context.projectPath,
                    "projectName": projectNames[context.projectPath] ?? "未识别项目",
                    "projectKind": projectKind(for: context.projectPath),
                    "usedTokens": context.usedTokens,
                    "maxTokens": context.maxTokens,
                    "usedPercent": usedPercent,
                    "remainingPercent": max(0, 100 - usedPercent),
                    "status": contextHealthStatus(usedPercent: usedPercent),
                    "lastActive": ISO8601DateFormatter().string(from: context.updatedAt),
                    "compactions": context.compactions
                ]
                if let threadID = context.threadId { row["threadId"] = threadID }
                if let windowID = context.contextWindowId { row["contextWindowId"] = windowID }
                return row
            }
        return [
            "today": [
                "questions": todayConversations.count,
                "tokens": stats.tokens,
                "tokenSource": "local",
                "conversations": todayConversations.map { conversation in
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
                "dailyTokens": stats.dailyTokens.map {
                    ["date": $0.date, "tokens": $0.tokens, "source": $0.source]
                }
            ],
            "insights": [
                "localOnly": true,
                "projects": projects,
                "tasks": taskRows.values.sorted {
                    let leftDate = $0["date"] as? String ?? ""
                    let rightDate = $1["date"] as? String ?? ""
                    if leftDate != rightDate { return leftDate > rightDate }
                    return (($0["tokens"] as? NSNumber)?.intValue ?? 0) > (($1["tokens"] as? NSNumber)?.intValue ?? 0)
                }
            ],
            "contextHealth": [
                "source": "local",
                "sessions": contextRows
            ]
        ]
    }

    private func contextHealthStatus(usedPercent: Double) -> String {
        if usedPercent >= 90 { return "critical" }
        if usedPercent >= 80 { return "high" }
        if usedPercent >= 60 { return "attention" }
        return "healthy"
    }

    private func displayNames(for paths: Set<String>) -> [String: String] {
        let projectPaths = paths.filter { $0 != Self.nonProjectKey }
        let basenames = Dictionary(grouping: projectPaths) {
            URL(fileURLWithPath: $0).lastPathComponent.isEmpty ? "未识别项目" : URL(fileURLWithPath: $0).lastPathComponent
        }
        var result: [String: String] = [:]
        if paths.contains(Self.nonProjectKey) {
            result[Self.nonProjectKey] = Self.nonProjectName
        }
        for path in projectPaths {
            let url = URL(fileURLWithPath: path)
            let base = url.lastPathComponent.isEmpty ? "未识别项目" : url.lastPathComponent
            result[path] = (basenames[base]?.count ?? 0) > 1
                ? "\(base) — \(url.deletingLastPathComponent().lastPathComponent)"
                : base
        }
        return result
    }

    private func projectKind(for projectPath: String) -> String {
        projectPath == Self.nonProjectKey ? "non_project" : "project"
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
        let dates = (0..<90).compactMap { offset in
            calendar.date(byAdding: .day, value: offset - 89, to: today)
        }
        let keys = dates.map(formatter.string(from:))
        guard keys.contains(where: { values[$0] != nil }) else { return nil }

        let localValues = Dictionary(uniqueKeysWithValues: localDays.map { ($0.date, $0.tokens) })
        var localFallback = false
        let days = dates.map { date in
            let key = formatter.string(from: date)
            if let tokens = values[key] {
                return DailyTokenStats(date: key, tokens: tokens, source: "account")
            }
            let tokens = localValues[key] ?? 0
            if tokens > 0 { localFallback = true }
            return DailyTokenStats(date: key, tokens: tokens, source: tokens > 0 ? "local" : "empty")
        }
        guard let todayKey = keys.last else { return nil }
        return (
            days: days,
            localFallback: localFallback,
            todayFromAccount: values[todayKey] != nil
        )
    }

    private func currentContextPayload(from thread: [String: Any]) -> [String: Any]? {
        guard let threadID = thread["id"] as? String, !threadID.isEmpty else { return nil }
        let suppliedName = (thread["name"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines)
        let preview = (thread["preview"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines)
        let taskName = [suppliedName, preview]
            .compactMap { $0 }
            .first(where: { !$0.isEmpty }) ?? "未命名任务"
        var context: [String: Any] = [
            "source": "local",
            "trackingMode": "recent_conversation",
            "currentTaskId": threadID,
            "currentTaskName": taskName,
            "pollIntervalSeconds": 2
        ]

        guard let path = thread["path"] as? String, !path.isEmpty,
              let measurement = latestContextMeasurement(in: URL(fileURLWithPath: path)) else {
            return ["contextHealth": context]
        }
        let cwd = thread["cwd"] as? String ?? ""
        let projectPath = cwd.isEmpty ? Self.nonProjectKey : canonicalProjectPath(for: cwd)
        let projectName = displayNames(for: [projectPath])[projectPath] ?? Self.nonProjectName
        let usedPercent = min(100, max(0, Double(measurement.usedTokens) / Double(measurement.maxTokens) * 100))
        context["session"] = [
            "taskId": threadID,
            "threadId": threadID,
            "name": taskName,
            "projectKey": projectPath,
            "projectName": projectName,
            "projectKind": projectKind(for: projectPath),
            "usedTokens": measurement.usedTokens,
            "maxTokens": measurement.maxTokens,
            "usedPercent": usedPercent,
            "remainingPercent": max(0, 100 - usedPercent),
            "status": contextHealthStatus(usedPercent: usedPercent),
            "lastActive": ISO8601DateFormatter().string(from: measurement.updatedAt)
        ] as [String: Any]
        return ["contextHealth": context]
    }

    private func latestContextMeasurement(in file: URL) -> ContextMeasurement? {
        guard let attributes = try? FileManager.default.attributesOfItem(atPath: file.path),
              let modifiedAt = attributes[.modificationDate] as? Date else { return liveContexts[file.path]?.measurement }
        let size = (attributes[.size] as? NSNumber)?.intValue ?? 0
        let signature = SessionFileSignature(size: size, modifiedAt: modifiedAt)
        if let cached = liveContexts[file.path], cached.signature == signature {
            return cached.measurement
        }

        let previous = liveContexts[file.path]?.measurement
        guard let handle = try? FileHandle(forReadingFrom: file) else { return previous }
        defer { try? handle.close() }
        let maximumTailBytes: UInt64 = 1_048_576
        let end = (try? handle.seekToEnd()) ?? 0
        let start = end > maximumTailBytes ? end - maximumTailBytes : 0
        do {
            try handle.seek(toOffset: start)
        } catch {
            return previous
        }
        let contents = String(decoding: handle.readDataToEndOfFile(), as: UTF8.self)
        var measurement: ContextMeasurement?
        for line in contents.split(separator: "\n", omittingEmptySubsequences: true).reversed() {
            guard line.contains("\"token_count\""),
                  let data = String(line).data(using: .utf8),
                  let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  object["type"] as? String == "event_msg",
                  let payload = object["payload"] as? [String: Any],
                  payload["type"] as? String == "token_count",
                  let info = payload["info"] as? [String: Any],
                  let maximum = (info["model_context_window"] as? NSNumber)?.intValue,
                  maximum > 0,
                  let lastUsage = info["last_token_usage"] as? [String: Any],
                  let used = (lastUsage["total_tokens"] as? NSNumber)?.intValue,
                  let updatedAt = parseTimestamp(object["timestamp"] as? String) else { continue }
            measurement = ContextMeasurement(
                usedTokens: max(0, used),
                maxTokens: maximum,
                updatedAt: updatedAt
            )
            break
        }
        let resolved = measurement ?? previous
        liveContexts[file.path] = CachedLiveContext(signature: signature, measurement: resolved)
        return resolved
    }

    private func cachedLocalStats(now: Date = Date(), sessionsDirectory: URL? = nil) -> LocalStats {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = .current
        let today = calendar.startOfDay(for: now)
        let historyStart = calendar.date(byAdding: .day, value: -89, to: today) ?? today
        let formatter = dateFormatter()
        let dayKeys = (0..<90).compactMap { offset -> String? in
            guard let date = calendar.date(byAdding: .day, value: offset, to: historyStart) else { return nil }
            return formatter.string(from: date)
        }
        let todayKey = formatter.string(from: today)
        let windowKey = "\(TimeZone.current.identifier)|\(TimeZone.current.secondsFromGMT())|\(todayKey)"
        if sessionWindowKey != windowKey {
            sessionFiles.removeAll()
            sessionWindowKey = windowKey
            projectPathCache.removeAll()
        }

        let environment = ProcessInfo.processInfo.environment
        let codexHome: URL
        if let configuredHome = environment["CODEX_HOME"], !configuredHome.isEmpty {
            codexHome = URL(fileURLWithPath: configuredHome)
        } else {
            codexHome = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".codex")
        }
        let directory = sessionsDirectory ?? codexHome.appendingPathComponent("sessions")
        guard let enumerator = FileManager.default.enumerator(
            at: directory,
            includingPropertiesForKeys: [.contentModificationDateKey, .fileSizeKey],
            options: [.skipsHiddenFiles]
        ) else {
            sessionFiles.removeAll()
            return LocalStats(
                dailyTokens: dayKeys.map { DailyTokenStats(date: $0, tokens: 0, source: "empty") },
                conversations: [],
                projectDailyTokens: [],
                contextHealth: []
            )
        }

        var totals = Dictionary(uniqueKeysWithValues: dayKeys.map { ($0, 0) })
        var conversations: [String: ConversationStats] = [:]
        var projectTotals: [String: Int] = [:]
        var contextHealth: [String: ContextHealthStats] = [:]
        var seen = Set<String>()
        for case let file as URL in enumerator where file.pathExtension == "jsonl" {
            guard let values = try? file.resourceValues(forKeys: [.contentModificationDateKey, .fileSizeKey]),
                  let modifiedAt = values.contentModificationDate,
                  modifiedAt >= historyStart else { continue }
            let signature = SessionFileSignature(size: values.fileSize ?? 0, modifiedAt: modifiedAt)
            let path = file.path
            seen.insert(path)

            let stats: LocalStats
            if let cached = sessionFiles[path], cached.signature == signature {
                stats = cached.stats
            } else if let parsed = parseSessionFile(file, dayKeys: dayKeys, todayKey: todayKey, formatter: formatter) {
                stats = parsed
                sessionFiles[path] = CachedSessionFile(signature: signature, stats: parsed)
            } else {
                sessionFiles.removeValue(forKey: path)
                continue
            }

            for day in stats.dailyTokens where totals[day.date] != nil {
                totals[day.date, default: 0] += day.tokens
            }
            for conversation in stats.conversations {
                conversations[conversation.turnId] = conversation
            }
            for projectDay in stats.projectDailyTokens {
                projectTotals["\(projectDay.date)\u{0}\(projectDay.projectPath)", default: 0] += projectDay.tokens
            }
            for context in stats.contextHealth {
                let key = context.threadId ?? context.contextWindowId ?? path
                if context.updatedAt > (contextHealth[key]?.updatedAt ?? .distantPast) {
                    contextHealth[key] = context
                }
            }
        }
        let stalePaths = sessionFiles.keys.filter { !seen.contains($0) }
        for path in stalePaths {
            sessionFiles.removeValue(forKey: path)
        }

        return LocalStats(
            dailyTokens: dayKeys.map {
                let tokens = totals[$0] ?? 0
                return DailyTokenStats(date: $0, tokens: tokens, source: tokens > 0 ? "local" : "empty")
            },
            conversations: conversations.values.sorted { $0.startedAt > $1.startedAt },
            projectDailyTokens: projectTotals.compactMap { key, tokens in
                let parts = key.split(separator: "\u{0}", maxSplits: 1).map(String.init)
                guard parts.count == 2 else { return nil }
                return ProjectDailyStats(date: parts[0], projectPath: parts[1], tokens: tokens)
            },
            contextHealth: contextHealth.values.sorted { $0.updatedAt > $1.updatedAt }
        )
    }

#if CODEX_METER_TESTING
    func localPayloadForTesting(sessionsDirectory: URL, now: Date) -> [String: Any] {
        localPayload(from: cachedLocalStats(now: now, sessionsDirectory: sessionsDirectory))
    }

    func contextMeasurementForTesting(file: URL) -> [String: Any]? {
        guard let measurement = latestContextMeasurement(in: file) else { return nil }
        return [
            "usedTokens": measurement.usedTokens,
            "maxTokens": measurement.maxTokens,
            "lastActive": ISO8601DateFormatter().string(from: measurement.updatedAt)
        ]
    }
#endif

    private func parseSessionFile(
        _ file: URL,
        dayKeys: [String],
        todayKey _: String,
        formatter: DateFormatter
    ) -> LocalStats? {
        var dailyTokens = Dictionary(uniqueKeysWithValues: dayKeys.map { ($0, 0) })
        var conversations: [String: ConversationStats] = [:]
        var projectDailyTokens: [String: Int] = [:]
        var contextHealth: ContextHealthStats?
        guard let contents = try? String(contentsOf: file, encoding: .utf8) else { return nil }
            var threadId: String?
            var contextWindowId: String?
            var currentTurnId: String?
            var projectPath = Self.nonProjectKey
            var includeConversations = true
            var previousTotal = 0
            var usageRecordSinceTokenCount = false
            var compactions = 0

            contents.enumerateLines { line, _ in
                guard let data = line.data(using: .utf8),
                      let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                      let rootType = object["type"] as? String,
                      let payload = object["payload"] as? [String: Any] else { return }

                if rootType == "session_meta" {
                    threadId = payload["id"] as? String ?? threadId
                    includeConversations = self.isUserConversationSession(payload)
                    if let cwd = payload["cwd"] as? String, !cwd.isEmpty {
                        projectPath = self.canonicalProjectPath(for: cwd)
                    }
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
                              includeConversations,
                              dailyTokens[formatter.string(from: startedAt)] != nil else { return }
                        let date = formatter.string(from: startedAt)
                        conversations[turnId] = ConversationStats(
                            turnId: turnId,
                            threadId: threadId,
                            contextWindowId: contextWindowId,
                            startedAt: startedAt,
                            date: date,
                            projectPath: projectPath,
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
                        if var health = contextHealth, health.preview == "未命名任务" {
                            health.preview = conversation.preview
                            contextHealth = health
                        }
                        return
                    }

                    guard eventType == "token_count",
                          let info = payload["info"] as? [String: Any],
                          let totalUsage = info["total_token_usage"] as? [String: Any],
                          let total = (totalUsage["total_tokens"] as? NSNumber)?.intValue else { return }

                    if includeConversations,
                       let maximum = (info["model_context_window"] as? NSNumber)?.intValue,
                       maximum > 0,
                       let lastUsage = info["last_token_usage"] as? [String: Any],
                       let used = (lastUsage["total_tokens"] as? NSNumber)?.intValue,
                       let updatedAt = self.parseTimestamp(object["timestamp"] as? String) {
                        contextHealth = ContextHealthStats(
                            threadId: threadId,
                            contextWindowId: contextWindowId,
                            projectPath: projectPath,
                            preview: contextHealth?.preview ?? conversations[currentTurnId ?? ""]?.preview ?? "未命名任务",
                            usedTokens: max(0, used),
                            maxTokens: maximum,
                            updatedAt: updatedAt,
                            compactions: compactions
                        )
                    }
                    let delta = total >= previousTotal ? total - previousTotal : total
                    previousTotal = total
                    if usageRecordSinceTokenCount {
                        usageRecordSinceTokenCount = false
                        return
                    }
                    guard let timestamp = self.parseTimestamp(object["timestamp"] as? String) else { return }
                    let dateKey = formatter.string(from: timestamp)
                    if dailyTokens[dateKey] != nil {
                        dailyTokens[dateKey, default: 0] += max(0, delta)
                        projectDailyTokens["\(dateKey)\u{0}\(projectPath)", default: 0] += max(0, delta)
                    }
                    if let turnId = currentTurnId, var conversation = conversations[turnId] {
                        conversation.tokens = (conversation.tokens ?? 0) + max(0, delta)
                        conversations[turnId] = conversation
                    }
                    return
                }

                if rootType == "compacted" {
                    compactions += 1
                    contextWindowId = payload["window_id"] as? String ?? contextWindowId
                    if var health = contextHealth {
                        health.contextWindowId = contextWindowId
                        health.compactions = compactions
                        contextHealth = health
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
                            projectDailyTokens["\(dateKey)\u{0}\(projectPath)", default: 0] += max(0, responseTokens)
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
                    if var health = contextHealth, health.preview == "未命名任务" {
                        health.preview = conversation.preview
                        contextHealth = health
                    }
                }
            }

        return LocalStats(
            dailyTokens: dayKeys.map {
                let tokens = dailyTokens[$0] ?? 0
                return DailyTokenStats(date: $0, tokens: tokens, source: tokens > 0 ? "local" : "empty")
            },
            conversations: conversations.values.sorted { $0.startedAt > $1.startedAt },
            projectDailyTokens: projectDailyTokens.compactMap { key, tokens in
                let parts = key.split(separator: "\u{0}", maxSplits: 1).map(String.init)
                guard parts.count == 2 else { return nil }
                return ProjectDailyStats(date: parts[0], projectPath: parts[1], tokens: tokens)
            },
            contextHealth: contextHealth.map { [$0] } ?? []
        )
    }

    private func canonicalProjectPath(for cwd: String) -> String {
        if let cached = projectPathCache[cwd] { return cached }
        let normalized = URL(fileURLWithPath: cwd).standardizedFileURL.path
        var candidate = URL(fileURLWithPath: normalized, isDirectory: true)
        while candidate.path != "/" {
            let marker = candidate.appendingPathComponent(".git")
            var isDirectory: ObjCBool = false
            if FileManager.default.fileExists(atPath: marker.path, isDirectory: &isDirectory) {
                if isDirectory.boolValue {
                    projectPathCache[cwd] = candidate.path
                    return candidate.path
                }
                if let contents = try? String(contentsOf: marker, encoding: .utf8),
                   let range = contents.range(of: "/.git/worktrees/") {
                    let root = String(contents[..<range.lowerBound])
                    projectPathCache[cwd] = root
                    return root
                }
                projectPathCache[cwd] = candidate.path
                return candidate.path
            }
            candidate.deleteLastPathComponent()
        }
        projectPathCache[cwd] = Self.nonProjectKey
        return Self.nonProjectKey
    }

    private func parseTimestamp(_ value: String?) -> Date? {
        guard let value else { return nil }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = formatter.date(from: value) { return date }
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.date(from: value)
    }

    private func isUserConversationSession(_ payload: [String: Any]) -> Bool {
        if let threadSource = payload["thread_source"] as? String,
           !threadSource.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return threadSource.caseInsensitiveCompare("user") == .orderedSame
        }
        if let source = payload["source"] as? [String: Any], source["subagent"] != nil {
            return false
        }
        return true
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
