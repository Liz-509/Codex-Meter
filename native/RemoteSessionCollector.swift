import Foundation

struct RemoteCodexHost: Equatable {
    let id: String
    let displayName: String
    let destination: String
    let port: Int?
    let identity: String?
}

struct RemoteSessionSnapshot {
    let host: RemoteCodexHost
    let dailyTokens: [String: Int]
    let conversations: [[String: Any]]
    let projectDailyTokens: [[String: Any]]
    let contextHealth: [[String: Any]]
}

struct RemoteCodexMetadata {
    let projectPaths: [String: String]
    let projectNames: [String: String]
    let threadProjectPaths: [String: String]
    let threadNames: [String: String]
}

final class RemoteSessionCollector {
    private static let script = #"""
import datetime
import json
import os
import pathlib
import sys
import time
import uuid

CONFIG = __CONFIG__
day_keys = set(CONFIG["dayKeys"])
history_start = float(CONFIG["historyStart"])
timezone = CONFIG.get("timezone")
project_names = {
    str(pathlib.Path(path).expanduser().resolve(strict=False)): name
    for path, name in CONFIG.get("projectNames", {}).items()
}
configured_projects = sorted({
    str(pathlib.Path(path).expanduser().resolve(strict=False))
    for path in CONFIG.get("projects", [])
}, key=len, reverse=True)
thread_projects = {
    thread_id: str(pathlib.Path(path).expanduser().resolve(strict=False))
    for thread_id, path in CONFIG.get("threadProjects", {}).items()
}
thread_names = CONFIG.get("threadNames", {})
if timezone:
    os.environ["TZ"] = timezone
    if hasattr(time, "tzset"):
        time.tzset()

def timestamp(value):
    if not isinstance(value, str) or not value:
        return None
    try:
        return datetime.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except (TypeError, ValueError):
        return None

def day(value):
    parsed = timestamp(value)
    return parsed.astimezone().strftime("%Y-%m-%d") if parsed else None

def preview(value):
    normalized = " ".join(str(value).split())
    return normalized if len(normalized) <= 160 else normalized[:160].rstrip() + "…"

def user_text(payload):
    content = payload.get("content")
    if not isinstance(content, list):
        return ""
    values = []
    ignored_prefixes = (
        "<app-context>",
        "<skills_instructions>",
        "<permissions instructions>",
        "<collaboration_mode>",
        "<apps_instructions>",
        "<plugins_instructions>",
        "<environment_context>",
    )
    for item in content:
        if not isinstance(item, dict) or item.get("type") not in ("input_text", "text"):
            continue
        text = item.get("text")
        if not isinstance(text, str) or not text.strip():
            continue
        stripped = text.lstrip()
        if stripped.startswith(ignored_prefixes):
            continue
        values.append(text)
    return " ".join(values)

project_cache = {}
def project_root(cwd):
    if not isinstance(cwd, str) or not cwd:
        return "__non_project__"
    if cwd in project_cache:
        return project_cache[cwd]
    candidate = pathlib.Path(cwd).expanduser().resolve(strict=False)
    normalized = str(candidate)
    for configured in configured_projects:
        if normalized == configured or normalized.startswith(configured + os.sep):
            project_cache[cwd] = configured
            return configured
    while candidate != candidate.parent:
        marker = candidate / ".git"
        if marker.exists():
            if marker.is_file():
                try:
                    pointer = marker.read_text(encoding="utf-8", errors="ignore")
                    marker_text = "/.git/worktrees/"
                    if marker_text in pointer:
                        result = pointer.split(marker_text, 1)[0].split("gitdir:", 1)[-1].strip()
                        project_cache[cwd] = result
                        return result
                except OSError:
                    pass
            result = str(candidate)
            project_cache[cwd] = result
            return result
        candidate = candidate.parent
    project_cache[cwd] = "__non_project__"
    return "__non_project__"

def is_user_session(payload):
    source = payload.get("thread_source")
    if isinstance(source, str) and source.strip():
        return source.lower() == "user"
    nested = payload.get("source")
    return not (isinstance(nested, dict) and nested.get("subagent") is not None)

daily = {key: 0 for key in day_keys}
project_daily = {}
conversations = {}
contexts = []
codex_home = pathlib.Path(os.environ.get("CODEX_HOME") or "~/.codex").expanduser()
sessions = codex_home / "sessions"
try:
    with (codex_home / "session_index.jsonl").open("r", encoding="utf-8", errors="replace") as index_lines:
        for line in index_lines:
            try:
                item = json.loads(line)
                thread_id = item.get("id")
                thread_name = item.get("thread_name")
                if isinstance(thread_id, str) and thread_id and isinstance(thread_name, str) and thread_name.strip():
                    thread_names[thread_id] = thread_name.strip()
            except (TypeError, ValueError):
                continue
except OSError:
    pass

try:
    files = sessions.rglob("*.jsonl") if sessions.is_dir() else []
    for path in files:
        try:
            if path.stat().st_mtime < history_start:
                continue
            lines = path.open("r", encoding="utf-8", errors="replace")
        except OSError:
            continue

        thread_id = None
        window_id = None
        current_turn = None
        project = "__non_project__"
        include_conversations = True
        previous_total = 0
        usage_record_since_count = False
        compactions = 0
        context = None
        current_turn_active = False
        turn_totals = {}

        with lines:
            for line in lines:
                try:
                    obj = json.loads(line)
                    root_type = obj.get("type")
                    payload = obj.get("payload")
                    if not isinstance(payload, dict):
                        continue
                except (TypeError, ValueError):
                    continue

                if root_type == "session_meta":
                    thread_id = payload.get("id") or thread_id
                    include_conversations = is_user_session(payload)
                    if thread_id in thread_projects:
                        project = thread_projects[thread_id]
                    elif payload.get("cwd"):
                        project = project_root(payload["cwd"])
                    context_window = payload.get("context_window")
                    if isinstance(context_window, dict):
                        window_id = context_window.get("window_id") or window_id
                    continue

                if root_type == "event_msg":
                    event_type = payload.get("type")
                    if event_type == "task_started":
                        current_turn = payload.get("turn_id") or str(uuid.uuid4())
                        current_turn_active = True
                        turn_totals.setdefault(current_turn, 0)
                        date_key = day(obj.get("timestamp"))
                        if include_conversations and date_key in daily:
                            conversations[current_turn] = {
                                "turnId": current_turn,
                                "threadId": thread_id,
                                "contextWindowId": window_id,
                                "startedAt": obj.get("timestamp"),
                                "date": date_key,
                                "projectPath": project,
                                "projectName": project_names.get(project),
                                "threadName": thread_names.get(thread_id),
                                "preview": "未命名对话",
                            }
                        continue

                    if event_type == "task_complete":
                        completed_turn = payload.get("turn_id") or current_turn
                        if completed_turn == current_turn:
                            current_turn_active = False
                        continue

                    if event_type == "user_message":
                        conversation = conversations.get(current_turn)
                        message = payload.get("message")
                        if conversation is not None and isinstance(message, str) and message.strip():
                            conversation["preview"] = preview(message)
                            if context is not None and context["preview"] == "未命名任务":
                                context["preview"] = conversation["preview"]
                        continue

                    if event_type != "token_count":
                        continue
                    info = payload.get("info")
                    total_usage = info.get("total_token_usage") if isinstance(info, dict) else None
                    total = total_usage.get("total_tokens") if isinstance(total_usage, dict) else None
                    if not isinstance(total, (int, float)):
                        continue
                    total = max(0, int(total))
                    if current_turn is not None:
                        turn_totals[current_turn] = total
                    maximum = info.get("model_context_window")
                    last_usage = info.get("last_token_usage")
                    used = last_usage.get("total_tokens") if isinstance(last_usage, dict) else None
                    if include_conversations and isinstance(maximum, (int, float)) and maximum > 0 and isinstance(used, (int, float)) and timestamp(obj.get("timestamp")):
                        context = {
                            "threadId": thread_id,
                            "contextWindowId": window_id,
                            "projectPath": project,
                            "projectName": project_names.get(project),
                            "threadName": thread_names.get(thread_id),
                            "preview": (context or {}).get("preview") or conversations.get(current_turn, {}).get("preview") or "未命名任务",
                            "usedTokens": max(0, int(used)),
                            "maxTokens": int(maximum),
                            "updatedAt": obj.get("timestamp"),
                            "compactions": compactions,
                        }
                    delta = total - previous_total if total >= previous_total else total
                    previous_total = total
                    if usage_record_since_count:
                        usage_record_since_count = False
                        continue
                    date_key = day(obj.get("timestamp"))
                    if date_key in daily:
                        amount = max(0, delta)
                        daily[date_key] += amount
                        project_daily[(date_key, project)] = project_daily.get((date_key, project), 0) + amount
                    conversation = conversations.get(current_turn)
                    if conversation is not None:
                        conversation["tokens"] = conversation.get("tokens", 0) + max(0, delta)
                    continue

                if root_type == "compacted":
                    compactions += 1
                    window_id = payload.get("window_id") or window_id
                    if context is not None:
                        context["contextWindowId"] = window_id
                        context["compactions"] = compactions
                    continue

                if root_type == "token_usage_record":
                    turn_id = payload.get("turn_id") or current_turn
                    counted = False
                    usage = payload.get("usage")
                    response_tokens = usage.get("total_tokens") if isinstance(usage, dict) else None
                    date_key = day(obj.get("timestamp"))
                    if isinstance(response_tokens, (int, float)) and date_key in daily:
                        amount = max(0, int(response_tokens))
                        daily[date_key] += amount
                        project_daily[(date_key, project)] = project_daily.get((date_key, project), 0) + amount
                        counted = True
                    conversation = conversations.get(turn_id)
                    turn_usage = payload.get("turn_token_usage")
                    turn_tokens = turn_usage.get("total_tokens") if isinstance(turn_usage, dict) else None
                    if turn_id is not None and isinstance(turn_tokens, (int, float)):
                        turn_totals[turn_id] = max(0, int(turn_tokens))
                    if conversation is not None and isinstance(turn_tokens, (int, float)):
                        conversation["tokens"] = max(0, int(turn_tokens))
                    usage_record_since_count = counted
                    continue

                if root_type == "response_item" and payload.get("type") == "message" and payload.get("role") == "user":
                    metadata = payload.get("internal_chat_message_metadata_passthrough")
                    turn_id = (metadata.get("turn_id") if isinstance(metadata, dict) else None) or current_turn
                    conversation = conversations.get(turn_id)
                    text = user_text(payload)
                    if conversation is not None and text.strip():
                        conversation["preview"] = preview(text)
                        if context is not None and context["preview"] == "未命名任务":
                            context["preview"] = conversation["preview"]
        if context is not None:
            context["conversationTokens"] = sum(turn_totals.values())
            context["currentTurnId"] = current_turn
            context["currentTurnTokens"] = turn_totals.get(current_turn) if current_turn is not None else None
            context["currentTurnActive"] = current_turn_active
            contexts.append(context)
except OSError:
    pass

result = {
    "dailyTokens": daily,
    "conversations": list(conversations.values()),
    "projectDailyTokens": [
        {"date": key[0], "projectPath": key[1], "projectName": project_names.get(key[1]), "tokens": value}
        for key, value in project_daily.items()
    ],
    "contextHealth": contexts,
}
json.dump(result, sys.stdout, ensure_ascii=False, separators=(",", ":"))
"""#

    static func discoverHosts(globalStateData: Data) -> [RemoteCodexHost] {
        guard let root = try? JSONSerialization.jsonObject(with: globalStateData) as? [String: Any],
              let connections = root["codex-managed-remote-connections"] as? [[String: Any]] else { return [] }
        var seen = Set<String>()
        return connections.compactMap { connection in
            guard let id = connection["hostId"] as? String,
                  id.hasPrefix("remote-ssh-"),
                  seen.insert(id).inserted else { return nil }
            let alias = (connection["alias"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines)
            let hostname = (connection["hostname"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines)
            guard let destination = [alias, hostname].compactMap({ $0 }).first(where: { !$0.isEmpty }) else { return nil }
            let displayName = ((connection["displayName"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines)).flatMap { $0.isEmpty ? nil : $0 }
                ?? alias.flatMap { $0.isEmpty ? nil : $0 }
                ?? hostname.flatMap { $0.isEmpty ? nil : $0 }
                ?? destination
            let port = (connection["sshPort"] as? NSNumber)?.intValue
            let identity = (connection["identity"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines)
            return RemoteCodexHost(
                id: id,
                displayName: displayName,
                destination: destination,
                port: port.flatMap { $0 > 0 && $0 <= 65_535 ? $0 : nil },
                identity: identity.flatMap { $0.isEmpty ? nil : $0 }
            )
        }
    }

    static func metadata(globalStateData: Data, hostID: String) -> RemoteCodexMetadata {
        guard let root = try? JSONSerialization.jsonObject(with: globalStateData) as? [String: Any] else {
            return RemoteCodexMetadata(projectPaths: [:], projectNames: [:], threadProjectPaths: [:], threadNames: [:])
        }
        let projects = (root["remote-projects"] as? [[String: Any]] ?? []).filter {
            $0["hostId"] as? String == hostID
        }
        var projectPaths: [String: String] = [:]
        var projectNames: [String: String] = [:]
        for project in projects {
            guard let id = project["id"] as? String,
                  let path = project["remotePath"] as? String,
                  !id.isEmpty,
                  !path.isEmpty else { continue }
            projectPaths[id] = path
            if let label = (project["label"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines),
               !label.isEmpty {
                projectNames[path] = label
            }
        }

        let assignments = root["thread-project-assignments"] as? [String: [String: Any]] ?? [:]
        var threadProjectPaths: [String: String] = [:]
        let hostAssignments = assignments.filter { $0.value["hostId"] as? String == hostID }
        for (threadID, assignment) in hostAssignments {
            guard let projectID = assignment["projectId"] as? String,
                  let path = projectPaths[projectID] else { continue }
            threadProjectPaths[threadID] = path
        }
        let persisted = root["electron-persisted-atom-state"] as? [String: Any]
        let descriptions = persisted?["thread-descriptions-v1"] as? [String: String] ?? [:]
        let threadNames = descriptions.filter { hostAssignments[$0.key] != nil }
        return RemoteCodexMetadata(
            projectPaths: projectPaths,
            projectNames: projectNames,
            threadProjectPaths: threadProjectPaths,
            threadNames: threadNames
        )
    }

    func discoverHosts(codexHome: URL) -> [RemoteCodexHost] {
        let state = codexHome.appendingPathComponent(".codex-global-state.json")
        guard let data = try? Data(contentsOf: state) else { return [] }
        return Self.discoverHosts(globalStateData: data)
    }

    func connectedHosts(codexHome: URL) -> [RemoteCodexHost] {
        let state = codexHome.appendingPathComponent(".codex-global-state.json")
        let stateData = try? Data(contentsOf: state)
        let configuredHosts = stateData.map(Self.discoverHosts(globalStateData:)) ?? []
        let selectedHostID = stateData.flatMap { data -> String? in
            guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
            return root["selected-remote-host-id"] as? String
        }
        let activeEndpoints = Self.establishedSSHRemoteEndpointKeys()
        let resolvedEndpoints = Dictionary(uniqueKeysWithValues: configuredHosts.compactMap { host in
            resolvedEndpointKey(for: host).map { (host.id, $0) }
        })
        let hosts = Self.selectConnectedHosts(
            configuredHosts: configuredHosts,
            selectedHostID: selectedHostID,
            resolvedEndpoints: resolvedEndpoints,
            activeEndpoints: activeEndpoints
        )

        return hosts
    }

    static func selectConnectedHosts(
        configuredHosts: [RemoteCodexHost],
        selectedHostID: String?,
        resolvedEndpoints: [String: String],
        activeEndpoints: Set<String>
    ) -> [RemoteCodexHost] {
        let candidates = configuredHosts.compactMap { host -> (String, RemoteCodexHost)? in
            guard let endpoint = resolvedEndpoints[host.id], activeEndpoints.contains(endpoint) else { return nil }
            return (endpoint, host)
        }
        return Dictionary(grouping: candidates, by: { $0.0 }).values.compactMap { matches in
            matches.first(where: { $0.1.id == selectedHostID })?.1
                ?? matches.sorted { $0.1.id < $1.1.id }.first?.1
        }.sorted { $0.displayName.localizedCaseInsensitiveCompare($1.displayName) == .orderedAscending }
    }

    static func establishedSSHRemoteEndpointKeys(from output: String) -> Set<String> {
        Set(output.split(whereSeparator: \Character.isNewline).compactMap { rawLine in
            let line = String(rawLine)
            guard line.first == "n", let arrow = line.range(of: "->", options: .backwards) else { return nil }
            return endpointKey(from: String(line[arrow.upperBound...]))
        })
    }

    private static func establishedSSHRemoteEndpointKeys() -> Set<String> {
        let executable = "/usr/sbin/lsof"
        guard FileManager.default.isExecutableFile(atPath: executable) else { return [] }
        let process = Process()
        let output = Pipe()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = ["-nP", "-a", "-c", "ssh", "-iTCP", "-sTCP:ESTABLISHED", "-Fn"]
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        do {
            try process.run()
        } catch {
            return []
        }
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard let text = String(data: data, encoding: .utf8) else { return [] }
        return establishedSSHRemoteEndpointKeys(from: text)
    }

    private func resolvedEndpointKey(for host: RemoteCodexHost) -> String? {
        let process = Process()
        let output = Pipe()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/ssh")
        var arguments = ["-G"]
        if let port = host.port { arguments += ["-p", String(port)] }
        if let identity = host.identity { arguments += ["-i", identity] }
        arguments += ["--", host.destination]
        process.arguments = arguments
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        do {
            try process.run()
        } catch {
            return Self.endpointKey(from: host.destination, fallbackPort: host.port ?? 22)
        }
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0,
              let text = String(data: data, encoding: .utf8) else {
            return Self.endpointKey(from: host.destination, fallbackPort: host.port ?? 22)
        }
        var hostname: String?
        var port = host.port ?? 22
        for line in text.split(whereSeparator: \Character.isNewline) {
            let parts = line.split(maxSplits: 1, whereSeparator: \Character.isWhitespace)
            guard parts.count == 2 else { continue }
            switch parts[0] {
            case "hostname": hostname = String(parts[1])
            case "port": port = Int(parts[1]) ?? port
            default: continue
            }
        }
        return hostname.flatMap { Self.endpointKey(from: $0, fallbackPort: port) }
            ?? Self.endpointKey(from: host.destination, fallbackPort: port)
    }

    private static func endpointKey(from value: String, fallbackPort: Int? = nil) -> String? {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        let withoutUser = trimmed.split(separator: "@", maxSplits: 1).last.map(String.init) ?? trimmed
        var hostname = withoutUser
        var port = fallbackPort
        if withoutUser.hasPrefix("["), let close = withoutUser.firstIndex(of: "]") {
            hostname = String(withoutUser[withoutUser.index(after: withoutUser.startIndex)..<close])
            let suffix = withoutUser[withoutUser.index(after: close)...]
            if suffix.first == ":" { port = Int(suffix.dropFirst()) ?? port }
        } else if let colon = withoutUser.lastIndex(of: ":"),
                  let parsedPort = Int(withoutUser[withoutUser.index(after: colon)...]) {
            hostname = String(withoutUser[..<colon])
            port = parsedPort
        }
        guard let port, !hostname.isEmpty else { return nil }
        return "\(hostname.lowercased())|\(port)"
    }

    func collect(codexHome: URL, now: Date, dayKeys: [String], timezone: TimeZone) -> [RemoteSessionSnapshot] {
        let hosts = connectedHosts(codexHome: codexHome)
        guard !hosts.isEmpty else { return [] }
        let stateURL = codexHome.appendingPathComponent(".codex-global-state.json")
        let stateData = try? Data(contentsOf: stateURL)
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timezone
        let start = calendar.date(byAdding: .day, value: -89, to: calendar.startOfDay(for: now)) ?? now

        let lock = NSLock()
        var snapshots: [RemoteSessionSnapshot] = []
        DispatchQueue.concurrentPerform(iterations: hosts.count) { index in
            let host = hosts[index]
            let metadata = stateData.map { Self.metadata(globalStateData: $0, hostID: host.id) }
                ?? RemoteCodexMetadata(projectPaths: [:], projectNames: [:], threadProjectPaths: [:], threadNames: [:])
            let config: [String: Any] = [
                "dayKeys": dayKeys,
                "historyStart": start.timeIntervalSince1970,
                "timezone": timezone.identifier,
                "projects": Array(Set(metadata.projectPaths.values)),
                "projectNames": metadata.projectNames,
                "threadProjects": metadata.threadProjectPaths,
                "threadNames": metadata.threadNames
            ]
            guard let configData = try? JSONSerialization.data(withJSONObject: config),
                  let configJSON = String(data: configData, encoding: .utf8),
                  let snapshot = self.collect(
                    host: host,
                    script: Self.script.replacingOccurrences(of: "__CONFIG__", with: configJSON)
                  ) else { return }
            lock.lock()
            snapshots.append(snapshot)
            lock.unlock()
        }
        return snapshots.sorted { $0.host.displayName.localizedCaseInsensitiveCompare($1.host.displayName) == .orderedAscending }
    }

    private func collect(host: RemoteCodexHost, script: String) -> RemoteSessionSnapshot? {
        let process = Process()
        let output = Pipe()
        let input = Pipe()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/ssh")
        var arguments = [
            "-T",
            "-o", "BatchMode=yes",
            "-o", "ConnectionAttempts=1",
            "-o", "ConnectTimeout=4",
            "-o", "LogLevel=ERROR"
        ]
        if let port = host.port { arguments += ["-p", String(port)] }
        if let identity = host.identity { arguments += ["-i", identity] }
        arguments += ["--", host.destination, "python3", "-"]
        process.arguments = arguments
        process.standardInput = input
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice

        do {
            try process.run()
            try input.fileHandleForWriting.write(contentsOf: Data(script.utf8))
            try input.fileHandleForWriting.close()
        } catch {
            if process.isRunning { process.terminate() }
            return nil
        }

        let timeout = DispatchWorkItem {
            if process.isRunning { process.terminate() }
        }
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 12, execute: timeout)
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        timeout.cancel()
        guard process.terminationStatus == 0 else { return nil }
        return Self.decode(data: data, host: host)
    }

    static func decode(data: Data, host: RemoteCodexHost) -> RemoteSessionSnapshot? {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        let daily = (root["dailyTokens"] as? [String: NSNumber] ?? [:]).mapValues(\.intValue)
        return RemoteSessionSnapshot(
            host: host,
            dailyTokens: daily,
            conversations: root["conversations"] as? [[String: Any]] ?? [],
            projectDailyTokens: root["projectDailyTokens"] as? [[String: Any]] ?? [],
            contextHealth: root["contextHealth"] as? [[String: Any]] ?? []
        )
    }

    #if CODEX_METER_TESTING
    static func runAggregationForTesting(codexHome: URL, config: [String: Any]) -> [String: Any]? {
        guard let configData = try? JSONSerialization.data(withJSONObject: config),
              let configJSON = String(data: configData, encoding: .utf8) else { return nil }
        let process = Process()
        let input = Pipe()
        let output = Pipe()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/python3")
        process.arguments = ["-"]
        var environment = ProcessInfo.processInfo.environment
        environment["CODEX_HOME"] = codexHome.path
        process.environment = environment
        process.standardInput = input
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        do {
            try process.run()
            try input.fileHandleForWriting.write(contentsOf: Data(
                script.replacingOccurrences(of: "__CONFIG__", with: configJSON).utf8
            ))
            try input.fileHandleForWriting.close()
        } catch {
            if process.isRunning { process.terminate() }
            return nil
        }
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else { return nil }
        return try? JSONSerialization.jsonObject(with: data) as? [String: Any]
    }
    #endif
}
