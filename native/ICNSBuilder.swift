import Foundation

@main
struct ICNSBuilder {
    static func main() throws {
        guard CommandLine.arguments.count == 3 else {
            throw NSError(domain: "ICNSBuilder", code: 1)
        }

        let directory = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
        let output = URL(fileURLWithPath: CommandLine.arguments[2])
        let entries: [(String, String)] = [
            ("icp4", "icon_16x16.png"),
            ("icp5", "icon_32x32.png"),
            ("icp6", "icon_32x32@2x.png"),
            ("ic07", "icon_128x128.png"),
            ("ic08", "icon_256x256.png"),
            ("ic09", "icon_512x512.png"),
            ("ic10", "icon_512x512@2x.png"),
            ("ic11", "icon_16x16@2x.png"),
            ("ic12", "icon_32x32@2x.png"),
            ("ic13", "icon_128x128@2x.png"),
            ("ic14", "icon_256x256@2x.png")
        ]

        var body = Data()
        for (type, filename) in entries {
            let image = try Data(contentsOf: directory.appendingPathComponent(filename))
            body.append(type.data(using: .ascii)!)
            appendBigEndian(UInt32(image.count + 8), to: &body)
            body.append(image)
        }

        var result = Data("icns".utf8)
        appendBigEndian(UInt32(body.count + 8), to: &result)
        result.append(body)
        try result.write(to: output, options: .atomic)
    }

    private static func appendBigEndian(_ value: UInt32, to data: inout Data) {
        var bigEndian = value.bigEndian
        withUnsafeBytes(of: &bigEndian) { data.append(contentsOf: $0) }
    }
}
