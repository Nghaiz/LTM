#!/usr/bin/env python3
"""Writes the achievement-unlocked chime (owner's list of 2026-10-09, item 4).

    python tools/ui/make_achievement_sound.py

A soft low swell under a bright rising arpeggio of bell tones, then a shimmer that rings out:
about 1.6 s, 44.1 kHz mono 16-bit, into
``Ironfront_Reborn/Assets/Resources/IronfrontUi/achievement-unlocked.wav``, where
``AchievementToast`` loads it. Synthesised rather than sampled so the game ships no third-party
audio; the output is deterministic (fixed seed), so re-running it changes nothing.
"""
import os
import wave

import numpy as np

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(ROOT, "Ironfront_Reborn", "Assets", "Resources", "IronfrontUi", "achievement-unlocked.wav")
RATE = 44100
LENGTH = 1.6


def bell(freq, start, duration, gain, t):
    """A bell-like tone: a few inharmonic partials, a quick attack and an exponential ring-out."""
    local = t - start
    on = local >= 0
    env = np.where(on, (1 - np.exp(-local * 180)) * np.exp(-local * (3.2 / duration)), 0.0)
    partials = ((1.0, 1.0), (2.0, 0.42), (2.76, 0.2), (5.4, 0.07))
    tone = sum(level * np.sin(2 * np.pi * freq * ratio * local) for ratio, level in partials)
    return gain * env * tone


def main():
    t = np.arange(int(RATE * LENGTH)) / RATE
    rng = np.random.default_rng(20261009)

    # The swell: a low fifth that rises in and fades, the "weight" under the chime.
    swell_env = np.sin(np.clip(t / 0.9, 0, 1) * np.pi) ** 2
    swell = 0.16 * swell_env * (np.sin(2 * np.pi * 164.81 * t) + 0.6 * np.sin(2 * np.pi * 246.94 * t))

    # E major, rising: E5, G#5, B5, then E6 held a little longer.
    notes = ((659.25, 0.00, 0.55), (830.61, 0.09, 0.55), (987.77, 0.18, 0.6), (1318.51, 0.29, 1.1))
    chime = sum(bell(f, s, d, 0.22, t) for f, s, d in notes)

    # The shimmer: bright, decaying high noise, band-limited by a crude moving-average high-pass.
    noise = rng.standard_normal(t.size)
    high = noise - np.convolve(noise, np.ones(6) / 6, mode="same")
    shimmer_env = np.where(t >= 0.3, np.exp(-(t - 0.3) * 4.5) * (1 - np.exp(-(t - 0.3) * 60)), 0.0)
    shimmer = 0.035 * shimmer_env * high

    mix = swell + chime + shimmer
    fade = np.clip((LENGTH - t) / 0.25, 0, 1)
    mix *= fade
    mix /= np.max(np.abs(mix)) / 0.82

    pcm = (mix * 32767).astype("<i2")
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with wave.open(OUT, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(pcm.tobytes())
    print(f"wrote {OUT} ({LENGTH:.1f} s)")


if __name__ == "__main__":
    main()
