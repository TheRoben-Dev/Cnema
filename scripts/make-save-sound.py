#!/usr/bin/env python3
"""Makes Savefile's own "clip saved" sound: src/Cnema/Assets/Sounds/save.wav.

A soft, rising two-note chime (G5 then D6, a perfect fifth) with a quiet sparkle on top. Original and
synthesized here from plain sine partials, so it belongs to Savefile and plays the same on every computer,
whatever the operating system's sound scheme says. Only standard library; run it again after changing it.
"""
import math
import os
import struct
import wave

RATE = 44100
LENGTH = 0.62  # seconds, tail included


def bell(t: float, freq: float, decay: float) -> float:
    """A soft bell: fundamental, a gentle octave and a faint inharmonic shimmer, quick attack, smooth decay."""
    if t < 0:
        return 0.0
    attack = min(1.0, t / 0.004)              # 4 ms: soft, but still a clear "tick" of an onset
    env = attack * math.exp(-t / decay)
    tone = (math.sin(2 * math.pi * freq * t)
            + 0.28 * math.exp(-t / (decay * 0.5)) * math.sin(2 * math.pi * freq * 2 * t)
            + 0.08 * math.exp(-t / (decay * 0.3)) * math.sin(2 * math.pi * freq * 2.76 * t))
    return env * tone


def sample(t: float) -> float:
    return (0.55 * bell(t, 783.99, 0.16)            # G5
            + 0.62 * bell(t - 0.085, 1174.66, 0.20)  # D6, a fifth up: "saved"
            + 0.10 * bell(t - 0.17, 2349.32, 0.08))  # D7, a faint sparkle


def main() -> None:
    n = int(RATE * LENGTH)
    raw = [sample(i / RATE) for i in range(n)]
    fade = int(RATE * 0.06)
    for i in range(fade):                            # no click at the end
        raw[n - fade + i] *= 0.5 * (1 + math.cos(math.pi * i / fade))
    peak = max(abs(x) for x in raw)
    gain = 10 ** (-3 / 20) / peak                     # peak at -3 dBFS: audible over a game, not harsh
    out = os.path.join(os.path.dirname(__file__), "..", "src", "Cnema", "Assets", "Sounds", "save.wav")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with wave.open(out, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(b"".join(struct.pack("<h", int(round(x * gain * 32767))) for x in raw))
    print(os.path.normpath(out))


if __name__ == "__main__":
    main()
