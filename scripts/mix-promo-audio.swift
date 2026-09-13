import AVFoundation
import Foundation

let arguments = CommandLine.arguments
guard arguments.count == 4 else {
    fputs("Usage: mix-promo-audio VIDEO AUDIO OUTPUT\n", stderr)
    exit(2)
}

let videoURL = URL(fileURLWithPath: arguments[1])
let audioURL = URL(fileURLWithPath: arguments[2])
let outputURL = URL(fileURLWithPath: arguments[3])
let videoAsset = AVURLAsset(url: videoURL)
let audioAsset = AVURLAsset(url: audioURL)

guard let sourceVideoTrack = videoAsset.tracks(withMediaType: .video).first,
      let sourceAudioTrack = audioAsset.tracks(withMediaType: .audio).first
else {
    fputs("Missing video or audio track.\n", stderr)
    exit(3)
}

let composition = AVMutableComposition()
guard let videoTrack = composition.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid),
      let audioTrack = composition.addMutableTrack(withMediaType: .audio, preferredTrackID: kCMPersistentTrackID_Invalid)
else {
    fputs("Could not create composition tracks.\n", stderr)
    exit(4)
}

let duration = videoAsset.duration
try videoTrack.insertTimeRange(CMTimeRange(start: .zero, duration: duration), of: sourceVideoTrack, at: .zero)
videoTrack.preferredTransform = sourceVideoTrack.preferredTransform
let audioDuration = CMTimeMinimum(duration, audioAsset.duration)
try audioTrack.insertTimeRange(CMTimeRange(start: .zero, duration: audioDuration), of: sourceAudioTrack, at: .zero)

try? FileManager.default.removeItem(at: outputURL)
try FileManager.default.createDirectory(at: outputURL.deletingLastPathComponent(), withIntermediateDirectories: true)

guard let exporter = AVAssetExportSession(asset: composition, presetName: AVAssetExportPresetPassthrough) else {
    fputs("Could not create export session.\n", stderr)
    exit(5)
}

exporter.outputURL = outputURL
exporter.outputFileType = .mp4
exporter.shouldOptimizeForNetworkUse = true

let semaphore = DispatchSemaphore(value: 0)
exporter.exportAsynchronously {
    semaphore.signal()
}
semaphore.wait()

guard exporter.status == .completed else {
    fputs("Audio mix failed: \(exporter.error?.localizedDescription ?? "unknown error")\n", stderr)
    exit(6)
}

let resultAsset = AVURLAsset(url: outputURL)
let resultVideoTracks = resultAsset.tracks(withMediaType: .video).count
let resultAudioTracks = resultAsset.tracks(withMediaType: .audio).count
let resultSeconds = CMTimeGetSeconds(resultAsset.duration)
print("Created \(outputURL.path)")
print(String(format: "Verified %.2fs, video tracks: %d, audio tracks: %d", resultSeconds, resultVideoTracks, resultAudioTracks))
