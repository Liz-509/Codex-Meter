const ICONS = {
  spark: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 2.8c.5 4.9 4.3 8.7 9.2 9.2-4.9.5-8.7 4.3-9.2 9.2-.5-4.9-4.3-8.7-9.2-9.2 4.9-.5 8.7-4.3 9.2-9.2Z"/></svg>`,
  sun: `<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="3.6"/><path d="M12 2v2M12 20v2M4.93 4.93l1.42 1.42m11.3 11.3 1.42 1.42M2 12h2m16 0h2M4.93 19.07l1.42-1.42m11.3-11.3 1.42-1.42"/></svg>`,
  moon: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M20.2 15.1A8.5 8.5 0 0 1 8.9 3.8a8.5 8.5 0 1 0 11.3 11.3Z"/></svg>`,
  refresh: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M20 6v5h-5M4 18v-5h5"/><path d="M18.1 9A7 7 0 0 0 6.5 6.5L4 11m16 2-2.5 4.5A7 7 0 0 1 5.9 15"/></svg>`,
  chevron: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m8 10 4 4 4-4"/></svg>`,
  close: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m7 7 10 10M17 7 7 17"/></svg>`,
  reset: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M20 11a8 8 0 1 0-2.34 5.66M20 4v7h-7"/></svg>`,
  token: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m12 3 7.8 4.5v9L12 21l-7.8-4.5v-9L12 3Z"/><path d="m4.5 7.7 7.5 4.4 7.5-4.4M12 12.1V21"/></svg>`,
  message: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M20 15a4 4 0 0 1-4 4H8l-5 3V8a4 4 0 0 1 4-4h9a4 4 0 0 1 4 4v7Z"/></svg>`,
};

const DEFAULT_DATA = {
  primary: { label: "5 小时额度", remainingPercent: null, resetsAt: null },
  secondary: { label: "每周额度", remainingPercent: null, resetsAt: null },
  resetCredits: null,
  todayTokens: null,
  todayQuestions: null,
  tokenSource: "local",
  conversations: [],
  history: { source: "local", dailyTokens: [] },
  plan: "同步中",
  updatedAt: Date.now(),
  syncMessage: "正在连接 Codex",
};

const clamp = (value, min, max) => Math.min(max, Math.max(min, value));
const quotaTone = (value) => value == null ? "normal" : value < 10 ? "critical" : value < 20 ? "warning" : "normal";

const hostBridge = {
  getUsage() {
    return window.codexMeterBridge?.getUsage?.() ?? window.codexUsageBridge?.getUsage?.();
  },
  resize(payload) {
    if (window.codexMeterBridge?.resize) {
      window.codexMeterBridge.resize(payload);
      return true;
    }
    if (window.webkit?.messageHandlers?.panel) {
      window.webkit.messageHandlers.panel.postMessage({ action: "resize", ...payload });
      return true;
    }
    return false;
  },
  quit() {
    if (window.codexMeterBridge?.quit) {
      window.codexMeterBridge.quit();
      return;
    }
    window.webkit?.messageHandlers?.panel?.postMessage({ action: "quit" });
  },
  beginDrag(payload) {
    window.codexMeterBridge?.beginDrag?.(payload);
  },
  consumeReset() {
    if (window.codexMeterBridge?.consumeReset) {
      window.codexMeterBridge.consumeReset({ confirmed: true });
      return true;
    }
    if (window.webkit?.messageHandlers?.panel) {
      window.webkit.messageHandlers.panel.postMessage({ action: "consumeReset", confirmed: true });
      return true;
    }
    return false;
  },
};

class CodexUsageWidget extends HTMLElement {
  constructor() {
    super();
    this.attachShadow({ mode: "open" });
    this.data = structuredClone(DEFAULT_DATA);
    this.autoHover = this.hasAttribute("native");
    this.collapsed = this.autoHover ? true : localStorage.getItem("codex-widget-collapsed") === "true";
    this.theme = localStorage.getItem("codex-widget-theme") || "auto";
    this.syncState = "loading";
    this.expandedHeight = 443;
    this.hoverCloseTimer = null;
    this.collapseResizeTimer = null;
    this.quotaAnimationFrame = null;
    this.expansionPointer = { x: 33, y: 33 };
    this.dragging = false;
    this.suppressHoverUntilLeave = false;
    this.suppressHoverTimer = null;
    this.activeDialog = null;
    this.expandedConversationGroups = new Set();
    this.pointerInside = false;
    this.resetting = false;
  }

  connectedCallback() {
    this.render();
    this.bindEvents();
    if (this.autoHover) this.bindHoverExpansion();
    requestAnimationFrame(() => this.syncNativeSize(false));
    this.refresh();
  }

  async refresh() {
    this.setLoading(true);
    try {
      let payload;
      if (window.codexMeterBridge?.getUsage || window.codexUsageBridge?.getUsage) {
        payload = await hostBridge.getUsage();
      } else {
        const response = await fetch("/api/usage", { headers: { Accept: "application/json" } });
        if (response.ok && response.headers.get("content-type")?.includes("application/json")) {
          payload = await response.json();
        }
      }
      if (payload) this.updateUsage(payload);
    } catch {
      // Standalone preview: keep the last known/demo data visible.
    } finally {
      this.data.updatedAt = Date.now();
      this.renderValues();
      this.setLoading(false);
    }
  }

  updateUsage(payload) {
    if (payload.partial) {
      const localStats = payload.today || {};
      this.syncState = "loading";
      this.data.todayTokens = localStats.tokens ?? this.data.todayTokens;
      this.data.todayQuestions = localStats.questions ?? this.data.todayQuestions;
      this.data.tokenSource = localStats.tokenSource || this.data.tokenSource;
      if (Array.isArray(localStats.conversations)) this.data.conversations = localStats.conversations;
      if (payload.history?.dailyTokens) this.data.history = payload.history;
      this.data.syncMessage = payload.syncMessage || "正在同步额度";
      this.data.updatedAt = Date.now();
      this.renderValues();
      return;
    }

    this.syncState = payload.error ? "error" : "success";
    if (payload.error) {
      const localStats = payload.today || {};
      this.data.todayTokens = localStats.tokens ?? this.data.todayTokens;
      this.data.todayQuestions = localStats.questions ?? this.data.todayQuestions;
      this.data.tokenSource = localStats.tokenSource || this.data.tokenSource;
      if (Array.isArray(localStats.conversations)) this.data.conversations = localStats.conversations;
      if (payload.history?.dailyTokens) this.data.history = payload.history;
      if (this.data.primary.remainingPercent == null) this.data.plan = "重试中";
      this.data.syncMessage = payload.error;
      this.data.updatedAt = Date.now();
      this.renderValues();
      return;
    }
    const limits = payload.rateLimitsByLimitId?.codex || payload.rateLimits || payload;
    const primary = limits.primary || payload.primary;
    const secondary = limits.secondary || payload.secondary;
    const localStats = payload.today || this.readTodayStats();

    const remaining = (bucket) => {
      if (!bucket) return null;
      if (bucket.remainingPercent != null) return clamp(bucket.remainingPercent, 0, 100);
      if (bucket.usedPercent != null) return clamp(100 - bucket.usedPercent, 0, 100);
      return null;
    };

    this.data = {
      primary: {
        label: primary?.label || "5 小时额度",
        remainingPercent: remaining(primary),
        resetsAt: primary?.resetsAt,
      },
      secondary: {
        label: secondary?.label || "每周额度",
        remainingPercent: remaining(secondary),
        resetsAt: secondary?.resetsAt,
      },
      resetCredits: payload.rateLimitResetCredits?.availableCount ?? payload.resetCredits ?? null,
      todayTokens: localStats.tokens ?? payload.todayTokens ?? null,
      todayQuestions: localStats.questions ?? payload.todayQuestions ?? null,
      tokenSource: localStats.tokenSource || payload.history?.source || "local",
      conversations: Array.isArray(localStats.conversations) ? localStats.conversations : this.data.conversations,
      history: payload.history?.dailyTokens ? payload.history : this.data.history,
      plan: (limits.planType || payload.plan || "已连接").replace(/^./, (char) => char.toUpperCase()),
      updatedAt: Date.now(),
      syncMessage: payload.error || "实时数据",
    };
    this.renderValues();
  }

  recordTurn({ inputTokens = 0, outputTokens = 0 } = {}) {
    const today = this.localDateKey();
    const stats = this.readTodayStats();
    const next = {
      date: today,
      tokens: stats.tokens + inputTokens + outputTokens,
      questions: stats.questions + 1,
    };
    localStorage.setItem("codex-widget-daily", JSON.stringify(next));
    this.data.todayTokens = next.tokens;
    this.data.todayQuestions = next.questions;
    this.data.tokenSource = "local";
    if (this.data.history?.dailyTokens?.length) {
      const days = this.data.history.dailyTokens.map((day) => day.date === today ? { ...day, tokens: next.tokens } : day);
      this.data.history = { source: "local", dailyTokens: days };
    }
    this.renderValues();
  }

  readTodayStats() {
    const today = this.localDateKey();
    try {
      const saved = JSON.parse(localStorage.getItem("codex-widget-daily"));
      return saved?.date === today ? saved : { date: today, tokens: 0, questions: 0 };
    } catch {
      return { date: today, tokens: 0, questions: 0 };
    }
  }

  formatNumber(value) {
    if (value == null) return "—";
    return new Intl.NumberFormat("zh-CN", { notation: value >= 10000 ? "compact" : "standard", maximumFractionDigits: 1 }).format(value);
  }

  localDateKey(date = new Date()) {
    const year = date.getFullYear();
    const month = String(date.getMonth() + 1).padStart(2, "0");
    const day = String(date.getDate()).padStart(2, "0");
    return `${year}-${month}-${day}`;
  }

  formatPercent(value) {
    return value == null ? "—" : `${Math.round(value)}%`;
  }

  formatReset(unixSeconds) {
    if (!unixSeconds) return "等待同步";
    const remaining = unixSeconds * 1000 - Date.now();
    if (remaining <= 0) return "即将刷新";
    const hours = Math.floor(remaining / 3600000);
    const minutes = Math.max(1, Math.floor((remaining % 3600000) / 60000));
    if (hours >= 24) return `${Math.floor(hours / 24)} 天 ${hours % 24} 小时后刷新`;
    return `${hours} 小时 ${minutes} 分后刷新`;
  }

  escapeHTML(value) {
    return String(value ?? "")
      .replaceAll("&", "&amp;")
      .replaceAll("<", "&lt;")
      .replaceAll(">", "&gt;")
      .replaceAll('"', "&quot;")
      .replaceAll("'", "&#39;");
  }

  cycleTheme() {
    const themes = ["auto", "light", "dark"];
    this.theme = themes[(themes.indexOf(this.theme) + 1) % themes.length];
    localStorage.setItem("codex-widget-theme", this.theme);
    this.applyTheme();
  }

  applyTheme() {
    this.setAttribute("data-theme", this.theme);
    const label = this.shadowRoot.querySelector(".theme-label");
    const button = this.shadowRoot.querySelector("[data-action='theme']");
    if (label) label.textContent = { auto: "跟随系统", light: "浅色", dark: "深色" }[this.theme];
    if (button) button.innerHTML = this.theme === "dark" ? ICONS.sun : ICONS.moon;
  }

  bindEvents() {
    this.shadowRoot.addEventListener("click", (event) => {
      const action = event.target.closest("[data-action]")?.dataset.action;
      if (action === "theme") this.cycleTheme();
      if (action === "refresh") this.refresh();
      if (action === "close") hostBridge.quit();
      if (action === "reset-credit") this.openResetDialog();
      if (action === "tokens-detail") this.openDialog("tokens");
      if (action === "conversations-detail") this.openDialog("conversations");
      if (action === "toggle-conversation-group") {
        const groupKey = event.target.closest("[data-group-key]")?.dataset.groupKey;
        if (groupKey) this.toggleConversationGroup(groupKey);
      }
      if (action === "close-dialog" || action === "cancel-reset") this.closeDialog();
      if (action === "confirm-reset") this.confirmReset();
      if (action === "collapse") {
        this.collapsed = !this.collapsed;
        localStorage.setItem("codex-widget-collapsed", String(this.collapsed));
        const widget = this.shadowRoot.querySelector(".widget");
        const toggle = this.shadowRoot.querySelector("[data-action='collapse']");
        toggle?.setAttribute("aria-expanded", String(!this.collapsed));

        if (this.collapsed) {
          this.syncNativeSize(true);
          setTimeout(() => widget?.classList.add("collapsed"), 220);
        } else {
          widget?.classList.remove("collapsed");
          requestAnimationFrame(() => this.syncNativeSize(true));
        }
      }
    });
    this.shadowRoot.addEventListener("keydown", (event) => {
      if (event.key === "Escape" && this.activeDialog && !this.resetting) {
        event.preventDefault();
        this.closeDialog();
      }
    });
  }

  openResetDialog() {
    if (this.resetting || !(this.data.resetCredits > 0)) return;
    this.openDialog("reset", "[data-action='cancel-reset']");
  }

  openDialog(kind, focusSelector = "[data-action='close-dialog']") {
    clearTimeout(this.hoverCloseTimer);
    clearTimeout(this.collapseResizeTimer);
    this.activeDialog = kind;
    this.shadowRoot.querySelectorAll(".app-dialog").forEach((dialog) => dialog.setAttribute("hidden", ""));
    if (kind === "tokens") this.renderTokenHistory();
    if (kind === "conversations") {
      this.expandedConversationGroups.clear();
      this.renderConversations();
    }
    const dialog = this.shadowRoot.querySelector(`[data-dialog='${kind}']`);
    dialog?.removeAttribute("hidden");
    requestAnimationFrame(() => dialog?.querySelector(focusSelector)?.focus());
  }

  closeDialog() {
    if (this.resetting && this.activeDialog === "reset") return;
    if (this.activeDialog === "conversations") {
      this.expandedConversationGroups.clear();
    }
    this.activeDialog = null;
    this.shadowRoot.querySelectorAll(".app-dialog").forEach((dialog) => dialog.setAttribute("hidden", ""));
    if (this.autoHover && !this.pointerInside) this.scheduleHoverCollapse();
  }

  confirmReset() {
    if (this.resetting || this.activeDialog !== "reset") return;
    this.resetting = true;
    this.renderResetState();
    if (!hostBridge.consumeReset()) {
      this.handleResetResult({ error: "当前宿主不支持额度重置" });
    }
  }

  handleResetResult(payload = {}) {
    this.resetting = false;
    this.activeDialog = null;
    const messages = {
      reset: "额度重置成功",
      nothingToReset: "当前额度无需重置，次数未消耗",
      noCredit: "没有可用的重置次数",
      alreadyRedeemed: "该次重置已经生效",
    };
    if (payload.outcome === "noCredit") this.data.resetCredits = 0;
    this.syncState = payload.error ? "error" : "success";
    this.data.syncMessage = payload.error || messages[payload.outcome] || "重置请求已完成";
    this.data.updatedAt = Date.now();
    this.shadowRoot.querySelector("[data-dialog='reset']")?.setAttribute("hidden", "");
    this.renderValues();
    if (this.autoHover && !this.pointerInside) this.scheduleHoverCollapse();
  }

  renderTokenHistory() {
    const chart = this.shadowRoot.querySelector(".token-chart");
    if (!chart) return;
    const days = Array.isArray(this.data.history?.dailyTokens) ? this.data.history.dailyTokens.slice(-7) : [];
    if (!days.length) {
      chart.innerHTML = '<div class="dialog-empty">暂无历史用量数据</div>';
      return;
    }
    const maximum = Math.max(1, ...days.map((day) => Math.max(0, Number(day.tokens) || 0)));
    const today = this.localDateKey();
    chart.innerHTML = days.map((day) => {
      const tokens = Math.max(0, Number(day.tokens) || 0);
      const height = tokens === 0 ? 0 : Math.max(5, Math.round(tokens / maximum * 100));
      const date = new Date(`${day.date}T00:00:00`);
      const label = Number.isNaN(date.getTime())
        ? day.date
        : new Intl.DateTimeFormat("zh-CN", { month: "numeric", day: "numeric" }).format(date);
      return `<div class="chart-column ${day.date === today ? "is-today" : ""}">
        <span class="chart-value">${this.escapeHTML(this.formatNumber(tokens))}</span>
        <span class="chart-track"><i style="height:${height}%"></i></span>
        <span class="chart-label">${this.escapeHTML(label)}</span>
      </div>`;
    }).join("");
  }

  renderConversations() {
    const list = this.shadowRoot.querySelector(".conversation-list");
    const summary = this.shadowRoot.querySelector(".conversation-summary");
    if (!list || !summary) return;
    const conversations = Array.isArray(this.data.conversations) ? this.data.conversations : [];
    const groups = this.groupConversations(conversations);
    summary.textContent = `本机记录 · ${groups.length} 个上下文 · ${conversations.length} 轮`;
    if (!conversations.length) {
      list.innerHTML = '<div class="dialog-empty">今天还没有可显示的对话</div>';
      return;
    }
    list.innerHTML = groups.map((group, groupIndex) => {
      const expanded = this.expandedConversationGroups.has(group.key);
      const groupId = `conversation-group-${groupIndex}`;
      const firstTime = this.formatConversationTime(group.turns[0]?.startedAt);
      const lastTime = this.formatConversationTime(group.turns.at(-1)?.startedAt);
      const timeRange = firstTime === lastTime || lastTime === "—" ? firstTime : `${firstTime}–${lastTime}`;
      const allTokensKnown = group.turns.every((conversation) =>
        conversation.tokens != null && Number.isFinite(Number(conversation.tokens)));
      const totalTokens = allTokensKnown
        ? group.turns.reduce((total, conversation) => total + Math.max(0, Number(conversation.tokens)), 0)
        : null;
      const groupTokenBadge = totalTokens == null
        ? ""
        : `<span class="conversation-group-tokens">${this.escapeHTML(this.formatNumber(totalTokens))} Tokens</span>`;
      const rows = group.turns.map((conversation) => {
        const tokenBadge = conversation.tokens == null
          ? ""
          : `<span class="conversation-tokens">${this.escapeHTML(this.formatNumber(conversation.tokens))} Tokens</span>`;
        return `<div class="conversation-row">
          <span class="conversation-time">${this.escapeHTML(this.formatConversationTime(conversation.startedAt))}</span>
          <span class="conversation-preview">${this.escapeHTML(conversation.preview || "未命名对话")}</span>
          ${tokenBadge}
        </div>`;
      }).join("");
      return `<section class="conversation-group ${expanded ? "is-expanded" : ""}">
        <button class="conversation-group-toggle" data-action="toggle-conversation-group" data-group-key="${this.escapeHTML(group.key)}" type="button" aria-expanded="${expanded}" aria-controls="${groupId}">
          <span class="conversation-group-copy">
            <span class="conversation-group-title" title="${this.escapeHTML(group.title)}">${this.escapeHTML(group.title)}</span>
            <span class="conversation-group-meta"><span>${this.escapeHTML(timeRange)}</span><span>${group.turns.length} 轮</span>${groupTokenBadge}</span>
          </span>
          <span class="conversation-group-chevron">${ICONS.chevron}</span>
        </button>
        <div class="conversation-group-turns" id="${groupId}"${expanded ? "" : " hidden"}>${rows}</div>
      </section>`;
    }).join("");
  }

  groupConversations(conversations) {
    const groupsByKey = new Map();
    conversations.forEach((conversation, index) => {
      const contextWindowId = String(conversation.contextWindowId || "").trim();
      const threadId = String(conversation.threadId || "").trim();
      const turnId = String(conversation.turnId || "").trim();
      const key = contextWindowId
        ? `context:${contextWindowId}`
        : threadId
          ? `thread:${threadId}`
          : `turn:${turnId || `${conversation.startedAt || "unknown"}:${index}`}`;
      if (!groupsByKey.has(key)) groupsByKey.set(key, { key, insertionIndex: index, turns: [] });
      groupsByKey.get(key).turns.push({ ...conversation, insertionIndex: index });
    });

    return [...groupsByKey.values()].map((group) => {
      group.turns.sort((left, right) => {
        const leftTime = Date.parse(left.startedAt);
        const rightTime = Date.parse(right.startedAt);
        if (Number.isFinite(leftTime) && Number.isFinite(rightTime) && leftTime !== rightTime) return leftTime - rightTime;
        if (Number.isFinite(leftTime) !== Number.isFinite(rightTime)) return Number.isFinite(leftTime) ? -1 : 1;
        return left.insertionIndex - right.insertionIndex;
      });
      const validTimes = group.turns
        .map((conversation) => Date.parse(conversation.startedAt))
        .filter(Number.isFinite);
      return {
        ...group,
        title: group.turns.find((conversation) => String(conversation.threadName || "").trim())?.threadName
          || group.turns[0]?.preview
          || "未命名对话",
        latestAt: validTimes.length ? Math.max(...validTimes) : Number.NEGATIVE_INFINITY,
      };
    }).sort((left, right) => right.latestAt - left.latestAt || left.insertionIndex - right.insertionIndex);
  }

  formatConversationTime(value) {
    const startedAt = new Date(value);
    return Number.isNaN(startedAt.getTime())
      ? "—"
      : new Intl.DateTimeFormat("zh-CN", { hour: "2-digit", minute: "2-digit", hour12: false }).format(startedAt);
  }

  toggleConversationGroup(groupKey) {
    const restoreFocus = this.shadowRoot.activeElement?.dataset.groupKey === groupKey;
    if (this.expandedConversationGroups.has(groupKey)) {
      this.expandedConversationGroups.delete(groupKey);
    } else {
      this.expandedConversationGroups.add(groupKey);
    }
    this.renderConversations();
    if (restoreFocus) {
      requestAnimationFrame(() => {
        const button = [...this.shadowRoot.querySelectorAll("[data-group-key]")]
          .find((item) => item.dataset.groupKey === groupKey);
        button?.focus();
      });
    }
  }

  renderResetState() {
    const available = this.data.resetCredits > 0;
    const resetButton = this.shadowRoot.querySelector("[data-action='reset-credit']");
    if (resetButton) {
      resetButton.disabled = !available || this.resetting;
      if (available) {
        resetButton.removeAttribute("title");
      } else {
        resetButton.title = "没有可用的重置次数";
      }
    }
    const resetLabel = this.shadowRoot.querySelector(".reset-label");
    if (resetLabel) resetLabel.textContent = "重置次数";
    const confirmButton = this.shadowRoot.querySelector("[data-action='confirm-reset']");
    const cancelButton = this.shadowRoot.querySelector("[data-action='cancel-reset']");
    if (confirmButton) {
      confirmButton.disabled = this.resetting;
      confirmButton.textContent = this.resetting ? "正在重置…" : "确认使用";
    }
    if (cancelButton) cancelButton.disabled = this.resetting;
  }

  bindHoverExpansion() {
    let pendingDrag = null;

    const beginDrag = () => {
      this.dragging = true;
      clearTimeout(this.hoverCloseTimer);
      clearTimeout(this.collapseResizeTimer);
      this.collapsed = true;
      this.setPanelAnchor({ compactX: 0, compactY: 0, pointerX: 33, pointerY: 33 });
      this.shadowRoot.querySelector(".widget")?.classList.add("collapsed", "dragging");
    };

    const enter = (pointer) => {
      this.pointerInside = true;
      if (this.dragging) return;
      if (this.suppressHoverUntilLeave) return;
      clearTimeout(this.hoverCloseTimer);
      clearTimeout(this.collapseResizeTimer);
      if (!this.collapsed) return;
      if (Number.isFinite(pointer?.x ?? pointer?.clientX) && Number.isFinite(pointer?.y ?? pointer?.clientY)) {
        this.expansionPointer = {
          x: clamp(pointer.x ?? pointer.clientX, 0, 66),
          y: clamp(pointer.y ?? pointer.clientY, 0, 66),
        };
      }
      this.collapsed = false;
      this.syncNativeSize(false);
      requestAnimationFrame(() => requestAnimationFrame(() => {
        this.shadowRoot.querySelector(".widget")?.classList.remove("collapsed");
        this.animateQuotaFill();
      }));
    };

    const leave = () => {
      this.pointerInside = false;
      if (this.suppressHoverUntilLeave) {
        this.suppressHoverUntilLeave = false;
        clearTimeout(this.suppressHoverTimer);
        return;
      }
      if (this.dragging) return;
      if (this.activeDialog || this.resetting) return;
      this.scheduleHoverCollapse();
    };

    window.codexUsageHoverEnter = enter;
    window.codexUsageHoverLeave = leave;
    window.codexUsageDragStarted = beginDrag;
    window.codexUsageDragEnded = () => {
      this.dragging = false;
      this.suppressHoverUntilLeave = true;
      clearTimeout(this.suppressHoverTimer);
      this.suppressHoverTimer = setTimeout(() => {
        this.suppressHoverUntilLeave = false;
      }, 300);
      clearTimeout(this.hoverCloseTimer);
      clearTimeout(this.collapseResizeTimer);
      this.setPanelAnchor({
        compactX: 0,
        compactY: 0,
        pointerX: this.expansionPointer.x,
        pointerY: this.expansionPointer.y,
      });
      this.collapsed = true;
      const widget = this.shadowRoot.querySelector(".widget");
      widget?.classList.add("collapsed");
      widget?.classList.remove("dragging");
      this.syncNativeSize(false);
    };
    this.addEventListener("mouseenter", enter);
    this.addEventListener("mouseleave", leave);
    this.addEventListener("pointerdown", (event) => {
      if (event.button !== 0 || !event.isPrimary) return;
      if (event.clientX < 0 || event.clientX >= 66 || event.clientY < 0 || event.clientY >= 66) return;
      event.preventDefault();
      pendingDrag = {
        pointerId: event.pointerId,
        screenX: event.screenX,
        screenY: event.screenY,
        x: event.clientX,
        y: event.clientY,
      };
    });
    window.addEventListener("pointermove", (event) => {
      if (!pendingDrag || pendingDrag.pointerId !== event.pointerId || (event.buttons & 1) === 0) return;
      if (Math.hypot(event.screenX - pendingDrag.screenX, event.screenY - pendingDrag.screenY) < 3) return;
      const drag = pendingDrag;
      pendingDrag = null;
      beginDrag();
      hostBridge.beginDrag({ x: drag.x, y: drag.y });
    });
    const cancelPendingDrag = (event) => {
      if (pendingDrag?.pointerId === event.pointerId) pendingDrag = null;
    };
    window.addEventListener("pointerup", cancelPendingDrag);
    window.addEventListener("pointercancel", cancelPendingDrag);
  }

  scheduleHoverCollapse() {
    clearTimeout(this.hoverCloseTimer);
    this.hoverCloseTimer = setTimeout(() => {
      if (this.collapsed || this.dragging || this.activeDialog || this.pointerInside) return;
      this.collapsed = true;
      this.shadowRoot.querySelector(".widget")?.classList.add("collapsed");
      this.collapseResizeTimer = setTimeout(() => {
        if (this.collapsed) this.syncNativeSize(false);
      }, 280);
    }, 120);
  }

  animateQuotaFill() {
    cancelAnimationFrame(this.quotaAnimationFrame);
    const ring = this.shadowRoot.querySelector(".primary-ring");
    const fill = this.shadowRoot.querySelector(".secondary-fill");
    if (!ring || !fill) return;

    const primaryTarget = this.data.primary.remainingPercent ?? 0;
    const secondaryTarget = this.data.secondary.remainingPercent ?? 0;
    if (matchMedia("(prefers-reduced-motion: reduce)").matches) {
      ring.style.setProperty("--value", primaryTarget);
      fill.style.width = `${secondaryTarget}%`;
      return;
    }

    const startedAt = performance.now();
    const easeOut = (value) => 1 - Math.pow(1 - value, 3);
    const draw = (now) => {
      const primaryProgress = Math.min(1, (now - startedAt) / 720);
      const secondaryProgress = Math.min(1, Math.max(0, now - startedAt - 90) / 650);
      ring.style.setProperty("--value", primaryTarget * easeOut(primaryProgress));
      fill.style.width = `${secondaryTarget * easeOut(secondaryProgress)}%`;
      if (primaryProgress < 1 || secondaryProgress < 1) {
        this.quotaAnimationFrame = requestAnimationFrame(draw);
      }
    };

    ring.style.setProperty("--value", 0);
    fill.style.width = "0%";
    this.quotaAnimationFrame = requestAnimationFrame(draw);
  }

  syncNativeSize(animated) {
    const widget = this.shadowRoot.querySelector(".widget");
    if (widget && (this.autoHover || (!this.collapsed && window.innerWidth >= 350))) {
      this.expandedHeight = Math.ceil(widget.getBoundingClientRect().height);
    }
    hostBridge.resize({
      width: this.collapsed ? 66 : 360,
      height: this.collapsed ? 66 : this.expandedHeight,
      animated,
      anchorX: this.expansionPointer.x,
      anchorY: this.expansionPointer.y,
    });
  }

  setPanelAnchor({ compactX = 0, compactY = 0, pointerX = 33, pointerY = 33 } = {}) {
    const widget = this.shadowRoot.querySelector(".widget");
    if (!widget) return;
    widget.style.setProperty("--compact-x", `${Math.max(0, compactX)}px`);
    widget.style.setProperty("--compact-y", `${Math.max(0, compactY)}px`);
    widget.style.setProperty("--expand-x", `${Math.max(0, pointerX)}px`);
    widget.style.setProperty("--expand-y", `${Math.max(0, pointerY)}px`);
  }

  applyQuotaTone(element, value) {
    if (!element) return;
    const tone = quotaTone(value);
    element.classList.toggle("quota-warning", tone === "warning");
    element.classList.toggle("quota-critical", tone === "critical");
  }

  setLoading(loading) {
    this.shadowRoot.querySelector(".widget")?.classList.toggle("loading", loading);
    if (loading) this.syncState = "loading";
    this.applySyncState();
  }

  applySyncState() {
    const widget = this.shadowRoot.querySelector(".widget");
    if (!widget) return;
    widget.classList.toggle("sync-loading", this.syncState === "loading");
    widget.classList.toggle("sync-error", this.syncState === "error");
    widget.classList.toggle("sync-success", this.syncState === "success");
  }

  renderValues() {
    if (!this.shadowRoot.querySelector(".widget")) return;
    const p = this.data.primary.remainingPercent;
    const s = this.data.secondary.remainingPercent;
    const primaryRing = this.shadowRoot.querySelector(".primary-ring");
    const secondaryFill = this.shadowRoot.querySelector(".secondary-fill");
    const brandIcon = this.shadowRoot.querySelector(".brand-icon");
    primaryRing?.style.setProperty("--value", p ?? 0);
    brandIcon?.style.setProperty("--liquid-level", clamp(p ?? 0, 0, 100));
    this.applyQuotaTone(primaryRing, p);
    this.applyQuotaTone(brandIcon, p);
    this.applyQuotaTone(secondaryFill, s);
    this.shadowRoot.querySelector(".primary-value").textContent = this.formatPercent(p);
    this.shadowRoot.querySelector(".primary-reset").textContent = this.formatReset(this.data.primary.resetsAt);
    this.shadowRoot.querySelector(".health").textContent = p == null ? "正在同步" : p < 10 ? "额度告警" : p < 20 ? "额度偏低" : "状态良好";
    this.shadowRoot.querySelector(".secondary-value").textContent = this.formatPercent(s);
    secondaryFill.style.width = `${s ?? 0}%`;
    this.shadowRoot.querySelector(".secondary-reset").textContent = this.formatReset(this.data.secondary.resetsAt);
    this.shadowRoot.querySelector(".reset-count").textContent = this.formatNumber(this.data.resetCredits);
    this.shadowRoot.querySelector(".token-count").textContent = this.formatNumber(this.data.todayTokens);
    this.shadowRoot.querySelector(".question-count").textContent = this.formatNumber(this.data.todayQuestions);
    this.shadowRoot.querySelector(".plan").textContent = this.data.plan;
    const updated = this.shadowRoot.querySelector(".updated");
    updated.textContent = `${this.data.syncMessage} · ${new Intl.DateTimeFormat("zh-CN", { hour: "2-digit", minute: "2-digit", hour12: false }).format(this.data.updatedAt)}`;
    updated.title = this.data.syncMessage;
    this.renderResetState();
    if (this.activeDialog === "tokens") this.renderTokenHistory();
    if (this.activeDialog === "conversations") this.renderConversations();
    this.applySyncState();
    requestAnimationFrame(() => this.syncNativeSize(false));
  }

  render() {
    this.shadowRoot.innerHTML = `
      <style>${this.styles}</style>
      <section class="widget ${this.collapsed ? "collapsed" : ""}" aria-label="Codex Meter">
        <header>
          <div class="brand">
            <span class="brand-icon" style="--liquid-level:${this.data.primary.remainingPercent ?? 0}">
              <span class="brand-liquid" aria-hidden="true">
                <svg class="liquid-wave liquid-wave-back" viewBox="0 0 60 10" preserveAspectRatio="none"><path d="M0 5 Q7.5 0 15 5 T30 5 T45 5 T60 5 V10 H0 Z"/></svg>
                <svg class="liquid-wave liquid-wave-front" viewBox="0 0 60 10" preserveAspectRatio="none"><path d="M0 5 Q7.5 9 15 5 T30 5 T45 5 T60 5 V10 H0 Z"/></svg>
              </span>
              <span class="brand-glyph">${ICONS.spark}</span>
            </span>
            <div><strong>Codex Meter</strong><span class="plan">${this.data.plan}</span></div>
          </div>
          <div class="actions">
            <button data-action="theme" aria-label="切换主题" title="切换主题">${ICONS.moon}</button>
            <button data-action="refresh" aria-label="刷新数据" title="刷新数据">${ICONS.refresh}</button>
            <button data-action="collapse" aria-label="折叠面板" aria-expanded="${!this.collapsed}" title="折叠">${ICONS.chevron}</button>
            <button class="native-close" data-action="close" aria-label="关闭应用" title="关闭">${ICONS.close}</button>
          </div>
        </header>

        <div class="body">
          <div class="quota-hero">
            <div class="ring primary-ring" style="--value:${this.data.primary.remainingPercent ?? 0}">
              <div class="ring-inner"><strong class="primary-value">${this.formatPercent(this.data.primary.remainingPercent)}</strong><span>剩余</span></div>
            </div>
            <div class="hero-copy">
              <span class="section-label">${this.data.primary.label}</span>
              <strong class="health">正在同步</strong>
              <span class="muted primary-reset">${this.formatReset(this.data.primary.resetsAt)}</span>
            </div>
          </div>

          <div class="weekly">
            <div class="row"><span>${this.data.secondary.label}</span><strong class="secondary-value">${this.formatPercent(this.data.secondary.remainingPercent)}</strong></div>
            <div class="progress"><i class="secondary-fill" style="width:${this.data.secondary.remainingPercent ?? 0}%"></i></div>
            <span class="muted secondary-reset">${this.formatReset(this.data.secondary.resetsAt)}</span>
          </div>

          <div class="stats">
            <button class="stat reset-stat" data-action="reset-credit" type="button" aria-describedby="reset-stat-tooltip" disabled><span class="stat-icon violet">${ICONS.reset}</span><span class="stat-value reset-count">${this.formatNumber(this.data.resetCredits)}</span><span class="stat-label reset-label">重置次数</span><span class="stat-tooltip" id="reset-stat-tooltip" role="tooltip">使用一次重置额度</span></button>
            <button class="stat detail-stat" data-action="tokens-detail" type="button" aria-haspopup="dialog" aria-describedby="tokens-stat-tooltip"><span class="stat-icon cyan">${ICONS.token}</span><span class="stat-value token-count">${this.formatNumber(this.data.todayTokens)}</span><span class="stat-label">今日 Tokens</span><span class="stat-tooltip" id="tokens-stat-tooltip" role="tooltip">最近7天Tokens</span></button>
            <button class="stat detail-stat" data-action="conversations-detail" type="button" aria-haspopup="dialog" aria-describedby="conversations-stat-tooltip"><span class="stat-icon coral">${ICONS.message}</span><span class="stat-value question-count">${this.data.todayQuestions}</span><span class="stat-label">今日对话</span><span class="stat-tooltip" id="conversations-stat-tooltip" role="tooltip">详情</span></button>
          </div>

          <footer><span class="status-dot"></span><span class="updated">刚刚更新</span><span class="theme-label">跟随系统</span></footer>
        </div>
        <div class="app-dialog" data-dialog="reset" role="dialog" aria-modal="true" aria-labelledby="reset-dialog-title" hidden>
          <div class="dialog-card reset-dialog-card">
            <span class="reset-dialog-icon">${ICONS.reset}</span>
            <strong id="reset-dialog-title">使用一次额度重置？</strong>
            <p>确认后会立即消耗一次珍贵的重置机会，并刷新当前可重置的额度周期。此操作不能撤销。</p>
            <div class="reset-dialog-actions">
              <button data-action="cancel-reset" type="button">取消</button>
              <button class="reset-confirm" data-action="confirm-reset" type="button">确认使用</button>
            </div>
          </div>
        </div>
        <div class="app-dialog" data-dialog="tokens" role="dialog" aria-modal="true" aria-labelledby="token-dialog-title" hidden>
          <div class="dialog-card token-dialog-card">
            <div class="dialog-heading">
              <div><strong id="token-dialog-title">最近 7 天 Tokens</strong></div>
              <button class="dialog-dismiss" data-action="close-dialog" type="button" aria-label="关闭用量图表">${ICONS.close}</button>
            </div>
            <div class="token-chart"></div>
          </div>
        </div>
        <div class="app-dialog" data-dialog="conversations" role="dialog" aria-modal="true" aria-labelledby="conversation-dialog-title" hidden>
          <div class="dialog-card conversation-dialog-card">
            <div class="dialog-heading">
              <div><strong id="conversation-dialog-title">今日对话</strong><span class="dialog-subtitle conversation-summary">本机记录</span></div>
              <button class="dialog-dismiss" data-action="close-dialog" type="button" aria-label="关闭对话列表">${ICONS.close}</button>
            </div>
            <div class="conversation-list"></div>
          </div>
        </div>
      </section>
    `;
    this.applyTheme();
  }

  get styles() {
    return `
      :host { --bg:rgba(250,252,255,.86); --panel:rgba(255,255,255,.68); --text:#172033; --muted:#7b8495; --line:rgba(43,55,78,.09); --shadow:0 24px 70px rgba(25,36,62,.18),0 3px 12px rgba(25,36,62,.08); position:fixed; top:24px; right:24px; z-index:2147483647; color:var(--text); font-family:-apple-system,BlinkMacSystemFont,"SF Pro Text",Inter,sans-serif; font-synthesis:none; user-select:none; -webkit-user-select:none; }
      :host([data-theme="dark"]) { --bg:rgba(24,27,34,.88); --panel:rgba(255,255,255,.055); --text:#f4f6fb; --muted:#969eae; --line:rgba(255,255,255,.085); --shadow:0 28px 80px rgba(0,0,0,.42),0 2px 8px rgba(0,0,0,.25); }
      @media (prefers-color-scheme:dark) { :host([data-theme="auto"]) { --bg:rgba(24,27,34,.88); --panel:rgba(255,255,255,.055); --text:#f4f6fb; --muted:#969eae; --line:rgba(255,255,255,.085); --shadow:0 28px 80px rgba(0,0,0,.42),0 2px 8px rgba(0,0,0,.25); } }
      * { box-sizing:border-box; }
      .widget { position:relative; width:min(360px,calc(100vw - 32px)); border:1px solid var(--line); border-radius:24px; overflow:hidden; background:var(--bg); box-shadow:var(--shadow); backdrop-filter:blur(28px) saturate(1.35); -webkit-backdrop-filter:blur(28px) saturate(1.35); transition:width .3s cubic-bezier(.2,.8,.2,1),background .2s; }
      :host([native]) { width:100vw; }
      :host([native]) .widget { --compact-x:0px; --compact-y:0px; --expand-x:33px; --expand-y:33px; width:360px; border:0; box-shadow:none; clip-path:inset(0 0 0 0 round 24px); will-change:clip-path; transition:clip-path .28s cubic-bezier(.2,.8,.2,1),background .2s; }
      :host([native]) .widget.dragging,
      :host([native]) .widget.dragging header,
      :host([native]) .widget.dragging .body { transition:none!important; }
      header { height:66px; display:flex; align-items:center; justify-content:space-between; padding:0 16px 0 18px; border-bottom:1px solid var(--line); }
      :host([native]) header { position:relative; z-index:2; transform:translate(0,0); transform-origin:var(--expand-x) var(--expand-y); transition:transform .28s cubic-bezier(.2,.8,.2,1),border-color .2s; }
      .brand { display:flex; align-items:center; gap:10px; min-width:0; }
      .primary-ring,.secondary-fill,.brand-icon { --quota-start:#6960e8; --quota-end:#8a87f4; --quota-glow:rgba(111,101,231,.25); }
      .quota-warning { --quota-start:#d99016; --quota-end:#f0ba38; --quota-glow:rgba(224,157,28,.3); }
      .quota-critical { --quota-start:#df4655; --quota-end:#f06b61; --quota-glow:rgba(229,72,82,.32); }
      .brand-icon { --liquid-empty:rgba(92,82,210,.48); position:relative; width:30px; height:30px; display:grid; place-items:center; flex:0 0 auto; overflow:hidden; border-radius:9px; color:white; background:var(--liquid-empty); box-shadow:inset 0 1px 0 rgba(255,255,255,.35),0 5px 14px var(--quota-glow); transition:background .35s ease,box-shadow .35s ease; }
      .brand-icon.quota-warning { --liquid-empty:rgba(190,125,10,.5); }
      .brand-icon.quota-critical { --liquid-empty:rgba(190,50,65,.5); }
      .brand-liquid { position:absolute; right:0; bottom:0; left:0; height:calc(var(--liquid-level)*1%); opacity:clamp(0,var(--liquid-level),1); color:var(--quota-end); background:linear-gradient(145deg,var(--quota-start),var(--quota-end)); background-size:180% 180%; animation:liquid-shimmer 3.2s ease-in-out infinite alternate; transition:height .75s cubic-bezier(.2,.8,.2,1),opacity .25s ease,background .35s ease,color .35s ease; }
      .brand-liquid::after { content:""; position:absolute; inset:0; background:radial-gradient(circle at 24% 76%,rgba(255,255,255,.7) 0 1px,transparent 1.4px),radial-gradient(circle at 68% 88%,rgba(255,255,255,.5) 0 1.2px,transparent 1.7px),radial-gradient(circle at 82% 48%,rgba(255,255,255,.38) 0 .8px,transparent 1.3px); animation:liquid-bubbles 2.6s linear infinite; }
      .liquid-wave { position:absolute; top:-6px; left:0; width:60px!important; height:10px; overflow:visible; fill:currentColor!important; animation:liquid-flow 1.55s linear infinite; }
      .liquid-wave-back { top:-4px; opacity:.42; animation-direction:reverse; animation-duration:2.35s; }
      .liquid-wave-front { opacity:.9; }
      .brand-glyph { position:relative; z-index:1; display:grid; place-items:center; filter:drop-shadow(0 1px 2px rgba(43,34,110,.25)); }
      .brand-glyph svg { width:17px; fill:currentColor; }
      .brand div { display:flex; align-items:baseline; gap:8px; white-space:nowrap; }
      .brand strong { font-size:14px; letter-spacing:-.015em; }
      .plan { color:#7268e8; font-size:10px; font-weight:700; letter-spacing:.02em; padding:3px 6px; border-radius:6px; background:rgba(111,99,230,.11); }
      .actions { display:flex; gap:3px; }
      button { width:30px; height:30px; display:grid; place-items:center; padding:0; border:0; border-radius:9px; color:var(--muted); background:transparent; cursor:pointer; transition:.16s ease; }
      button:hover { color:var(--text); background:var(--panel); }
      button svg { width:16px; height:16px; fill:none; stroke:currentColor; stroke-width:1.8; stroke-linecap:round; stroke-linejoin:round; pointer-events:none; transition:transform .18s ease; }
      [data-action="theme"]:hover svg { animation:theme-orbit .52s cubic-bezier(.22,.9,.3,1); }
      [data-action="refresh"]:hover svg { animation:refresh-turn .72s linear infinite; }
      [data-action="close"]:hover { color:#e95564; background:rgba(233,85,100,.11); }
      [data-action="close"]:hover svg { animation:close-pop .34s cubic-bezier(.2,1.25,.35,1) both; }
      :host(:not([native])) .native-close { display:none; }
      .body { padding:18px; max-height:520px; opacity:1; transition:max-height .3s ease,opacity .2s,padding .3s; }
      .collapsed .body { max-height:0; opacity:0; padding-top:0; padding-bottom:0; pointer-events:none; }
      :host([native]) .body { transform:scale(1); transform-origin:var(--expand-x) var(--expand-y); transition:opacity .16s ease .08s,transform .28s cubic-bezier(.2,.8,.2,1); }
      :host([native]) .collapsed .body { max-height:none; padding:18px; opacity:0; transform:scale(.965); }
      :host([native]) .widget.collapsed { clip-path:inset(var(--compact-y) calc(100% - var(--compact-x) - 66px) calc(100% - var(--compact-y) - 66px) var(--compact-x) round 18px); }
      :host([native]) .collapsed header { width:360px; padding:0 16px 0 18px; transform:translate(var(--compact-x),var(--compact-y)); }
      :host([native]) .collapsed .brand { gap:0; }
      :host([native]) .brand > div,
      :host([native]) .actions { opacity:1; transition:opacity .15s ease .1s; }
      :host([native]) .collapsed .brand > div,
      :host([native]) .collapsed .actions { opacity:0; pointer-events:none; transition-delay:0s; }
      :host([native]) [data-action="collapse"] { display:none; }
      .collapsed header { border-bottom-color:transparent; }
      .collapsed [data-action="collapse"] { transform:rotate(-90deg); }
      .quota-hero { display:flex; align-items:center; gap:17px; padding:15px; border:1px solid var(--line); border-radius:18px; background:var(--panel); }
      .ring { --value:45; width:88px; height:88px; flex:0 0 auto; display:grid; place-items:center; border-radius:50%; background:conic-gradient(var(--quota-start) calc(var(--value)*1%),rgba(120,126,147,.13) 0); position:relative; box-shadow:inset 0 0 0 1px rgba(255,255,255,.22),0 0 16px var(--quota-glow); transition:background .35s ease,box-shadow .35s ease; }
      .ring::before { content:""; position:absolute; inset:7px; border-radius:inherit; background:var(--bg); box-shadow:inset 0 0 0 1px var(--line); }
      .ring-inner { position:relative; z-index:1; display:flex; flex-direction:column; align-items:center; }
      .ring-inner strong { font-size:22px; letter-spacing:-.055em; }
      .ring-inner span { margin-top:1px; color:var(--muted); font-size:10px; }
      .hero-copy { min-width:0; display:flex; flex-direction:column; }
      .section-label { margin-bottom:5px; color:var(--muted); font-size:11px; font-weight:600; }
      .hero-copy>strong { margin-bottom:7px; font-size:18px; letter-spacing:-.04em; }
      .muted { color:var(--muted); font-size:10px; }
      .weekly { margin:12px 0; padding:14px 15px; border:1px solid var(--line); border-radius:16px; background:var(--panel); }
      .row { display:flex; align-items:center; justify-content:space-between; margin-bottom:9px; font-size:11px; }
      .row span { color:var(--muted); font-weight:600; }
      .row strong { font-size:13px; }
      .progress { height:6px; overflow:hidden; margin-bottom:8px; border-radius:9px; background:rgba(120,126,147,.13); }
      .progress i { display:block; height:100%; border-radius:inherit; background:linear-gradient(90deg,var(--quota-start),var(--quota-end)); box-shadow:0 0 10px var(--quota-glow); transition:width .3s ease,background .35s ease,box-shadow .35s ease; }
      .stats { display:grid; grid-template-columns:repeat(3,1fr); gap:8px; }
      .stat { position:relative; width:auto; height:auto; min-width:0; display:block; overflow:visible; padding:12px 9px 11px; border:1px solid var(--line); border-radius:15px; color:var(--text); background:var(--panel); text-align:left; }
      .detail-stat:hover,.detail-stat:focus-visible { border-color:rgba(21,154,188,.28); background:rgba(21,154,188,.075); transform:translateY(-1px); }
      .reset-stat:not(:disabled):hover { border-color:rgba(116,105,234,.28); background:rgba(116,105,234,.09); transform:translateY(-1px); }
      .reset-stat:disabled { cursor:default; opacity:.72; }
      .stat-tooltip { position:absolute; left:50%; bottom:calc(100% + 8px); z-index:5; visibility:hidden; padding:6px 9px; border:1px solid var(--line); border-radius:8px; color:var(--text); background:var(--bg); box-shadow:0 8px 24px rgba(25,30,48,.18); font-size:10px; font-weight:600; line-height:1; white-space:nowrap; pointer-events:none; opacity:0; transform:translate(-50%,0); }
      .detail-stat:hover .stat-tooltip,
      .detail-stat:focus-visible .stat-tooltip,
      .reset-stat:not(:disabled):hover .stat-tooltip,
      .reset-stat:not(:disabled):focus-visible .stat-tooltip { visibility:visible; opacity:1; }
      .stat-icon { width:25px; height:25px; display:grid; place-items:center; margin-bottom:10px; border-radius:8px; }
      .stat-icon svg { width:14px; height:14px; fill:none; stroke:currentColor; stroke-width:1.8; stroke-linecap:round; stroke-linejoin:round; }
      .violet { color:#7469ea; background:rgba(116,105,234,.12); }.cyan { color:#159abc; background:rgba(21,154,188,.11); }.coral { color:#e76876; background:rgba(231,104,118,.11); }
      .stat-value { display:block; overflow:hidden; font-size:18px; font-weight:700; letter-spacing:-.04em; text-overflow:ellipsis; }
      .stat-label { display:block; margin-top:3px; color:var(--muted); font-size:9px; white-space:nowrap; }
      footer { display:flex; align-items:center; margin-top:14px; padding:0 3px; color:var(--muted); font-size:9px; }
      .updated { min-width:0; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
      .status-dot { width:6px; height:6px; flex:0 0 auto; margin-right:6px; border-radius:50%; background:#45be83; box-shadow:0 0 0 3px rgba(69,190,131,.1); }
      .sync-loading .status-dot { background:#e6a23c; box-shadow:0 0 0 3px rgba(230,162,60,.12); }
      .sync-error .status-dot { background:#ee5c67; box-shadow:0 0 0 3px rgba(238,92,103,.12); }
      .theme-label { flex:0 0 auto; margin-left:auto; }
      .app-dialog[hidden] { display:none; }
      .app-dialog { position:absolute; inset:0; z-index:20; display:grid; min-width:0; min-height:0; place-items:center; overflow:hidden; padding:14px; background:rgba(31,35,48,.28); backdrop-filter:blur(8px); -webkit-backdrop-filter:blur(8px); }
      .dialog-card { width:100%; max-width:100%; max-height:100%; min-width:0; min-height:0; display:flex; flex-direction:column; overflow:hidden; padding:18px; border:1px solid var(--line); border-radius:19px; color:var(--text); background:var(--bg); box-shadow:0 18px 54px rgba(25,30,48,.24); }
      .reset-dialog-card { padding:22px; }
      .reset-dialog-icon { width:34px; height:34px; display:grid; place-items:center; margin-bottom:14px; border-radius:10px; color:#7469ea; background:rgba(116,105,234,.13); }
      .reset-dialog-icon svg { width:18px; height:18px; fill:none; stroke:currentColor; stroke-width:1.8; stroke-linecap:round; stroke-linejoin:round; }
      .reset-dialog-card strong { display:block; font-size:16px; letter-spacing:-.02em; }
      .reset-dialog-card p { margin:9px 0 18px; color:var(--muted); font-size:11px; line-height:1.65; }
      .reset-dialog-actions { display:flex; justify-content:flex-end; gap:8px; }
      .reset-dialog-actions button { width:auto; min-width:72px; height:34px; padding:0 13px; border:1px solid var(--line); font-size:11px; font-weight:650; }
      .reset-dialog-actions .reset-confirm { color:white; border-color:transparent; background:linear-gradient(135deg,#6559df,#8278ef); }
      .reset-dialog-actions .reset-confirm:hover { color:white; background:linear-gradient(135deg,#594dcc,#7469e5); }
      .reset-dialog-actions button:disabled { cursor:wait; opacity:.65; }
      .dialog-heading { display:flex; align-items:flex-start; justify-content:space-between; gap:12px; flex:0 0 auto; }
      .dialog-heading>div { min-width:0; display:flex; flex-direction:column; }
      .dialog-heading strong { font-size:16px; letter-spacing:-.025em; }
      .dialog-subtitle { margin-top:4px; color:var(--muted); font-size:10px; }
      .dialog-dismiss { flex:0 0 auto; margin:-5px -5px 0 0; }
      .token-chart { height:260px; min-height:0; display:flex; align-items:stretch; justify-content:space-between; gap:5px; margin-top:19px; padding-top:18px; }
      .chart-column { min-width:0; flex:1 1 0; display:grid; grid-template-rows:18px minmax(0,1fr) 18px; align-items:end; text-align:center; }
      .chart-value { overflow:hidden; color:var(--muted); font-size:8px; font-weight:650; text-overflow:ellipsis; white-space:nowrap; }
      .chart-track { position:relative; height:100%; min-height:80px; display:flex; align-items:flex-end; justify-content:center; overflow:hidden; border-radius:7px 7px 4px 4px; background:rgba(120,126,147,.08); }
      .chart-track i { width:70%; min-height:0; display:block; border-radius:6px 6px 3px 3px; background:linear-gradient(180deg,#31b6d1,#159abc); box-shadow:0 4px 12px rgba(21,154,188,.2); transition:height .35s cubic-bezier(.2,.8,.2,1); }
      .chart-column.is-today .chart-track { background:rgba(116,105,234,.1); }
      .chart-column.is-today .chart-track i { background:linear-gradient(180deg,#8a87f4,#6960e8); box-shadow:0 4px 12px rgba(105,96,232,.25); }
      .chart-label { align-self:end; overflow:hidden; color:var(--muted); font-size:8px; white-space:nowrap; }
      .chart-column.is-today .chart-label { color:#7469ea; font-weight:750; }
      .conversation-dialog-card { padding-bottom:12px; }
      .conversation-list { min-height:0; display:flex; flex-direction:column; gap:8px; overflow-x:hidden; overflow-y:auto; margin:14px -7px 0; padding:0 7px 6px; overscroll-behavior:contain; scrollbar-width:thin; }
      .conversation-group { flex:0 0 auto; overflow:hidden; border:1px solid var(--line); border-radius:13px; background:rgba(120,126,147,.035); }
      .conversation-group-toggle { width:100%; height:auto; min-height:54px; display:grid; grid-template-columns:minmax(0,1fr) 20px; align-items:center; justify-items:stretch; gap:8px; padding:10px 10px 9px 12px; border-radius:0; color:var(--text); text-align:left; }
      .conversation-group-toggle:hover,.conversation-group-toggle:focus-visible { background:var(--panel); }
      .conversation-group-copy { min-width:0; width:100%; display:grid; grid-template-rows:14px 12px; justify-items:stretch; gap:5px; }
      .conversation-group-title { width:100%; overflow:hidden; font-size:10px; font-weight:700; line-height:14px; text-align:left; text-overflow:ellipsis; white-space:nowrap; }
      .conversation-group-meta { width:100%; display:grid; grid-template-columns:72px 34px minmax(0,1fr); align-items:center; gap:7px; color:var(--muted); font-size:8px; font-variant-numeric:tabular-nums; text-align:left; }
      .conversation-group-tokens { color:#159abc; font-weight:700; text-align:right; white-space:nowrap; }
      .conversation-group-chevron { display:grid; place-items:center; color:var(--muted); transition:transform .18s ease; }
      .conversation-group-chevron svg { width:14px; height:14px; }
      .conversation-group.is-expanded .conversation-group-chevron { transform:rotate(180deg); }
      .conversation-group-turns[hidden] { display:none; }
      .conversation-group-turns { border-top:1px solid var(--line); }
      .conversation-row { display:grid; grid-template-columns:38px minmax(0,1fr) auto; align-items:start; gap:8px; padding:10px 8px; border-bottom:1px solid var(--line); }
      .conversation-row:last-child { border-bottom:0; }
      .conversation-time { padding-top:1px; color:var(--muted); font-size:9px; font-variant-numeric:tabular-nums; }
      .conversation-preview { min-width:0; display:-webkit-box; overflow:hidden; font-size:10px; font-weight:580; line-height:1.45; overflow-wrap:anywhere; -webkit-box-orient:vertical; -webkit-line-clamp:2; }
      .conversation-tokens { align-self:start; padding:3px 5px; border-radius:6px; color:#159abc; background:rgba(21,154,188,.1); font-size:8px; font-weight:700; white-space:nowrap; }
      .dialog-empty { width:100%; height:100%; display:grid; place-items:center; color:var(--muted); font-size:11px; text-align:center; }
      .loading [data-action="refresh"] svg { animation:spin .8s linear infinite; }
      @keyframes spin { to { transform:rotate(360deg); } }
      @keyframes liquid-flow { from { transform:translateX(0); } to { transform:translateX(-30px); } }
      @keyframes liquid-shimmer { from { background-position:0% 25%; } to { background-position:100% 75%; } }
      @keyframes liquid-bubbles { 0% { transform:translateY(8px); opacity:0; } 18% { opacity:.48; } 82% { opacity:.32; } 100% { transform:translateY(-18px); opacity:0; } }
      @keyframes theme-orbit { 0% { transform:rotate(0) scale(1); } 48% { transform:rotate(-22deg) scale(1.18); } 76% { transform:rotate(7deg) scale(1.06); } 100% { transform:rotate(0) scale(1); } }
      @keyframes refresh-turn { to { transform:rotate(360deg); } }
      @keyframes close-pop { 0% { transform:rotate(0) scale(1); } 60% { transform:rotate(96deg) scale(1.16); } 100% { transform:rotate(90deg) scale(1.08); } }
      @media (max-width:520px) { :host { top:16px; right:16px; } }
      @media (prefers-reduced-motion:reduce) { *,*::before,*::after { transition:none!important; animation:none!important; } }
    `;
  }
}

customElements.define("codex-usage-widget", CodexUsageWidget);

// Host integration helpers:
window.updateCodexUsage = (payload) => document.querySelector("codex-usage-widget")?.updateUsage(payload);
window.codexResetResult = (payload) => document.querySelector("codex-usage-widget")?.handleResetResult(payload);
window.recordCodexTurn = (usage) => document.querySelector("codex-usage-widget")?.recordTurn(usage);
window.codexUsageSetPanelAnchor = (payload) => document.querySelector("codex-usage-widget")?.setPanelAnchor(payload);
