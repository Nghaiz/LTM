"""Peak-normalise 16-bit PCM WAV files in place.

Usage: python -I tools/normalize_wav.py --peak-db -1 path/to/a.wav [path/to/b.wav ...]

Each file is scaled so its loudest sample sits at --peak-db dBFS (default -1). The gain is
printed per file, so a commit can say what changed. Only 16-bit PCM is accepted; anything else
is refused rather than guessed at.

Why a tool: Unity's AudioImporter "normalize" applies only while it mixes a multichannel source
down to mono, so a mono clip cannot be made louder at import time (checked 2026-10-08 on the
player's footsteps, which stayed at -13.8 dBFS with forceToMono and normalize both on).
"""
import argparse
import array
import math
import sys
import wave


def normalise(path: str, peak_db: float) -> float:
    with wave.open(path, "rb") as source:
        params = source.getparams()
        frames = source.readframes(params.nframes)
    if params.sampwidth != 2 or params.comptype != "NONE":
        raise SystemExit(f"{path}: only 16-bit PCM is supported ({params.sampwidth * 8}-bit {params.comptype})")

    samples = array.array("h", frames)
    if sys.byteorder == "big":
        samples.byteswap()
    peak = max((abs(s) for s in samples), default=0)
    if peak == 0:
        raise SystemExit(f"{path}: silent, nothing to normalise")

    target = 32767 * 10 ** (peak_db / 20)
    gain = target / peak
    scaled = array.array("h", (max(-32768, min(32767, round(s * gain))) for s in samples))
    if sys.byteorder == "big":
        scaled.byteswap()

    with wave.open(path, "wb") as out:
        out.setparams(params)
        out.writeframes(scaled.tobytes())
    return 20 * math.log10(gain)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--peak-db", type=float, default=-1.0)
    parser.add_argument("files", nargs="+")
    args = parser.parse_args()
    for path in args.files:
        print(f"{path}: {normalise(path, args.peak_db):+.1f} dB")


if __name__ == "__main__":
    main()
