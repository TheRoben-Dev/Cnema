// cnema-audio: streams the Mac's system audio (what the speakers play) as raw 48 kHz stereo
// float32 PCM into the file or FIFO given as the only argument. Cnema's clipping service starts
// it and reads the FIFO with ffmpeg ("-f f32le -ar 48000 -ac 2").
//
// ffmpeg's avfoundation input only sees microphones; ScreenCaptureKit (macOS 13+) is Apple's
// supported way to record system audio. It runs under the Screen Recording permission Cnema
// already needs for clipping, and records nothing from Cnema itself.
import AVFoundation
import CoreMedia
import Foundation
import ScreenCaptureKit

let sampleRate = 48_000
let channels = 2

guard CommandLine.arguments.count == 2 else {
    FileHandle.standardError.write("usage: cnema-audio <output fifo>\n".data(using: .utf8)!)
    exit(2)
}
signal(SIGPIPE, SIG_IGN)

/// Serialises writes and fills gaps with silence: ScreenCaptureKit only delivers buffers while
/// something is playing, but ffmpeg needs an unbroken audio clock or the whole capture stalls.
final class PcmWriter {
    private let handle: FileHandle
    private let queue = DispatchQueue(label: "cnema.audio.writer")
    private let start = DispatchTime.now()
    private var framesWritten = 0
    private var timer: DispatchSourceTimer?

    init(handle: FileHandle) { self.handle = handle }

    func startSilenceFill() {
        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now(), repeating: .milliseconds(20))
        t.setEventHandler { [weak self] in self?.fillGap() }
        t.resume()
        timer = t
    }

    private var expectedFrames: Int {
        let ns = DispatchTime.now().uptimeNanoseconds - start.uptimeNanoseconds
        return Int(Double(ns) / 1e9 * Double(sampleRate))
    }

    private func fillGap() {
        let behind = expectedFrames - framesWritten
        guard behind > sampleRate / 10 else { return } // more than 100 ms without audio
        write(Data(count: behind * channels * MemoryLayout<Float>.size), frames: behind)
    }

    func append(_ data: Data, frames: Int) {
        queue.async { self.write(data, frames: frames) }
    }

    private func write(_ data: Data, frames: Int) {
        do {
            try handle.write(contentsOf: data)
            framesWritten += frames
        } catch {
            exit(0) // ffmpeg went away: capture stopped
        }
    }
}

final class AudioSink: NSObject, SCStreamOutput, SCStreamDelegate {
    let writer: PcmWriter
    init(writer: PcmWriter) { self.writer = writer }

    func stream(_ stream: SCStream, didOutputSampleBuffer buffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .audio, buffer.isValid,
              let format = buffer.formatDescription?.audioStreamBasicDescription else { return }
        let frames = buffer.numSamples
        let planar = format.mFormatFlags & kAudioFormatFlagIsNonInterleaved != 0
        let inChannels = Int(format.mChannelsPerFrame)
        guard frames > 0, inChannels > 0, format.mFormatFlags & kAudioFormatFlagIsFloat != 0 else { return }

        try? buffer.withAudioBufferList { list, _ in
            var out = [Float](repeating: 0, count: frames * channels)
            if planar {
                for c in 0..<channels {
                    let src = list[min(c, list.count - 1)]
                    guard let p = src.mData?.assumingMemoryBound(to: Float.self) else { continue }
                    for i in 0..<frames { out[i * channels + c] = p[i] }
                }
            } else if let p = list[0].mData?.assumingMemoryBound(to: Float.self) {
                for i in 0..<frames {
                    for c in 0..<channels { out[i * channels + c] = p[i * inChannels + min(c, inChannels - 1)] }
                }
            }
            writer.append(out.withUnsafeBufferPointer { Data(buffer: $0) }, frames: frames)
        }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        FileHandle.standardError.write("cnema-audio: stream stopped: \(error.localizedDescription)\n".data(using: .utf8)!)
        exit(1)
    }
}

var keepAlive: [AnyObject] = []

Task {
    do {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = content.displays.first else { throw NSError(domain: "cnema-audio", code: 1) }

        let config = SCStreamConfiguration()
        config.capturesAudio = true
        config.excludesCurrentProcessAudio = true
        config.sampleRate = sampleRate
        config.channelCount = channels
        // Video cannot be switched off entirely; ask for the least possible.
        config.width = 2
        config.height = 2
        config.minimumFrameInterval = CMTime(value: 1, timescale: 1)

        // Blocks until ffmpeg opens the FIFO for reading.
        guard let handle = FileHandle(forWritingAtPath: CommandLine.arguments[1]) else {
            throw NSError(domain: "cnema-audio", code: 2)
        }
        let writer = PcmWriter(handle: handle)
        let sink = AudioSink(writer: writer)
        let filter = SCContentFilter(display: display, excludingWindows: [])
        let stream = SCStream(filter: filter, configuration: config, delegate: sink)
        try stream.addStreamOutput(sink, type: .audio, sampleHandlerQueue: DispatchQueue(label: "cnema.audio.capture"))
        try await stream.startCapture()
        writer.startSilenceFill()
        keepAlive = [stream, sink, writer]
    } catch {
        FileHandle.standardError.write("cnema-audio: \(error.localizedDescription)\n".data(using: .utf8)!)
        exit(1)
    }
}
dispatchMain()
