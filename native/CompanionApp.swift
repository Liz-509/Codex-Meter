import AppKit
import ServiceManagement
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

final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, WKNavigationDelegate {
    private struct LifecycleObserver {
        let center: NotificationCenter
        let token: NSObjectProtocol
    }

    private var panel: NSPanel?
    private weak var webView: WKWebView?
    private let usageService = CodexUsageService()
    private var isRefreshing = false
    private var isResetting = false
    private var refreshAfterReset = false
    private var refreshTimer: Timer?
    private var hasReceivedUsage = false
    private var retryAttempt = 0
    private var retryWorkItem: DispatchWorkItem?
    private let bridge = PanelBridge()
    private var lifecycleObservers: [LifecycleObserver] = []
    private var sessionActive = true
    private var screensActive = true
    private var powerActive = true
    private var appVisible = true

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
        observeLifecycle()
    }

    func applicationWillTerminate(_ notification: Notification) {
        refreshTimer?.invalidate()
        retryWorkItem?.cancel()
        lifecycleObservers.forEach { $0.center.removeObserver($0.token) }
        lifecycleObservers.removeAll()
        usageService.shutdown()
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
        usageService.fetch { [weak self] payload in
            guard let self else { return }
            self.deliver(payload)

            guard payload["partial"] as? Bool != true else { return }
            self.isRefreshing = false
            if self.refreshAfterReset {
                self.refreshAfterReset = false
                self.refreshUsage()
                return
            }
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
        refreshTimer = Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { [weak self] _ in
            self?.refreshUsage()
        }
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
