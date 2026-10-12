"""Package the desktop SVG as PNG, ICNS, and ICO assets."""

import argparse
import shutil
import struct
import subprocess
from pathlib import Path

PNG_SIZES = (16, 24, 32, 44, 48, 64, 128, 256, 512, 1024)
ICNS_SLOTS = (
    (b"icp4", 16),
    (b"ic11", 32),
    (b"icp5", 32),
    (b"ic12", 64),
    (b"ic07", 128),
    (b"ic13", 256),
    (b"ic08", 256),
    (b"ic14", 512),
    (b"ic09", 512),
    (b"ic10", 1024),
)
ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)


def encode_icns(images: dict[int, bytes]) -> bytes:
    chunks = [
        kind + struct.pack(">I", 8 + len(images[size])) + images[size]
        for kind, size in ICNS_SLOTS
    ]
    payload = b"".join(chunks)
    return b"icns" + struct.pack(">I", 8 + len(payload)) + payload


def encode_ico(images: dict[int, bytes]) -> bytes:
    offset = 6 + 16 * len(ICO_SIZES)
    entries = []
    for size in ICO_SIZES:
        data = images[size]
        dimension = 0 if size == 256 else size
        entries.append(
            struct.pack(
                "<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(data), offset
            )
        )
        offset += len(data)
    header = struct.pack("<HHH", 0, 1, len(entries))
    return header + b"".join(entries) + b"".join(images[size] for size in ICO_SIZES)


def read_png(path: Path, size: int) -> bytes:
    data = path.read_bytes()
    if len(data) < 33 or data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError(f"Invalid PNG: {path}")
    if data[12:16] != b"IHDR" or struct.unpack_from(">II", data, 16) != (size, size):
        raise ValueError(f"Expected {size}x{size} PNG: {path}")
    return data


def render_icons(source: Path, output: Path, renderer: str) -> None:
    output.mkdir(parents=True, exist_ok=True)
    for size in PNG_SIZES:
        subprocess.run(
            [
                renderer,
                "--width",
                str(size),
                "--height",
                str(size),
                "--output",
                str(output / f"calculator-{size}.png"),
                str(source),
            ],
            check=True,
        )
    images = {
        size: read_png(output / f"calculator-{size}.png", size) for size in PNG_SIZES
    }
    (output / "Calculator.icns").write_bytes(encode_icns(images))
    (output / "Calculator.ico").write_bytes(encode_ico(images))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if not args.source.is_file():
        parser.error("--source must name the desktop SVG")
    renderer = shutil.which("rsvg-convert")
    if renderer is None:
        parser.error("rsvg-convert is required to regenerate assets; install librsvg")
    render_icons(args.source, args.output, renderer)


if __name__ == "__main__":
    main()
