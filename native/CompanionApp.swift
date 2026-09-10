import AppKit
import WebKit

final class FloatingPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

final class HoverWebView: WKWebView {
    var onHoverChanged: ((Bool) -> Void)?
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
        onHoverChanged?(true)
        super.mouseEntered(with: event)
    }

    override func mouseExited(with event: NSEvent) {
        onHoverChanged?(false)
        super.mouseExited(with: event)
    }

    override func mouseDown(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        let distanceFromTop = isFlipped ? point.y : bounds.height - point.y
        if point.x >= 0, point.x <= 66, distanceFromTop >= 0, distanceFromTop <= 66,
           let window {
            window.performDrag(with: event)
            return
        }
        super.mouseDown(with: event)
    }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool {
        true
    }
}

final class PanelBridge: NSObject, WKScriptMessageHandler {
    weak var panel: NSPanel?
    var onUsageRequested: (() -> Void)?

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

        guard body["action"] as? String == "resize",
              let height = (body["height"] as? NSNumber)?.doubleValue,
              let panel else { return }

        let requestedWidth = (body["width"] as? NSNumber)?.doubleValue ?? panel.frame.width
        let newWidth = CGFloat(min(max(requestedWidth, 66), 360))
        let newHeight = CGFloat(min(max(height, 66), 560))
        var frame = panel.frame
        frame.origin.y += frame.height - newHeight
        frame.size.width = newWidth
        frame.size.height = newHeight
        if body["animated"] as? Bool == true {
            NSAnimationContext.runAnimationGroup { context in
                context.duration = 0.22
                context.allowsImplicitAnimation = true
                panel.animator().setFrame(frame, display: true)
            }
        } else {
            panel.setFrame(frame, display: true)
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    private var panel: NSPanel?
    private weak var webView: WKWebView?
    private let usageService = CodexUsageService()
    private var isRefreshing = false
    private var refreshTimer: Timer?
    private var retryAttempt = 0
    private var retryWorkItem: DispatchWorkItem?
    private let bridge = PanelBridge()

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        if let iconURL = Bundle.main.url(forResource: "AppIcon", withExtension: "icns"),
           let icon = NSImage(contentsOf: iconURL) {
            NSApp.applicationIconImage = icon
        }
        createPanel()
        refreshTimer = Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { [weak self] _ in
            self?.refreshUsage()
        }
        NSApp.activate(ignoringOtherApps: true)
    }

    func applicationWillTerminate(_ notification: Notification) {
        refreshTimer?.invalidate()
        retryWorkItem?.cancel()
    }

    private func createPanel() {
        let compactSize = NSSize(width: 66, height: 66)
        let expandedSize = NSSize(width: 360, height: 443)
        let screenFrame = NSScreen.main?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        let origin = NSPoint(
            x: screenFrame.maxX - expandedSize.width - 18,
            y: screenFrame.maxY - compactSize.height - 18
        )

        let panel = FloatingPanel(
            contentRect: NSRect(origin: origin, size: compactSize),
            styleMask: [.borderless],
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
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.delegate = self

        let configuration = WKWebViewConfiguration()
        configuration.userContentController.add(bridge, name: "panel")
        let bridgeScript = """
        window.codexMeterBridge = {
          getUsage() {
            window.webkit.messageHandlers.panel.postMessage({ action: 'getUsage' });
            return null;
          },
          resize(payload) {
            window.webkit.messageHandlers.panel.postMessage({ action: 'resize', ...payload });
          },
          quit() {
            window.webkit.messageHandlers.panel.postMessage({ action: 'quit' });
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
        webView.onHoverChanged = { [weak webView, weak panel] entered in
            if entered {
                NSApp.activate(ignoringOtherApps: true)
                panel?.makeKeyAndOrderFront(nil)
            }
            let function = entered ? "window.codexUsageHoverEnter" : "window.codexUsageHoverLeave"
            webView?.evaluateJavaScript("\(function)?.();")
        }

        guard let resourceURL = Bundle.main.resourceURL,
              let pageURL = Bundle.main.url(forResource: "companion", withExtension: "html") else {
            fputs("Companion resources are missing from \(Bundle.main.bundlePath)\n", stderr)
            NSApp.terminate(nil)
            return
        }

        webView.loadFileURL(pageURL, allowingReadAccessTo: resourceURL)
        panel.contentView = webView
        bridge.panel = panel
        bridge.onUsageRequested = { [weak self] in self?.refreshUsage() }
        self.webView = webView
        panel.orderFrontRegardless()
        panel.makeKey()
        self.panel = panel
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
            if payload["error"] != nil {
                self.scheduleRetry()
            } else {
                self.retryAttempt = 0
            }
        }
    }

    private func deliver(_ payload: [String: Any]) {
        guard JSONSerialization.isValidJSONObject(payload),
              let data = try? JSONSerialization.data(withJSONObject: payload),
              let json = String(data: data, encoding: .utf8) else { return }
        webView?.evaluateJavaScript("window.updateCodexUsage(\(json));")
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

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        true
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        NSApp.terminate(nil)
        return true
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
