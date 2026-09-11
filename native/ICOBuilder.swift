import Foundation

@main
struct ICOBuilder {
    static func main() throws {
        guard CommandLine.arguments.count == 3 else {
            throw NSError(domain: "ICOBuilder", code: 1)
        }

        let directory = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
        let output = URL(fileURLWithPath: CommandLine.arguments[2])
        let entries: [(Int, String)] = [
            (16, "icon_16x16.png"),
            (32, "icon_32x32.png"),
            (64, "icon_32x32@2x.png"),
            (128, "icon_128x128.png"),
            (256, "icon_256x256.png")
        ]

        let images = try entries.map { size, filename in
            (size, try Data(contentsOf: directory.appendingPathComponent(filename)))
        }
        let directorySize = 6 + images.count * 16
        var offset = directorySize
        var result = Data()

        appendLittleEndian(UInt16(0), to: &result)
        appendLittleEndian(UInt16(1), to: &result)
        appendLittleEndian(UInt16(images.count), to: &result)

        for (size, image) in images {
            result.append(UInt8(size == 256 ? 0 : size))
            result.append(UInt8(size == 256 ? 0 : size))
            result.append(0)
            result.append(0)
            appendLittleEndian(UInt16(1), to: &result)
            appendLittleEndian(UInt16(32), to: &result)
            appendLittleEndian(UInt32(image.count), to: &result)
            appendLittleEndian(UInt32(offset), to: &result)
            offset += image.count
        }

        for (_, image) in images { result.append(image) }
        try result.write(to: output, options: .atomic)
    }

    private static func appendLittleEndian<T: FixedWidthInteger>(_ value: T, to data: inout Data) {
        var littleEndian = value.littleEndian
        withUnsafeBytes(of: &littleEndian) { data.append(contentsOf: $0) }
    }
}
