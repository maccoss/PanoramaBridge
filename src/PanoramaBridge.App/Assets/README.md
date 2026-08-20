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

The fill deliberately reaches the inside of the ring, through the gap where the Space Needle
crosses it, so the emblem sits on transparency rather than on a white tile. That is what stops it
showing as a white square on a dark taskbar.
