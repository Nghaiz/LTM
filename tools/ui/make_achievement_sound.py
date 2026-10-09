#!/usr/bin/env python3
"""Writes the achievement sounds (achievements v2, docs/achievements.md section 5.2 item 7).

    python tools/ui/make_achievement_sound.py

One per metal and one for the three disasters, into ``Ironfront_Reborn/Assets/Resources/IronfrontUi``
as ``achievement-<name>.wav``, where ``AchievementToast`` loads them:

- bronze: a single bell
- silver: two rising bells
- gold: three brass notes
- platinum: a short brass fanfare over bells
- mythic: three deep drums, then a short choir chord
- disaster (BULLET SPONGE, PARTICIPATION TROPHY, CANNON FODDER): a sad trombone, "wah wah wah waaah"

plus ``achievement-unlocked.wav``, the original chime, kept as the fallback. 44.1 kHz mono 16-bit,
synthesised rather than sampled so the game ships no third-party audio; deterministic (fixed
seeds), so re-running it changes nothing.
"""
import os
import wave

import numpy as np

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
FOLDER = os.path.join(ROOT, "Ironfront_Reborn", "Assets", "Resources", "IronfrontUi")
RATE = 44100


def timeline(length):
    return np.arange(int(RATE * length)) / RATE


def bell(freq, start, duration, gain, t):
    """A bell-like tone: a few inharmonic partials, a quick attack and an exponential ring-out."""
    local = t - start
    on = local >= 0
    env = np.where(on, (1 - np.exp(-local * 180)) * np.exp(-local * (3.2 / duration)), 0.0)
    partials = ((1.0, 1.0), (2.0, 0.42), (2.76, 0.2), (5.4, 0.07))
    tone = sum(level * np.sin(2 * np.pi * freq * ratio * local) for ratio, level in partials)
    return gain * env * tone


def envelope(local, attack, duration, release):
    """Up over ``attack``, held, and down over ``release`` before ``duration`` ends; zero outside."""
    rise = np.clip(local / attack, 0, 1)
    fall = np.clip((duration - local) / release, 0, 1)
    return np.where((local >= 0) & (local <= duration), rise * fall, 0.0)


def brass(freq, start, duration, gain, t, vibrato=0.0, slide=0.0):
    """A brass note: odd and even harmonics that brighten as it opens, optional vibrato and pitch slide."""
    local = t - start
    env = envelope(local, 0.035, duration, min(0.14, duration * 0.4))
    progress = np.clip(local / max(duration, 1e-3), 0, 1)
    wobble = 1 + vibrato * np.sin(2 * np.pi * 5.2 * np.maximum(local, 0)) * np.clip(local / 0.25, 0, 1)
    f = freq * (1 + slide * progress) * wobble
    phase = 2 * np.pi * np.cumsum(f) / RATE
    bright = 0.55 + 0.45 * env
    tone = sum((bright ** (n - 1)) * np.sin(n * phase) / n for n in range(1, 9))
    return gain * env * tone


def drum(start, gain, t, rng):
    """A deep drum: a pitch that falls from 120 Hz to 48 Hz, and a short skin slap."""
    local = t - start
    on = local >= 0
    f = 48 + 72 * np.exp(-np.maximum(local, 0) * 16)
    phase = 2 * np.pi * np.cumsum(np.where(on, f, 0.0)) / RATE
    body = np.where(on, np.sin(phase) * np.exp(-np.maximum(local, 0) * 6.5), 0.0)
    slap = np.where(on, rng.standard_normal(t.size) * np.exp(-np.maximum(local, 0) * 70), 0.0)
    return gain * (body + 0.25 * slap)


def choir(freqs, start, duration, gain, t):
    """A short "aah": each note sung by three slightly detuned voices with a soft swell."""
    local = t - start
    env = envelope(local, 0.32, duration, 0.7)
    voice = np.zeros_like(t)
    for freq in freqs:
        for cents in (-6.0, 0.0, 7.0):
            f = freq * 2 ** (cents / 1200.0)
            for n, level in ((1, 1.0), (2, 0.55), (3, 0.32), (4, 0.16), (5, 0.08)):
                voice += level * np.sin(2 * np.pi * f * n * np.maximum(local, 0))
    return gain * env * voice / (3 * len(freqs))


def shimmer(start, gain, t, rng, decay=4.5):
    """Bright, decaying high noise."""
    noise = rng.standard_normal(t.size)
    high = noise - np.convolve(noise, np.ones(6) / 6, mode="same")
    env = np.where(t >= start, np.exp(-(t - start) * decay) * (1 - np.exp(-(t - start) * 60)), 0.0)
    return gain * env * high


def finish(mix, t, length, tail=0.25, loudness=None):
    """Fades the tail and normalises to a 0.82 peak; brass is dense, so ``loudness`` caps its RMS
    to sit level with the bells rather than blare over them."""
    mix = mix * np.clip((length - t) / tail, 0, 1)
    scale = 0.82 / np.max(np.abs(mix))
    if loudness is not None:
        scale = min(scale, loudness / np.sqrt(np.mean(mix ** 2)))
    return mix * scale


def write(name, mix):
    path = os.path.join(FOLDER, "achievement-" + name + ".wav")
    pcm = (mix * 32767).astype("<i2")
    os.makedirs(FOLDER, exist_ok=True)
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(pcm.tobytes())
    print(f"wrote {path} ({mix.size / RATE:.1f} s)")


def unlocked():
    """The original chime (owner's list of 2026-10-09, item 4), unchanged: the fallback."""
    length = 1.6
    t = timeline(length)
    rng = np.random.default_rng(20261009)
    swell_env = np.sin(np.clip(t / 0.9, 0, 1) * np.pi) ** 2
    swell = 0.16 * swell_env * (np.sin(2 * np.pi * 164.81 * t) + 0.6 * np.sin(2 * np.pi * 246.94 * t))
    notes = ((659.25, 0.00, 0.55), (830.61, 0.09, 0.55), (987.77, 0.18, 0.6), (1318.51, 0.29, 1.1))
    chime = sum(bell(f, s, d, 0.22, t) for f, s, d in notes)
    noise = rng.standard_normal(t.size)
    high = noise - np.convolve(noise, np.ones(6) / 6, mode="same")
    shimmer_env = np.where(t >= 0.3, np.exp(-(t - 0.3) * 4.5) * (1 - np.exp(-(t - 0.3) * 60)), 0.0)
    mix = swell + chime + 0.035 * shimmer_env * high
    return finish(mix, t, length)


def bronze():
    length = 1.2
    t = timeline(length)
    mix = bell(1046.50, 0.0, 0.9, 0.3, t) + bell(523.25, 0.0, 0.9, 0.12, t)
    return finish(mix, t, length)


def silver():
    length = 1.4
    t = timeline(length)
    mix = bell(880.00, 0.0, 0.6, 0.26, t) + bell(1318.51, 0.14, 1.0, 0.28, t) + bell(659.25, 0.14, 1.0, 0.08, t)
    return finish(mix, t, length)


def gold():
    length = 1.8
    t = timeline(length)
    rng = np.random.default_rng(20261010)
    mix = (brass(392.00, 0.00, 0.15, 0.3, t) + brass(523.25, 0.17, 0.15, 0.3, t)
           + brass(659.25, 0.34, 0.95, 0.34, t, vibrato=0.006)
           + bell(1318.51, 0.36, 1.1, 0.08, t) + shimmer(0.36, 0.02, t, rng))
    return finish(mix, t, length, loudness=0.17)


def platinum():
    length = 2.3
    t = timeline(length)
    rng = np.random.default_rng(20261011)
    run = ((523.25, 0.00, 0.13), (659.25, 0.13, 0.13), (783.99, 0.26, 0.13), (1046.50, 0.39, 0.3))
    mix = sum(brass(f, s, d, 0.26, t) for f, s, d in run)
    for f in (523.25, 659.25, 783.99):
        mix = mix + brass(f, 0.66, 1.3, 0.16, t, vibrato=0.005)
    mix = mix + bell(1567.98, 0.66, 1.4, 0.08, t) + bell(2093.00, 0.72, 1.2, 0.05, t) + shimmer(0.66, 0.025, t, rng, 3.0)
    return finish(mix, t, length, tail=0.35, loudness=0.16)


def mythic():
    length = 3.4
    t = timeline(length)
    rng = np.random.default_rng(20261012)
    mix = drum(0.00, 0.55, t, rng) + drum(0.34, 0.55, t, rng) + drum(0.68, 0.75, t, rng)
    mix = mix + choir((220.00, 277.18, 329.63, 440.00), 0.72, 2.5, 0.5, t)
    mix = mix + brass(110.00, 0.70, 2.2, 0.12, t, vibrato=0.004)
    mix = mix + bell(880.00, 0.74, 1.8, 0.05, t) + shimmer(0.74, 0.02, t, rng, 2.2)
    return finish(mix, t, length, tail=0.5)


def disaster():
    length = 2.5
    t = timeline(length)
    notes = ((233.08, 0.00, 0.42), (220.00, 0.48, 0.42), (207.65, 0.96, 0.42))
    mix = sum(brass(f, s, d, 0.34, t, vibrato=0.004) for f, s, d in notes)
    mix = mix + brass(196.00, 1.44, 1.0, 0.36, t, vibrato=0.022, slide=-0.035)
    return finish(mix, t, length, tail=0.3, loudness=0.16)


def main():
    write("unlocked", unlocked())
    for name, make in (("bronze", bronze), ("silver", silver), ("gold", gold), ("platinum", platinum),
                       ("mythic", mythic), ("disaster", disaster)):
        write(name, make())


if __name__ == "__main__":
    main()
