import AppKit
import CoreGraphics

let arguments = CommandLine.arguments
guard arguments.count >= 2 else {
    fputs("Usage: capture-live-window.swift OUTPUT.png [WAIT_SECONDS]\n", stderr)
    exit(2)
}

let outputURL = URL(fileURLWithPath: arguments[1])
let waitSeconds = arguments.count >= 3 ? (Double(arguments[2]) ?? 1.2) : 1.2
let holdPointer = arguments.contains("--hold")
let ownerName = "Codex Meter"

func windows() -> [[String: Any]] {
    CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] ?? []
}

func meterWindow(in items: [[String: Any]]) -> [String: Any]? {
    items
        .filter { ($0[kCGWindowOwnerName as String] as? String) == ownerName }
        .filter {
            let bounds = $0[kCGWindowBounds as String] as? [String: CGFloat] ?? [:]
            return (bounds["Width"] ?? 0) > 8 && (bounds["Height"] ?? 0) > 8
        }
        .max { lhs, rhs in
            let leftBounds = lhs[kCGWindowBounds as String] as? [String: CGFloat] ?? [:]
            let rightBounds = rhs[kCGWindowBounds as String] as? [String: CGFloat] ?? [:]
            return (leftBounds["Width"] ?? 0) * (leftBounds["Height"] ?? 0) < (rightBounds["Width"] ?? 0) * (rightBounds["Height"] ?? 0)
        }
}

guard let compact = meterWindow(in: windows()),
      let windowIDValue = compact[kCGWindowNumber as String] as? NSNumber,
      let compactBounds = compact[kCGWindowBounds as String] as? [String: CGFloat]
else {
    fputs("No on-screen Codex Meter window found.\n", stderr)
    exit(3)
}

let windowID = CGWindowID(windowIDValue.uint32Value)
let originalPointer = CGEvent(source: nil)?.location ?? .zero
let hoverPoint = CGPoint(
    x: (compactBounds["X"] ?? 0) + (compactBounds["Width"] ?? 66) / 2,
    y: (compactBounds["Y"] ?? 0) + (compactBounds["Height"] ?? 66) / 2
)

print("Window \(windowID) compact bounds: \(compactBounds)")
print("Pointer: \(originalPointer) -> \(hoverPoint)")

CGEvent(mouseEventSource: nil, mouseType: .mouseMoved, mouseCursorPosition: hoverPoint, mouseButton: .left)?.post(tap: .cghidEventTap)
Thread.sleep(forTimeInterval: waitSeconds)
if let currentPointer = CGEvent(source: nil)?.location {
    print("Pointer after move: \(currentPointer)")
}

defer {
    if !holdPointer {
        CGEvent(mouseEventSource: nil, mouseType: .mouseMoved, mouseCursorPosition: originalPointer, mouseButton: .left)?.post(tap: .cghidEventTap)
    }
}

guard let image = CGWindowListCreateImage(.null, .optionIncludingWindow, windowID, [.boundsIgnoreFraming, .bestResolution]) else {
    fputs("Window capture failed. Screen Recording permission may be required.\n", stderr)
    exit(4)
}

let bitmap = NSBitmapImageRep(cgImage: image)
guard let data = bitmap.representation(using: .png, properties: [:]) else {
    fputs("PNG encoding failed.\n", stderr)
    exit(5)
}

try FileManager.default.createDirectory(at: outputURL.deletingLastPathComponent(), withIntermediateDirectories: true)
try data.write(to: outputURL)
print("Captured \(image.width)x\(image.height) to \(outputURL.path)")
