#!/usr/bin/env python3
"""Writes the achievement sounds (achievements v2, docs/achievements.md section 5.2 item 7).

    python tools/ui/make_achievement_sound.py

One per metal, one for the disasters and one for a hidden achievement's reveal, into
``Ironfront_Reborn/Assets/Resources/IronfrontUi`` as ``achievement-<name>.wav``, where
``AchievementToast`` loads them. Remade for the owner's run of 2026-10-10 (task 6: "the sounds are
bland, nothing special"); each kind now has its own instrument and its own size:

- bronze: a coin's clink and two warm mallet notes
- silver: a crystal arpeggio over a soft pad, glinting
- gold: a brass "ta-da" over a timpani roll and a cymbal swell
- platinum: a low swell rising into a brass fanfare, a choir and crystal bells
- mythic: three war drums with a sub drop, then a choir and brass in A minor turning to A major
- hidden: a glitching riser and a thump, the file declassified; the metal's own sound follows it
- disaster (BULLET SPONGE, PARTICIPATION TROPHY, CANNON FODDER): a muted sad trombone that
  wobbles down and ends in a bonk

plus ``achievement-unlocked.wav``, the original chime, kept unchanged as the fallback. 44.1 kHz mono
16-bit, synthesised rather than sampled so the game ships no third-party audio; deterministic (fixed
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


def envelope(local, attack, duration, release):
    """Up over ``attack``, held, and down over ``release`` before ``duration`` ends; zero outside."""
    rise = np.clip(local / attack, 0, 1)
    fall = np.clip((duration - local) / release, 0, 1)
    return np.where((local >= 0) & (local <= duration), rise * fall, 0.0)


def bell(freq, start, duration, gain, t, bright=1.0):
    """A bell: a few inharmonic partials, a quick attack and an exponential ring-out."""
    local = t - start
    on = local >= 0
    env = np.where(on, (1 - np.exp(-local * 180)) * np.exp(-local * (3.2 / duration)), 0.0)
    partials = ((1.0, 1.0), (2.0, 0.42 * bright), (2.76, 0.2 * bright), (5.4, 0.07 * bright))
    tone = sum(level * np.sin(2 * np.pi * freq * ratio * local) for ratio, level in partials)
    return gain * env * tone


def mallet(freq, start, duration, gain, t):
    """A warm wooden mallet (marimba): a strong fundamental, a fourth partial, a soft knock."""
    local = t - start
    on = local >= 0
    env = np.where(on, (1 - np.exp(-local * 400)) * np.exp(-local * (4.0 / duration)), 0.0)
    knock = np.where(on, np.exp(-local * 160), 0.0)
    tone = np.sin(2 * np.pi * freq * local) + 0.25 * np.sin(2 * np.pi * freq * 4.0 * local) * knock
    return gain * env * tone


def coin(start, gain, t):
    """A coin's clink: two high, close inharmonic partials that ring briefly."""
    local = t - start
    on = local >= 0
    env = np.where(on, np.exp(-local * 22), 0.0)
    tone = np.sin(2 * np.pi * 3520 * local) + 0.7 * np.sin(2 * np.pi * 4699 * local) + 0.35 * np.sin(2 * np.pi * 7040 * local)
    return gain * env * tone


def brass(freq, start, duration, gain, t, vibrato=0.0, slide=0.0, mute=0.0):
    """A brass note: harmonics that brighten as it opens, optional vibrato, pitch slide and a
    plunger mute (``mute`` > 0 opens and closes the upper harmonics, "wah")."""
    local = t - start
    env = envelope(local, 0.035, duration, min(0.14, duration * 0.4))
    progress = np.clip(local / max(duration, 1e-3), 0, 1)
    wobble = 1 + vibrato * np.sin(2 * np.pi * 5.2 * np.maximum(local, 0)) * np.clip(local / 0.25, 0, 1)
    f = freq * (1 + slide * progress) * wobble
    phase = 2 * np.pi * np.cumsum(f) / RATE
    bright = 0.55 + 0.45 * env
    if mute > 0:
        bright = bright * (1 - mute * 0.5 * (1 + np.cos(np.pi * np.clip(local / max(duration, 1e-3), 0, 1) * 2)))
    tone = sum((bright ** (n - 1)) * np.sin(n * phase) / n for n in range(1, 9))
    return gain * env * tone


def pad(freqs, start, duration, gain, t, attack=0.3, release=0.6):
    """A soft string pad: each note as three detuned saw-ish voices, low-passed by its own harmonics."""
    local = t - start
    env = envelope(local, attack, duration, release)
    voice = np.zeros_like(t)
    for freq in freqs:
        for cents in (-8.0, 0.0, 9.0):
            f = freq * 2 ** (cents / 1200.0)
            for n in range(1, 7):
                voice += (0.6 ** n) * np.sin(2 * np.pi * f * n * np.maximum(local, 0))
    return gain * env * voice / (3 * len(freqs))


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


def drum(start, gain, t, rng, low=48.0, high=120.0):
    """A deep drum: a pitch that falls from ``high`` to ``low`` Hz, and a short skin slap."""
    local = t - start
    on = local >= 0
    f = low + (high - low) * np.exp(-np.maximum(local, 0) * 16)
    phase = 2 * np.pi * np.cumsum(np.where(on, f, 0.0)) / RATE
    body = np.where(on, np.sin(phase) * np.exp(-np.maximum(local, 0) * 6.5), 0.0)
    slap = np.where(on, rng.standard_normal(t.size) * np.exp(-np.maximum(local, 0) * 70), 0.0)
    return gain * (body + 0.25 * slap)


def timpani_roll(freq, start, duration, gain, t, rng):
    """A timpani roll: rapid soft strokes swelling to the end."""
    out = np.zeros_like(t)
    strokes = int(duration / 0.055)
    for i in range(strokes):
        s = start + i * 0.055 + rng.uniform(-0.006, 0.006)
        level = 0.35 + 0.65 * (i / max(strokes - 1, 1))
        out += level * drum(s, 1.0, t, rng, low=freq, high=freq * 1.4) * 0.5
    return gain * out


def noise_swell(start, duration, gain, t, rng, bright=True, reverse=False):
    """A cymbal swell (or, reversed, a riser): filtered noise that grows to its end."""
    noise = rng.standard_normal(t.size)
    noise = np.convolve(noise, np.ones(3) / 3, mode="same")
    if bright:
        noise = noise - np.convolve(noise, np.ones(9) / 9, mode="same")
    local = t - start
    shape = np.clip(local / duration, 0, 1) ** 2.2 if reverse else np.exp(-np.maximum(local, 0) * 2.5)
    env = np.where((local >= 0) & (local <= duration + (0 if reverse else 2.0)), shape, 0.0)
    return gain * env * noise


def sparkle(start, duration, gain, t, rng, base=2000.0, count=14):
    """Glints: short high pings scattered over ``duration``."""
    out = np.zeros_like(t)
    for _ in range(count):
        s = start + rng.uniform(0, duration)
        f = base * 2 ** rng.uniform(0, 1.6)
        out += bell(f, s, 0.25, 1.0, t, bright=0.4)
    return gain * out / count * 3


def sub_drop(start, gain, t):
    """A sub-bass drop: 60 Hz falling to 30 Hz over a second."""
    local = t - start
    on = local >= 0
    f = 30 + 30 * np.exp(-np.maximum(local, 0) * 3)
    phase = 2 * np.pi * np.cumsum(np.where(on, f, 0.0)) / RATE
    return gain * np.where(on, np.sin(phase) * np.exp(-np.maximum(local, 0) * 1.6), 0.0)


def glitch(start, duration, gain, t, rng):
    """Digital chirps: square-ish bursts at stepping pitches, crushed to a few bits."""
    out = np.zeros_like(t)
    steps = 9
    for i in range(steps):
        s = start + i * duration / steps
        local = t - s
        on = (local >= 0) & (local < duration / steps * 0.7)
        f = 400 * 2 ** rng.uniform(0, 3)
        out += np.where(on, np.sign(np.sin(2 * np.pi * f * local)), 0.0)
    crushed = np.round(out * 3) / 3
    return gain * crushed


def reverb(mix, seconds, wet, rng):
    """A room: the dry sound convolved with decaying noise (FFT, so long tails stay fast)."""
    n = int(RATE * seconds)
    ir = rng.standard_normal(n) * np.exp(-np.arange(n) / RATE * (6.9 / seconds))
    # Darkened: a real room swallows the highs first, and white noise left bright reads as hiss.
    ir = np.convolve(ir, np.ones(10) / 10, mode="same")
    ir[0] = 0.0
    size = 1 << int(np.ceil(np.log2(mix.size + n)))
    tail = np.fft.irfft(np.fft.rfft(mix, size) * np.fft.rfft(ir, size), size)[: mix.size]
    tail = tail / (np.max(np.abs(tail)) + 1e-9) * np.max(np.abs(mix))
    return mix * (1 - wet) + tail * wet


def finish(mix, t, length, tail=0.25, loudness=None):
    """Fades the tail and normalises to a 0.82 peak; ``loudness`` caps the RMS so the dense ones
    sit level with the sparse ones rather than blare over them."""
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
    length = 1.5
    t = timeline(length)
    rng = np.random.default_rng(20261101)
    mix = coin(0.0, 0.18, t) + coin(0.07, 0.1, t)
    mix = mix + mallet(783.99, 0.10, 0.7, 0.42, t) + mallet(1046.50, 0.24, 0.9, 0.46, t) + mallet(523.25, 0.24, 0.9, 0.16, t)
    return finish(reverb(mix, 0.9, 0.22, rng), t, length)


def silver():
    length = 1.9
    t = timeline(length)
    rng = np.random.default_rng(20261102)
    run = ((1318.51, 0.00), (1661.22, 0.07), (1975.53, 0.14), (2637.02, 0.22))
    mix = sum(bell(f, s, 0.9, 0.2, t, bright=0.6) for f, s in run)
    mix = mix + pad((329.63, 415.30, 493.88), 0.0, 1.4, 0.22, t, attack=0.15, release=0.5)
    mix = mix + sparkle(0.25, 1.0, 0.05, t, rng, base=3000.0)
    return finish(reverb(mix, 1.3, 0.3, rng), t, length, tail=0.35)


def gold():
    length = 2.5
    t = timeline(length)
    rng = np.random.default_rng(20261103)
    mix = timpani_roll(98.0, 0.0, 0.42, 0.35, t, rng) + drum(0.44, 0.5, t, rng, low=65.0, high=110.0)
    mix = mix + brass(392.00, 0.18, 0.12, 0.28, t) + brass(392.00, 0.31, 0.12, 0.28, t)
    for f in (523.25, 659.25, 783.99):
        mix = mix + brass(f, 0.44, 1.3, 0.22, t, vibrato=0.006)
    mix = mix + brass(130.81, 0.44, 1.3, 0.16, t)
    mix = mix + noise_swell(0.44, 1.6, 0.025, t, rng) + bell(1567.98, 0.46, 1.2, 0.08, t) + sparkle(0.5, 1.2, 0.04, t, rng)
    return finish(reverb(mix, 1.5, 0.28, rng), t, length, tail=0.45, loudness=0.17)


def platinum():
    length = 3.2
    t = timeline(length)
    rng = np.random.default_rng(20261104)
    mix = pad((65.41, 98.00, 130.81), 0.0, 0.9, 0.5, t, attack=0.7, release=0.25)
    mix = mix + noise_swell(0.0, 0.8, 0.04, t, rng, reverse=True)
    run = ((523.25, 0.80, 0.12), (659.25, 0.92, 0.12), (783.99, 1.04, 0.12), (1046.50, 1.16, 0.3))
    mix = mix + sum(brass(f, s, d, 0.26, t) for f, s, d in run)
    for f in (523.25, 659.25, 783.99, 1046.50):
        mix = mix + brass(f, 1.44, 1.5, 0.14, t, vibrato=0.005)
    mix = mix + choir((261.63, 329.63, 392.00, 523.25), 1.44, 1.6, 0.42, t)
    mix = mix + drum(1.44, 0.5, t, rng, low=55.0, high=100.0) + noise_swell(1.44, 2.0, 0.03, t, rng)
    mix = mix + bell(2093.00, 1.46, 1.6, 0.09, t) + bell(2637.02, 1.56, 1.4, 0.07, t) + sparkle(1.5, 1.4, 0.05, t, rng, base=2600.0)
    return finish(reverb(mix, 2.0, 0.32, rng), t, length, tail=0.5, loudness=0.16)


def mythic():
    length = 4.2
    t = timeline(length)
    rng = np.random.default_rng(20261105)
    mix = drum(0.00, 0.6, t, rng) + drum(0.34, 0.6, t, rng) + drum(0.68, 0.85, t, rng, low=40.0, high=110.0)
    mix = mix + sub_drop(0.68, 0.5, t) + noise_swell(0.0, 0.66, 0.03, t, rng, reverse=True)
    # A minor, then the third lifts: A major.
    mix = mix + choir((220.00, 261.63, 329.63, 440.00), 0.72, 1.2, 0.48, t)
    mix = mix + choir((220.00, 277.18, 329.63, 440.00), 1.86, 2.0, 0.55, t)
    mix = mix + brass(110.00, 0.70, 3.0, 0.14, t, vibrato=0.004) + brass(164.81, 1.86, 1.9, 0.1, t, vibrato=0.004)
    mix = mix + noise_swell(1.86, 2.2, 0.03, t, rng) + bell(880.00, 1.88, 2.0, 0.07, t) + bell(1760.00, 1.95, 1.8, 0.05, t)
    return finish(reverb(mix, 2.6, 0.35, rng), t, length, tail=0.7)


def hidden():
    """The file declassified: a glitching riser that cuts to a thump and a glassy minor chord."""
    length = 1.3
    t = timeline(length)
    rng = np.random.default_rng(20261106)
    mix = noise_swell(0.0, 0.45, 0.12, t, rng, reverse=True) + glitch(0.05, 0.4, 0.06, t, rng)
    mix = mix + drum(0.46, 0.55, t, rng, low=45.0, high=90.0)
    for f in (466.16, 554.37, 698.46):
        mix = mix + bell(f, 0.47, 0.8, 0.12, t, bright=0.8)
    return finish(reverb(mix, 0.9, 0.3, rng), t, length, tail=0.3)


def disaster():
    length = 2.8
    t = timeline(length)
    rng = np.random.default_rng(20261107)
    notes = ((233.08, 0.00, 0.42), (220.00, 0.48, 0.42), (207.65, 0.96, 0.42))
    mix = sum(brass(f, s, d, 0.34, t, vibrato=0.004, mute=0.7) for f, s, d in notes)
    mix = mix + brass(196.00, 1.44, 1.0, 0.36, t, vibrato=0.03, slide=-0.05, mute=0.5)
    # ...and a cartoon bonk on the way out.
    local = t - 2.5
    on = local >= 0
    f = 900 * np.exp(-np.maximum(local, 0) * 9) + 180
    bonk = np.where(on, np.sin(2 * np.pi * np.cumsum(np.where(on, f, 0.0)) / RATE) * np.exp(-np.maximum(local, 0) * 14), 0.0)
    mix = mix + 0.3 * bonk
    return finish(reverb(mix, 0.8, 0.15, rng), t, length, tail=0.15, loudness=0.16)


def main():
    write("unlocked", unlocked())
    for name, make in (("bronze", bronze), ("silver", silver), ("gold", gold), ("platinum", platinum),
                       ("mythic", mythic), ("hidden", hidden), ("disaster", disaster)):
        write(name, make())


if __name__ == "__main__":
    main()
