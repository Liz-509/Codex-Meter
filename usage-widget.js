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
  plan: "同步中",
  updatedAt: Date.now(),
  syncMessage: "正在连接 Codex",
};

const clamp = (value, min, max) => Math.min(max, Math.max(min, value));

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
      plan: (limits.planType || payload.plan || "已连接").replace(/^./, (char) => char.toUpperCase()),
      updatedAt: Date.now(),
      syncMessage: payload.error || "实时数据",
    };
    this.renderValues();
  }

  recordTurn({ inputTokens = 0, outputTokens = 0 } = {}) {
    const today = new Date().toISOString().slice(0, 10);
    const stats = this.readTodayStats();
    const next = {
      date: today,
      tokens: stats.tokens + inputTokens + outputTokens,
      questions: stats.questions + 1,
    };
    localStorage.setItem("codex-widget-daily", JSON.stringify(next));
    this.data.todayTokens = next.tokens;
    this.data.todayQuestions = next.questions;
    this.renderValues();
  }

  readTodayStats() {
    const today = new Date().toISOString().slice(0, 10);
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
  }

  bindHoverExpansion() {
    const enter = () => {
      clearTimeout(this.hoverCloseTimer);
      clearTimeout(this.collapseResizeTimer);
      if (!this.collapsed) return;
      this.collapsed = false;
      this.syncNativeSize(false);
      requestAnimationFrame(() => requestAnimationFrame(() => {
        this.shadowRoot.querySelector(".widget")?.classList.remove("collapsed");
        this.animateQuotaFill();
      }));
    };

    const leave = () => {
      clearTimeout(this.hoverCloseTimer);
      this.hoverCloseTimer = setTimeout(() => {
        if (this.collapsed) return;
        this.collapsed = true;
        this.shadowRoot.querySelector(".widget")?.classList.add("collapsed");
        this.collapseResizeTimer = setTimeout(() => {
          if (this.collapsed) this.syncNativeSize(false);
        }, 280);
      }, 120);
    };

    window.codexUsageHoverEnter = enter;
    window.codexUsageHoverLeave = leave;
    this.addEventListener("mouseenter", enter);
    this.addEventListener("mouseleave", leave);
    this.addEventListener("pointerdown", (event) => {
      if (event.button !== 0 || !event.isPrimary) return;
      hostBridge.beginDrag({ x: event.clientX, y: event.clientY });
    });
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
    if (!this.collapsed && widget && window.innerWidth >= 350) {
      this.expandedHeight = Math.ceil(widget.getBoundingClientRect().height);
    }
    hostBridge.resize({
      width: this.collapsed ? 66 : 360,
      height: this.collapsed ? 66 : this.expandedHeight,
      animated,
    });
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
    this.shadowRoot.querySelector(".primary-ring")?.style.setProperty("--value", p ?? 0);
    this.shadowRoot.querySelector(".primary-value").textContent = this.formatPercent(p);
    this.shadowRoot.querySelector(".primary-reset").textContent = this.formatReset(this.data.primary.resetsAt);
    this.shadowRoot.querySelector(".health").textContent = p == null ? "正在同步" : p <= 10 ? "额度较低" : "状态良好";
    this.shadowRoot.querySelector(".secondary-value").textContent = this.formatPercent(s);
    this.shadowRoot.querySelector(".secondary-fill").style.width = `${s ?? 0}%`;
    this.shadowRoot.querySelector(".secondary-reset").textContent = this.formatReset(this.data.secondary.resetsAt);
    this.shadowRoot.querySelector(".reset-count").textContent = this.formatNumber(this.data.resetCredits);
    this.shadowRoot.querySelector(".token-count").textContent = this.formatNumber(this.data.todayTokens);
    this.shadowRoot.querySelector(".question-count").textContent = this.formatNumber(this.data.todayQuestions);
    this.shadowRoot.querySelector(".plan").textContent = this.data.plan;
    this.shadowRoot.querySelector(".updated").textContent = `${this.data.syncMessage} · ${new Intl.DateTimeFormat("zh-CN", { hour: "2-digit", minute: "2-digit", hour12: false }).format(this.data.updatedAt)}`;
    this.applySyncState();
  }

  render() {
    this.shadowRoot.innerHTML = `
      <style>${this.styles}</style>
      <section class="widget ${this.collapsed ? "collapsed" : ""}" aria-label="Codex Meter">
        <header>
          <div class="brand">
            <span class="brand-icon">${ICONS.spark}</span>
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
            <article><span class="stat-icon violet">${ICONS.reset}</span><span class="stat-value reset-count">${this.formatNumber(this.data.resetCredits)}</span><span class="stat-label">重置次数</span></article>
            <article><span class="stat-icon cyan">${ICONS.token}</span><span class="stat-value token-count">${this.formatNumber(this.data.todayTokens)}</span><span class="stat-label">今日 Tokens</span></article>
            <article><span class="stat-icon coral">${ICONS.message}</span><span class="stat-value question-count">${this.data.todayQuestions}</span><span class="stat-label">今日问题</span></article>
          </div>

          <footer><span class="status-dot"></span><span class="updated">刚刚更新</span><span class="theme-label">跟随系统</span></footer>
        </div>
      </section>
    `;
    this.applyTheme();
  }

  get styles() {
    return `
      :host { --bg:rgba(250,252,255,.86); --panel:rgba(255,255,255,.68); --text:#172033; --muted:#7b8495; --line:rgba(43,55,78,.09); --shadow:0 24px 70px rgba(25,36,62,.18),0 3px 12px rgba(25,36,62,.08); position:fixed; top:24px; right:24px; z-index:2147483647; color:var(--text); font-family:-apple-system,BlinkMacSystemFont,"SF Pro Text",Inter,sans-serif; font-synthesis:none; }
      :host([data-theme="dark"]) { --bg:rgba(24,27,34,.88); --panel:rgba(255,255,255,.055); --text:#f4f6fb; --muted:#969eae; --line:rgba(255,255,255,.085); --shadow:0 28px 80px rgba(0,0,0,.42),0 2px 8px rgba(0,0,0,.25); }
      @media (prefers-color-scheme:dark) { :host([data-theme="auto"]) { --bg:rgba(24,27,34,.88); --panel:rgba(255,255,255,.055); --text:#f4f6fb; --muted:#969eae; --line:rgba(255,255,255,.085); --shadow:0 28px 80px rgba(0,0,0,.42),0 2px 8px rgba(0,0,0,.25); } }
      * { box-sizing:border-box; }
      .widget { width:min(360px,calc(100vw - 32px)); border:1px solid var(--line); border-radius:24px; overflow:hidden; background:var(--bg); box-shadow:var(--shadow); backdrop-filter:blur(28px) saturate(1.35); -webkit-backdrop-filter:blur(28px) saturate(1.35); transition:width .3s cubic-bezier(.2,.8,.2,1),background .2s; }
      :host([native]) { width:100vw; }
      :host([native]) .widget { width:360px; height:443px; border:0; box-shadow:none; clip-path:inset(0 0 0 0 round 24px); will-change:clip-path; transition:clip-path .28s cubic-bezier(.2,.8,.2,1),background .2s; }
      header { height:66px; display:flex; align-items:center; justify-content:space-between; padding:0 16px 0 18px; border-bottom:1px solid var(--line); }
      .brand { display:flex; align-items:center; gap:10px; min-width:0; }
      .brand-icon { width:30px; height:30px; display:grid; place-items:center; flex:0 0 auto; border-radius:9px; color:white; background:linear-gradient(145deg,#6257e6,#7c77f5 55%,#5e9cf4); box-shadow:inset 0 1px 0 rgba(255,255,255,.35),0 5px 14px rgba(101,91,223,.26); }
      .brand-icon svg { width:17px; fill:currentColor; }
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
      :host([native]) .body { transition:opacity .16s ease .08s; }
      :host([native]) .collapsed .body { max-height:none; padding:18px; opacity:0; }
      :host([native]) .widget.collapsed { height:443px; clip-path:inset(0 294px 377px 0 round 18px); }
      :host([native]) .collapsed header { width:360px; padding:0 16px 0 18px; }
      :host([native]) .collapsed .brand { gap:0; }
      :host([native]) .brand > div,
      :host([native]) .actions { opacity:1; transition:opacity .15s ease .1s; }
      :host([native]) .collapsed .brand > div,
      :host([native]) .collapsed .actions { opacity:0; pointer-events:none; transition-delay:0s; }
      :host([native]) [data-action="collapse"] { display:none; }
      .collapsed header { border-bottom-color:transparent; }
      .collapsed [data-action="collapse"] { transform:rotate(-90deg); }
      .quota-hero { display:flex; align-items:center; gap:17px; padding:15px; border:1px solid var(--line); border-radius:18px; background:var(--panel); }
      .ring { --value:45; width:88px; height:88px; flex:0 0 auto; display:grid; place-items:center; border-radius:50%; background:conic-gradient(#6c63e8 calc(var(--value)*1%),rgba(120,126,147,.13) 0); position:relative; box-shadow:inset 0 0 0 1px rgba(255,255,255,.22); }
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
      .progress i { display:block; height:100%; border-radius:inherit; background:linear-gradient(90deg,#6960e8,#8a87f4); box-shadow:0 0 10px rgba(111,101,231,.25); }
      .stats { display:grid; grid-template-columns:repeat(3,1fr); gap:8px; }
      article { min-width:0; padding:12px 9px 11px; border:1px solid var(--line); border-radius:15px; background:var(--panel); }
      .stat-icon { width:25px; height:25px; display:grid; place-items:center; margin-bottom:10px; border-radius:8px; }
      .stat-icon svg { width:14px; height:14px; fill:none; stroke:currentColor; stroke-width:1.8; stroke-linecap:round; stroke-linejoin:round; }
      .violet { color:#7469ea; background:rgba(116,105,234,.12); }.cyan { color:#159abc; background:rgba(21,154,188,.11); }.coral { color:#e76876; background:rgba(231,104,118,.11); }
      .stat-value { display:block; overflow:hidden; font-size:18px; font-weight:700; letter-spacing:-.04em; text-overflow:ellipsis; }
      .stat-label { display:block; margin-top:3px; color:var(--muted); font-size:9px; white-space:nowrap; }
      footer { display:flex; align-items:center; margin-top:14px; padding:0 3px; color:var(--muted); font-size:9px; }
      .status-dot { width:6px; height:6px; margin-right:6px; border-radius:50%; background:#45be83; box-shadow:0 0 0 3px rgba(69,190,131,.1); }
      .sync-loading .status-dot { background:#e6a23c; box-shadow:0 0 0 3px rgba(230,162,60,.12); }
      .sync-error .status-dot { background:#ee5c67; box-shadow:0 0 0 3px rgba(238,92,103,.12); }
      .theme-label { margin-left:auto; }
      .loading [data-action="refresh"] svg { animation:spin .8s linear infinite; }
      @keyframes spin { to { transform:rotate(360deg); } }
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
window.recordCodexTurn = (usage) => document.querySelector("codex-usage-widget")?.recordTurn(usage);
