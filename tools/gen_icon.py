"""Graphite app icon — a pencil that has just drawn a flowing "G" ink stroke.

Concept: the brand initial rendered as a single calligraphic stroke (the thing
you *do* in Graphite: draw on documents), with the pencil resting at the
stroke's origin and the app's blue accent (#6CA0FF) as the collar + tip glow.

Outputs Assets/Graphite-256.png and a multi-size Assets/Graphite.ico.
"""
from PIL import Image, ImageDraw, ImageFilter
import math, os

S = 1024  # supersample canvas
OUT_DIR = os.path.join(os.path.dirname(__file__), "..", "src", "Graphite.App", "Assets")

INK = (242, 242, 238, 255)
INK_DIM = (205, 205, 200, 255)
BLUE = (108, 160, 255, 255)
WOOD = (224, 205, 170, 255)
GRAPHITE = (43, 44, 49, 255)

# ---------------------------------------------------------------- tile
tile = Image.new("RGBA", (S, S), (0, 0, 0, 0))
radius = int(S * 0.235)

top, bot = (42, 43, 49), (19, 20, 24)
grad = Image.new("RGB", (1, S))
for y in range(S):
    t = y / (S - 1)
    grad.putpixel((0, y), tuple(int(top[i] + (bot[i] - top[i]) * t) for i in range(3)))
grad = grad.resize((S, S))
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=radius, fill=255)
tile.paste(grad, (0, 0), mask)

# faint blue ambience lower-right (where the pencil works)
glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
ImageDraw.Draw(glow).ellipse([S * 0.45, S * 0.45, S * 1.05, S * 1.05], fill=(108, 160, 255, 26))
glow = glow.filter(ImageFilter.GaussianBlur(S * 0.06))
tile = Image.alpha_composite(tile, Image.composite(glow, Image.new("RGBA", (S, S), (0, 0, 0, 0)), mask))

# ---------------------------------------------------------------- "G" stroke
stroke = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(stroke)

cx, cy, r = S * 0.485, S * 0.535, S * 0.265
A0, A1 = math.radians(52), math.radians(310)   # open on the right
W_MAX, W_MIN = S * 0.098, S * 0.058

def pt(theta):
    return (cx + r * math.cos(theta), cy + r * math.sin(theta))

def width_at(t):
    # calligraphic taper: thin tips, belly at the bottom of the bowl
    belly = math.sin(t * math.pi) ** 0.8
    return W_MIN + (W_MAX - W_MIN) * belly

N = 220
pts = [pt(A0 + (A1 - A0) * i / N) for i in range(N + 1)]
for i in range(N):
    t = i / N
    w = width_at(t)
    d.line([pts[i], pts[i + 1]], fill=INK, width=int(w))
    d.ellipse([pts[i][0] - w / 2, pts[i][1] - w / 2, pts[i][0] + w / 2, pts[i][1] + w / 2], fill=INK)
w_end = width_at(1)
d.ellipse([pts[-1][0] - w_end / 2, pts[-1][1] - w_end / 2,
           pts[-1][0] + w_end / 2, pts[-1][1] + w_end / 2], fill=INK)

# crossbar of the G: horizontal bar at mid-right with a short descending stub
bx1, bx0 = cx + r * 1.02, cx + r * 0.18
bw = W_MAX * 0.92
d.line([(bx0, cy), (bx1, cy)], fill=INK, width=int(bw))
d.ellipse([bx0 - bw / 2, cy - bw / 2, bx0 + bw / 2, cy + bw / 2], fill=INK)
stub = r * 0.34
d.line([(bx1, cy), (bx1, cy + stub)], fill=INK, width=int(bw))
d.ellipse([bx1 - bw / 2, cy + stub - bw / 2, bx1 + bw / 2, cy + stub + bw / 2], fill=INK)

tile = Image.alpha_composite(tile, stroke)

# ---------------------------------------------------------------- pencil at the stroke origin
# Stroke starts at A0 (lower right); the pencil rests there, tip touching the
# point, body angled outward along the tangent — as if it just finished the G.
tip = pts[0]
tang = (math.sin(A0), -math.cos(A0))           # away from the arc, up-right
L = math.hypot(*tang)
tang = (tang[0] / L, tang[1] / L)
angle = math.degrees(math.atan2(-tang[1], tang[0]))  # PIL rotate is CCW-positive

PW, PL = S * 0.105, S * 0.40   # pencil width / length
CONE = S * 0.12                # wood cone length
COLLAR = S * 0.032
ERASER = S * 0.045

pen = Image.new("RGBA", (S, S), (0, 0, 0, 0))
p = ImageDraw.Draw(pen)
# local coords: tip at (0,0), body extends along +x
def X(v): return S * 0.5 + v
def Y(v): return S * 0.5 + v

# wood cone
p.polygon([(X(0), Y(0)), (X(CONE), Y(-PW / 2)), (X(CONE), Y(PW / 2))], fill=WOOD)
# graphite point
gp = CONE * 0.45
p.polygon([(X(0), Y(0)), (X(gp), Y(-PW * 0.16)), (X(gp), Y(PW * 0.16))], fill=GRAPHITE)
# lacquer body
p.rectangle([X(CONE), Y(-PW / 2), X(PL - COLLAR - ERASER), Y(PW / 2)], fill=INK)
# facet lines
p.line([(X(CONE), Y(-PW * 0.18)), (X(PL - COLLAR - ERASER), Y(-PW * 0.18))], fill=INK_DIM, width=int(S * 0.006))
p.line([(X(CONE), Y(PW * 0.18)), (X(PL - COLLAR - ERASER), Y(PW * 0.18))], fill=INK_DIM, width=int(S * 0.006))
# collar (blue accent)
p.rectangle([X(PL - COLLAR - ERASER), Y(-PW / 2), X(PL - ERASER), Y(PW / 2)], fill=BLUE)
# eraser
p.rounded_rectangle([X(PL - ERASER), Y(-PW / 2), X(PL), Y(PW / 2)], radius=PW * 0.28, fill=(214, 214, 210, 255))

# place: rotate so local +x maps onto the tangent, tip landing on the stroke origin
pen = pen.rotate(angle, resample=Image.BICUBIC, center=(S / 2, S / 2))
# After rotate(angle): local +x points along `tang`. The tip sits at local (0,0)
# = canvas center; translate so it lands exactly on the stroke start.
dx = tip[0] - S / 2
pen = pen.transform((S, S), Image.AFFINE, (1, 0, -dx, 0, 1, -(tip[1] - S / 2)), resample=Image.BICUBIC)

# soft contact glow where the tip meets the stroke
tipglow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
ImageDraw.Draw(tipglow).ellipse([tip[0] - S * 0.05, tip[1] - S * 0.05,
                                 tip[0] + S * 0.05, tip[1] + S * 0.05], fill=(108, 160, 255, 90))
tipglow = tipglow.filter(ImageFilter.GaussianBlur(S * 0.02))
tile = Image.alpha_composite(tile, tipglow)
tile = Image.alpha_composite(tile, pen)

# edge highlight
edge = Image.new("RGBA", (S, S), (0, 0, 0, 0))
ImageDraw.Draw(edge).rounded_rectangle([2, 2, S - 3, S - 3], radius=radius, outline=(255, 255, 255, 26), width=3)
tile = Image.alpha_composite(tile, edge)

# ---------------------------------------------------------------- export
os.makedirs(OUT_DIR, exist_ok=True)
tile.resize((256, 256), Image.LANCZOS).save(os.path.join(OUT_DIR, "Graphite-256.png"))
tile.save(os.path.join(OUT_DIR, "Graphite.ico"),
          sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
# also a large preview for the README
tile.resize((512, 512), Image.LANCZOS).save(os.path.join(OUT_DIR, "Graphite-512.png"))
print("wrote", os.path.abspath(OUT_DIR))
