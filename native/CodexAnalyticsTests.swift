import Foundation

@main
enum CodexAnalyticsTests {
    static func main() throws {
        try testRefreshPreferences()
        try testNinetyDaySessionAggregation()
        try testContextHealthAggregation()
        try testLiveContextTailReading()
        try testNonProjectAggregation()
        try testSameNameGitProjects()
        try testRemoteHostDiscoveryAndDecoding()
        try testRemoteResponseItemPreview()
        try testCachedRemoteComposition()
        try testForecastAndEvents()
        try testReportFormats()
        print("CodexAnalyticsTests passed")
    }

    private static func testRefreshPreferences() throws {
        let suite = "CodexMeterRefreshTests.\(UUID().uuidString)"
        guard let defaults = UserDefaults(suiteName: suite) else {
            throw TestFailure("无法创建刷新设置测试 UserDefaults")
        }
        defer { defaults.removePersistentDomain(forName: suite) }

        var preferences = RefreshPreferences(defaults: defaults)
        expect(preferences == RefreshPreferences(liveSeconds: 2, generalSeconds: 30, sshSeconds: 60), "刷新设置应使用安全默认值")

        preferences.update(from: ["liveSeconds": 1, "generalSeconds": 120, "sshSeconds": 30])
        preferences.persist(to: defaults)
        expect(RefreshPreferences(defaults: defaults) == preferences, "有效刷新档位应持久化")

        var invalid = RefreshPreferences(liveSeconds: 2, generalSeconds: 30, sshSeconds: 60)
        invalid.update(from: ["liveSeconds": 3, "generalSeconds": 12, "sshSeconds": 5])
        expect(invalid == RefreshPreferences(liveSeconds: 2, generalSeconds: 30, sshSeconds: 60), "非法档位不得覆盖当前设置")

        defaults.set(999, forKey: "liveRefreshIntervalSeconds")
        defaults.set(1, forKey: "generalRefreshIntervalSeconds")
        defaults.set(10, forKey: "sshRefreshIntervalSeconds")
        let recovered = RefreshPreferences(defaults: defaults)
        expect(recovered == RefreshPreferences(liveSeconds: 2, generalSeconds: 30, sshSeconds: 60), "损坏的持久化值应回退默认档位")
        let payload = preferences.payload
        expect(payload["liveOptions"] as? [Int] == [1, 2, 5, 10], "宿主应返回实时刷新白名单")
        expect(payload["sshOptions"] as? [Int] == [30, 60, 120, 300], "SSH 最快档位必须为 30 秒")

        var requests = RefreshRequestAccumulator()
        requests.enqueue(general: true, remote: false, remoteEnabled: true)
        requests.enqueue(general: false, remote: true, remoteEnabled: true)
        expect(requests.take(remoteEnabled: true) == RefreshRequest(general: true, remote: true), "同时到期的常规与 SSH 请求应合并")
        expect(requests.take(remoteEnabled: true) == nil, "取出请求后不得重复执行")
        requests.enqueue(general: false, remote: true, remoteEnabled: false)
        expect(requests.isEmpty, "SSH 关闭时不得排入远端刷新")
        requests.enqueue(general: true, remote: true, remoteEnabled: true)
        requests.discardRemote()
        expect(requests.take(remoteEnabled: false) == RefreshRequest(general: true, remote: false), "关闭 SSH 应丢弃待执行远端请求但保留常规刷新")
    }

    private static func testRemoteHostDiscoveryAndDecoding() throws {
        let state: [String: Any] = [
            "codex-managed-remote-connections": [
                [
                    "hostId": "remote-ssh-codex-managed:build-box",
                    "displayName": "Build Box",
                    "hostname": "developer@10.0.0.8",
                    "sshPort": 2222,
                    "identity": "/tmp/test-key"
                ],
                [
                    "hostId": "remote-ssh-discovered:staging",
                    "displayName": "Staging",
                    "alias": "staging"
                ],
                [
                    "hostId": "remote-ssh-discovered:staging",
                    "alias": "duplicate-must-be-ignored"
                ],
                ["hostId": "local", "hostname": "localhost"]
            ],
            "remote-projects": [[
                "id": "remote-project-1",
                "hostId": "remote-ssh-codex-managed:build-box",
                "remotePath": "/srv/not-a-git-project",
                "label": "Configured Project"
            ]],
            "thread-project-assignments": [
                "remote-thread": [
                    "projectKind": "remote",
                    "projectId": "remote-project-1",
                    "hostId": "remote-ssh-codex-managed:build-box"
                ]
            ],
            "electron-persisted-atom-state": [
                "thread-descriptions-v1": ["remote-thread": "Remote task title"]
            ]
        ]
        let stateData = try JSONSerialization.data(withJSONObject: state)
        let hosts = RemoteSessionCollector.discoverHosts(globalStateData: stateData)
        expect(hosts.count == 2, "应发现 Codex 保存的托管与 SSH config 主机，并按 hostId 去重")
        expect(hosts[0] == RemoteCodexHost(
            id: "remote-ssh-codex-managed:build-box",
            displayName: "Build Box",
            destination: "developer@10.0.0.8",
            port: 2222,
            identity: "/tmp/test-key"
        ), "托管主机应保留目标、端口和密钥路径")
        expect(hosts[1].destination == "staging", "发现型主机应复用 SSH config 别名")

        let metadata = RemoteSessionCollector.metadata(
            globalStateData: stateData,
            hostID: "remote-ssh-codex-managed:build-box"
        )
        expect(metadata.threadProjectPaths["remote-thread"] == "/srv/not-a-git-project", "应使用 Codex 的远端任务归属识别非 Git 项目")
        expect(metadata.projectNames["/srv/not-a-git-project"] == "Configured Project", "应保留 Codex 中配置的远端项目名称")
        expect(metadata.threadNames["remote-thread"] == "Remote task title", "应读取 Codex 保存的远端任务标题")

        let activeEndpoints = RemoteSessionCollector.establishedSSHRemoteEndpointKeys(from: """
        p14540
        n192.168.0.76:52823->192.168.0.71:22
        p18014
        n[fe80::1]:53259->[fe80::71]:2222
        n127.0.0.1:53258->127.0.0.1:53268
        """)
        expect(activeEndpoints.contains("192.168.0.71|22"), "应识别当前已建立的 IPv4 SSH 远端")
        expect(activeEndpoints.contains("fe80::71|2222"), "应识别当前已建立的 IPv6 SSH 远端")
        expect(!activeEndpoints.contains("192.168.8.15|22"), "不得把仅保存但未连接的服务器视为活动连接")

        let duplicateEndpointHost = RemoteCodexHost(
            id: "remote-ssh-codex-managed:same-address",
            displayName: "Same Address",
            destination: "other-user@10.0.0.8",
            port: 2222,
            identity: "/tmp/other-key"
        )
        let selectedHosts = RemoteSessionCollector.selectConnectedHosts(
            configuredHosts: hosts + [duplicateEndpointHost],
            selectedHostID: duplicateEndpointHost.id,
            resolvedEndpoints: [
                hosts[0].id: "10.0.0.8|2222",
                duplicateEndpointHost.id: "10.0.0.8|2222",
                hosts[1].id: "10.0.0.9|22"
            ],
            activeEndpoints: ["10.0.0.8|2222"]
        )
        expect(selectedHosts == [duplicateEndpointHost], "同一活动地址对应多个保存项时应去重，并优先采用 Codex 当前选择的主机")

        let response: [String: Any] = [
            "dailyTokens": ["2033-05-13": 42],
            "conversations": [["turnId": "remote-turn"]],
            "projectDailyTokens": [["date": "2033-05-13", "projectPath": "/srv/project", "tokens": 42]],
            "contextHealth": [["threadId": "remote-thread", "usedTokens": 10, "maxTokens": 100]]
        ]
        let responseData = try JSONSerialization.data(withJSONObject: response)
        let decoded = RemoteSessionCollector.decode(data: responseData, host: hosts[0])
        expect(decoded?.dailyTokens["2033-05-13"] == 42, "应解码远程每日 Token")
        expect(decoded?.conversations.count == 1 && decoded?.contextHealth.count == 1, "应解码远程对话与上下文数据")
    }

    private static func testRemoteResponseItemPreview() throws {
        let temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("codex-meter-remote-preview-tests-\(UUID().uuidString)", isDirectory: true)
        let sessions = temporary.appendingPathComponent("sessions/2033/05/13", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: temporary) }

        let metadata = ["turn_id": "remote-turn"]
        let objects: [[String: Any]] = [
            [
                "timestamp": "2033-05-13T10:00:00Z",
                "type": "session_meta",
                "payload": ["id": "remote-thread", "cwd": "/srv/project", "thread_source": "user"]
            ],
            [
                "timestamp": "2033-05-13T10:01:00Z",
                "type": "event_msg",
                "payload": ["type": "task_started", "turn_id": "remote-turn"]
            ],
            [
                "timestamp": "2033-05-13T10:01:01Z",
                "type": "response_item",
                "payload": [
                    "type": "message",
                    "role": "user",
                    "content": [["type": "input_text", "text": "<app-context>不能作为问题名称</app-context>"]],
                    "internal_chat_message_metadata_passthrough": metadata
                ]
            ],
            [
                "timestamp": "2033-05-13T10:01:02Z",
                "type": "response_item",
                "payload": [
                    "type": "message",
                    "role": "user",
                    "content": [["type": "input_text", "text": "修复远程对话名称"]],
                    "internal_chat_message_metadata_passthrough": metadata
                ]
            ],
            [
                "timestamp": "2033-05-13T10:01:03Z",
                "type": "event_msg",
                "payload": [
                    "type": "token_count",
                    "info": [
                        "model_context_window": 200_000,
                        "last_token_usage": ["total_tokens": 80_000],
                        "total_token_usage": ["total_tokens": 100_000]
                    ]
                ]
            ],
            [
                "timestamp": "2033-05-13T10:01:04Z",
                "type": "token_usage_record",
                "payload": ["turn_id": "remote-turn", "turn_token_usage": ["total_tokens": 100_000]]
            ],
            [
                "timestamp": "2033-05-13T10:01:05Z",
                "type": "event_msg",
                "payload": [
                    "type": "token_count",
                    "info": [
                        "model_context_window": 200_000,
                        "last_token_usage": ["total_tokens": 100_000],
                        "total_token_usage": ["total_tokens": 35_100_000]
                    ]
                ]
            ],
            [
                "timestamp": "2033-05-13T10:01:06Z",
                "type": "event_msg",
                "payload": ["type": "task_complete", "turn_id": "remote-turn"]
            ],
            [
                "timestamp": "2033-05-13T10:02:00Z",
                "type": "event_msg",
                "payload": ["type": "task_started", "turn_id": "remote-turn-next"]
            ],
            [
                "timestamp": "2033-05-13T10:02:01Z",
                "type": "event_msg",
                "payload": [
                    "type": "token_count",
                    "info": [
                        "model_context_window": 200_000,
                        "last_token_usage": ["total_tokens": 30_000],
                        "total_token_usage": ["total_tokens": 35_140_000]
                    ]
                ]
            ]
        ]
        let lines = try objects.map { object -> String in
            let data = try JSONSerialization.data(withJSONObject: object)
            return String(data: data, encoding: .utf8)!
        }
        try lines.joined(separator: "\n").write(
            to: sessions.appendingPathComponent("remote.jsonl"),
            atomically: true,
            encoding: .utf8
        )
        let indexRows: [[String: Any]] = [
            ["id": "remote-thread", "thread_name": "旧的远端标题", "updated_at": "2033-05-13T10:00:00Z"],
            ["id": "remote-thread", "thread_name": "远端 Codex 对话标题", "updated_at": "2033-05-13T10:02:00Z"]
        ]
        let indexLines = try indexRows.map { row -> String in
            let data = try JSONSerialization.data(withJSONObject: row)
            return String(data: data, encoding: .utf8)!
        }
        try indexLines.joined(separator: "\n").write(
            to: temporary.appendingPathComponent("session_index.jsonl"),
            atomically: true,
            encoding: .utf8
        )
        let result = RemoteSessionCollector.runAggregationForTesting(
            codexHome: temporary,
            config: [
                "dayKeys": ["2033-05-13"],
                "historyStart": 0,
                "timezone": "UTC",
                "projects": ["/srv/project"],
                "projectNames": ["/srv/project": "Remote Project"],
                "threadProjects": ["remote-thread": "/srv/project"],
                "threadNames": ["remote-thread": "Remote Task"]
            ]
        )
        let conversations = result?["conversations"] as? [[String: Any]] ?? []
        expect(conversations.first?["preview"] as? String == "修复远程对话名称", "新版远端 response_item 即使没有 content_item_kinds 也应读取用户问题")
        expect(conversations.first?["threadName"] as? String == "远端 Codex 对话标题", "对话分组应优先使用远端会话索引中的最新 Codex 标题")
        let remoteContext = (result?["contextHealth"] as? [[String: Any]])?.first
        expect((remoteContext?["conversationTokens"] as? NSNumber)?.intValue == 140_000, "远端对话应按轮次累计 Token")
        expect((remoteContext?["currentTurnTokens"] as? NSNumber)?.intValue == 40_000, "远端上下文应输出当前回答 Token")
        expect(remoteContext?["currentTurnActive"] as? Bool == true, "远端当前回答应保留实时状态")
    }

    private static func testCachedRemoteComposition() throws {
        let temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("codex-meter-remote-cache-tests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: temporary, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: temporary) }

        let host = RemoteCodexHost(id: "remote-cache", displayName: "Build Box", destination: "build-box", port: nil, identity: nil)
        let snapshot = RemoteSessionSnapshot(
            host: host,
            dailyTokens: ["2033-05-13": 42],
            conversations: [[
                "turnId": "remote-turn",
                "threadId": "remote-thread",
                "startedAt": "2033-05-13T10:00:00Z",
                "date": "2033-05-13",
                "projectPath": "/srv/project",
                "preview": "远端缓存任务",
                "tokens": 42
            ]],
            projectDailyTokens: [["date": "2033-05-13", "projectPath": "/srv/project", "tokens": 42]],
            contextHealth: []
        )
        let now = ISO8601DateFormatter().date(from: "2033-05-13T12:00:00Z")!
        let service = CodexUsageService()
        service.setRemoteSnapshotsForTesting([snapshot])

        let first = service.localPayloadWithCachedRemoteForTesting(sessionsDirectory: temporary, now: now)
        let second = service.localPayloadWithCachedRemoteForTesting(sessionsDirectory: temporary, now: now)
        let firstToday = first["today"] as? [String: Any]
        let secondToday = second["today"] as? [String: Any]
        expect((firstToday?["tokens"] as? NSNumber)?.intValue == 42, "首次组合应包含缓存的 SSH Token")
        expect((secondToday?["tokens"] as? NSNumber)?.intValue == 42, "常规重组不得丢失缓存的 SSH Token")
        expect((secondToday?["questions"] as? NSNumber)?.intValue == 1, "常规重组应保留缓存的 SSH 对话")

        service.clearRemoteSnapshotsForTesting()
        let localOnly = service.localPayloadWithCachedRemoteForTesting(sessionsDirectory: temporary, now: now)
        let localToday = localOnly["today"] as? [String: Any]
        expect((localToday?["tokens"] as? NSNumber)?.intValue == 0, "关闭 SSH 后应清除远端 Token 缓存")
        expect((localToday?["questions"] as? NSNumber)?.intValue == 0, "关闭 SSH 后应清除远端对话缓存")
        service.shutdown()
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
            "{\"timestamp\":\"2033-05-13T10:04:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"model_context_window\":100000,\"last_token_usage\":{\"total_tokens\":25000},\"total_token_usage\":{\"total_tokens\":90000}}}}",
            "{\"timestamp\":\"2033-05-13T10:04:01Z\",\"type\":\"token_usage_record\",\"payload\":{\"turn_id\":\"turn-health\",\"turn_token_usage\":{\"total_tokens\":90000}}}",
            "{\"timestamp\":\"2033-05-13T10:04:01.500Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"model_context_window\":100000,\"last_token_usage\":{\"total_tokens\":90000},\"total_token_usage\":{\"total_tokens\":35090000}}}}",
            "{\"timestamp\":\"2033-05-13T10:04:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"turn-health\"}}",
            "{\"timestamp\":\"2033-05-13T10:05:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"turn-next\"}}",
            "{\"timestamp\":\"2033-05-13T10:06:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"model_context_window\":100000,\"last_token_usage\":{\"total_tokens\":30000},\"total_token_usage\":{\"total_tokens\":35130000}}}}"
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
        expect((rows.first?["usedTokens"] as? NSNumber)?.intValue == 30_000, "应使用最新窗口 Token，而非累计 Token")
        expect((rows.first?["conversationTokens"] as? NSNumber)?.intValue == 130_000, "本对话累计应为各轮最新值之和")
        expect((rows.first?["currentTurnTokens"] as? NSNumber)?.intValue == 40_000, "应输出当前回答累计 Token")
        expect(rows.first?["currentTurnActive"] as? Bool == true, "未完成的最新回答应标记为实时")
        expect((rows.first?["remainingPercent"] as? NSNumber)?.doubleValue == 70, "应计算上下文剩余比例")
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
            "{\"timestamp\":\"2033-05-13T10:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"turn-a\"}}",
            "{\"timestamp\":\"2033-05-13T10:01:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"model_context_window\":200000,\"last_token_usage\":{\"total_tokens\":75000},\"total_token_usage\":{\"total_tokens\":95000}}}}",
            "{\"timestamp\":\"2033-05-13T10:01:01Z\",\"type\":\"token_usage_record\",\"payload\":{\"turn_id\":\"turn-a\",\"turn_token_usage\":{\"total_tokens\":95000}}}",
            "{\"timestamp\":\"2033-05-13T10:01:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"model_context_window\":200000,\"last_token_usage\":{\"total_tokens\":95000},\"total_token_usage\":{\"total_tokens\":35095000}}}}"
        ]
        try lines.joined(separator: "\n").write(to: file, atomically: true, encoding: .utf8)

        let service = CodexUsageService()
        let first = service.contextMeasurementForTesting(file: file)
        expect((first?["usedTokens"] as? NSNumber)?.intValue == 95_000, "实时上下文应读取日志尾部的最新窗口用量")
        expect((first?["maxTokens"] as? NSNumber)?.intValue == 200_000, "实时上下文应读取模型窗口上限")
        expect((first?["conversationTokens"] as? NSNumber)?.intValue == 95_000, "实时读取应同时返回当前对话累计 Token")
        expect((first?["currentTurnTokens"] as? NSNumber)?.intValue == 95_000, "实时读取应返回本轮回答累计 Token")
        expect(first?["currentTurnActive"] as? Bool == true, "回答进行中应标记为实时")

        let appended = "\n{\"timestamp\":\"2033-05-13T10:01:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"turn-a\"}}\n{\"timestamp\":\"2033-05-13T10:01:04Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"turn-b\"}}\n{\"timestamp\":\"2033-05-13T10:02:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"model_context_window\":200000,\"last_token_usage\":{\"total_tokens\":20000},\"total_token_usage\":{\"total_tokens\":35120000}}}}"
        let handle = try FileHandle(forWritingTo: file)
        try handle.seekToEnd()
        try handle.write(contentsOf: Data(appended.utf8))
        try handle.close()
        let second = service.contextMeasurementForTesting(file: file)
        expect((second?["usedTokens"] as? NSNumber)?.intValue == 20_000, "会话文件增长后应立即更新上下文用量")
        expect((second?["conversationTokens"] as? NSNumber)?.intValue == 120_000, "会话文件增长后应立即更新当前对话累计 Token")
        expect((second?["currentTurnTokens"] as? NSNumber)?.intValue == 25_000, "新一轮应从自己的累计值开始")

        let partial = "{\"timestamp\":\"2033-05-13T10:02:01Z\",\"type\":\"token_usage_record\",\"payload\":{\"turn_id\":\"turn-b\",\"turn_token_usage\":{\"total_tokens\":40000}}}"
        let midpoint = partial.index(partial.startIndex, offsetBy: partial.count / 2)
        let partialHandle = try FileHandle(forWritingTo: file)
        try partialHandle.seekToEnd()
        try partialHandle.write(contentsOf: Data(("\n" + partial[..<midpoint]).utf8))
        try partialHandle.close()
        let unchanged = service.contextMeasurementForTesting(file: file)
        expect((unchanged?["currentTurnTokens"] as? NSNumber)?.intValue == 25_000, "半行 JSON 不得提前改变实时值")

        let completionHandle = try FileHandle(forWritingTo: file)
        try completionHandle.seekToEnd()
        try completionHandle.write(contentsOf: Data((partial[midpoint...] + "\n").utf8))
        try completionHandle.close()
        let completedLine = service.contextMeasurementForTesting(file: file)
        expect((completedLine?["currentTurnTokens"] as? NSNumber)?.intValue == 40_000, "补全 JSON 后应从缓存偏移继续读取")
        expect((completedLine?["conversationTokens"] as? NSNumber)?.intValue == 135_000, "同一轮更新不得重复累加")

        let completeHandle = try FileHandle(forWritingTo: file)
        try completeHandle.seekToEnd()
        try completeHandle.write(contentsOf: Data("{\"timestamp\":\"2033-05-13T10:02:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"turn-b\"}}\n".utf8))
        try completeHandle.close()
        let finished = service.contextMeasurementForTesting(file: file)
        expect(finished?["currentTurnActive"] as? Bool == false, "回答完成后应保留最终值并切换状态")

        let replacement = [
            "{\"timestamp\":\"2033-05-13T11:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"turn-c\"}}",
            "{\"timestamp\":\"2033-05-13T11:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"model_context_window\":200000,\"last_token_usage\":{\"total_tokens\":8000},\"total_token_usage\":{\"total_tokens\":10000}}}}"
        ].joined(separator: "\n")
        try replacement.write(to: file, atomically: false, encoding: .utf8)
        let reset = service.contextMeasurementForTesting(file: file)
        expect((reset?["conversationTokens"] as? NSNumber)?.intValue == 10_000, "日志截断后应清空旧轮次并重新扫描")
        expect((reset?["currentTurnTokens"] as? NSNumber)?.intValue == 10_000, "日志截断后的新轮次应正确读取")
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
