"""Convert the approved PNG to Windows icon sizes; requires Pillow."""
from pathlib import Path
from PIL import Image

root = Path(__file__).resolve().parent
original = Image.open(root / "EDCBepgEx-icon-v1.png").convert("RGBA")
# Ignore faint generator noise so the crop follows the visible symbol, not the near-transparent haze.
alpha = original.getchannel("A").point(lambda v: v if v >= 24 else 0)
bounds = alpha.getbbox()
if bounds is None:
    raise ValueError("Empty source icon")
original.putalpha(alpha)
art = original.crop(bounds)
# Retain a small transparent margin while making the symbol legible at 16px.
side = max(art.size)
margin = max(1, round(side * 0.045))
canvas = Image.new("RGBA", (side + margin * 2, side + margin * 2))
canvas.alpha_composite(art, ((canvas.width-art.width)//2, (canvas.height-art.height)//2))
master = canvas.resize((1024, 1024), Image.Resampling.LANCZOS)
for size in (16, 24, 32, 48, 64, 128, 256):
    master.resize((size, size), Image.Resampling.LANCZOS).save(root / f"EDCBepgEx-{size}.png")
master.save(root / "EDCBepgEx.ico", sizes=[(s,s) for s in (16,24,32,48,64,128,256)])
print("Created PNG sizes 16/24/32/48/64/128/256 and multi-resolution ICO")
