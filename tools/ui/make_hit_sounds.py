#!/usr/bin/env python3
"""Writes the hit-confirmation sounds the hitmarker plays (owner's run of 2026-10-10, phase P38).

    python tools/ui/make_hit_sounds.py

Into ``Ironfront_Reborn/Assets/Resources/IronfrontUi`` as ``hit-<name>.wav``, where ``IngameUi``
loads them. A body hit keeps the HUD's authored tick; these mark what a body hit is not:

- headshot: a bright metal "dink", the helmet ring every shooter teaches its players to hear
- kill: a deep thump under a dry crack, the round that finished it
- headshot-kill: the dink, then the thump, so the two read as one shot

44.1 kHz mono 16-bit, synthesised rather than sampled so the game ships no third-party audio, and
deterministic (fixed seeds): re-running it changes nothing. Short on purpose -- an automatic weapon
can land ten of these a second, and a long tail would smear them together.
"""
import os
import wave

import numpy as np

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
FOLDER = os.path.join(ROOT, "Ironfront_Reborn", "Assets", "Resources", "IronfrontUi")
RATE = 44100

# Peak level of every clip: the authored body tick sits near this, so a headshot is not quieter
# than the hit it outranks.
PEAK = 0.8


def timeline(length):
    return np.arange(int(RATE * length)) / RATE


def ping(t, start, partials, decay, gain):
    """Struck metal: inharmonic partials, a one-millisecond attack, each partial dying faster the
    higher it rings."""
    local = t - start
    on = local >= 0
    attack = 1 - np.exp(-np.maximum(local, 0) * 900)
    tone = np.zeros_like(t)
    for freq, level in partials:
        ring = np.exp(-np.maximum(local, 0) * decay * (freq / partials[0][0]) ** 0.6)
        tone += level * np.sin(2 * np.pi * freq * np.maximum(local, 0)) * ring
    return gain * np.where(on, attack * tone, 0.0)


def click(t, start, rng, length, gain, smooth=1):
    """A short burst of noise, high-passed by subtracting its own moving average."""
    noise = rng.standard_normal(t.size)
    if smooth > 1:
        noise = noise - np.convolve(noise, np.ones(smooth) / smooth, mode="same")
    local = t - start
    env = np.where((local >= 0), np.exp(-np.maximum(local, 0) / max(length, 1e-4)), 0.0)
    return gain * env * noise


def band(t, start, rng, low_smooth, high_smooth, length, gain):
    """Noise between two moving-average corners: a dry crack rather than a hiss."""
    noise = rng.standard_normal(t.size)
    low = np.convolve(noise, np.ones(low_smooth) / low_smooth, mode="same")
    high = np.convolve(noise, np.ones(high_smooth) / high_smooth, mode="same")
    local = t - start
    env = np.where(local >= 0, np.exp(-np.maximum(local, 0) / length), 0.0)
    return gain * env * (high - low)


def thump(t, start, gain):
    """A body blow: a tone falling from 200 Hz to 70 Hz, gone in a fifth of a second, with its
    second harmonic so a laptop's small speakers still carry it."""
    local = np.maximum(t - start, 0)
    on = t >= start
    f = 70 + 130 * np.exp(-local * 22)
    phase = 2 * np.pi * np.cumsum(np.where(on, f, 0.0)) / RATE
    env = (1 - np.exp(-local * 400)) * np.exp(-local * 14)
    tone = np.sin(phase) + 0.45 * np.sin(2 * phase) * np.exp(-local * 10)
    return gain * np.where(on, tone * env, 0.0)


def finish(mix, t, length, tail=0.05):
    mix = mix * np.clip((length - t) / tail, 0, 1)
    return mix * (PEAK / np.max(np.abs(mix)))


def write(name, mix):
    path = os.path.join(FOLDER, "hit-" + name + ".wav")
    pcm = (mix * 32767).astype("<i2")
    os.makedirs(FOLDER, exist_ok=True)
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(pcm.tobytes())
    print(f"wrote {path} ({mix.size / RATE:.2f} s)")


DINK = ((2380.0, 1.0), (3710.0, 0.55), (5290.0, 0.32), (7930.0, 0.14))


def headshot():
    length = 0.32
    t = timeline(length)
    rng = np.random.default_rng(20261010)
    mix = ping(t, 0.0, DINK, 15.0, 1.0) + click(t, 0.0, rng, 0.0018, 0.6, smooth=4)
    return finish(mix, t, length)


def kill():
    length = 0.42
    t = timeline(length)
    rng = np.random.default_rng(20261011)
    mix = (thump(t, 0.0, 1.0)
           + band(t, 0.0, rng, 40, 6, 0.022, 0.9)
           + click(t, 0.0, rng, 0.0025, 0.35, smooth=3))
    return finish(mix, t, length, tail=0.08)


def headshot_kill():
    length = 0.5
    t = timeline(length)
    rng = np.random.default_rng(20261012)
    mix = (ping(t, 0.0, DINK, 13.0, 0.85) + click(t, 0.0, rng, 0.0018, 0.5, smooth=4)
           + thump(t, 0.045, 1.0) + band(t, 0.045, rng, 40, 6, 0.022, 0.8))
    return finish(mix, t, length, tail=0.08)


def main():
    write("headshot", headshot())
    write("kill", kill())
    write("headshot-kill", headshot_kill())


if __name__ == "__main__":
    main()
