#!/usr/bin/env python3
"""Developer asset generator; CairoSVG rasterizes every size from the SVG.
No imaging dependency is installed with ClipScope. PNG-in-ICO requires Vista+.
"""
import pathlib
import struct
import tempfile
import cairosvg

root = pathlib.Path(__file__).resolve().parent.parent
sizes = (16, 20, 24, 32, 40, 48, 64, 128, 256)
with tempfile.TemporaryDirectory(prefix="clipscope-icons-") as directory:
    payloads = []
    for size in sizes:
        output = pathlib.Path(directory) / f"{size}.png"
        cairosvg.svg2png(url=str(root / "assets/ClipScope.placeholder.svg"),
                        write_to=str(output), output_width=size, output_height=size)
        payloads.append(output.read_bytes())
    offset = 6 + 16 * len(sizes)
    entries = []
    for size, payload in zip(sizes, payloads):
        entries.append(struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(payload), offset))
        offset += len(payload)
    destination = root / "src/ClipScope.App/Assets/ClipScope.ico"
    destination.write_bytes(struct.pack("<HHH", 0, 1, len(sizes)) + b"".join(entries) + b"".join(payloads))
    print(f"Generated {destination.relative_to(root)}: {', '.join(map(str, sizes))} pixels")
