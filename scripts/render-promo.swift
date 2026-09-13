import AppKit
import AVFoundation
import CoreMedia
import CoreVideo

let canvasWidth = 1920
let canvasHeight = 1080
let fps: Int32 = 30
let duration = 32.0
let frameCount = Int(duration * Double(fps))

let arguments = CommandLine.arguments
guard arguments.count >= 2 else {
    fputs("Usage: render-promo.swift OUTPUT.mp4 [PREVIEW_DIR]\n", stderr)
    exit(2)
}

let outputURL = URL(fileURLWithPath: arguments[1])
let previewDirectory = arguments.count >= 3 ? URL(fileURLWithPath: arguments[2], isDirectory: true) : nil
let repoRoot = URL(fileURLWithPath: FileManager.default.currentDirectoryPath, isDirectory: true)
let darkShotURL = repoRoot.appendingPathComponent("outputs/promo/live-expanded.png")
let lightShotURL = repoRoot.appendingPathComponent("outputs/promo/live-expanded.png")
let tokensShotURL = repoRoot.appendingPathComponent("outputs/promo/live-tokens-light.png")
let resetShotURL = repoRoot.appendingPathComponent("outputs/promo/live-reset.png")
let conversationsShotURL = repoRoot.appendingPathComponent("outputs/promo/live-conversations.png")

guard let darkShot = NSImage(contentsOf: darkShotURL),
      let lightShot = NSImage(contentsOf: lightShotURL),
      let tokensShot = NSImage(contentsOf: tokensShotURL),
      let resetShot = NSImage(contentsOf: resetShotURL),
      let conversationsShot = NSImage(contentsOf: conversationsShotURL)
else {
    fputs("Could not load live app captures. Run capture-live-window.swift first.\n", stderr)
    exit(2)
}

try? FileManager.default.removeItem(at: outputURL)
try FileManager.default.createDirectory(at: outputURL.deletingLastPathComponent(), withIntermediateDirectories: true)
if let previewDirectory {
    try FileManager.default.createDirectory(at: previewDirectory, withIntermediateDirectories: true)
}

func clamp(_ value: Double, _ low: Double = 0, _ high: Double = 1) -> Double {
    min(high, max(low, value))
}

func smooth(_ value: Double) -> Double {
    let x = clamp(value)
    return x * x * (3 - 2 * x)
}

func rangeProgress(_ time: Double, _ start: Double, _ end: Double) -> Double {
    smooth((time - start) / (end - start))
}

func sceneAlpha(_ time: Double, _ start: Double, _ end: Double, fade: Double = 0.55) -> Double {
    min(rangeProgress(time, start, start + fade), 1 - rangeProgress(time, end - fade, end))
}

func topRect(_ x: CGFloat, _ y: CGFloat, _ width: CGFloat, _ height: CGFloat) -> NSRect {
    NSRect(x: x, y: CGFloat(canvasHeight) - y - height, width: width, height: height)
}

func color(_ hex: UInt32, alpha: CGFloat = 1) -> NSColor {
    NSColor(
        calibratedRed: CGFloat((hex >> 16) & 0xff) / 255,
        green: CGFloat((hex >> 8) & 0xff) / 255,
        blue: CGFloat(hex & 0xff) / 255,
        alpha: alpha
    )
}

func font(_ size: CGFloat, weight: NSFont.Weight = .regular) -> NSFont {
    NSFont.systemFont(ofSize: size, weight: weight)
}

func drawText(
    _ value: String,
    x: CGFloat,
    y: CGFloat,
    width: CGFloat,
    height: CGFloat,
    size: CGFloat,
    weight: NSFont.Weight = .regular,
    textColor: NSColor = color(0x171d31),
    alignment: NSTextAlignment = .left,
    alpha: CGFloat = 1,
    lineHeight: CGFloat? = nil
) {
    let paragraph = NSMutableParagraphStyle()
    paragraph.alignment = alignment
    paragraph.lineBreakMode = .byWordWrapping
    paragraph.minimumLineHeight = lineHeight ?? size * 1.18
    paragraph.maximumLineHeight = lineHeight ?? size * 1.18
    let attributes: [NSAttributedString.Key: Any] = [
        .font: font(size, weight: weight),
        .foregroundColor: textColor.withAlphaComponent(alpha),
        .paragraphStyle: paragraph,
        .kern: size > 30 ? -1.2 : 0.3
    ]
    NSAttributedString(string: value, attributes: attributes).draw(in: topRect(x, y, width, height))
}

func drawGradientBackground(time: Double) {
    let base = NSBezierPath(rect: NSRect(x: 0, y: 0, width: canvasWidth, height: canvasHeight))
    let shift = CGFloat((sin(time * 0.22) + 1) * 0.5)
    let gradient = NSGradient(colors: [
        color(0xf8f9ff),
        color(0xeff1fb),
        NSColor(calibratedRed: 0.92 + 0.025 * shift, green: 0.94, blue: 0.99, alpha: 1)
    ])!
    gradient.draw(in: base, angle: -82)

    NSGraphicsContext.current?.saveGraphicsState()
    let glowAlpha = 0.11 + 0.025 * CGFloat(sin(time * 0.7))
    color(0x6d5dfc, alpha: glowAlpha).setFill()
    NSBezierPath(ovalIn: topRect(-260 + CGFloat(sin(time * 0.3)) * 60, 40, 900, 900)).fill()
    color(0x36b9d6, alpha: 0.075).setFill()
    NSBezierPath(ovalIn: topRect(1390, 500 + CGFloat(cos(time * 0.25)) * 50, 700, 700)).fill()
    NSGraphicsContext.current?.restoreGraphicsState()

    let overlay = NSGradient(colors: [color(0xffffff, alpha: 0.02), color(0xdfe5f7, alpha: 0.18)])!
    overlay.draw(in: base, angle: -90)
}

func drawLogo(x: CGFloat, y: CGFloat, size: CGFloat, alpha: CGFloat = 1) {
    let rect = topRect(x, y, size, size)
    let path = NSBezierPath(roundedRect: rect, xRadius: size * 0.24, yRadius: size * 0.24)
    NSGraphicsContext.current?.saveGraphicsState()
    path.addClip()
    NSGradient(colors: [color(0x8a72ff, alpha: alpha), color(0x586df1, alpha: alpha), color(0x58b7ef, alpha: alpha)])!.draw(in: path, angle: -48)
    NSGraphicsContext.current?.restoreGraphicsState()

    let cx = rect.midX
    let cy = rect.midY
    let r = size * 0.29
    let sparkle = NSBezierPath()
    sparkle.move(to: NSPoint(x: cx, y: cy + r))
    sparkle.curve(to: NSPoint(x: cx + r, y: cy), controlPoint1: NSPoint(x: cx + r * 0.08, y: cy + r * 0.18), controlPoint2: NSPoint(x: cx + r * 0.82, y: cy + r * 0.08))
    sparkle.curve(to: NSPoint(x: cx, y: cy - r), controlPoint1: NSPoint(x: cx + r * 0.18, y: cy - r * 0.08), controlPoint2: NSPoint(x: cx + r * 0.08, y: cy - r * 0.82))
    sparkle.curve(to: NSPoint(x: cx - r, y: cy), controlPoint1: NSPoint(x: cx - r * 0.08, y: cy - r * 0.18), controlPoint2: NSPoint(x: cx - r * 0.82, y: cy - r * 0.08))
    sparkle.curve(to: NSPoint(x: cx, y: cy + r), controlPoint1: NSPoint(x: cx - r * 0.18, y: cy + r * 0.08), controlPoint2: NSPoint(x: cx - r * 0.08, y: cy + r * 0.82))
    color(0xffffff, alpha: alpha).setFill()
    sparkle.fill()
}

func drawPill(_ value: String, x: CGFloat, y: CGFloat, width: CGFloat, alpha: CGFloat) {
    let rect = topRect(x, y, width, 78)
    color(0x322b63, alpha: 0.075 * alpha).setFill()
    NSBezierPath(roundedRect: rect, xRadius: 39, yRadius: 39).fill()
    drawText(value, x: x, y: y + 20, width: width, height: 44, size: 29, weight: .medium, textColor: color(0x4d526b), alignment: .center, alpha: alpha)
}

func drawScreenshot(_ image: NSImage, time: Double, start: Double, x: CGFloat, y: CGFloat, width: CGFloat, height: CGFloat, alpha: CGFloat) {
    let appear = rangeProgress(time, start, start + 0.8)
    let scale = CGFloat(0.91 + 0.09 * appear)
    let w = width * scale
    let h = height * scale
    let rect = topRect(x + (width - w) / 2, y + (height - h) / 2, w, h)

    NSGraphicsContext.current?.saveGraphicsState()
    let shadow = NSShadow()
    shadow.shadowColor = color(0x59628f, alpha: 0.22 * alpha)
    shadow.shadowBlurRadius = 52
    shadow.shadowOffset = NSSize(width: 0, height: -18)
    shadow.set()
    color(0xffffff, alpha: alpha).setFill()
    NSBezierPath(roundedRect: rect, xRadius: 34, yRadius: 34).fill()
    NSGraphicsContext.current?.restoreGraphicsState()

    NSGraphicsContext.current?.saveGraphicsState()
    NSBezierPath(roundedRect: rect, xRadius: 34, yRadius: 34).addClip()
    image.draw(in: rect, from: .zero, operation: .sourceOver, fraction: alpha, respectFlipped: false, hints: [.interpolation: NSImageInterpolation.high])
    NSGraphicsContext.current?.restoreGraphicsState()

    color(0x59628f, alpha: 0.16 * alpha).setStroke()
    let border = NSBezierPath(roundedRect: rect, xRadius: 34, yRadius: 34)
    border.lineWidth = 2
    border.stroke()
}

func drawFrame(time: Double) {
    drawGradientBackground(time: time)

    // Opening hook.
    let a0 = CGFloat(sceneAlpha(time, 0, 4.6))
    if a0 > 0 {
        let rise = CGFloat(32 * (1 - rangeProgress(time, 0, 0.8)))
        drawText("CODEX · USAGE", x: 160, y: 205 + rise, width: 1600, height: 60, size: 28, weight: .bold, textColor: color(0x675cc8), alignment: .center, alpha: a0)
        drawText("还在反复打开 Codex 看用量？", x: 120, y: 350 + rise, width: 1680, height: 130, size: 92, weight: .bold, alignment: .center, alpha: a0)
        drawText("额度、重置时间、今日消耗……", x: 180, y: 535, width: 1560, height: 70, size: 38, textColor: color(0x6e748b), alignment: .center, alpha: a0)
    }

    // Product reveal with a live app capture.
    let a1 = CGFloat(sceneAlpha(time, 4.1, 9.0))
    if a1 > 0 {
        let p = CGFloat(rangeProgress(time, 4.1, 5.0))
        drawLogo(x: 145, y: 155 + 24 * (1 - p), size: 150, alpha: a1)
        drawText("Codex Meter", x: 145, y: 355, width: 930, height: 120, size: 92, weight: .bold, alpha: a1)
        drawText("一个浮窗，全都知道。", x: 145, y: 505, width: 920, height: 75, size: 46, weight: .medium, textColor: color(0x444a63), alpha: a1)
        drawText("专注创作，用量一目了然。", x: 145, y: 600, width: 920, height: 70, size: 34, textColor: color(0x74798f), alpha: a1)
        drawPill("悬浮桌面 · 随时可见", x: 145, y: 735, width: 480, alpha: a1)
        drawScreenshot(lightShot, time: time, start: 4.4, x: 1205, y: 85, width: 620, height: 787, alpha: a1)
    }

    // Core limits.
    let a2 = CGFloat(sceneAlpha(time, 8.5, 13.4))
    if a2 > 0 {
        drawText("每一份额度，心里有数", x: 130, y: 205, width: 900, height: 110, size: 72, weight: .bold, alpha: a2)
        drawText("实时查看剩余额度与刷新时间", x: 130, y: 345, width: 900, height: 70, size: 38, textColor: color(0x686e85), alpha: a2)
        let p1 = CGFloat(rangeProgress(time, 9.2, 9.9)) * a2
        let p2 = CGFloat(rangeProgress(time, 9.7, 10.4)) * a2
        let p3 = CGFloat(rangeProgress(time, 10.2, 10.9)) * a2
        drawPill("5 小时额度", x: 130, y: 545, width: 280, alpha: p1)
        drawPill("每周额度", x: 440, y: 545, width: 270, alpha: p2)
        drawPill("重置倒计时", x: 740, y: 545, width: 300, alpha: p3)
        drawText("低额度自动变色提醒", x: 130, y: 700, width: 900, height: 65, size: 34, weight: .medium, textColor: color(0x675be8), alpha: a2)
        drawScreenshot(darkShot, time: time, start: 8.8, x: 1205, y: 85, width: 620, height: 787, alpha: a2)
    }

    // Reset credit flow. The live capture stops at the confirmation dialog.
    let a3 = CGFloat(sceneAlpha(time, 12.9, 18.0))
    if a3 > 0 {
        drawText("额度不够？", x: 130, y: 185, width: 850, height: 110, size: 78, weight: .bold, alpha: a3)
        drawText("可用重置次数，一键发起", x: 130, y: 320, width: 860, height: 85, size: 48, weight: .semibold, textColor: color(0x3e445c), alpha: a3)
        drawText("只有明确确认后才会执行\n不会在后台自动消耗", x: 130, y: 470, width: 850, height: 150, size: 38, textColor: color(0x6e748b), alpha: a3, lineHeight: 58)
        let safeAlpha = CGFloat(rangeProgress(time, 14.0, 14.8)) * a3
        drawPill("确认后执行", x: 130, y: 705, width: 280, alpha: safeAlpha)
        drawPill("支持取消", x: 445, y: 705, width: 250, alpha: safeAlpha)
        drawScreenshot(resetShot, time: time, start: 13.2, x: 1135, y: 80, width: 650, height: 825, alpha: a3)
    }

    // Today's conversations.
    let a4 = CGFloat(sceneAlpha(time, 17.5, 22.6))
    if a4 > 0 {
        drawScreenshot(conversationsShot, time: time, start: 17.8, x: 120, y: 80, width: 650, height: 825, alpha: a4)
        drawText("今天聊了什么，\n一眼回顾", x: 900, y: 190, width: 890, height: 210, size: 76, weight: .bold, alpha: a4, lineHeight: 92)
        drawText("按任务查看对话轮次与 Token", x: 900, y: 455, width: 900, height: 80, size: 42, weight: .medium, textColor: color(0x555b73), alpha: a4)
        let c1 = CGFloat(rangeProgress(time, 18.8, 19.5)) * a4
        let c2 = CGFloat(rangeProgress(time, 19.3, 20.0)) * a4
        let c3 = CGFloat(rangeProgress(time, 19.8, 20.5)) * a4
        drawPill("任务名称", x: 900, y: 630, width: 250, alpha: c1)
        drawPill("对话轮次", x: 1180, y: 630, width: 250, alpha: c2)
        drawPill("Token 用量", x: 1460, y: 630, width: 280, alpha: c3)
    }

    // Seven-day token history.
    let a5 = CGFloat(sceneAlpha(time, 22.1, 27.2))
    if a5 > 0 {
        drawText("不只看今天", x: 130, y: 210, width: 850, height: 110, size: 76, weight: .bold, alpha: a5)
        drawText("最近 7 天趋势也清清楚楚", x: 130, y: 360, width: 900, height: 85, size: 46, weight: .semibold, textColor: color(0x444a63), alpha: a5)
        drawText("账户数据缺失时，本地记录自动补齐", x: 130, y: 505, width: 900, height: 70, size: 34, textColor: color(0x74798f), alpha: a5)
        let t1 = CGFloat(rangeProgress(time, 23.3, 24.0)) * a5
        let t2 = CGFloat(rangeProgress(time, 23.8, 24.5)) * a5
        drawPill("今日 Tokens", x: 130, y: 670, width: 300, alpha: t1)
        drawPill("最近 7 天趋势", x: 465, y: 670, width: 350, alpha: t2)
        drawScreenshot(tokensShot, time: time, start: 22.4, x: 1135, y: 80, width: 650, height: 825, alpha: a5)
    }

    // CTA and trust message.
    let a6 = CGFloat(sceneAlpha(time, 26.7, 32.0, fade: 0.6))
    if a6 > 0 {
        let p = CGFloat(rangeProgress(time, 26.7, 27.7))
        drawLogo(x: 150, y: 195 + 30 * (1 - p), size: 210, alpha: a6)
        drawText("Codex Meter", x: 420, y: 190, width: 1250, height: 130, size: 104, weight: .bold, alpha: a6)
        drawText("专注创作，用量一目了然。", x: 420, y: 355, width: 1250, height: 90, size: 52, weight: .semibold, textColor: color(0x444a63), alpha: a6)
        let featureAlpha = CGFloat(rangeProgress(time, 27.5, 28.3)) * a6
        drawPill("本地处理", x: 420, y: 510, width: 250, alpha: featureAlpha)
        drawPill("macOS · Windows", x: 705, y: 510, width: 340, alpha: featureAlpha)
        drawPill("开源 · MIT", x: 1080, y: 510, width: 270, alpha: featureAlpha)
        let buttonAlpha = CGFloat(rangeProgress(time, 28.1, 28.9)) * a6
        let buttonRect = topRect(420, 685, 1030, 112)
        NSGradient(colors: [color(0x806bfa, alpha: buttonAlpha), color(0x5b9af5, alpha: buttonAlpha)])!.draw(in: NSBezierPath(roundedRect: buttonRect, xRadius: 56, yRadius: 56), angle: -12)
        drawText("github.com/Liz-509/Codex-Meter", x: 420, y: 718, width: 1030, height: 50, size: 34, weight: .semibold, textColor: color(0xffffff), alignment: .center, alpha: buttonAlpha)
        drawText("现在就下载", x: 420, y: 835, width: 1030, height: 65, size: 36, weight: .medium, textColor: color(0x555b73), alignment: .center, alpha: buttonAlpha)
    }
}

func makeContext(for pixelBuffer: CVPixelBuffer) -> CGContext? {
    guard let baseAddress = CVPixelBufferGetBaseAddress(pixelBuffer) else { return nil }
    return CGContext(
        data: baseAddress,
        width: canvasWidth,
        height: canvasHeight,
        bitsPerComponent: 8,
        bytesPerRow: CVPixelBufferGetBytesPerRow(pixelBuffer),
        space: CGColorSpaceCreateDeviceRGB(),
        bitmapInfo: CGBitmapInfo.byteOrder32Little.rawValue | CGImageAlphaInfo.premultipliedFirst.rawValue
    )
}

func savePreview(_ image: CGImage, frame: Int, directory: URL) {
    let bitmap = NSBitmapImageRep(cgImage: image)
    guard let data = bitmap.representation(using: .png, properties: [:]) else { return }
    let seconds = Double(frame) / Double(fps)
    let filename = String(format: "preview-%04.1fs.png", seconds)
    try? data.write(to: directory.appendingPathComponent(filename))
}

let writer = try AVAssetWriter(outputURL: outputURL, fileType: .mp4)
let outputSettings: [String: Any] = [
    AVVideoCodecKey: AVVideoCodecType.h264,
    AVVideoWidthKey: canvasWidth,
    AVVideoHeightKey: canvasHeight,
    AVVideoCompressionPropertiesKey: [
        AVVideoAverageBitRateKey: 8_000_000,
        AVVideoProfileLevelKey: AVVideoProfileLevelH264HighAutoLevel,
        AVVideoMaxKeyFrameIntervalKey: Int(fps * 2)
    ]
]
let input = AVAssetWriterInput(mediaType: .video, outputSettings: outputSettings)
input.expectsMediaDataInRealTime = false
let adaptor = AVAssetWriterInputPixelBufferAdaptor(
    assetWriterInput: input,
    sourcePixelBufferAttributes: [
        kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
        kCVPixelBufferWidthKey as String: canvasWidth,
        kCVPixelBufferHeightKey as String: canvasHeight,
        kCVPixelBufferCGImageCompatibilityKey as String: true,
        kCVPixelBufferCGBitmapContextCompatibilityKey as String: true
    ]
)

guard writer.canAdd(input) else {
    fputs("Cannot add video input.\n", stderr)
    exit(2)
}
writer.add(input)
guard writer.startWriting() else {
    fputs("Could not start writer: \(writer.error?.localizedDescription ?? "unknown error")\n", stderr)
    exit(2)
}
writer.startSession(atSourceTime: .zero)

let previewFrames = Set([45, 165, 300, 450, 600, 735, 870])

for frame in 0..<frameCount {
    autoreleasepool {
        while !input.isReadyForMoreMediaData {
            Thread.sleep(forTimeInterval: 0.002)
        }
        guard let pool = adaptor.pixelBufferPool else {
            fputs("Pixel buffer pool unavailable.\n", stderr)
            return
        }
        var maybeBuffer: CVPixelBuffer?
        let result = CVPixelBufferPoolCreatePixelBuffer(nil, pool, &maybeBuffer)
        guard result == kCVReturnSuccess, let pixelBuffer = maybeBuffer else {
            fputs("Could not allocate pixel buffer.\n", stderr)
            return
        }

        CVPixelBufferLockBaseAddress(pixelBuffer, [])
        if let context = makeContext(for: pixelBuffer) {
            let graphicsContext = NSGraphicsContext(cgContext: context, flipped: false)
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = graphicsContext
            drawFrame(time: Double(frame) / Double(fps))
            if let previewDirectory, previewFrames.contains(frame), let image = context.makeImage() {
                savePreview(image, frame: frame, directory: previewDirectory)
            }
            NSGraphicsContext.restoreGraphicsState()
        }
        CVPixelBufferUnlockBaseAddress(pixelBuffer, [])

        let presentationTime = CMTime(value: Int64(frame), timescale: fps)
        if !adaptor.append(pixelBuffer, withPresentationTime: presentationTime) {
            fputs("Failed to append frame \(frame): \(writer.error?.localizedDescription ?? "unknown error")\n", stderr)
        }
        if frame % Int(fps * 2) == 0 {
            let percent = Int(Double(frame) / Double(frameCount) * 100)
            print("Rendering \(percent)%")
        }
    }
}

input.markAsFinished()
let semaphore = DispatchSemaphore(value: 0)
writer.finishWriting {
    semaphore.signal()
}
semaphore.wait()

guard writer.status == .completed else {
    fputs("Video export failed: \(writer.error?.localizedDescription ?? "unknown error")\n", stderr)
    exit(1)
}

print("Created \(outputURL.path)")
