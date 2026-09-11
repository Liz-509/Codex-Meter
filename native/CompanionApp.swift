import AppKit
import WebKit

final class FloatingPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
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
        let screenFrame = NSScreen.main?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        let origin = NSPoint(
            x: screenFrame.maxX - compactSize.width - 18,
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
        webView.onHoverChanged = { [weak webView, weak panel] entered, pointer in
            if entered {
                NSApp.activate(ignoringOtherApps: true)
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
