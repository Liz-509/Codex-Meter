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

let nextFrameID = 1;
const pendingFrames = new Map();
sandbox.requestAnimationFrame = (callback) => {
  const id = nextFrameID++;
  pendingFrames.set(id, callback);
  return id;
};
sandbox.cancelAnimationFrame = (id) => pendingFrames.delete(id);
const advanceFrames = (timestamp) => {
  const callbacks = [...pendingFrames.values()];
  pendingFrames.clear();
  callbacks.forEach((callback) => callback(timestamp));
};
const counterClasses = new Set();
const counterElement = {
  textContent: "",
  offsetWidth: 20,
  classList: {
    add: (name) => counterClasses.add(name),
    remove: (name) => counterClasses.delete(name),
  },
};
widget.animateNumber("animated-test", counterElement, 100, String, "same-day");
assert.equal(counterElement.textContent, "0", "首次载入应从 0 开始而不是跳到目标值");
advanceFrames(0);
advanceFrames(425);
assert.ok(Number(counterElement.textContent) > 0 && Number(counterElement.textContent) < 100, "动画中应显示递增的中间值");
advanceFrames(850);
assert.equal(counterElement.textContent, "100", "动画结束必须精确落到目标值");

widget.animateNumber("animated-test", counterElement, 200, String, "same-day");
advanceFrames(1_000);
advanceFrames(1_325);
const rebasedValue = Number(counterElement.textContent);
widget.animateNumber("animated-test", counterElement, 300, String, "same-day");
assert.equal(Number(counterElement.textContent), rebasedValue, "动画中收到新值时应从当前画面继续衔接");
advanceFrames(1_400);
advanceFrames(2_050);
assert.equal(counterElement.textContent, "300", "重新衔接后的动画应到达最新目标");

widget.animateNumber("animated-test", counterElement, 50, String, "next-turn");
assert.equal(counterElement.textContent, "0", "切换轮次时应先重置本轮计数");
assert.ok(counterClasses.has("counter-reset"), "切换轮次应触发淡出重置样式");
advanceFrames(2_100);
advanceFrames(2_750);
assert.equal(counterElement.textContent, "50");

widget.animateNumber("animated-test", counterElement, 25, String, "next-turn");
advanceFrames(2_800);
advanceFrames(3_450);
assert.equal(counterElement.textContent, "25", "同一指标向下修正时也应平滑到达目标");

widget.pageVisible = false;
widget.animateNumber("animated-test", counterElement, 80, String, "next-turn");
assert.equal(counterElement.textContent, "80", "页面隐藏时应直接落到最终值");
assert.equal(pendingFrames.size, 0, "页面隐藏时不得保留数字动画帧");
widget.pageVisible = true;
const normalMatchMedia = sandbox.matchMedia;
sandbox.matchMedia = () => ({ matches: true });
widget.animateNumber("reduced-motion-test", counterElement, 144, String, "same-day");
assert.equal(counterElement.textContent, "144", "减少动态效果开启时应直接显示最终值");
assert.equal(pendingFrames.size, 0, "减少动态效果开启时不得请求动画帧");
sandbox.matchMedia = normalMatchMedia;

widget.animateNumber("pause-animation-test", counterElement, 200, String, "same-day");
advanceFrames(3_500);
advanceFrames(3_700);
assert.ok(Number(counterElement.textContent) < 200, "暂停前应仍处于动画中");
widget.setHostActive(false);
assert.equal(counterElement.textContent, "200", "宿主暂停时应将运行中的动画落到最终值");
assert.equal(pendingFrames.size, 0, "宿主暂停时应取消所有数字动画帧");
widget.setHostActive(true);

widget.handleRemoteSessionSettings({
  supported: true,
  enabled: false,
  connectedHosts: 2,
  promptNeeded: true,
});
assert.equal(widget.remoteSessionSettings.connectedHosts, 2, "应保存 Codex 当前已连接的 SSH 主机数量");
assert.equal(widget.data.capabilities.remoteSessionMonitoringEnabled, false, "SSH 对话监控应支持默认关闭");
assert.equal(widget.data.capabilities.remoteSessionPromptNeeded, true, "发现主机且未开启时应显示一次引导");

widget.handleRemoteSessionSettings({
  supported: true,
  enabled: true,
  connectedHosts: 2,
  promptNeeded: false,
});
assert.equal(widget.data.capabilities.remoteSessionMonitoringEnabled, true, "开启后应同步更新 SSH 监控状态");
assert.equal(widget.data.capabilities.remoteSessionPromptNeeded, false, "开启后应隐藏 SSH 监控引导");

const remoteSettingsContainer = { innerHTML: "" };
widget.shadowRoot.querySelector = (selector) => selector === ".remote-session-settings" ? remoteSettingsContainer : null;
widget.remoteSessionSettings.connectedHosts = 2;
widget.data.capabilities.remoteSessionHostCount = 1;
widget.renderRemoteSessionSettings();
assert.match(remoteSettingsContainer.innerHTML, /读取 1 台当前已连接服务器/, "刷新后的活动连接数应覆盖设置页旧状态");
widget.shadowRoot.querySelector = () => null;

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

const localAndRemoteGroups = widget.groupConversations([
  { threadId: "shared-id", threadName: "本机任务", startedAt: "2033-05-13T10:00:00Z" },
  { threadId: "shared-id", threadName: "远程任务", sourceHost: "Build Box", startedAt: "2033-05-13T11:00:00Z" },
]);
assert.equal(localAndRemoteGroups.length, 2, "本机与远程主机上的任务标识不得错误合并");
assert.equal(localAndRemoteGroups[0].title, "远程任务 · Build Box", "远程对话标题应标出服务器名称");

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
    conversationTokens: 128_500,
    currentTurnId: "turn-live",
    currentTurnTokens: 31_250,
    currentTurnActive: true,
    usedPercent,
    remainingPercent: expectedRemaining,
  }];
  contextSummary = contextSummaryWidget.contextHealthSummaryState();
  assert.equal(contextSummary.status, expectedStatus, `已用 ${usedPercent}% 时应进入 ${expectedStatus} 状态`);
  assert.equal(contextSummary.remaining, expectedRemaining, "上下文圆盘必须显示剩余比例");
  assert.equal(contextSummary.conversationTokens, 128_500, "当前对话卡必须使用整段对话累计 Token");
  assert.equal(contextSummary.currentTurnTokens, 31_250, "当前对话卡必须提供本轮回答 Token");
  assert.equal(contextSummary.currentTurnActive, true, "回答中应显示实时状态");
}

contextSummaryWidget.data.capabilities.currentConversationTokens = true;
assert.equal(contextSummaryWidget.contextHealthSummaryState().conversationTokens, 128_500, "macOS 能力开启后应保留当前对话累计值");

contextSummaryWidget.data.capabilities = {};
assert.equal(contextSummaryWidget.contextHealthSummaryState().supported, false, "旧宿主未声明 capability 时应隐藏上下文圆盘");

const remoteContextSummaryWidget = new Widget();
remoteContextSummaryWidget.data.capabilities = { contextHealth: true };
remoteContextSummaryWidget.data.contextHealth = {
  currentTaskId: "last-local-task",
  currentTaskName: "上一个本机任务",
  sessions: [
    {
      taskId: "last-local-task",
      threadId: "last-local-task",
      name: "上一个本机任务",
      usedPercent: 20,
      remainingPercent: 80,
      lastActive: "2033-05-13T10:00:00Z",
    },
    {
      taskId: "active-ssh-task",
      threadId: "active-ssh-task",
      name: "当前 SSH 任务",
      sourceHost: "Build Box",
      usedPercent: 70,
      remainingPercent: 30,
      lastActive: "2033-05-13T10:05:00Z",
    },
  ],
};
let remoteContextSummary = remoteContextSummaryWidget.contextHealthSummaryState();
assert.equal(remoteContextSummary.available, true, "SSH 当前任务存在上下文时主卡片不应显示为空");
assert.equal(remoteContextSummary.detail, "当前 SSH 任务", "本机 App Server 仍指向旧任务时应展示更新的 SSH 上下文");
assert.equal(remoteContextSummary.remaining, 30, "SSH 当前任务的剩余上下文比例应显示在主卡片");

remoteContextSummaryWidget.data.contextHealth.sessions[0].lastActive = "2033-05-13T10:10:00Z";
remoteContextSummary = remoteContextSummaryWidget.contextHealthSummaryState();
assert.equal(remoteContextSummary.detail, "上一个本机任务", "切回并继续本机任务后应恢复精确匹配的本机上下文");

widget.updateCurrentContextHealth({
  contextHealth: {
    currentTaskId: "thread-current",
    currentTaskName: "当前任务",
    trackingMode: "recent_conversation",
    session: {
      taskId: "thread-current",
      threadId: "thread-current",
      name: "当前任务",
      conversationTokens: 96_000,
      currentTurnId: "turn-current",
      currentTurnTokens: 24_000,
      currentTurnActive: true,
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
assert.equal(widget.currentContextHealthSession().conversationTokens, 96_000, "实时更新应插入当前对话累计 Token");
assert.equal(widget.currentContextHealthSession().currentTurnTokens, 24_000, "实时更新应插入本轮回答 Token");

widget.updateCurrentContextHealth({
  contextHealth: {
    currentTaskId: "thread-current",
    session: { taskId: "thread-current", threadId: "thread-current", currentTurnId: "turn-current", currentTurnTokens: 32_000, usedTokens: 82_000, usedPercent: 82, remainingPercent: 18 },
  },
});
assert.equal(widget.contextHealthSessions().filter((session) => session.taskId === "thread-current").length, 1, "同一当前对话不得产生重复条目");
assert.equal(widget.currentContextHealthSession().maxTokens, 100_000, "增量更新应保留已有窗口上限");
assert.equal(widget.currentContextHealthSession().usedTokens, 82_000, "增量更新应替换最新用量");
assert.equal(widget.currentContextHealthSession().conversationTokens, 96_000, "缺少累计值的增量更新应保留最近一次当前对话 Tokens");
assert.equal(widget.currentContextHealthSession().currentTurnTokens, 32_000, "同一轮的实时 Token 应替换为最新值");

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
