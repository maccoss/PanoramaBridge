# Application icon

`panoramabridge.ico` is generated from `panoramabridge-logo.png`, which is the source of truth.
Do not edit the `.ico` by hand.

It carries **16, 24, 32, 48, 64, 128 and 256 pixel** frames. Windows picks between them: 16 for
the title bar and small list views, 32 for the taskbar, 48 for medium icons, 256 for the
extra-large view and the installer. An icon holding a single frame gets scaled by the shell
instead, which is what the previous one did -- and it was 256x170, so it was also stretched,
because an icon frame has to be square.

To regenerate after changing the logo:

```python
from PIL import Image, ImageDraw

SIZES = [16, 24, 32, 48, 64, 128, 256]
src = Image.open('panoramabridge-logo.png').convert('RGBA')

# Flood the flat background away from each corner. The tolerance matters: the source has a
# slightly uneven near-white background rather than a flat one.
flat = src.convert('RGB')
for corner in [(0, 0), (src.width - 1, 0), (0, src.height - 1), (src.width - 1, src.height - 1)]:
    ImageDraw.floodfill(flat, corner, (255, 0, 255), thresh=40)

alpha = Image.new('L', src.size, 255)
alpha.putdata([0 if p == (255, 0, 255) else 255 for p in flat.getdata()])
out = src.copy()
out.putalpha(alpha)

# Trim to what is drawn, then square it. Filling the canvas is most of what makes a 16-pixel
# icon readable.
out = out.crop(out.getbbox())
side = max(out.size)
square = Image.new('RGBA', (side, side), (0, 0, 0, 0))
square.paste(out, ((side - out.width) // 2, (side - out.height) // 2))

square.resize((256, 256), Image.LANCZOS).save(
    'panoramabridge.ico', format='ICO', sizes=[(s, s) for s in SIZES])
```

## Why the disc is opaque

An earlier version left the inside of the ring transparent, which looked clean in isolation and
was wrong in use: the blue skyline sat straight on the taskbar colour and disappeared against a
dark one. The emblem now sits on an opaque **white disc**, clipped to the ring rather than to the
whole tile.

White rather than a grey matched to the Windows 11 title bar (`#F2F3F4`, sampled from a
screenshot): an icon carries one fixed colour and cannot follow the theme, and white is the one
that survives a dark taskbar. On a light taskbar the yellow ring supplies the edge, so the disc
disappearing into the background does not matter. A square white tile was tried too and is worse
-- a round emblem in a hard white box, heavy against a dark bar.

To produce the disc, find the yellow ring's extent and clip to it:

```python
xs, ys = [], []
for y in range(0, h, 2):
    for x in range(0, w, 2):
        r, g, b = rgb.getpixel((x, y))
        if r > 200 and 130 < g < 215 and b < 120:   # the ring's yellow
            xs.append(x); ys.append(y)

cx, cy = (min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2
radius = max(max(xs) - min(xs), max(ys) - min(ys)) / 2
```

The ring is what defines the circle, not the bounding box of everything drawn: the Space Needle
pokes out above the ring, and it is kept opaque so the tip is not clipped off. Draw the disc at
four times the size and downsample it, or the rim is visibly stepped.
