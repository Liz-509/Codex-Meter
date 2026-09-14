import AppKit
import ServiceManagement
import UniformTypeIdentifiers
import UserNotifications
import WebKit

final class FloatingPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
}

final class HoverWebView: WKWebView {
    var onHoverChanged: ((Bool, NSPoint?) -> Void)?
    var suppressHoverExit = false
    private var hoverTrackingArea: NSTrackingArea?

    override func updateTrackingAreas() {
        if let hoverTrackingArea {
            removeTrackingArea(hoverTrackingArea)
        }
        let trackingArea = NSTrackingArea(
            rect: .zero,
            options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect],
            owner: self,
            userInfo: nil
        )
        addTrackingArea(trackingArea)
        hoverTrackingArea = trackingArea
        super.updateTrackingAreas()
    }

    override func mouseEntered(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        let pointer = NSPoint(
            x: point.x,
            y: isFlipped ? point.y : bounds.height - point.y
        )
        onHoverChanged?(true, pointer)
        super.mouseEntered(with: event)
    }

    override func mouseExited(with event: NSEvent) {
        if suppressHoverExit { return }
        if let contentView = window?.contentView {
            let location = contentView.convert(event.locationInWindow, from: nil)
            if contentView.bounds.contains(location) { return }
        }
        onHoverChanged?(false, nil)
        super.mouseExited(with: event)
    }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool {
        true
    }
}

final class NativeDragHandle: NSView {
    var onHoverChanged: ((Bool, NSPoint?) -> Void)?
    var onDragEnded: ((NSRect) -> Void)?
    var onDragChanged: ((Bool) -> Void)?
    private var hoverTrackingArea: NSTrackingArea?
    private var isDragging = false
    private var isDragCandidate = false
    private var pointerOffset = NSPoint.zero

    override func updateTrackingAreas() {
        if let hoverTrackingArea { removeTrackingArea(hoverTrackingArea) }
        let trackingArea = NSTrackingArea(
            rect: .zero,
            options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect],
            owner: self,
            userInfo: nil
        )
        addTrackingArea(trackingArea)
        hoverTrackingArea = trackingArea
        super.updateTrackingAreas()
    }

    override func mouseEntered(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        onHoverChanged?(true, NSPoint(x: point.x, y: bounds.height - point.y))
        super.mouseEntered(with: event)
    }

    override func mouseExited(with event: NSEvent) {
        if isDragging { return }
        if let contentView = window?.contentView {
            let location = contentView.convert(event.locationInWindow, from: nil)
            if contentView.bounds.contains(location) { return }
        }
        onHoverChanged?(false, nil)
        super.mouseExited(with: event)
    }

    override func mouseDown(with event: NSEvent) {
        guard let window else { return }
        let cursor = NSEvent.mouseLocation
        pointerOffset = NSPoint(
            x: cursor.x - window.frame.minX,
            y: window.frame.maxY - cursor.y
        )
        isDragCandidate = true
    }

    override func mouseDragged(with event: NSEvent) {
        guard isDragCandidate, let window else { return }
        if !isDragging {
            isDragging = true
            onDragChanged?(true)
        }
        let cursor = NSEvent.mouseLocation
        window.setFrameOrigin(NSPoint(
            x: cursor.x - pointerOffset.x,
            y: cursor.y + pointerOffset.y - window.frame.height
        ))
    }

    override func mouseUp(with event: NSEvent) {
        defer { isDragCandidate = false }
        guard isDragging, let window else { return }
        isDragging = false
        onDragChanged?(false)
        onDragEnded?(window.frame)
    }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

final class PanelBridge: NSObject, WKScriptMessageHandler {
    weak var panel: NSPanel?
    var onUsageRequested: (() -> Void)?
    var onResetRequested: (() -> Void)?
    var onLaunchAtLoginStatusRequested: (() -> Void)?
    var onLaunchAtLoginChangeRequested: ((Bool) -> Void)?
    var onNotificationSettingsRequested: (() -> Void)?
    var onNotificationSettingsChangeRequested: (([String: Any]) -> Void)?
    var onNotificationAuthorizationRequested: (() -> Void)?
    var onNotificationTestRequested: (() -> Void)?
    var onNotificationPromptDismissed: (() -> Void)?
    var onRemoteSessionSettingsRequested: (() -> Void)?
    var onRemoteSessionSettingsChangeRequested: ((Bool) -> Void)?
    var onRemoteSessionPromptDismissed: (() -> Void)?
    var onMenuBarVisibilityChangeRequested: ((Bool) -> Void)?
    var onExportRequested: ((String) -> Void)?
    private var compactFrame: NSRect?

    func prepareForDrag() {
        guard let panel else { return }
        let frame = NSRect(
            x: panel.frame.minX,
            y: panel.frame.maxY - 66,
            width: 66,
            height: 66
        )
        compactFrame = frame
        panel.setFrame(frame, display: true)
        updateExpansionAnchor(compactX: 0, compactY: 0, pointerX: 33, pointerY: 33, in: panel)
    }

    func finishDrag(at frame: NSRect) {
        compactFrame = frame
        guard let panel else { return }
        panel.setFrame(frame, display: true)
        updateExpansionAnchor(compactX: 0, compactY: 0, pointerX: 33, pointerY: 33, in: panel)
    }

    func userContentController(
        _ userContentController: WKUserContentController,
        didReceive message: WKScriptMessage
    ) {
        guard message.name == "panel",
              let body = message.body as? [String: Any] else { return }

        if body["action"] as? String == "quit" {
            NSApp.terminate(nil)
            return
        }

        if body["action"] as? String == "getUsage" {
            onUsageRequested?()
            return
        }

        if body["action"] as? String == "consumeReset",
           body["confirmed"] as? Bool == true {
            onResetRequested?()
            return
        }

        if body["action"] as? String == "getLaunchAtLogin" {
            onLaunchAtLoginStatusRequested?()
            return
        }

        if body["action"] as? String == "setLaunchAtLogin",
           let enabled = body["enabled"] as? Bool {
            onLaunchAtLoginChangeRequested?(enabled)
            return
        }

        if body["action"] as? String == "getNotificationSettings" {
            onNotificationSettingsRequested?()
            return
        }

        if body["action"] as? String == "setNotificationSettings" {
            onNotificationSettingsChangeRequested?(body)
            return
        }

        if body["action"] as? String == "requestNotificationAuthorization" {
            onNotificationAuthorizationRequested?()
            return
        }

        if body["action"] as? String == "sendTestNotification" {
            onNotificationTestRequested?()
            return
        }

        if body["action"] as? String == "dismissNotificationPrompt" {
            onNotificationPromptDismissed?()
            return
        }

        if body["action"] as? String == "getRemoteSessionSettings" {
            onRemoteSessionSettingsRequested?()
            return
        }

        if body["action"] as? String == "setRemoteSessionMonitoring",
           let enabled = body["enabled"] as? Bool {
            onRemoteSessionSettingsChangeRequested?(enabled)
            return
        }

        if body["action"] as? String == "dismissRemoteSessionPrompt" {
            onRemoteSessionPromptDismissed?()
            return
        }

        if body["action"] as? String == "setMenuBarVisible",
           let enabled = body["enabled"] as? Bool {
            onMenuBarVisibilityChangeRequested?(enabled)
            return
        }

        if body["action"] as? String == "exportReport",
           let format = body["format"] as? String {
            onExportRequested?(format)
            return
        }

        guard body["action"] as? String == "resize",
              let height = (body["height"] as? NSNumber)?.doubleValue,
              let panel else { return }

        let requestedWidth = (body["width"] as? NSNumber)?.doubleValue ?? panel.frame.width
        let newWidth = CGFloat(min(max(requestedWidth, 66), 360))
        let newHeight = CGFloat(min(max(height, 66), 560))
        let expanding = newWidth > 66 || newHeight > 66
        let visibleFrame = panel.screen?.visibleFrame ?? NSScreen.main?.visibleFrame ?? panel.frame

        if expanding, compactFrame == nil {
            var savedFrame = panel.frame
            savedFrame.size = NSSize(width: 66, height: 66)
            compactFrame = savedFrame
        }

        let anchorFrame = compactFrame ?? panel.frame
        var frame = anchorFrame
        frame.size.width = newWidth
        frame.size.height = newHeight
        frame.origin.y = anchorFrame.maxY - newHeight
        if expanding {
            frame.origin.x = min(max(frame.origin.x, visibleFrame.minX), visibleFrame.maxX - newWidth)
            frame.origin.y = min(max(frame.origin.y, visibleFrame.minY), visibleFrame.maxY - newHeight)
        }

        if body["animated"] as? Bool == true {
            NSAnimationContext.runAnimationGroup { context in
                context.duration = 0.22
                context.allowsImplicitAnimation = true
                panel.animator().setFrame(frame, display: true)
            }
        } else {
            panel.setFrame(frame, display: true)
        }

        if expanding, let compactFrame {
            let anchorX = CGFloat(min(max((body["anchorX"] as? NSNumber)?.doubleValue ?? 33, 0), 66))
            let anchorY = CGFloat(min(max((body["anchorY"] as? NSNumber)?.doubleValue ?? 33, 0), 66))
            updateExpansionAnchor(
                compactX: compactFrame.minX - frame.minX,
                compactY: frame.maxY - compactFrame.maxY,
                pointerX: anchorX,
                pointerY: anchorY,
                in: panel
            )
        } else {
            compactFrame = nil
            updateExpansionAnchor(compactX: 0, compactY: 0, pointerX: 33, pointerY: 33, in: panel)
        }
    }

    private func updateExpansionAnchor(
        compactX: CGFloat,
        compactY: CGFloat,
        pointerX: CGFloat,
        pointerY: CGFloat,
        in panel: NSPanel
    ) {
        let payload: [String: Double] = [
            "compactX": Double(compactX),
            "compactY": Double(compactY),
            "pointerX": Double(compactX + pointerX),
            "pointerY": Double(compactY + pointerY)
        ]
        guard JSONSerialization.isValidJSONObject(payload),
              let data = try? JSONSerialization.data(withJSONObject: payload),
              let json = String(data: data, encoding: .utf8),
              let webView = panel.contentView as? WKWebView else { return }
        webView.evaluateJavaScript("window.codexUsageSetPanelAnchor?.(\(json));")
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, WKNavigationDelegate, UNUserNotificationCenterDelegate {
    private struct LifecycleObserver {
        let center: NotificationCenter
        let token: NSObjectProtocol
    }

    private var panel: NSPanel?
    private weak var webView: WKWebView?
    private let usageService = CodexUsageService()
    private let quotaMonitor = CodexQuotaMonitor()
    private let notificationCenter = UNUserNotificationCenter.current()
    private let defaults = UserDefaults.standard
    private var isRefreshing = false
    private var isResetting = false
    private var refreshAfterReset = false
    private var refreshAfterSettingsChange = false
    private var refreshTimer: Timer?
    private var contextHealthTimer: Timer?
    private var isRefreshingContextHealth = false
    private var hasReceivedUsage = false
    private var retryAttempt = 0
    private var retryWorkItem: DispatchWorkItem?
    private let bridge = PanelBridge()
    private var lifecycleObservers: [LifecycleObserver] = []
    private var sessionActive = true
    private var screensActive = true
    private var powerActive = true
    private var appVisible = true
    private var latestPayload: [String: Any] = [:]
    private var latestQuotaReadings: [CodexQuotaReading] = []
    private var latestQuotaForecast: [String: Any] = [:]
    private var statusItem: NSStatusItem?
    private let statusMenu = NSMenu()
    private let primaryStatusMenuItem = NSMenuItem(title: "5 小时额度：—", action: nil, keyEquivalent: "")
    private let secondaryStatusMenuItem = NSMenuItem(title: "每周额度：—", action: nil, keyEquivalent: "")
    private let forecastStatusMenuItem = NSMenuItem(title: "趋势估算：暂无足够数据", action: nil, keyEquivalent: "")

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        ProcessInfo.processInfo.disableSuddenTermination()
        ProcessInfo.processInfo.disableAutomaticTermination(
            "Codex Meter stays visible until the user explicitly quits"
        )
        if let iconURL = Bundle.main.url(forResource: "AppIcon", withExtension: "icns"),
           let icon = NSImage(contentsOf: iconURL) {
            NSApp.applicationIconImage = icon
        }
        createPanel()
        notificationCenter.delegate = self
        configureStatusMenu()
        setMenuBarVisible(menuBarVisible)
        observeLifecycle()
    }

    func applicationWillTerminate(_ notification: Notification) {
        refreshTimer?.invalidate()
        contextHealthTimer?.invalidate()
        retryWorkItem?.cancel()
        lifecycleObservers.forEach { $0.center.removeObserver($0.token) }
        lifecycleObservers.removeAll()
        usageService.shutdown()
        if let statusItem { NSStatusBar.system.removeStatusItem(statusItem) }
    }

    private func createPanel() {
        let compactSize = NSSize(width: 66, height: 66)
        let screenFrame = NSScreen.main?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        let origin = NSPoint(
            x: screenFrame.maxX - compactSize.width - 18,
            y: screenFrame.maxY - compactSize.height - 18
        )

        let panel = FloatingPanel(
            contentRect: NSRect(origin: origin, size: compactSize),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        panel.identifier = NSUserInterfaceItemIdentifier("com.local.codex-usage")
        panel.level = .floating
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.acceptsMouseMovedEvents = true
        panel.isMovableByWindowBackground = true
        panel.hidesOnDeactivate = false
        // The panel stays non-activating, but must become key on hover so WKWebView
        // controls accept the first click when the pointer arrives from another app.
        panel.becomesKeyOnlyIfNeeded = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.delegate = self

        let configuration = WKWebViewConfiguration()
        configuration.userContentController.add(bridge, name: "panel")
        let bridgeScript = """
        window.codexMeterHostActive = true;
        window.codexMeterBridge = {
          getUsage() {
            window.webkit.messageHandlers.panel.postMessage({ action: 'getUsage' });
            return null;
          },
          consumeReset(payload) {
            window.webkit.messageHandlers.panel.postMessage({ action: 'consumeReset', confirmed: payload?.confirmed === true });
          },
          resize(payload) {
            window.webkit.messageHandlers.panel.postMessage({ action: 'resize', ...payload });
          },
          quit() {
            window.webkit.messageHandlers.panel.postMessage({ action: 'quit' });
          },
          getLaunchAtLogin() {
            window.webkit.messageHandlers.panel.postMessage({ action: 'getLaunchAtLogin' });
          },
          setLaunchAtLogin(payload) {
            window.webkit.messageHandlers.panel.postMessage({ action: 'setLaunchAtLogin', enabled: payload?.enabled === true });
          },
          getNotificationSettings() {
            window.webkit.messageHandlers.panel.postMessage({ action: 'getNotificationSettings' });
          },
          setNotificationSettings(payload) {
            window.webkit.messageHandlers.panel.postMessage({ action: 'setNotificationSettings', ...payload });
          },
          requestNotificationAuthorization() {
            window.webkit.messageHandlers.panel.postMessage({ action: 'requestNotificationAuthorization' });
          },
          sendTestNotification() {
            window.webkit.messageHandlers.panel.postMessage({ action: 'sendTestNotification' });
          },
          dismissNotificationPrompt() {
            window.webkit.messageHandlers.panel.postMessage({ action: 'dismissNotificationPrompt' });
          },
          getRemoteSessionSettings() {
            window.webkit.messageHandlers.panel.postMessage({ action: 'getRemoteSessionSettings' });
          },
          setRemoteSessionMonitoring(payload) {
            window.webkit.messageHandlers.panel.postMessage({ action: 'setRemoteSessionMonitoring', enabled: payload?.enabled === true });
          },
          dismissRemoteSessionPrompt() {
            window.webkit.messageHandlers.panel.postMessage({ action: 'dismissRemoteSessionPrompt' });
          },
          setMenuBarVisible(payload) {
            window.webkit.messageHandlers.panel.postMessage({ action: 'setMenuBarVisible', enabled: payload?.enabled !== false });
          },
          exportReport(payload) {
            window.webkit.messageHandlers.panel.postMessage({ action: 'exportReport', format: payload?.format || 'md' });
          }
        };
        """
        configuration.userContentController.addUserScript(WKUserScript(
            source: bridgeScript,
            injectionTime: .atDocumentStart,
            forMainFrameOnly: true
        ))
        let webView = HoverWebView(frame: panel.contentView?.bounds ?? .zero, configuration: configuration)
        webView.autoresizingMask = [.width, .height]
        webView.setValue(false, forKey: "drawsBackground")
        webView.navigationDelegate = self
        webView.onHoverChanged = { [weak webView, weak panel] entered, pointer in
            if entered {
                panel?.makeKeyAndOrderFront(nil)
            }
            let function = entered ? "window.codexUsageHoverEnter" : "window.codexUsageHoverLeave"
            var argument = ""
            if let pointer,
               let data = try? JSONSerialization.data(withJSONObject: ["x": pointer.x, "y": pointer.y]),
               let json = String(data: data, encoding: .utf8) {
                argument = json
            }
            webView?.evaluateJavaScript("\(function)?.(\(argument));")
        }
        let dragHandle = NativeDragHandle(frame: NSRect(
            x: 0,
            y: webView.isFlipped ? 0 : max(0, webView.bounds.height - 66),
            width: 66,
            height: 66
        ))
        dragHandle.autoresizingMask = webView.isFlipped ? [.maxYMargin] : [.minYMargin]
        dragHandle.onHoverChanged = webView.onHoverChanged
        dragHandle.onDragChanged = { [weak self, weak webView] dragging in
            webView?.suppressHoverExit = dragging
            if dragging {
                self?.bridge.prepareForDrag()
                webView?.evaluateJavaScript("window.codexUsageDragStarted?.();")
            }
        }
        dragHandle.onDragEnded = { [weak self, weak webView] draggedFrame in
            guard let self, let webView else { return }
            let compactFrame = NSRect(
                x: draggedFrame.minX,
                y: draggedFrame.maxY - 66,
                width: 66,
                height: 66
            )
            self.bridge.finishDrag(at: compactFrame)
            webView.evaluateJavaScript("window.codexUsageDragEnded?.();")
        }

        guard let resourceURL = Bundle.main.resourceURL,
              let pageURL = Bundle.main.url(forResource: "companion", withExtension: "html") else {
            fputs("Companion resources are missing from \(Bundle.main.bundlePath)\n", stderr)
            NSApp.terminate(nil)
            return
        }

        webView.loadFileURL(pageURL, allowingReadAccessTo: resourceURL)
        panel.contentView = webView
        webView.addSubview(dragHandle, positioned: .above, relativeTo: nil)
        bridge.panel = panel
        bridge.onUsageRequested = { [weak self] in self?.refreshUsage() }
        bridge.onResetRequested = { [weak self] in self?.consumeResetCredit() }
        bridge.onLaunchAtLoginStatusRequested = { [weak self] in self?.deliverLaunchAtLoginResult() }
        bridge.onLaunchAtLoginChangeRequested = { [weak self] enabled in self?.setLaunchAtLogin(enabled) }
        bridge.onNotificationSettingsRequested = { [weak self] in self?.deliverNotificationSettings() }
        bridge.onNotificationSettingsChangeRequested = { [weak self] body in self?.setNotificationSettings(body) }
        bridge.onNotificationAuthorizationRequested = { [weak self] in self?.requestNotificationAuthorization() }
        bridge.onNotificationTestRequested = { [weak self] in self?.sendTestNotification() }
        bridge.onNotificationPromptDismissed = { [weak self] in self?.dismissNotificationPrompt() }
        bridge.onRemoteSessionSettingsRequested = { [weak self] in self?.deliverRemoteSessionSettings() }
        bridge.onRemoteSessionSettingsChangeRequested = { [weak self] enabled in self?.setRemoteSessionMonitoring(enabled) }
        bridge.onRemoteSessionPromptDismissed = { [weak self] in self?.dismissRemoteSessionPrompt() }
        bridge.onMenuBarVisibilityChangeRequested = { [weak self] enabled in self?.setMenuBarVisible(enabled) }
        bridge.onExportRequested = { [weak self] format in self?.exportReport(format: format) }
        self.webView = webView
        panel.orderFrontRegardless()
        self.panel = panel
    }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        publishHostActive()
    }

    private func observeLifecycle() {
        let workspaceCenter = NSWorkspace.shared.notificationCenter
        observe(workspaceCenter, NSWorkspace.sessionDidResignActiveNotification) { $0.sessionActive = false }
        observe(workspaceCenter, NSWorkspace.sessionDidBecomeActiveNotification) { $0.sessionActive = true }
        observe(workspaceCenter, NSWorkspace.screensDidSleepNotification) { $0.screensActive = false }
        observe(workspaceCenter, NSWorkspace.screensDidWakeNotification) { $0.screensActive = true }
        observe(workspaceCenter, NSWorkspace.willSleepNotification) { $0.powerActive = false }
        observe(workspaceCenter, NSWorkspace.didWakeNotification) { $0.powerActive = true }

        let applicationCenter = NotificationCenter.default
        observe(applicationCenter, NSApplication.didHideNotification) { $0.appVisible = false }
        observe(applicationCenter, NSApplication.didUnhideNotification) { $0.appVisible = true }
    }

    private func observe(
        _ center: NotificationCenter,
        _ name: Notification.Name,
        update: @escaping (AppDelegate) -> Void
    ) {
        let token = center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
            guard let self else { return }
            update(self)
            self.publishHostActive()
        }
        lifecycleObservers.append(LifecycleObserver(center: center, token: token))
    }

    private func publishHostActive() {
        let active = sessionActive && screensActive && powerActive && appVisible && panel?.isVisible == true
        let value = active ? "true" : "false"
        webView?.evaluateJavaScript(
            "window.codexMeterHostActive=\(value);window.codexUsageSetHostActive?.(\(value));"
        )
    }

    private func refreshUsage() {
        guard !isRefreshing else { return }
        retryWorkItem?.cancel()
        retryWorkItem = nil
        isRefreshing = true
        let includeRemoteSessions = defaults.bool(forKey: "remoteSessionMonitoringEnabled")
        usageService.fetch(includeRemoteSessions: includeRemoteSessions) { [weak self] payload in
            guard let self else { return }
            if payload["partial"] as? Bool != true,
               self.refreshAfterReset || self.refreshAfterSettingsChange {
                self.isRefreshing = false
                self.refreshAfterReset = false
                self.refreshAfterSettingsChange = false
                self.refreshUsage()
                return
            }
            var enriched = payload
            enriched["capabilities"] = self.capabilitiesPayload
            if payload["partial"] as? Bool != true, payload["error"] == nil {
                let result = self.quotaMonitor.process(payload)
                enriched["forecast"] = result.forecast
                self.latestQuotaReadings = result.readings
                self.latestQuotaForecast = result.forecast
                self.updateStatusItem(readings: result.readings, forecast: result.forecast)
                self.deliverNotifications(result.events)
            } else if payload["partial"] as? Bool != true {
                self.updateStatusItem(readings: [], forecast: [:])
            }
            if payload["partial"] as? Bool != true || !self.hasReceivedUsage {
                self.latestPayload = enriched
            }
            self.deliver(enriched)

            guard payload["partial"] as? Bool != true else { return }
            self.isRefreshing = false
            if payload["error"] != nil {
                if self.hasReceivedUsage {
                    self.scheduleRetry()
                } else {
                    self.scheduleStartupRetry()
                }
            } else {
                self.hasReceivedUsage = true
                self.retryAttempt = 0
                self.startRefreshTimerIfNeeded()
            }
            self.startContextHealthTimerIfNeeded()
            self.refreshCurrentContextHealth()
        }
    }

    private func consumeResetCredit() {
        guard !isResetting else { return }
        isResetting = true
        let idempotencyKey = UUID().uuidString.lowercased()
        usageService.consumeResetCredit(idempotencyKey: idempotencyKey) { [weak self] payload in
            guard let self else { return }
            self.isResetting = false
            self.deliverResetResult(payload)
            if self.isRefreshing {
                self.refreshAfterReset = true
            } else {
                self.refreshUsage()
            }
        }
    }

    private func deliver(_ payload: [String: Any]) {
        guard JSONSerialization.isValidJSONObject(payload),
              let data = try? JSONSerialization.data(withJSONObject: payload),
              let json = String(data: data, encoding: .utf8) else { return }
        webView?.evaluateJavaScript("window.updateCodexUsage(\(json));")
    }

    private func deliverResetResult(_ payload: [String: Any]) {
        guard JSONSerialization.isValidJSONObject(payload),
              let data = try? JSONSerialization.data(withJSONObject: payload),
              let json = String(data: data, encoding: .utf8) else { return }
        webView?.evaluateJavaScript("window.codexResetResult?.(\(json));")
    }

    private var menuBarVisible: Bool {
        defaults.object(forKey: "menuBarVisible") == nil ? true : defaults.bool(forKey: "menuBarVisible")
    }

    private var capabilitiesPayload: [String: Any] {
        let remoteSettings = remoteSessionSettingsDictionary()
        return [
            "extendedInsights": true,
            "notifications": true,
            "menuBar": true,
            "reportExport": true,
            "contextHealth": true,
            "notificationPromptNeeded": !defaults.bool(forKey: "notificationPromptSeen"),
            "remoteSessionMonitoring": true,
            "remoteSessionMonitoringEnabled": remoteSettings["enabled"] as? Bool ?? false,
            "remoteSessionHostCount": remoteSettings["connectedHosts"] as? Int ?? 0,
            "remoteSessionPromptNeeded": remoteSettings["promptNeeded"] as? Bool ?? false
        ]
    }

    private func configureStatusMenu() {
        primaryStatusMenuItem.isEnabled = false
        secondaryStatusMenuItem.isEnabled = false
        forecastStatusMenuItem.isEnabled = false
        statusMenu.addItem(primaryStatusMenuItem)
        statusMenu.addItem(secondaryStatusMenuItem)
        statusMenu.addItem(forecastStatusMenuItem)
        statusMenu.addItem(.separator())
        statusMenu.addItem(NSMenuItem(title: "显示 Codex Meter", action: #selector(showPanelFromMenu), keyEquivalent: ""))
        statusMenu.addItem(NSMenuItem(title: "立即刷新", action: #selector(refreshFromMenu), keyEquivalent: "r"))
        statusMenu.addItem(NSMenuItem(title: "打开设置", action: #selector(openSettingsFromMenu), keyEquivalent: ","))
        statusMenu.addItem(.separator())
        statusMenu.addItem(NSMenuItem(title: "退出", action: #selector(quitFromMenu), keyEquivalent: "q"))
        statusMenu.items.forEach { $0.target = self }
    }

    private func setMenuBarVisible(_ visible: Bool) {
        defaults.set(visible, forKey: "menuBarVisible")
        if visible, statusItem == nil {
            let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
            if let button = item.button {
                button.image = NSImage(systemSymbolName: "sparkles", accessibilityDescription: "Codex Meter")
                button.imagePosition = .imageLeading
                button.title = "—"
                button.target = self
                button.action = #selector(statusItemClicked)
                button.sendAction(on: [.leftMouseUp, .rightMouseUp])
                button.toolTip = "左键显示 Codex Meter，右键打开菜单"
            }
            statusItem = item
            if !latestQuotaReadings.isEmpty {
                updateStatusItem(readings: latestQuotaReadings, forecast: latestQuotaForecast)
            }
        } else if !visible, let item = statusItem {
            NSStatusBar.system.removeStatusItem(item)
            statusItem = nil
        }
        deliverNotificationSettings()
    }

    @objc private func statusItemClicked() {
        if NSApp.currentEvent?.type == .rightMouseUp, let button = statusItem?.button {
            statusMenu.popUp(positioning: nil, at: NSPoint(x: 0, y: button.bounds.height + 4), in: button)
        } else {
            showPanel(expand: true)
        }
    }

    @objc private func showPanelFromMenu() { showPanel(expand: true) }
    @objc private func refreshFromMenu() { refreshUsage() }
    @objc private func openSettingsFromMenu() {
        showPanel(expand: true)
        webView?.evaluateJavaScript("window.codexUsageOpenDialog?.('settings');")
    }
    @objc private func quitFromMenu() { NSApp.terminate(nil) }

    private func showPanel(expand: Bool) {
        panel?.orderFrontRegardless()
        panel?.makeKey()
        if expand { webView?.evaluateJavaScript("window.codexUsageExpand?.();") }
    }

    private func updateStatusItem(readings: [CodexQuotaReading], forecast: [String: Any]) {
        let primary = readings.first { $0.key == "primary" }
        let secondary = readings.first { $0.key == "secondary" }
        statusItem?.button?.title = primary.map { "\(Int($0.remainingPercent.rounded()))%" } ?? "—"
        primaryStatusMenuItem.title = "5 小时额度：\(primary.map { "\(Int($0.remainingPercent.rounded()))% · \(formatReset($0.resetsAt))" } ?? "—")"
        secondaryStatusMenuItem.title = "每周额度：\(secondary.map { "\(Int($0.remainingPercent.rounded()))% · \(formatReset($0.resetsAt))" } ?? "—")"
        let primaryForecast = forecast["primary"] as? [String: Any]
        forecastStatusMenuItem.title = "趋势估算：\(forecastDescription(primaryForecast))"
        statusItem?.button?.toolTip = "5 小时 \(primary.map { "\(Int($0.remainingPercent.rounded()))%" } ?? "—") · 每周 \(secondary.map { "\(Int($0.remainingPercent.rounded()))%" } ?? "—")"
    }

    private func forecastDescription(_ forecast: [String: Any]?) -> String {
        guard let forecast else { return "暂无足够数据" }
        if forecast["status"] as? String == "will_deplete",
           let value = (forecast["estimatedExhaustsAt"] as? NSNumber)?.doubleValue {
            let formatter = DateFormatter()
            formatter.dateFormat = "M月d日 HH:mm"
            return "预计 \(formatter.string(from: Date(timeIntervalSince1970: value))) 耗尽"
        }
        return forecast["message"] as? String ?? "暂无足够数据"
    }

    private func formatReset(_ timestamp: TimeInterval?) -> String {
        guard let timestamp else { return "等待同步" }
        let seconds = max(0, timestamp - Date().timeIntervalSince1970)
        if seconds >= 86_400 { return "\(Int(seconds / 86_400)) 天后重置" }
        if seconds >= 3_600 { return "\(Int(seconds / 3_600)) 小时后重置" }
        return "\(max(1, Int(seconds / 60))) 分钟后重置"
    }

    private func deliverNotifications(_ events: [CodexQuotaEvent]) {
        guard defaults.bool(forKey: "notificationsEnabled") else { return }
        for event in events {
            let enabled: Bool
            switch event.kind {
            case .threshold: enabled = defaults.object(forKey: "notifyThresholds") == nil || defaults.bool(forKey: "notifyThresholds")
            case .exhausted: enabled = defaults.object(forKey: "notifyExhausted") == nil || defaults.bool(forKey: "notifyExhausted")
            case .restored: enabled = defaults.object(forKey: "notifyRestored") == nil || defaults.bool(forKey: "notifyRestored")
            }
            guard enabled else { continue }
            let content = UNMutableNotificationContent()
            content.sound = .default
            switch event.kind {
            case .threshold:
                content.title = "\(event.reading.label)偏低"
                content.body = "当前剩余 \(Int(event.reading.remainingPercent.rounded()))%，请留意本周期用量。"
            case .exhausted:
                content.title = "\(event.reading.label)已耗尽"
                content.body = "额度将在\(formatReset(event.reading.resetsAt))。"
            case .restored:
                content.title = "\(event.reading.label)已恢复"
                content.body = "当前剩余 \(Int(event.reading.remainingPercent.rounded()))%，可以继续使用。"
            }
            notificationCenter.add(UNNotificationRequest(
                identifier: event.deduplicationKey,
                content: content,
                trigger: nil
            ))
        }
    }

    private func notificationSettingsDictionary(authorization: UNAuthorizationStatus) -> [String: Any] {
        [
            "supported": true,
            "enabled": defaults.bool(forKey: "notificationsEnabled"),
            "thresholds": defaults.object(forKey: "notifyThresholds") == nil ? true : defaults.bool(forKey: "notifyThresholds"),
            "exhausted": defaults.object(forKey: "notifyExhausted") == nil ? true : defaults.bool(forKey: "notifyExhausted"),
            "restored": defaults.object(forKey: "notifyRestored") == nil ? true : defaults.bool(forKey: "notifyRestored"),
            "menuBarVisible": menuBarVisible,
            "authorization": authorization == .authorized || authorization == .provisional ? "authorized" : (authorization == .denied ? "denied" : "notDetermined")
        ]
    }

    private func deliverNotificationSettings(error: String? = nil) {
        notificationCenter.getNotificationSettings { [weak self] settings in
            guard let self else { return }
            var payload = self.notificationSettingsDictionary(authorization: settings.authorizationStatus)
            if let error { payload["error"] = error }
            DispatchQueue.main.async { self.evaluateJavaScriptCallback("window.codexNotificationSettingsResult", payload: payload) }
        }
    }

    private func setNotificationSettings(_ body: [String: Any]) {
        if let value = body["enabled"] as? Bool { defaults.set(value, forKey: "notificationsEnabled") }
        if let value = body["thresholds"] as? Bool { defaults.set(value, forKey: "notifyThresholds") }
        if let value = body["exhausted"] as? Bool { defaults.set(value, forKey: "notifyExhausted") }
        if let value = body["restored"] as? Bool { defaults.set(value, forKey: "notifyRestored") }
        defaults.set(true, forKey: "notificationPromptSeen")
        deliverNotificationSettings()
    }

    private func requestNotificationAuthorization() {
        defaults.set(true, forKey: "notificationPromptSeen")
        notificationCenter.requestAuthorization(options: [.alert, .sound]) { [weak self] granted, error in
            guard let self else { return }
            self.defaults.set(granted, forKey: "notificationsEnabled")
            self.deliverNotificationSettings(error: error?.localizedDescription)
        }
    }

    private func dismissNotificationPrompt() {
        defaults.set(true, forKey: "notificationPromptSeen")
        deliverNotificationSettings()
    }

    private func remoteSessionSettingsDictionary() -> [String: Any] {
        let enabled = defaults.bool(forKey: "remoteSessionMonitoringEnabled")
        let connectedHosts = usageService.connectedRemoteHostCount()
        return [
            "supported": true,
            "enabled": enabled,
            "connectedHosts": connectedHosts,
            "promptNeeded": connectedHosts > 0 && !enabled && !defaults.bool(forKey: "remoteSessionPromptSeen")
        ]
    }

    private func deliverRemoteSessionSettings() {
        evaluateJavaScriptCallback(
            "window.codexRemoteSessionSettingsResult",
            payload: remoteSessionSettingsDictionary()
        )
    }

    private func setRemoteSessionMonitoring(_ enabled: Bool) {
        defaults.set(enabled, forKey: "remoteSessionMonitoringEnabled")
        defaults.set(true, forKey: "remoteSessionPromptSeen")
        deliverRemoteSessionSettings()
        if isRefreshing {
            refreshAfterSettingsChange = true
        } else {
            refreshUsage()
        }
    }

    private func dismissRemoteSessionPrompt() {
        defaults.set(true, forKey: "remoteSessionPromptSeen")
        deliverRemoteSessionSettings()
    }

    private func sendTestNotification() {
        let content = UNMutableNotificationContent()
        content.title = "Codex Meter 通知测试"
        content.body = "通知已成功启用。"
        content.sound = .default
        notificationCenter.add(UNNotificationRequest(identifier: "codex-meter-test-\(UUID().uuidString)", content: content, trigger: nil)) { [weak self] error in
            if let error { self?.deliverNotificationSettings(error: error.localizedDescription) }
        }
    }

    private func exportReport(format: String) {
        let normalized = format == "csv" ? "csv" : "md"
        let data: Data
        if normalized == "csv" {
            data = CodexReportGenerator.csv(from: latestPayload)
        } else {
            data = Data(CodexReportGenerator.markdown(from: latestPayload).utf8)
        }
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd"
        let savePanel = NSSavePanel()
        savePanel.allowedContentTypes = [UTType(filenameExtension: normalized) ?? .data]
        savePanel.nameFieldStringValue = "Codex-Meter-Report-\(formatter.string(from: Date())).\(normalized)"
        savePanel.canCreateDirectories = true
        let completion: (NSApplication.ModalResponse) -> Void = { [weak self] response in
            guard let self else { return }
            if response != .OK {
                self.evaluateJavaScriptCallback("window.codexExportResult", payload: ["cancelled": true])
                return
            }
            guard let url = savePanel.url else { return }
            do {
                try data.write(to: url, options: .atomic)
                self.evaluateJavaScriptCallback("window.codexExportResult", payload: ["success": true, "path": url.path])
            } catch {
                self.evaluateJavaScriptCallback("window.codexExportResult", payload: ["error": error.localizedDescription])
            }
        }
        NSApp.activate(ignoringOtherApps: true)
        if let panel {
            panel.makeKeyAndOrderFront(nil)
            savePanel.beginSheetModal(for: panel, completionHandler: completion)
        } else {
            savePanel.level = .floating
            savePanel.begin(completionHandler: completion)
        }
    }

    private func evaluateJavaScriptCallback(_ function: String, payload: [String: Any]) {
        guard JSONSerialization.isValidJSONObject(payload),
              let data = try? JSONSerialization.data(withJSONObject: payload),
              let json = String(data: data, encoding: .utf8) else { return }
        webView?.evaluateJavaScript("\(function)?.(\(json));")
    }

    func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        willPresent notification: UNNotification,
        withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void
    ) {
        completionHandler([.banner, .sound])
    }

    func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        didReceive response: UNNotificationResponse,
        withCompletionHandler completionHandler: @escaping () -> Void
    ) {
        showPanel(expand: true)
        completionHandler()
    }

    private func setLaunchAtLogin(_ enabled: Bool) {
        do {
            if enabled {
                try SMAppService.mainApp.register()
            } else {
                try SMAppService.mainApp.unregister()
            }
            deliverLaunchAtLoginResult()
        } catch {
            deliverLaunchAtLoginResult(error: error.localizedDescription)
        }
    }

    private func deliverLaunchAtLoginResult(error: String? = nil) {
        let status = SMAppService.mainApp.status
        var statusMessage = error
        if statusMessage == nil && status == .requiresApproval {
            statusMessage = "请在系统设置的“登录项”中允许 Codex Meter"
        }
        var payload: [String: Any] = [
            "supported": true,
            "enabled": status == .enabled
        ]
        if let statusMessage {
            payload["error"] = statusMessage
        } else {
            payload["error"] = NSNull()
        }
        guard JSONSerialization.isValidJSONObject(payload),
              let data = try? JSONSerialization.data(withJSONObject: payload),
              let json = String(data: data, encoding: .utf8) else { return }
        webView?.evaluateJavaScript("window.codexLaunchAtLoginResult?.(\(json));")
    }

    private func scheduleRetry() {
        let delays: [TimeInterval] = [2, 5, 10]
        guard retryAttempt < delays.count else { return }
        let delay = delays[retryAttempt]
        retryAttempt += 1
        let workItem = DispatchWorkItem { [weak self] in
            self?.refreshUsage()
        }
        retryWorkItem = workItem
        DispatchQueue.main.asyncAfter(deadline: .now() + delay, execute: workItem)
    }

    private func scheduleStartupRetry() {
        let workItem = DispatchWorkItem { [weak self] in
            self?.refreshUsage()
        }
        retryWorkItem = workItem
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5, execute: workItem)
    }

    private func startRefreshTimerIfNeeded() {
        guard refreshTimer == nil else { return }
        refreshTimer = Timer.scheduledTimer(withTimeInterval: 30, repeats: true) { [weak self] _ in
            self?.refreshUsage()
        }
    }

    private func startContextHealthTimerIfNeeded() {
        guard contextHealthTimer == nil else { return }
        contextHealthTimer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            self?.refreshCurrentContextHealth()
        }
    }

    private func refreshCurrentContextHealth() {
        guard !isRefreshingContextHealth,
              sessionActive,
              screensActive,
              powerActive,
              appVisible else { return }
        isRefreshingContextHealth = true
        usageService.fetchCurrentContextHealth { [weak self] payload in
            guard let self else { return }
            self.isRefreshingContextHealth = false
            guard let payload else { return }
            self.deliverCurrentContextHealth(payload)
        }
    }

    private func deliverCurrentContextHealth(_ payload: [String: Any]) {
        guard JSONSerialization.isValidJSONObject(payload),
              let data = try? JSONSerialization.data(withJSONObject: payload),
              let json = String(data: data, encoding: .utf8) else { return }
        webView?.evaluateJavaScript("window.updateCodexContextHealth?.(\(json));")
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        sender.orderFrontRegardless()
        return false
    }
}

@main
struct CodexUsageApplication {
    static func main() {
        let application = NSApplication.shared
        let delegate = AppDelegate()
        application.delegate = delegate
        application.run()
    }
}
