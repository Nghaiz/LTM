#!/usr/bin/env python3
"""Writes the guided launcher's seeker tones (owner's run of 2026-10-11).

    python tools/ui/make_lock_sounds.py

Into ``Ironfront_Reborn/Assets/Resources/IronfrontUi`` as ``lock-<name>.wav``, where ``Javelin``
loads them for the local player only:

- tracking: a short beep, repeated while the seeker is taking a lock
- locked: a steady tone, looped while the lock holds and the launcher will fire
- denied: two low blips, a pull that cannot launch (from the hip, empty, mid-reload)

The lock-on launchers of the big shooters talk to the player this way, because nobody can watch
the lock box and the target at once. 44.1 kHz mono 16-bit, synthesised so the game ships no
third-party audio, and deterministic: re-running it changes nothing. The locked tone holds a whole
number of cycles, so its loop has no click at the seam.
"""
import os
import wave

import numpy as np

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
FOLDER = os.path.join(ROOT, "Ironfront_Reborn", "Assets", "Resources", "IronfrontUi")
RATE = 44100

# Quieter than the hit sounds (0.8): the locked tone runs for as long as the player holds a lock.
PEAK = 0.6

TRACKING_HZ = 1000.0
LOCKED_HZ = 1500.0
DENIED_HZ = 400.0


def timeline(length):
    return np.arange(int(round(RATE * length))) / RATE


def blip(t, start, length, freq, edge=0.004):
    """A tone with a few milliseconds of fade at each end, so it starts and stops without a click.
    Its third harmonic keeps it audible on a laptop's small speakers."""
    local = t - start
    on = (local >= 0) & (local <= length)
    env = np.clip(np.minimum(local, length - local) / edge, 0, 1)
    tone = np.sin(2 * np.pi * freq * local) + 0.25 * np.sin(2 * np.pi * 3 * freq * local)
    return np.where(on, env * tone, 0.0)


def normalise(mix):
    return mix * (PEAK / np.max(np.abs(mix)))


def write(name, mix):
    path = os.path.join(FOLDER, "lock-" + name + ".wav")
    pcm = (mix * 32767).astype("<i2")
    os.makedirs(FOLDER, exist_ok=True)
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(pcm.tobytes())
    print(f"wrote {path} ({mix.size / RATE:.2f} s)")


def tracking():
    t = timeline(0.07)
    return normalise(blip(t, 0.0, 0.06, TRACKING_HZ))


def locked():
    # One second of LOCKED_HZ is exactly 1500 cycles in 44100 samples: the last sample leads
    # straight back into the first, so the loop runs with no seam and needs no fade.
    t = timeline(1.0)
    tone = np.sin(2 * np.pi * LOCKED_HZ * t) + 0.25 * np.sin(2 * np.pi * 3 * LOCKED_HZ * t)
    return normalise(tone)


def denied():
    t = timeline(0.2)
    return normalise(blip(t, 0.0, 0.07, DENIED_HZ) + blip(t, 0.12, 0.07, DENIED_HZ))


def main():
    write("tracking", tracking())
    write("locked", locked())
    write("denied", denied())


if __name__ == "__main__":
    main()
