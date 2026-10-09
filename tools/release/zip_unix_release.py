"""Zip a staged macOS or Linux release folder so the player still starts after it is unzipped.

Usage: python tools/release/zip_unix_release.py <macos|linux> <staging-folder> <zip-path>

WHY NOT System.IO.Compression (what package-release.ps1 uses for the Windows zip). A zip carries a
file's Unix permissions only in the high 16 bits of its external attributes, and unzippers -- macOS
Archive Utility included -- read those bits only when the entry says it was made on Unix. .NET on
Windows writes "made by MS-DOS" and gives no way to change it, so every file in the bundle arrives
as 0644 and Contents/MacOS/Ironfront is not executable: Finder shows the app, double-clicking it
does nothing useful, and the player has no way to tell why. Unity's own manual says it: a macOS
build made on Windows needs its executable flag set before it will open. A Linux player has the
same problem with Ironfront.x86_64: unzip restores 0644 and the shell answers "Permission denied".

So every entry here is written as made on Unix (create_system = 3) with an explicit mode:
  - directories 0755,
  - every native binary of the platform (Mach-O for macos, ELF for linux: the executable and the
    libraries, found by magic number rather than by path so a new native library cannot be
    missed) 0755,
  - everything else 0644.
The top-level folder is kept, so the zip opens into IronfrontReborn-<version>/ like the Windows one.
"""

import os
import stat
import sys
import time
import zipfile

# Mach-O and universal ("fat") headers, as the first four bytes appear on disk.
MACHO_MAGICS = {
    bytes.fromhex("cafebabe"), bytes.fromhex("cafebabf"),  # fat, fat 64
    bytes.fromhex("feedface"), bytes.fromhex("cefaedfe"),  # 32-bit, either byte order
    bytes.fromhex("feedfacf"), bytes.fromhex("cffaedfe"),  # 64-bit, either byte order
}

# ELF, as the first four bytes of a Linux executable or shared library.
ELF_MAGIC = b"\x7fELF"

UNIX = 3
DOS_DIRECTORY = 0x10


def is_native(path, kind):
    with open(path, "rb") as f:
        head = f.read(4)
    return head in MACHO_MAGICS if kind == "macos" else head == ELF_MAGIC


def entry(arcname, mode, date_time):
    info = zipfile.ZipInfo(arcname, date_time=date_time)
    info.create_system = UNIX
    info.external_attr = mode << 16
    return info


def main(kind, staging, zip_path):
    if kind not in ("macos", "linux"):
        raise SystemExit(f"kind must be macos or linux, not {kind}")
    staging = os.path.abspath(staging)
    if not os.path.isdir(staging):
        raise SystemExit(f"not a folder: {staging}")
    parent = os.path.dirname(staging)

    executables = 0
    files = 0
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as zf:
        for root, dirs, names in os.walk(staging):
            dirs.sort()
            rel_dir = os.path.relpath(root, parent).replace(os.sep, "/")
            stamp = os.path.getmtime(root)
            info = entry(rel_dir + "/", stat.S_IFDIR | 0o755, _date_time(stamp))
            info.external_attr |= DOS_DIRECTORY
            zf.writestr(info, b"")
            for name in sorted(names):
                full = os.path.join(root, name)
                if os.path.islink(full):
                    raise SystemExit(f"symlinks are not packaged (a Windows build has none): {full}")
                executable = is_native(full, kind)
                mode = stat.S_IFREG | (0o755 if executable else 0o644)
                info = entry(f"{rel_dir}/{name}", mode, _date_time(os.path.getmtime(full)))
                info.compress_type = zipfile.ZIP_DEFLATED
                with open(full, "rb") as src, zf.open(info, "w") as dst:
                    while True:
                        chunk = src.read(1 << 20)
                        if not chunk:
                            break
                        dst.write(chunk)
                files += 1
                executables += executable

    native = "Mach-O" if kind == "macos" else "ELF"
    if executables == 0:
        raise SystemExit(f"no {native} file was found: this is not a {kind} player")
    print(f"[release] zipped {files} file(s), {executables} marked executable ({native})")


def _date_time(epoch):
    t = time.localtime(max(epoch, 315532800))  # zip cannot store dates before 1980
    return t[:6]


if __name__ == "__main__":
    if len(sys.argv) != 4:
        raise SystemExit(__doc__)
    main(sys.argv[1], sys.argv[2], sys.argv[3])
