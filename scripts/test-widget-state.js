const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

let Widget;
class FakeHTMLElement {
  attachShadow() {
    this.shadowRoot = { querySelector: () => null, querySelectorAll: () => [] };
    return this.shadowRoot;
  }

  hasAttribute() { return false; }
}

const sandbox = {
  HTMLElement: FakeHTMLElement,
  customElements: { define: (_name, constructor) => { Widget = constructor; } },
  window: {},
  document: { hidden: false, querySelector: () => null, addEventListener: () => {}, removeEventListener: () => {} },
  localStorage: { getItem: () => null, setItem: () => {} },
  structuredClone,
  Intl,
  Date,
  Math,
  Number,
  String,
  Set,
  Map,
  Array,
  Object,
  JSON,
  console,
  performance: { now: () => 0 },
  requestAnimationFrame: () => 0,
  cancelAnimationFrame: () => {},
  setTimeout: () => 0,
  clearTimeout: () => {},
  setInterval: () => 0,
  clearInterval: () => {},
  matchMedia: () => ({ matches: false }),
};
sandbox.window = sandbox;
vm.createContext(sandbox);
const source = fs.readFileSync(path.join(__dirname, "..", "usage-widget.js"), "utf8");
vm.runInContext(source, sandbox, { filename: "usage-widget.js" });

const widget = new Widget();
widget.renderValues = () => {};

const compactedTaskGroups = widget.groupConversations([
  {
    threadId: "thread-same-task",
    contextWindowId: "window-before-compaction",
    threadName: "规划 Codex Meter 增值功能",
    turnId: "turn-before",
    startedAt: "2033-05-13T10:00:00Z",
    tokens: 40,
  },
  {
    threadId: "thread-same-task",
    contextWindowId: "window-after-compaction",
    threadName: "规划 Codex Meter 增值功能",
    turnId: "turn-after",
    startedAt: "2033-05-13T11:00:00Z",
    tokens: 60,
  },
]);
assert.equal(compactedTaskGroups.length, 1, "同一任务压缩前后的上下文窗口应合并为一个对话列表");
assert.equal(compactedTaskGroups[0].turns.length, 2, "合并后应保留同一任务的全部轮次");
assert.equal(compactedTaskGroups[0].title, "规划 Codex Meter 增值功能", "合并后应继续使用 Codex 任务名称");

const legacyContextGroups = widget.groupConversations([
  { contextWindowId: "legacy-a", turnId: "legacy-turn-a", startedAt: "2033-05-13T10:00:00Z" },
  { contextWindowId: "legacy-b", turnId: "legacy-turn-b", startedAt: "2033-05-13T11:00:00Z" },
]);
assert.equal(legacyContextGroups.length, 2, "缺少任务 ID 的旧载荷仍应按上下文窗口分组");

const localPayload = (tokens, name, extra = {}) => ({
  ...extra,
  partial: true,
  today: { tokens, questions: 1, tokenSource: "local", conversations: [] },
  history: { source: "local", dailyTokens: [{ date: "2033-05-13", tokens, source: "local" }] },
  insights: { localOnly: true, tasks: [{ name, tokens }], projects: [] },
  contextHealth: { source: "local", sessions: [{ name, usedPercent: 25, remainingPercent: 75 }] },
  capabilities: { extendedInsights: true, ...(extra.capabilities || {}) },
});

widget.updateUsage(localPayload(10, "冷启动本地任务"));
assert.equal(widget.data.history.dailyTokens[0].tokens, 10, "冷启动应接受本地中间数据");
assert.equal(widget.data.insights.tasks[0].name, "冷启动本地任务");

widget.updateUsage({
  rateLimits: {
    planType: "plus",
    primary: { remainingPercent: 80 },
    secondary: { remainingPercent: 60 },
  },
  today: { tokens: 100, questions: 2, tokenSource: "account", conversations: [] },
  history: { source: "account", dailyTokens: [{ date: "2033-05-13", tokens: 100, source: "account" }] },
  insights: { localOnly: true, tasks: [{ name: "完整任务名称", tokens: 100 }], projects: [] },
  contextHealth: { source: "local", sessions: [{ name: "完整任务名称", usedPercent: 65, remainingPercent: 35, status: "attention" }] },
  forecast: { primary: { status: "insufficient" } },
  capabilities: { extendedInsights: true },
});
assert.equal(widget.hasCompleteUsageSnapshot, true);
assert.equal(widget.data.history.dailyTokens[0].tokens, 100);

widget.updateUsage(localPayload(999, "刷新中的临时名称", { capabilities: { extendedInsights: true, menuBar: true } }));
assert.equal(widget.data.history.dailyTokens[0].tokens, 100, "刷新中间态不得覆盖完整历史");
assert.equal(widget.data.insights.tasks[0].name, "完整任务名称", "刷新中间态不得改变周报任务");
assert.equal(widget.data.contextHealth.sessions[0].usedPercent, 65, "刷新中间态不得改变上下文健康快照");
assert.equal(widget.data.capabilities.menuBar, true, "中间态仍应更新能力声明");

widget.updateUsage({ ...localPayload(777, "错误回退名称"), partial: false, error: "同步失败" });
assert.equal(widget.data.history.dailyTokens[0].tokens, 100, "失败回退不得覆盖完整历史");
assert.equal(widget.data.insights.tasks[0].name, "完整任务名称", "失败回退不得改变周报任务");
assert.equal(widget.contextHealthStatus({ usedPercent: 59 }).status, "healthy");
assert.equal(widget.contextHealthStatus({ usedPercent: 60 }).status, "attention");
assert.equal(widget.contextHealthStatus({ usedPercent: 80 }).status, "high");
assert.equal(widget.contextHealthStatus({ usedPercent: 90 }).status, "critical");
assert.equal(widget.contextHealthStatus({ remainingPercent: 18 }).status, "high", "仅提供剩余比例时也应正确判断健康度");

const contextSummaryWidget = new Widget();
contextSummaryWidget.data.capabilities = { contextHealth: true };
contextSummaryWidget.data.contextHealth = {
  currentTaskId: "context-summary-task",
  currentTaskName: "当前上下文任务名称",
  sessions: [],
};
let contextSummary = contextSummaryWidget.contextHealthSummaryState();
assert.equal(contextSummary.supported, true, "声明 capability 后应显示上下文圆盘");
assert.equal(contextSummary.available, false, "无窗口数据时圆盘应进入中性状态");
assert.equal(contextSummary.remaining, null, "无窗口数据时不得伪造剩余比例");
assert.equal(contextSummary.detail, "当前上下文任务名称", "无窗口数据时仍应保留当前任务名称");

for (const [usedPercent, expectedStatus, expectedRemaining] of [
  [25, "healthy", 75],
  [65, "attention", 35],
  [85, "high", 15],
  [95, "critical", 5],
]) {
  contextSummaryWidget.data.contextHealth.sessions = [{
    taskId: "context-summary-task",
    threadId: "context-summary-task",
    name: "当前上下文任务名称",
    usedPercent,
    remainingPercent: expectedRemaining,
  }];
  contextSummary = contextSummaryWidget.contextHealthSummaryState();
  assert.equal(contextSummary.status, expectedStatus, `已用 ${usedPercent}% 时应进入 ${expectedStatus} 状态`);
  assert.equal(contextSummary.remaining, expectedRemaining, "上下文圆盘必须显示剩余比例");
}

contextSummaryWidget.data.capabilities = {};
assert.equal(contextSummaryWidget.contextHealthSummaryState().supported, false, "旧宿主未声明 capability 时应隐藏上下文圆盘");

widget.updateCurrentContextHealth({
  contextHealth: {
    currentTaskId: "thread-current",
    currentTaskName: "当前任务",
    trackingMode: "recent_conversation",
    session: {
      taskId: "thread-current",
      threadId: "thread-current",
      name: "当前任务",
      usedTokens: 75_000,
      maxTokens: 100_000,
      usedPercent: 75,
      remainingPercent: 25,
      status: "attention",
    },
  },
});
assert.equal(widget.data.contextHealth.currentTaskId, "thread-current", "实时更新应记录当前对话");
assert.equal(widget.currentContextHealthSession().usedTokens, 75_000, "实时更新应插入当前对话健康度");

widget.updateCurrentContextHealth({
  contextHealth: {
    currentTaskId: "thread-current",
    session: { taskId: "thread-current", threadId: "thread-current", usedTokens: 82_000, usedPercent: 82, remainingPercent: 18 },
  },
});
assert.equal(widget.contextHealthSessions().filter((session) => session.taskId === "thread-current").length, 1, "同一当前对话不得产生重复条目");
assert.equal(widget.currentContextHealthSession().maxTokens, 100_000, "增量更新应保留已有窗口上限");
assert.equal(widget.currentContextHealthSession().usedTokens, 82_000, "增量更新应替换最新用量");

widget.updateUsage({
  rateLimits: {
    planType: "plus",
    primary: { remainingPercent: 75 },
    secondary: { remainingPercent: 58 },
  },
  today: { tokens: 120, questions: 3, tokenSource: "account", conversations: [] },
  history: { source: "account", dailyTokens: [{ date: "2033-05-13", tokens: 120, source: "account" }] },
  insights: { localOnly: true, tasks: [{ name: "更新后的完整任务", tokens: 120 }], projects: [] },
  contextHealth: { source: "local", sessions: [{ name: "更新后的完整任务", usedPercent: 40, remainingPercent: 60 }] },
  capabilities: { extendedInsights: true },
});
assert.equal(widget.data.history.dailyTokens[0].tokens, 120, "下一次完整结果应原子替换历史");
assert.equal(widget.data.insights.tasks[0].name, "更新后的完整任务");
assert.equal(widget.data.contextHealth.currentTaskId, "thread-current", "完整额度同步不得清除当前对话跟踪状态");

console.log("Widget state tests passed");
