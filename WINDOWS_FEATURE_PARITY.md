# Windows P0/P1 功能补齐清单（已完成）

Windows 已完成与 macOS 的 P0/P1 功能对齐：90 天用量洞察、项目/任务聚合、上下文健康度、预测、系统通知、动态托盘和报告导出均已接入；五项 `capabilities` 已全部启用。以下条目保留为实现与回归验收清单。

状态（2026-09-13）：功能实现、自动化测试、Release 发布和安装器自检已完成；Windows 10 22H2 / Windows 11 的通知、DPI、休眠恢复及窗口置顶仍属于发布前人工验收项。

## 后续待开发（更新于 2026-09-14）

- [ ] 在 Windows 设置页加入“监控 SSH 对话”开关、刷新延时提示和首次发现连接时的引导；默认关闭。
- [ ] 识别由 Windows 版 Codex 建立且当前仍有效的 OpenSSH 连接，按实际远端地址去重，断开后及时更新连接数量。
- [ ] 仅在开关开启时读取当前已连接服务器的 Codex 会话；保存但未连接的服务器不得参与刷新或产生连接超时。
- [ ] 聚合远端最近 90 天 Token、今日问题、项目、任务和上下文数据，并读取远端项目标签、`session_index.jsonl` 对话标题及新版 `response_item` 用户问题文本。
- [ ] 补齐 Windows 自动化测试与 Windows 10/11 人工验收，覆盖多连接、切换网络、断线、同地址去重、非 Git 项目、中文标题、无 `python3` 及不可达主机。

## 数据与接口（已完成）

- 将 `SessionStatsCache.ReadToday` 泛化为最多 90 天的读取器，沿用文件大小与修改时间缓存，并解析 `session_meta.payload.cwd`。
- 项目键使用可解析的 Git 根目录，同名项目显示父目录以消歧；无法解析 Git 根目录的会话全部归入固定键 `__non_project__`，显示名称为“非项目中对话”。
- 输出与 macOS 一致的可选载荷：
  - `history.dailyTokens[]`：`date`、`tokens`、`source`，最多 90 天。
  - `insights.projects[]`：`key`、`name`、`path`、`projectKind`。
  - `insights.tasks[]`：`date`、`projectKey`、`projectName`、`projectKind`、`taskId`、`name`、`turns`、`tokens`、`lastActive`、`kind`。
  - `forecast.primary|secondary`：`status`、`message`、`ratePerHour`、`estimatedExhaustsAt`、`confidence`。
  - `contextHealth.sessions[]`：`taskId`、`threadId`、`contextWindowId`、`name`、项目字段、`usedTokens`、`maxTokens`、`usedPercent`、`remainingPercent`、`status`、`lastActive`、`compactions`。
  - `contextHealth.currentTaskId|currentTaskName|trackingMode|pollIntervalSeconds`：当前交互对话及实时跟随元数据。
  - `capabilities.extendedInsights|contextHealth|notifications|menuBar|reportExport`。
- 项目和任务统计只代表本机；账户每日桶仍优先用于整体趋势。未归入用户任务的项目消耗输出为“系统/子代理活动”。
- WebView2 bridge 补齐 `getNotificationSettings`、`setNotificationSettings`、`requestNotificationAuthorization`、`sendTestNotification`、`setMenuBarVisible` 和 `exportReport`，并回调共享组件现有的结果函数。
- 刷新采用稳定快照策略：冷启动可展示 `partial` 本地统计；取得完整结果后，后续 `partial` 或失败回退不得覆盖历史、任务、预测和周报，下一次完整结果再统一替换。
- 从 `token_count.info.last_token_usage.total_tokens` 和 `model_context_window` 计算当前窗口占用，解析 `compacted` 事件并在压缩后使用新窗口；只输出用户任务，按最近活动保留前 20 项。分级与 macOS 一致：已用低于 60% 为健康、60%–79% 注意、80%–89% 紧张、90% 以上危险。
- 使用 App Server 的 `thread/list` 最近交互顺序识别当前用户对话，约每 2 秒只读取该会话日志尾部并通过增量回调更新共享 UI；不得为实时显示反复扫描或重读完整 90 天日志。

## 预测与本地存储（已完成）

- 在 `%LOCALAPPDATA%\Codex Meter` 保存额度快照，每 5 分钟或百分比变化时追加，保留 14 天且最多 5,000 条。
- 算法必须与 macOS 一致：5 小时窗口观察最近 3 小时、每周窗口观察最近 7 天；至少 4 个同周期样本、满足最短跨度且净下降至少 2 个百分点后才输出预测。
- 预测耗尽时间晚于 `resetsAt` 时返回 `safe_until_reset`；否则返回 `will_deplete`。始终在 UI 标记为趋势估算。

## Windows 原生能力（已完成）

- 使用适用于未打包 WPF 应用的 Windows Toast 方案发送 20%、10%、5%、耗尽和恢复通知；首次同步只建立基线，按 `窗口 + resetsAt + 事件` 持久化去重。恢复通知仅在 `resetsAt` 比上次推进超过 60 秒、上次剩余低于 20%、本次剩余至少回升 5 个百分点时触发，并且只在成功同步后判断。
- 首次成功同步后显示一次应用内引导，用户确认后才注册或申请通知；权限不可用时给出可操作的系统设置提示。
- 扩展现有 `NotifyIcon`：动态渲染整数百分比图标，Tooltip 同时展示 5 小时、每周额度和预测。托盘菜单增加额度详情、预测、刷新、设置、显示面板和退出。
- 设置页提供通知总开关、阈值/耗尽/恢复分类开关、测试通知及托盘百分比开关。
- 启用 `capabilities.contextHealth` 后，主面板在 5 小时额度右侧显示等宽上下文剩余圆盘；无数据时使用中性状态，整个模块可打开详情。未启用能力时 5 小时额度模块必须自动占满整行。
- 使用 `SaveFileDialog` 手动导出最近 7 天报告，并将对话框显式绑定到已激活的 Codex Meter 主窗口，确保它显示在其他应用窗口之前。Markdown 内容与 macOS 一致；CSV 使用 UTF-8 BOM，固定列为日期、项目、任务、轮次、Tokens、最后活动时间、数据来源。

## 测试与验收（已完成）

- 扩展 `SessionStatsReaderTests` 和 `UsagePayloadBuilderTests`，覆盖 90 天边界、时区/DST、缓存、Git 项目归属、同名项目、任务聚合、子代理归集及账户/本地合并。
- 新增预测测试：数据不足、持续下降、稳定、跨周期、重置前不会耗尽和置信度。
- 新增通知测试：首次基线、直接跨越多个阈值只发最严重一条、耗尽、恢复、跨重启去重和通知不可用。
- 新增 Markdown/CSV 转义、中文、BOM、最近 7 天范围、保存取消与写入失败测试。
- 验证 7/30/90 天图表的即时详情提示；7 天柱状图及 30/90 天热力图必须在 360×560 面板中完整显示，趋势视图不依赖纵向滚动。
- 验证多个非 Git `cwd` 合并到“非项目中对话”，项目行使用左侧名称、右侧 Token 的布局，刷新中间态不会造成 Token 或周报任务跳变。
- 覆盖上下文占用边界、缺少模型上限、子代理排除、压缩后窗口切换、当前对话切换、日志增长后的 2 秒级增量更新、同一任务取最新快照和刷新期间稳定性；未声明 `capabilities.contextHealth` 时不得显示入口。
- 验证上下文圆盘的健康、注意、紧张、危险及无数据状态，并确认 360px 面板双列无溢出、旧宿主单列不留空位。
- 在 Windows 10 22H2 和 Windows 11 上验证 WebView2、动态 DPI、托盘重建、锁屏/休眠恢复、浅色/深色以及安装/卸载升级。
- [x] `capabilities.extendedInsights/contextHealth/notifications/menuBar/reportExport` 全部置为 `true`；旧宿主未声明能力时继续保持兼容布局。
