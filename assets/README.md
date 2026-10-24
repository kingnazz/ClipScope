The SVG is an engineering placeholder, not final brand artwork. Replace
`ClipScope.placeholder.svg`, then regenerate `src/ClipScope.App/Assets/ClipScope.ico`:

```sh
python -m pip install CairoSVG==2.8.2
python scripts/generate-icons.py
```

Each 16, 20, 24, 32, 40, 48, 64, 128 and 256 pixel image is rasterized directly
from the vector. The checked-in ICO serves the EXE, installer, shortcuts,
Apps & Features, taskbar and tray. Build and installed application need no Python
or imaging tools. The tray loads a DPI-sized owned icon and releases its handle
at shutdown. Mixed-monitor tray scaling still needs physical Windows 11 testing.
