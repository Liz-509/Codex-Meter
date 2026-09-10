import AppKit

@main
struct AppIconGenerator {
    static func main() throws {
        guard CommandLine.arguments.count == 3,
              let pixels = Int(CommandLine.arguments[1]),
              pixels > 0 else {
            throw NSError(domain: "AppIconGenerator", code: 1)
        }

        guard let bitmap = NSBitmapImageRep(
            bitmapDataPlanes: nil,
            pixelsWide: pixels,
            pixelsHigh: pixels,
            bitsPerSample: 8,
            samplesPerPixel: 4,
            hasAlpha: true,
            isPlanar: false,
            colorSpaceName: .deviceRGB,
            bitmapFormat: [],
            bytesPerRow: 0,
            bitsPerPixel: 0
        ), let context = NSGraphicsContext(bitmapImageRep: bitmap) else {
            throw NSError(domain: "AppIconGenerator", code: 2)
        }

        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = context
        context.imageInterpolation = .high

        let scale = CGFloat(pixels) / 1024
        let iconRect = NSRect(x: 72 * scale, y: 86 * scale, width: 880 * scale, height: 880 * scale)
        let iconPath = NSBezierPath(roundedRect: iconRect, xRadius: 218 * scale, yRadius: 218 * scale)

        NSColor.clear.setFill()
        NSRect(x: 0, y: 0, width: pixels, height: pixels).fill()

        let shadow = NSShadow()
        shadow.shadowColor = NSColor(calibratedRed: 0.16, green: 0.21, blue: 0.44, alpha: 0.28)
        shadow.shadowBlurRadius = 38 * scale
        shadow.shadowOffset = NSSize(width: 0, height: -22 * scale)
        shadow.set()

        let gradient = NSGradient(colors: [
            NSColor(calibratedRed: 0.51, green: 0.42, blue: 0.98, alpha: 1),
            NSColor(calibratedRed: 0.42, green: 0.40, blue: 0.95, alpha: 1),
            NSColor(calibratedRed: 0.35, green: 0.61, blue: 0.97, alpha: 1)
        ])!
        gradient.draw(in: iconPath, angle: -45)

        NSGraphicsContext.current?.saveGraphicsState()
        iconPath.addClip()
        let highlight = NSGradient(colorsAndLocations:
            (NSColor.white.withAlphaComponent(0.28), 0),
            (NSColor.white.withAlphaComponent(0.03), 0.52),
            (NSColor(calibratedRed: 0.12, green: 0.24, blue: 0.60, alpha: 0.14), 1)
        )!
        highlight.draw(in: iconRect, angle: -60)
        NSGraphicsContext.current?.restoreGraphicsState()

        NSColor.white.withAlphaComponent(0.24).setStroke()
        iconPath.lineWidth = max(1, 3 * scale)
        iconPath.stroke()

        let center = NSPoint(x: 512 * scale, y: 526 * scale)
        let radius = 264 * scale
        let inner = 0.095 * radius
        let sparkle = NSBezierPath()
        sparkle.move(to: NSPoint(x: center.x, y: center.y + radius))
        sparkle.curve(to: NSPoint(x: center.x + radius, y: center.y),
                      controlPoint1: NSPoint(x: center.x + inner, y: center.y + inner),
                      controlPoint2: NSPoint(x: center.x + inner, y: center.y + inner))
        sparkle.curve(to: NSPoint(x: center.x, y: center.y - radius),
                      controlPoint1: NSPoint(x: center.x + inner, y: center.y - inner),
                      controlPoint2: NSPoint(x: center.x + inner, y: center.y - inner))
        sparkle.curve(to: NSPoint(x: center.x - radius, y: center.y),
                      controlPoint1: NSPoint(x: center.x - inner, y: center.y - inner),
                      controlPoint2: NSPoint(x: center.x - inner, y: center.y - inner))
        sparkle.curve(to: NSPoint(x: center.x, y: center.y + radius),
                      controlPoint1: NSPoint(x: center.x - inner, y: center.y + inner),
                      controlPoint2: NSPoint(x: center.x - inner, y: center.y + inner))
        sparkle.close()
        NSColor.white.setFill()
        sparkle.fill()

        NSColor.white.withAlphaComponent(0.82).setFill()
        NSBezierPath(ovalIn: NSRect(x: 729 * scale, y: 724 * scale, width: 36 * scale, height: 36 * scale)).fill()

        NSGraphicsContext.restoreGraphicsState()

        guard let png = bitmap.representation(using: .png, properties: [:]) else {
            throw NSError(domain: "AppIconGenerator", code: 3)
        }
        try png.write(to: URL(fileURLWithPath: CommandLine.arguments[2]), options: .atomic)
    }
}
