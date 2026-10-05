"""Regenerates Ironfront_Reborn/Assets/AudioClip/quadbike_horn.wav, the motorbike's horn.

Why a generator: the original game has one horn recording, the jeep's (jeep_horn.ogg, a ~451 Hz
car horn), and the owner wants the motorbike to sound different from it. The first motorbike clip
(#531) was a thin sine-like "meep" whose pitch slid up 468 -> 500 Hz through each beep and whose
second beep stopped dead with no release: it sounded like a horn running out of air (owner report
2026-10-05). This one models what a motorbike actually carries, an electric disc horn:

- a steady pitch, higher than the jeep's (510 Hz against ~451 Hz), so the two never blur;
- the buzzy, harmonic-rich wave a vibrating diaphragm makes, not a sine, with the disc's two
  resonances lifting it around 2.0 and 3.6 kHz;
- a fast attack, a short clean release, and the "bip-biip" double tap a rider gives.

Usage (from the repo root):  python tools/generate_quadbike_horn.py
Needs numpy and scipy. Writes in place; the .meta file, and so the prefab reference, is untouched.
Deterministic: the jitter comes from a fixed seed, so a re-run writes the same bytes.
"""

from pathlib import Path

import numpy as np
from scipy.io import wavfile
from scipy.signal import butter, sosfilt

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "Ironfront_Reborn" / "Assets" / "AudioClip" / "quadbike_horn.wav"

RATE = 44100
PITCH_HZ = 510.0          # the jeep's horn sits near 451 Hz; a whole tone up keeps them apart
BEEPS = (0.13, 0.20)      # seconds each tap sounds: a short tap, then a longer one
GAP = 0.065               # seconds of silence between the taps
ATTACK = 0.005            # a horn's contact closes in a few milliseconds
RELEASE = 0.028           # and the diaphragm rings down about as fast once it opens
SETTLE = 0.012            # the first cycles run a touch flat while the diaphragm gets going
TAIL = 0.03               # silence after the last tap, so nothing is cut at the clip's end
PEAK = 0.89               # -1 dBFS
OUTPUT_DRIVE = 2.2        # the horn's own output is driven into compression: loud for its size
RESONANCES = ((2000.0, 3.0, 9.0), (3600.0, 4.0, 5.0))   # (Hz, Q, dB) of the horn's disc
SEED = 20261005


def peaking(freq, q, gain_db):
    """One RBJ peaking-EQ biquad as a second-order section."""
    a = 10 ** (gain_db / 40)
    w = 2 * np.pi * freq / RATE
    alpha = np.sin(w) / (2 * q)
    b = np.array([1 + alpha * a, -2 * np.cos(w), 1 - alpha * a])
    den = np.array([1 + alpha / a, -2 * np.cos(w), 1 - alpha / a])
    return np.concatenate([b / den[0], den / den[0]])


def smooth_noise(rng, n, cutoff_hz):
    sos = butter(2, cutoff_hz, fs=RATE, output="sos")
    noise = sosfilt(sos, rng.standard_normal(n))
    return noise / (np.abs(noise).max() + 1e-12)


def tap(rng, seconds):
    n = int(seconds * RATE)
    t = np.arange(n) / RATE

    # Pitch: steady, apart from the short settle at the start and a hair of jitter.
    settle = 1.0 - 0.015 * np.exp(-t / SETTLE)
    jitter = 1.0 + 0.0025 * smooth_noise(rng, n, 40.0)
    phase = 2 * np.pi * np.cumsum(PITCH_HZ * settle * jitter) / RATE

    # The diaphragm's wave: a band-limited sawtooth-like series, harmonics falling off slowly.
    wave = np.zeros(n)
    k = 1
    while k * PITCH_HZ < 9000.0:
        wave += np.sin(k * phase) / k ** 0.75
        k += 1

    # Its buzz: soft clipping, then a slight flutter in level.
    wave = np.tanh(1.6 * wave / np.abs(wave).max())
    wave *= 1.0 + 0.02 * smooth_noise(rng, n, 30.0)

    envelope = np.ones(n)
    a = int(ATTACK * RATE)
    r = int(RELEASE * RATE)
    envelope[:a] = 0.5 - 0.5 * np.cos(np.linspace(0, np.pi, a))
    envelope[-r:] = 0.5 + 0.5 * np.cos(np.linspace(0, np.pi, r))
    return wave * envelope


def main():
    rng = np.random.default_rng(SEED)
    parts = []
    for i, seconds in enumerate(BEEPS):
        if i:
            parts.append(np.zeros(int(GAP * RATE)))
        parts.append(tap(rng, seconds))
    parts.append(np.zeros(int(TAIL * RATE)))
    signal = np.concatenate(parts)

    sections = [peaking(f, q, g) for f, q, g in RESONANCES]
    sections.append(butter(2, 250.0, btype="highpass", fs=RATE, output="sos")[0])
    sections.append(butter(2, 7500.0, fs=RATE, output="sos")[0])
    signal = sosfilt(np.array(sections), signal)

    # The resonances put the peaks back that the buzz took off; compress them again, or the clip
    # is ~4 dB quieter than the jeep's at the same peak.
    signal = np.tanh(OUTPUT_DRIVE * signal / np.abs(signal).max())
    # The wave is lopsided, so compressing it leaves a DC offset; take it back out.
    signal = sosfilt(butter(2, 40.0, btype="highpass", fs=RATE, output="sos"), signal)
    signal *= PEAK / np.abs(signal).max()
    wavfile.write(OUT, RATE, np.round(signal * 32767).astype(np.int16))
    print(f"wrote {OUT.relative_to(ROOT)}: {len(signal) / RATE:.3f} s at {PITCH_HZ:.0f} Hz")


if __name__ == "__main__":
    main()
