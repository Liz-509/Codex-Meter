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
            let localStats = self.localStatsToday()
            var payload: [String: Any] = [
                "today": [
                    "questions": localStats.questions,
                    "tokens": localStats.tokens
                ]
            ]

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

                for (key, value) in limits {
                    payload[key] = value
                }

                var today = payload["today"] as? [String: Any] ?? [:]
                if localStats.tokens == 0,
                   let buckets = usage["dailyUsageBuckets"] as? [[String: Any]] {
                    let date = self.localDateString()
                    today["tokens"] = buckets.first(where: {
                        $0["startDate"] as? String == date
                    })?["tokens"] ?? 0
                }
                payload["today"] = today
                payload["source"] = "Codex App Server"
            } catch {
                payload["error"] = error.localizedDescription
            }

            DispatchQueue.main.async {
                completion(payload)
            }
        }
    }

    private func readAccountData() throws -> [Int: [String: Any]] {
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
                        "version": "1.1.2"
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
            try send(["method": "account/rateLimits/read", "id": 1], to: input.fileHandleForWriting)
            try send(["method": "account/usage/read", "id": 2], to: input.fileHandleForWriting)

            guard let responses = collector.wait(for: [1, 2], timeout: 20) else {
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
        if let customPath = ProcessInfo.processInfo.environment["CODEX_BINARY"],
           !customPath.isEmpty {
            candidates.append(URL(fileURLWithPath: customPath))
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

    private func localDateString() -> String {
        let formatter = DateFormatter()
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = .current
        formatter.dateFormat = "yyyy-MM-dd"
        return formatter.string(from: Date())
    }

    private func localStatsToday() -> (questions: Int, tokens: Int) {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = .current
        let start = calendar.startOfDay(for: Date())
        let end = calendar.date(byAdding: .day, value: 1, to: start) ?? Date.distantFuture

        let isoFormatter = ISO8601DateFormatter()
        isoFormatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let startTimestamp = isoFormatter.string(from: start)
        let endTimestamp = isoFormatter.string(from: end)

        let directory = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".codex/sessions")

        guard let enumerator = FileManager.default.enumerator(
            at: directory,
            includingPropertiesForKeys: [.contentModificationDateKey],
            options: [.skipsHiddenFiles]
        ) else { return (0, 0) }

        var questions = 0
        var tokens = 0
        for case let file as URL in enumerator where file.pathExtension == "jsonl" {
            guard let values = try? file.resourceValues(forKeys: [.contentModificationDateKey]),
                  let modifiedAt = values.contentModificationDate,
                  modifiedAt >= start else { continue }
            guard let contents = try? String(contentsOf: file, encoding: .utf8) else { continue }
            var previousTotal = 0

            contents.enumerateLines { line, _ in
                guard let data = line.data(using: .utf8),
                      let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                      object["type"] as? String == "event_msg",
                      let event = object["payload"] as? [String: Any] else { return }

                let timestamp = object["timestamp"] as? String ?? ""
                let isToday = timestamp >= startTimestamp && timestamp < endTimestamp

                if event["type"] as? String == "task_started", isToday {
                    questions += 1
                }

                guard event["type"] as? String == "token_count",
                      let info = event["info"] as? [String: Any],
                      let totalUsage = info["total_token_usage"] as? [String: Any],
                      let total = (totalUsage["total_tokens"] as? NSNumber)?.intValue else { return }

                if isToday {
                    tokens += total >= previousTotal ? total - previousTotal : total
                }
                previousTotal = total
            }
        }
        return (questions, tokens)
    }
}
