const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

let Widget;
let nextElementIsNative = false;
class FakeHTMLElement {
  attachShadow() {
    this.shadowRoot = { querySelector: () => null, querySelectorAll: () => [] };
    return this.shadowRoot;
  }

  hasAttribute(name) { return name === "native" && nextElementIsNative; }
  setAttribute() {}
}

const createWidget = ({ native = false } = {}) => {
  nextElementIsNative = native;
  const widget = new Widget();
  nextElementIsNative = false;
  return widget;
};

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

sandbox.codexMeterInitialCapabilities = {
  contextHealth: true,
  currentConversationTokens: true,
  refreshSettings: true,
};
const macColdStartWidget = createWidget({ native: true });
macColdStartWidget.render();
assert.equal(macColdStartWidget.data.capabilities.currentConversationTokens, true, "macOS 静态能力应在首个数据载荷前生效");
assert.match(macColdStartWidget.shadowRoot.innerHTML, /quota-overview has-live-token-card/, "无数据首屏也应直接使用双圆环布局");
assert.match(macColdStartWidget.shadowRoot.innerHTML, /context-health-summary is-conversation-token is-empty/, "无数据首屏应直接显示本轮回答占位卡片");
assert.match(macColdStartWidget.shadowRoot.innerHTML, /live-badge is-complete">等待/, "无数据首屏不得把本轮回答误报为已完成或实时");
assert.match(macColdStartWidget.shadowRoot.innerHTML, /section class="widget[^"]*initial-loading[^"]*"[^>]*aria-busy="true"/, "原生冷启动应启用首次加载遮罩和 aria-busy");
assert.match(macColdStartWidget.shadowRoot.innerHTML, /class="initial-loading-overlay" role="status"[^>]*aria-hidden="false"/, "原生冷启动遮罩应提供可访问的加载状态");
assert.match(macColdStartWidget.styles, /\.initial-loading \.initial-loading-star-main \{ animation:initial-star-breathe/, "主星动画只应在冷启动状态运行");
assert.match(macColdStartWidget.styles, /\.initial-loading \.initial-loading-star-small \{ animation:initial-star-glint/, "小星动画只应在冷启动状态运行");
assert.doesNotMatch(macColdStartWidget.styles, /\n\s+\.initial-loading-star-main \{[^}]*animation:/, "冷启动退出后主星不应保留动画");
assert.doesNotMatch(macColdStartWidget.styles, /\n\s+\.initial-loading-star-small \{[^}]*animation:/, "冷启动退出后小星不应保留动画");
delete sandbox.codexMeterInitialCapabilities;

const widget = new Widget();
assert.equal(widget.data.capabilities.currentConversationTokens, undefined, "未注入 macOS 能力的旧宿主应继续使用兼容布局");
widget.render();
assert.match(widget.shadowRoot.innerHTML, /section class="widget[^"]*"[^>]*aria-busy="false"/, "非原生组件不应进入冷启动加载状态");
assert.doesNotMatch(widget.shadowRoot.innerHTML, /class="initial-loading-overlay" role="status"/, "非原生组件不应渲染软件冷启动遮罩");
widget.renderValues = () => {};

macColdStartWidget.renderValues = () => {};
macColdStartWidget.updateUsage({ partial: true, syncMessage: "正在同步额度", today: { tokens: 12, questions: 1 } });
assert.equal(macColdStartWidget.initialLoadPending, true, "冷启动 partial 数据不得关闭首次加载遮罩");

const initialLoadingClasses = new Set(["initial-loading"]);
const initialLoadingAttributes = {};
const initialLoadingOverlayAttributes = {};
const initialLoadingWidgetElement = {
  classList: { remove: (name) => initialLoadingClasses.delete(name) },
  setAttribute: (name, value) => { initialLoadingAttributes[name] = value; },
};
const initialLoadingOverlayElement = {
  setAttribute: (name, value) => { initialLoadingOverlayAttributes[name] = value; },
};
macColdStartWidget.shadowRoot.querySelector = (selector) => selector === ".widget"
  ? initialLoadingWidgetElement
  : selector === ".initial-loading-overlay"
    ? initialLoadingOverlayElement
    : null;
macColdStartWidget.updateUsage({
  rateLimits: {
    planType: "plus",
    primary: { remainingPercent: 82 },
    secondary: { remainingPercent: 64 },
  },
  today: { tokens: 120, questions: 2, conversations: [] },
});
assert.equal(macColdStartWidget.initialLoadPending, false, "首次完整成功载荷应关闭冷启动遮罩");
assert.equal(initialLoadingClasses.has("initial-loading"), false, "完整载荷后应移除视觉加载状态");
assert.equal(initialLoadingAttributes["aria-busy"], "false", "完整载荷后应同步清除 aria-busy");
assert.equal(initialLoadingOverlayAttributes["aria-hidden"], "true", "完整载荷后应从辅助技术隐藏加载状态");
macColdStartWidget.updateUsage({ partial: true, syncMessage: "后台刷新中" });
macColdStartWidget.updateUsage({ error: "后台同步失败" });
assert.equal(macColdStartWidget.initialLoadPending, false, "后续刷新或错误不得重新启用冷启动遮罩");

const initialErrorWidget = createWidget({ native: true });
initialErrorWidget.renderValues = () => {};
initialErrorWidget.updateUsage({ error: "首次连接失败" });
assert.equal(initialErrorWidget.initialLoadPending, false, "首次完整错误载荷也应结束冷启动遮罩");
assert.equal(initialErrorWidget.syncState, "error", "关闭遮罩后应保留现有错误状态");
initialErrorWidget.render();
assert.match(initialErrorWidget.shadowRoot.innerHTML, /section class="widget(?![^"]*initial-loading)[^"]*"[^>]*aria-busy="false"/, "首次错误后重新渲染也不得恢复遮罩");
assert.match(initialErrorWidget.shadowRoot.innerHTML, /class="initial-loading-overlay" role="status"[^>]*aria-hidden="true"/, "首次错误后加载状态应对辅助技术隐藏");

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

widget.handleRefreshSettings({
  supported: true,
  liveSeconds: 1,
  generalSeconds: 60,
  sshSeconds: 30,
  liveOptions: [1, 2, 5, 10],
  generalOptions: [15, 30, 60, 120],
  sshOptions: [30, 60, 120, 300],
});
assert.equal(widget.refreshSettings.liveSeconds, 1, "应保存实时回答刷新档位");
assert.equal(widget.refreshSettings.generalSeconds, 60, "应保存常规数据刷新档位");
assert.equal(widget.refreshSettings.sshSeconds, 30, "应保存 SSH 刷新档位");
assert.equal(widget.data.capabilities.refreshSettings, true, "宿主声明后应启用刷新设置");
assert.equal(widget.refreshIntervalLabel(120), "2 分钟", "分钟档位应使用易读标签");

const refreshToggleClasses = new Set();
const refreshToggleAttributes = {};
const refreshSettingsToggle = {
  setAttribute: (name, value) => { refreshToggleAttributes[name] = value; },
  classList: { toggle: (name, enabled) => enabled ? refreshToggleClasses.add(name) : refreshToggleClasses.delete(name) },
};
const refreshSettingsSummary = { textContent: "" };
const refreshSettingsContainer = { innerHTML: "", hidden: false };
widget.shadowRoot.querySelector = (selector) => selector === ".refresh-settings"
  ? refreshSettingsContainer
  : selector === ".refresh-settings-toggle"
    ? refreshSettingsToggle
    : selector === ".refresh-settings-summary"
      ? refreshSettingsSummary
      : null;
widget.data.capabilities.remoteSessionMonitoringEnabled = false;
widget.refreshSettingsExpanded = false;
widget.renderRefreshSettings();
assert.equal(refreshSettingsContainer.hidden, true, "刷新频率在设置中应默认收起");
assert.equal(refreshToggleAttributes["aria-expanded"], "false", "收起状态应同步无障碍属性");
assert.equal(refreshSettingsSummary.textContent, "实时 1 秒 · 常规 1 分钟 · SSH 30 秒", "收起列表应概括当前三个刷新档位");
assert.match(refreshSettingsContainer.innerHTML, /开启 SSH 对话监控后生效/, "SSH 关闭时应提示档位尚未生效");
assert.match(refreshSettingsContainer.innerHTML, /data-refresh-kind="ssh"[^>]*disabled/, "SSH 关闭时应禁用远端档位");
widget.refreshSettingsExpanded = true;
widget.data.capabilities.remoteSessionMonitoringEnabled = true;
widget.renderRefreshSettings();
assert.equal(refreshSettingsContainer.hidden, false, "展开刷新频率后应显示三组档位");
assert.equal(refreshToggleAttributes["aria-expanded"], "true", "展开状态应同步无障碍属性");
assert.ok(refreshToggleClasses.has("is-expanded"), "展开状态应旋转列表箭头");
assert.match(refreshSettingsContainer.innerHTML, /高频 SSH 刷新会增加网络、耗电和远端主机负载/, "SSH 选择 30 秒时应显示高频提醒");
widget.openDialog("settings");
assert.equal(widget.refreshSettingsExpanded, false, "重新打开设置时刷新频率应恢复收起");
assert.equal(refreshSettingsContainer.hidden, true, "重新打开设置时不应保留上次展开状态");

const mediumFit = widget.numberFitResult(200, 100, 25, 10);
assert.equal(mediumFit.fontSize, 12.5, "大数字应优先通过字号缩放适配宽度");
assert.equal(mediumFit.scale, 1, "未触及安全字号时不应水平压缩");
const extremeFit = widget.numberFitResult(500, 100, 25, 10);
assert.equal(extremeFit.fontSize, 10, "极端大数应停在安全字号下限");
assert.equal(extremeFit.scale, 0.5, "达到安全下限后应水平微调以保证完整显示");
assert.equal(widget.formatNumber(9_999), "9,999", "四位数应保留标准格式");
assert.equal(widget.formatNumber(10_000), "1万", "万级数字应使用中文紧凑格式");
assert.equal(widget.formatNumber(12_345_000), "1234.5万", "较长万级数字不得提前截断");
assert.equal(widget.formatNumber(Number.MAX_SAFE_INTEGER), "9007.2万亿", "最大安全整数应生成可完整适配的文本");

const refreshSettingsGroup = { hidden: false };
widget.shadowRoot.querySelector = (selector) => selector === ".refresh-settings"
  ? refreshSettingsContainer
  : selector === ".refresh-settings-group"
    ? refreshSettingsGroup
    : null;
widget.refreshSettings.supported = false;
widget.data.capabilities.refreshSettings = false;
widget.renderRefreshSettings();
assert.equal(refreshSettingsGroup.hidden, true, "旧宿主未声明能力时应隐藏整个刷新设置区域");
widget.refreshSettings.supported = true;
widget.data.capabilities.refreshSettings = true;

const layoutClasses = new Set();
const layoutOverview = {
  classList: { toggle: (name, enabled) => enabled ? layoutClasses.add(name) : layoutClasses.delete(name) },
};
const layoutCardClasses = new Set();
const layoutCard = {
  toggleAttribute: () => {},
  setAttribute: () => {},
  classList: {
    remove: (...names) => names.forEach((name) => layoutCardClasses.delete(name)),
    toggle: (name, enabled) => enabled ? layoutCardClasses.add(name) : layoutCardClasses.delete(name),
  },
  querySelector: () => null,
  querySelectorAll: () => [],
};
const layoutWidget = new Widget();
layoutWidget.animateNumber = () => {};
layoutWidget.data.capabilities = { contextHealth: true, currentConversationTokens: true };
layoutWidget.data.contextHealth = { sessions: [] };
layoutWidget.shadowRoot.querySelector = (selector) => selector === ".context-health-summary"
  ? layoutCard
  : selector === ".quota-overview"
    ? layoutOverview
    : null;
layoutWidget.renderContextHealthSummary();
assert.ok(layoutClasses.has("has-live-token-card"), "macOS 实时 Token 能力应启用双圆环与全宽本轮布局");
layoutWidget.data.capabilities = { contextHealth: true };
layoutWidget.renderContextHealthSummary();
assert.equal(layoutClasses.has("has-live-token-card"), false, "旧宿主未声明实时 Token 能力时应保留原布局");
assert.ok(source.indexOf('<div class="quota-hero secondary-quota-hero">') < source.indexOf('data-action="context-health-detail"'), "每周圆环应排在本轮回答卡片之前");
assert.match(source, /\.quota-overview\.has-live-token-card \.context-health-summary \{ grid-column:1\/-1;/, "本轮回答在 macOS 布局中应占满整行");
assert.match(source, /\.quota-overview\.has-live-token-card \+ \.weekly \{ display:none;/, "macOS 双圆环布局应隐藏旧每周进度条");

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

const remoteContextList = { innerHTML: "" };
const remoteContextListSummary = { textContent: "" };
remoteContextSummaryWidget.shadowRoot.querySelector = (selector) => selector === ".context-health-list"
  ? remoteContextList
  : selector === ".context-health-dialog-summary"
    ? remoteContextListSummary
    : null;
remoteContextSummaryWidget.renderContextHealth();
assert.match(remoteContextList.innerHTML, /<strong>当前 SSH 任务<em>当前对话<\/em><\/strong>/, "详情应把主卡片选中的 SSH 任务标为当前对话");
assert.doesNotMatch(remoteContextList.innerHTML, /<strong>上一个本机任务<em>当前对话<\/em><\/strong>/, "详情不得把 App Server 中残留的本机任务标为当前对话");
assert.ok(remoteContextList.innerHTML.indexOf("当前 SSH 任务") < remoteContextList.innerHTML.indexOf("上一个本机任务"), "详情应把当前 SSH 任务排在首位");

remoteContextSummaryWidget.data.contextHealth.sessions[0].lastActive = "2033-05-13T10:10:00Z";
remoteContextSummary = remoteContextSummaryWidget.contextHealthSummaryState();
assert.equal(remoteContextSummary.detail, "上一个本机任务", "切回并继续本机任务后应恢复精确匹配的本机上下文");

const duplicateTaskIdWidget = new Widget();
duplicateTaskIdWidget.data.contextHealth = {
  currentTaskId: "shared-task-id",
  sessions: [
    { threadId: "shared-task-id", sourceHost: "Build Box", name: "同 ID 远端任务", lastActive: "2033-05-13T10:10:00Z" },
    { threadId: "shared-task-id", name: "同 ID 本机任务", lastActive: "2033-05-13T10:10:00Z" },
  ],
};
assert.equal(duplicateTaskIdWidget.currentContextHealthSession().name, "同 ID 本机任务", "本机 currentTaskId 不得误匹配同 ID 的 SSH 任务");

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
