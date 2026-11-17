#!/usr/bin/env python3
"""Makes Savefile's own "clip saved" sound: src/Cnema/Assets/Sounds/save.wav.

An original retro chiptune jingle, synthesized here from scratch the way old sound chips did it: band-limited
pulse waves (12.5/25/50 % duty), a triangle bass, volume that steps 60 times a second, a little vibrato and a
soft echo. It belongs to Savefile and plays the same on every computer, whatever the OS sound scheme says.

    python3 scripts/make-save-sound.py            # writes the chosen variant to the app
    python3 scripts/make-save-sound.py --all DIR  # writes every variant to DIR, to compare them

Only the standard library.
"""
import math
import os
import struct
import sys
import wave

RATE = 44100
CHOSEN = "arp"


def hz(midi: float) -> float:
    return 440.0 * 2 ** ((midi - 69) / 12)


def blep(t: float, dt: float) -> float:
    """PolyBLEP correction: takes the harsh aliasing off the pulse edges, so it sounds clean, not fizzy."""
    if t < dt:
        t /= dt
        return t + t - t * t - 1
    if t > 1 - dt:
        t = (t - 1) / dt
        return t * t + t + t + 1
    return 0.0


class Voice:
    """One sound-chip channel: plays notes as (start s, length s, midi, volume 0..1, duty or 'tri')."""

    def __init__(self):
        self.notes = []

    def note(self, start, length, midi, vol, wave_="pulse", duty=0.25, decay=0.0, vibrato=0.0, slide=0.0):
        self.notes.append(dict(start=start, length=length, midi=midi, vol=vol, wave=wave_, duty=duty,
                               decay=decay, vibrato=vibrato, slide=slide))
        return self

    def render(self, out):
        for n in self.notes:
            phase = 0.0
            first = int(n["start"] * RATE)
            count = int(n["length"] * RATE)
            for i in range(count):
                t = i / RATE
                if first + i >= len(out):
                    break
                midi = n["midi"] + n["slide"] * min(1.0, t / 0.06)          # quick glide into the note
                if n["vibrato"] and t > 0.07:
                    midi += n["vibrato"] * math.sin(2 * math.pi * 6.0 * (t - 0.07))
                f = hz(midi)
                dt = f / RATE
                phase += dt
                phase -= math.floor(phase)
                if n["wave"] == "tri":
                    s = 4 * abs(phase - 0.5) - 1
                    s = round(s * 7.5) / 7.5                                   # 4-bit stepped triangle
                else:
                    d = n["duty"]
                    s = (1.0 if phase < d else -1.0) + blep(phase, dt) - blep((phase - d) % 1.0, dt)
                    s -= 2 * d - 1                                             # keep it centred
                # Chip-style volume: steps at 60 Hz, 16 levels, decaying; tiny ramps at the ends, no clicks.
                frame = math.floor(t * 60) / 60
                env = n["vol"] * (math.exp(-frame / n["decay"]) if n["decay"] else 1.0)
                env = math.floor(env * 15 + 0.5) / 15
                edge = min(1.0, t / 0.002, (n["length"] - t) / 0.006)
                out[first + i] += s * env * max(0.0, edge)


# ------------------------------------------------------------------- the variants
def arp():
    """Save crystal: a quick rising G-major arpeggio that lands on a held, shimmering top note, over a bass."""
    lead = Voice()
    for k, m in enumerate([79, 83, 86]):                                     # G5 B5 D6
        lead.note(k * 0.055, 0.05, m, 0.55, duty=0.25)
    lead.note(0.165, 0.42, 91, 0.6, duty=0.25, decay=0.16, vibrato=0.18)     # G6, held
    harm = Voice().note(0.165, 0.38, 86, 0.22, duty=0.125, decay=0.14)       # D6 under it, thin
    bass = Voice().note(0.0, 0.16, 55, 0.5, "tri").note(0.165, 0.3, 67, 0.5, "tri", decay=0.18)
    return [lead, harm, bass], 0.62


def chord():
    """Power-up: a one-octave pitch sweep, then a fast chip "chord" (arpeggio at 30 Hz) that fades out."""
    lead = Voice().note(0.0, 0.07, 74, 0.5, duty=0.5, slide=12)               # D5 sweeping up to D6
    chordv = Voice()
    t, notes, k = 0.08, [86, 90, 93, 98], 0                                  # D6 F#6 A6 D7
    while t < 0.5:
        chordv.note(t, 1 / 30, notes[k % 4], 0.55 * math.exp(-(t - 0.08) / 0.18), duty=0.25)
        t += 1 / 30
        k += 1
    bass = Voice().note(0.08, 0.32, 62, 0.5, "tri", decay=0.2)
    return [lead, chordv, bass], 0.6


def twostep():
    """Checkpoint: two bright notes a fourth apart, then a little sparkle trill high up."""
    lead = Voice().note(0.0, 0.08, 84, 0.55, duty=0.5).note(0.09, 0.3, 89, 0.6, duty=0.25, decay=0.15, vibrato=0.15)
    sparkle = Voice()
    for k, m in enumerate([101, 105, 108, 105, 108, 113]):
        sparkle.note(0.2 + k * 0.035, 0.033, m, 0.2 * (1 - k / 7), duty=0.125)
    bass = Voice().note(0.0, 0.08, 60, 0.45, "tri").note(0.09, 0.25, 65, 0.45, "tri", decay=0.15)
    return [lead, sparkle, bass], 0.6


VARIANTS = {"arp": arp, "chord": chord, "twostep": twostep}


def render(name: str) -> list:
    voices, length = VARIANTS[name]()
    out = [0.0] * int(RATE * length)
    for v in voices:
        v.render(out)
    # Soft echo (one quieter repeat), like the delay trick chip composers used; then one gentle low-pass so
    # the square waves stay warm instead of piercing over a game.
    delay, wet = int(RATE * 0.11), 0.28
    for i in range(len(out) - 1, delay - 1, -1):
        out[i] += wet * out[i - delay]
    y, a = 0.0, 1 - math.exp(-2 * math.pi * 7000 / RATE)
    for i, x in enumerate(out):
        y += a * (x - y)
        out[i] = y
    fade = int(RATE * 0.05)
    for i in range(fade):
        out[len(out) - fade + i] *= 0.5 * (1 + math.cos(math.pi * i / fade))
    peak = max(abs(x) for x in out) or 1.0
    gain = 10 ** (-3 / 20) / peak                                             # peak at -3 dBFS
    return [x * gain for x in out]


def write(path: str, samples: list) -> None:
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(b"".join(struct.pack("<h", int(round(x * 32767))) for x in samples))
    print(os.path.normpath(path))


def main() -> None:
    if len(sys.argv) == 3 and sys.argv[1] == "--all":
        for name in VARIANTS:
            write(os.path.join(sys.argv[2], f"save-{name}.wav"), render(name))
        return
    here = os.path.dirname(__file__)
    write(os.path.join(here, "..", "src", "Cnema", "Assets", "Sounds", "save.wav"), render(CHOSEN))


if __name__ == "__main__":
    main()
