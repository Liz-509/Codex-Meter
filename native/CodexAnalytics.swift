import Foundation

struct CodexQuotaReading {
    let key: String
    let label: String
    let remainingPercent: Double
    let resetsAt: TimeInterval?
}

struct CodexQuotaEvent {
    enum Kind: String {
        case threshold
        case exhausted
        case restored
    }

    let kind: Kind
    let reading: CodexQuotaReading
    let threshold: Int?
    let deduplicationKey: String
}

private struct CodexQuotaSample: Codable {
    let timestamp: TimeInterval
    let window: String
    let remainingPercent: Double
    let resetsAt: TimeInterval?
}

final class CodexQuotaMonitor {
    private let fileURL: URL
    private let defaults: UserDefaults
    private var samples: [CodexQuotaSample]
    private let encoder = JSONEncoder()
    private let decoder = JSONDecoder()

    init(defaults: UserDefaults = .standard, fileURL overrideFileURL: URL? = nil) {
        self.defaults = defaults
        if let overrideFileURL {
            fileURL = overrideFileURL
        } else {
            let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
                ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support")
            let directory = support.appendingPathComponent("Codex Meter", isDirectory: true)
            try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            fileURL = directory.appendingPathComponent("usage-samples.json")
        }
        if let data = try? Data(contentsOf: fileURL),
           let decoded = try? decoder.decode([CodexQuotaSample].self, from: data) {
            samples = decoded
        } else {
            samples = []
        }
    }

    func process(_ payload: [String: Any], now: Date = Date()) -> (
        forecast: [String: Any],
        readings: [CodexQuotaReading],
        events: [CodexQuotaEvent]
    ) {
        let readings = Self.readings(from: payload)
        let nowValue = now.timeIntervalSince1970
        let events = quotaEvents(for: readings)

        for reading in readings {
            record(reading, at: nowValue)
        }
        pruneAndPersist(now: nowValue)

        var forecast: [String: Any] = [:]
        for reading in readings {
            forecast[reading.key] = forecastPayload(for: reading, now: nowValue)
        }
        return (forecast, readings, events)
    }

    static func readings(from payload: [String: Any]) -> [CodexQuotaReading] {
        let limits: [String: Any]
        if let byID = payload["rateLimitsByLimitId"] as? [String: Any],
           let codex = byID["codex"] as? [String: Any] {
            limits = codex
        } else if let rateLimits = payload["rateLimits"] as? [String: Any] {
            limits = rateLimits
        } else {
            limits = payload
        }

        return [("primary", "5 小时额度"), ("secondary", "每周额度")].compactMap { key, fallback in
            guard let bucket = limits[key] as? [String: Any] ?? payload[key] as? [String: Any] else { return nil }
            let remaining: Double?
            if let value = bucket["remainingPercent"] as? NSNumber {
                remaining = value.doubleValue
            } else if let value = bucket["usedPercent"] as? NSNumber {
                remaining = 100 - value.doubleValue
            } else {
                remaining = nil
            }
            guard let remaining else { return nil }
            return CodexQuotaReading(
                key: key,
                label: bucket["label"] as? String ?? fallback,
                remainingPercent: min(100, max(0, remaining)),
                resetsAt: (bucket["resetsAt"] as? NSNumber)?.doubleValue
            )
        }
    }

    private func record(_ reading: CodexQuotaReading, at now: TimeInterval) {
        let last = samples.last(where: { $0.window == reading.key })
        let elapsed = now - (last?.timestamp ?? 0)
        let changed = abs((last?.remainingPercent ?? -1) - reading.remainingPercent) >= 0.01
        let cycleChanged = last?.resetsAt != reading.resetsAt
        guard last == nil || elapsed >= 300 || changed || cycleChanged else { return }
        samples.append(CodexQuotaSample(
            timestamp: now,
            window: reading.key,
            remainingPercent: reading.remainingPercent,
            resetsAt: reading.resetsAt
        ))
    }

    private func pruneAndPersist(now: TimeInterval) {
        let cutoff = now - 14 * 86_400
        samples = Array(samples.filter { $0.timestamp >= cutoff }.suffix(5_000))
        guard let data = try? encoder.encode(samples) else { return }
        try? data.write(to: fileURL, options: .atomic)
    }

    private func forecastPayload(for reading: CodexQuotaReading, now: TimeInterval) -> [String: Any] {
        let lookback: TimeInterval = reading.key == "primary" ? 3 * 3_600 : 7 * 86_400
        let minimumSpan: TimeInterval = reading.key == "primary" ? 15 * 60 : 6 * 3_600
        let cycleSamples = samples.filter {
            $0.window == reading.key &&
            $0.timestamp >= now - lookback &&
            sameCycle($0.resetsAt, reading.resetsAt)
        }.sorted { $0.timestamp < $1.timestamp }

        guard cycleSamples.count >= 4,
              let first = cycleSamples.first,
              let last = cycleSamples.last,
              last.timestamp - first.timestamp >= minimumSpan,
              first.remainingPercent - last.remainingPercent >= 2 else {
            return [
                "status": "insufficient",
                "message": "暂无足够数据",
                "confidence": "low"
            ]
        }

        let meanX = cycleSamples.map(\.timestamp).reduce(0, +) / Double(cycleSamples.count)
        let meanY = cycleSamples.map(\.remainingPercent).reduce(0, +) / Double(cycleSamples.count)
        let numerator = cycleSamples.reduce(0.0) { result, sample in
            result + (sample.timestamp - meanX) * (sample.remainingPercent - meanY)
        }
        let denominator = cycleSamples.reduce(0.0) { result, sample in
            result + pow(sample.timestamp - meanX, 2)
        }
        let ratePerHour = denominator > 0 ? max(0, -(numerator / denominator) * 3_600) : 0
        guard ratePerHour > 0.01 else {
            return [
                "status": "steady",
                "message": "近期用量稳定",
                "ratePerHour": 0,
                "confidence": "low"
            ]
        }

        let exhaustsAt = now + reading.remainingPercent / ratePerHour * 3_600
        let span = last.timestamp - first.timestamp
        let drop = first.remainingPercent - last.remainingPercent
        let confidence: String
        if reading.key == "primary" {
            confidence = span >= 3_600 && drop >= 10 ? "high" : (span >= 1_800 && drop >= 5 ? "medium" : "low")
        } else {
            confidence = span >= 48 * 3_600 && drop >= 10 ? "high" : (span >= 24 * 3_600 && drop >= 5 ? "medium" : "low")
        }
        if let resetsAt = reading.resetsAt, exhaustsAt >= resetsAt {
            return [
                "status": "safe_until_reset",
                "message": "预计可用至本次重置",
                "ratePerHour": ratePerHour,
                "confidence": confidence
            ]
        }
        return [
            "status": "will_deplete",
            "message": "按近期速度可能提前耗尽",
            "ratePerHour": ratePerHour,
            "estimatedExhaustsAt": exhaustsAt,
            "confidence": confidence
        ]
    }

    private func sameCycle(_ left: TimeInterval?, _ right: TimeInterval?) -> Bool {
        switch (left, right) {
        case let (left?, right?): return abs(left - right) < 60
        case (nil, nil): return true
        default: return false
        }
    }

    private func quotaEvents(for readings: [CodexQuotaReading]) -> [CodexQuotaEvent] {
        var events: [CodexQuotaEvent] = []
        var delivered = Set(defaults.stringArray(forKey: "quotaDeliveredEvents") ?? [])
        for reading in readings {
            let prefix = "quotaPrevious.\(reading.key)"
            guard defaults.object(forKey: "\(prefix).remaining") != nil else {
                defaults.set(reading.remainingPercent, forKey: "\(prefix).remaining")
                if let resetsAt = reading.resetsAt { defaults.set(resetsAt, forKey: "\(prefix).resetsAt") }
                continue
            }
            let previous = defaults.double(forKey: "\(prefix).remaining")
            let previousReset = defaults.object(forKey: "\(prefix).resetsAt") == nil
                ? nil
                : defaults.double(forKey: "\(prefix).resetsAt")
            let cycle = String(Int(reading.resetsAt ?? 0))

            let crossed = [20, 10, 5].filter { previous > Double($0) && reading.remainingPercent <= Double($0) }
            if let threshold = crossed.min() {
                let key = "\(reading.key):\(cycle):threshold:\(threshold)"
                if !delivered.contains(key) {
                    events.append(CodexQuotaEvent(kind: .threshold, reading: reading, threshold: threshold, deduplicationKey: key))
                }
                for value in crossed { delivered.insert("\(reading.key):\(cycle):threshold:\(value)") }
            }
            if previous > 0.5 && reading.remainingPercent <= 0.5 {
                let key = "\(reading.key):\(cycle):exhausted"
                if delivered.insert(key).inserted {
                    events.append(CodexQuotaEvent(kind: .exhausted, reading: reading, threshold: nil, deduplicationKey: key))
                }
            }
            let resetAdvanced = (reading.resetsAt ?? 0) > (previousReset ?? 0) + 60
            if resetAdvanced && previous < 20 && reading.remainingPercent >= previous + 5 {
                let key = "\(reading.key):\(cycle):restored"
                if delivered.insert(key).inserted {
                    events.append(CodexQuotaEvent(kind: .restored, reading: reading, threshold: nil, deduplicationKey: key))
                }
            }
            defaults.set(reading.remainingPercent, forKey: "\(prefix).remaining")
            if let resetsAt = reading.resetsAt { defaults.set(resetsAt, forKey: "\(prefix).resetsAt") }
        }
        defaults.set(Array(delivered.suffix(240)), forKey: "quotaDeliveredEvents")
        return events
    }
}

enum CodexReportGenerator {
    static func markdown(from payload: [String: Any], now: Date = Date()) -> String {
        let report = reportData(from: payload, now: now)
        var lines = [
            "# Codex Meter 最近 7 天报告",
            "",
            "- 报告日期：\(report.endDate)",
            "- Tokens：\(report.totalTokens)",
            "- 对话轮次：\(report.totalTurns)",
            "- 活跃项目：\(report.activeProjectCount)",
            "",
            "## 每日趋势",
            "",
            "| 日期 | Tokens | 数据来源 |",
            "| --- | ---: | --- |"
        ]
        for day in report.days {
            lines.append("| \(day.date) | \(day.tokens) | \(sourceLabel(day.source)) |")
        }
        lines += ["", "## 项目排行", "", "| 项目 | Tokens | 轮次 |", "| --- | ---: | ---: |"]
        for project in report.projectTotals.prefix(20) {
            lines.append("| \(markdownEscape(project.name)) | \(project.tokens) | \(project.turns) |")
        }
        lines += ["", "## Top 任务", "", "| 任务 | 项目 | Tokens | 轮次 |", "| --- | --- | ---: | ---: |"]
        for task in report.taskTotals.prefix(20) {
            lines.append("| \(markdownEscape(task.name)) | \(markdownEscape(task.project)) | \(task.tokens) | \(task.turns) |")
        }
        lines += [
            "",
            report.hasRemote
                ? "> 整体每日用量优先采用账户数据，缺失日期由设备会话补齐；项目和任务统计来自本机与当前可达的 SSH 服务器。"
                : "> 整体每日用量优先采用账户数据，缺失日期由本地会话补齐；项目和任务统计仅来自本机 Codex 会话。",
            ""
        ]
        return lines.joined(separator: "\n")
    }

    static func csv(from payload: [String: Any], now: Date = Date()) -> Data {
        let report = reportData(from: payload, now: now)
        var rows = [["日期", "项目", "任务", "轮次", "Tokens", "最后活动时间", "数据来源"]]
        rows += report.taskRows.map {
            [$0.date, $0.project, $0.name, String($0.turns), String($0.tokens), $0.lastActive, $0.sourceHost ?? "本机"]
        }
        let text = rows.map { $0.map(csvEscape).joined(separator: ",") }.joined(separator: "\r\n") + "\r\n"
        return Data(([0xEF, 0xBB, 0xBF] as [UInt8]) + Array(text.utf8))
    }

    private struct Day { let date: String; let tokens: Int; let source: String }
    private struct TaskRow { let date: String; let project: String; let projectKind: String; let name: String; let turns: Int; let tokens: Int; let lastActive: String; let sourceHost: String? }
    private struct Total { let name: String; let project: String; let turns: Int; let tokens: Int }
    private struct Report { let endDate: String; let totalTokens: Int; let totalTurns: Int; let activeProjectCount: Int; let days: [Day]; let taskRows: [TaskRow]; let projectTotals: [Total]; let taskTotals: [Total]; let hasRemote: Bool }

    private static func reportData(from payload: [String: Any], now: Date) -> Report {
        let formatter = DateFormatter()
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = .current
        formatter.dateFormat = "yyyy-MM-dd"
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = .current
        let end = calendar.startOfDay(for: now)
        let start = calendar.date(byAdding: .day, value: -6, to: end) ?? end
        let startKey = formatter.string(from: start)
        let endKey = formatter.string(from: end)

        let history = payload["history"] as? [String: Any]
        let daily = history?["dailyTokens"] as? [[String: Any]] ?? []
        let days = daily.compactMap { item -> Day? in
            guard let date = item["date"] as? String, date >= startKey, date <= endKey else { return nil }
            return Day(date: date, tokens: (item["tokens"] as? NSNumber)?.intValue ?? 0, source: item["source"] as? String ?? "local")
        }
        let insights = payload["insights"] as? [String: Any]
        let tasks = insights?["tasks"] as? [[String: Any]] ?? []
        let taskRows = tasks.compactMap { item -> TaskRow? in
            guard let date = item["date"] as? String, date >= startKey, date <= endKey else { return nil }
            return TaskRow(
                date: date,
                project: item["projectName"] as? String ?? "未识别项目",
                projectKind: item["projectKind"] as? String ?? "project",
                name: item["name"] as? String ?? "未命名任务",
                turns: (item["turns"] as? NSNumber)?.intValue ?? 0,
                tokens: (item["tokens"] as? NSNumber)?.intValue ?? 0,
                lastActive: item["lastActive"] as? String ?? "",
                sourceHost: item["sourceHost"] as? String
            )
        }.sorted { $0.date == $1.date ? $0.tokens > $1.tokens : $0.date < $1.date }

        var projectValues: [String: (turns: Int, tokens: Int)] = [:]
        var taskValues: [String: (project: String, name: String, turns: Int, tokens: Int)] = [:]
        for row in taskRows {
            let project = projectValues[row.project] ?? (0, 0)
            projectValues[row.project] = (project.turns + row.turns, project.tokens + row.tokens)
            let key = "\(row.project)\u{0}\(row.name)"
            let task = taskValues[key] ?? (row.project, row.name, 0, 0)
            taskValues[key] = (task.project, task.name, task.turns + row.turns, task.tokens + row.tokens)
        }
        let projectTotals = projectValues.map { Total(name: $0.key, project: $0.key, turns: $0.value.turns, tokens: $0.value.tokens) }.sorted { $0.tokens > $1.tokens }
        let taskTotals = taskValues.values.map { Total(name: $0.name, project: $0.project, turns: $0.turns, tokens: $0.tokens) }.sorted { $0.tokens > $1.tokens }
        let activeProjectCount = Set(taskRows.filter { $0.projectKind != "non_project" }.map(\.project)).count
        return Report(
            endDate: endKey,
            totalTokens: days.reduce(0) { $0 + $1.tokens },
            totalTurns: taskRows.reduce(0) { $0 + $1.turns },
            activeProjectCount: activeProjectCount,
            days: days,
            taskRows: taskRows,
            projectTotals: projectTotals,
            taskTotals: taskTotals,
            hasRemote: taskRows.contains { $0.sourceHost != nil }
        )
    }

    private static func markdownEscape(_ value: String) -> String {
        value.replacingOccurrences(of: "|", with: "\\|").replacingOccurrences(of: "\n", with: " ")
    }

    private static func csvEscape(_ value: String) -> String {
        guard value.contains(",") || value.contains("\"") || value.contains("\n") || value.contains("\r") else { return value }
        return "\"\(value.replacingOccurrences(of: "\"", with: "\"\""))\""
    }

    private static func sourceLabel(_ source: String) -> String {
        switch source {
        case "account": return "账户"
        case "empty": return "无记录"
        default: return "本机"
        }
    }
}
