"""Render Graphite icon geometry strings for visual check (16px grid, 1.3px stroke)."""
import re
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.path import Path
from matplotlib.patches import PathPatch

ICONS = {
    "TextHighlight": "M4.6 9.4 L9.8 2.6 L12.6 4.8 L7.4 11.6 L4.4 12 Z M9 3.8 L11.8 6 M2.4 13.8 H13.6 M2.6 3.2 H6.2 M2.6 5.6 H5.2",
    "MarkerFreehand": "M2.4 11 C3.2 6.4 5.2 6 6.4 9.6 C7.4 12.8 9.2 13 10.2 9.2 C11 6.4 12.8 6.4 13.6 9.4",
    "Eraser": "M2.9 9.6 L5.5 12.2 L12 5.7 L9.4 3.1 Z M6.7 6.2 L8.9 8.5 M2.4 13.8 H13.6",
}

def parse(d):
    tokens = re.findall(r"[MLHVCZA]|-?\d*\.?\d+", d)
    verts, codes = [], []
    i, cur = 0, (0.0, 0.0)
    cmd = None
    def num():
        nonlocal i
        v = float(tokens[i]); i += 1; return v
    while i < len(tokens):
        if tokens[i].isalpha():
            cmd = tokens[i]; i += 1
        if cmd == "M":
            cur = (num(), num()); verts.append(cur); codes.append(Path.MOVETO); cmd = "L"
        elif cmd == "L":
            cur = (num(), num()); verts.append(cur); codes.append(Path.LINETO)
        elif cmd == "H":
            cur = (num(), cur[1]); verts.append(cur); codes.append(Path.LINETO)
        elif cmd == "V":
            cur = (cur[0], num()); verts.append(cur); codes.append(Path.LINETO)
        elif cmd == "C":
            for _ in range(3):
                cur = (num(), num()); verts.append(cur); codes.append(Path.CURVE4)
        elif cmd == "Z":
            codes[-1] = Path.CLOSEPOLY
    return verts, codes

fig, axes = plt.subplots(1, 3, figsize=(9, 3.4))
for ax, (name, d) in zip(axes, ICONS.items()):
    verts, codes = parse(d)
    # flip y for screen coordinates
    verts = [(x, 16 - y) for x, y in verts]
    patch = PathPatch(Path(verts, codes), fill=False, edgecolor="#E8E8E6",
                      linewidth=2.6, capstyle="round", joinstyle="round")
    ax.add_patch(patch)
    ax.set_xlim(0, 16); ax.set_ylim(0, 16)
    ax.set_aspect("equal")
    ax.set_facecolor("#2B2B2A")
    ax.set_xticks(range(0, 17, 4)); ax.set_yticks(range(0, 17, 4))
    ax.grid(color="#444", linewidth=0.3)
    ax.set_title(name, color="white", fontsize=10)
fig.patch.set_facecolor("#1E1E1D")
fig.savefig(r"C:\Users\micha\Documents\Claude Cowork Area\Cowork PDF viewer\repro-out\new-icons.png",
            bbox_inches="tight", dpi=150)
print("saved")
