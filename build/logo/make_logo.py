"""Generates the UpdateAll Win logo: .ico, PNGs and XAML path data, from one geometry.
Pure Python (no Pillow). Usage: python3 make_logo.py <assets-dir>"""
import math, struct, sys, zlib, os

CX = CY = 128.0
R_OUT, R_IN = 106.0, 76.0
HEAD_EXTRA, HEAD_LEN = 17.0, 42.0
# Screen angles in degrees, clockwise from 3 o'clock (y down). Arrows travel counter-clockwise (decreasing angle).
BLUE = dict(tail=303.0, head=172.0, c0=(0x2A, 0x8C, 0xF0), c1=(0x0B, 0x3E, 0xB5))
TEAL = dict(tail=128.0, head=-12.0, c0=(0x13, 0xB8, 0xC8), c1=(0x2E, 0xE6, 0xA6))
TILE, GAP, TILE_R = 45.0, 7.0, 8.0
TILE0 = CX - TILE - GAP / 2

def pt(theta_deg, r):
    t = math.radians(theta_deg)
    return (CX + r * math.cos(t), CY + r * math.sin(t))

def head_points(a):
    mid = (R_OUT + R_IN) / 2
    tip_angle = a['head'] - math.degrees(HEAD_LEN / mid)
    return pt(a['head'], R_OUT + HEAD_EXTRA), pt(tip_angle, mid), pt(a['head'], R_IN - HEAD_EXTRA)

def lerp(c0, c1, t):
    return tuple(int(c0[i] + (c1[i] - c0[i]) * max(0, min(1, t))) for i in range(3))

def in_tri(p, a, b, c):
    def s(p1, p2, p3): return (p1[0]-p3[0])*(p2[1]-p3[1]) - (p2[0]-p3[0])*(p1[1]-p3[1])
    d1, d2, d3 = s(p, a, b), s(p, b, c), s(p, c, a)
    return not ((d1 < 0 or d2 < 0 or d3 < 0) and (d1 > 0 or d2 > 0 or d3 > 0))

def arrow_color(x, y, a):
    r = math.hypot(x - CX, y - CY)
    ang = math.degrees(math.atan2(y - CY, x - CX)) % 360
    span = (a['tail'] - a['head']) % 360
    rel = (a['tail'] - ang) % 360  # distance travelled from tail toward head
    if R_IN <= r <= R_OUT and rel <= span:
        return lerp(a['c0'], a['c1'], rel / span)
    o, t, i = head_points(a)
    if in_tri((x, y), o, t, i):
        return a['c1']
    return None

def tile_color(x, y):
    for row in range(2):
        for col in range(2):
            x0 = TILE0 + col * (TILE + GAP); y0 = TILE0 + row * (TILE + GAP)
            if x0 <= x <= x0 + TILE and y0 <= y <= y0 + TILE:
                cx = min(max(x, x0 + TILE_R), x0 + TILE - TILE_R)
                cy = min(max(y, y0 + TILE_R), y0 + TILE - TILE_R)
                if (x - cx) ** 2 + (y - cy) ** 2 <= TILE_R ** 2:
                    t = ((x - TILE0) + (y - TILE0)) / (2 * (2 * TILE + GAP))
                    return lerp((0x5C, 0xCF, 0xFF), (0x0D, 0x4F, 0xC8), t)
    return None

def color_at(x, y):
    return tile_color(x, y) or arrow_color(x, y, TEAL) or arrow_color(x, y, BLUE)

def render(size, ss):
    rows = []
    for py in range(size):
        row = bytearray([0])
        for px in range(size):
            acc = [0, 0, 0]; hits = 0
            for sy in range(ss):
                for sx in range(ss):
                    c = color_at((px + (sx + .5) / ss) * 256 / size, (py + (sy + .5) / ss) * 256 / size)
                    if c:
                        hits += 1
                        for k in range(3): acc[k] += c[k]
            n = ss * ss
            row += bytes([acc[0] // hits, acc[1] // hits, acc[2] // hits, 255 * hits // n] if hits else [0, 0, 0, 0])
        rows.append(bytes(row))
    def chunk(t, d): return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(b"".join(rows), 9)) + chunk(b"IEND", b""))

def arrow_path(a):
    f = lambda p: f"{p[0]:.2f},{p[1]:.2f}"
    o, t, i = head_points(a)
    large = 1 if (a['tail'] - a['head']) % 360 > 180 else 0
    return (f"M{f(pt(a['tail'], R_OUT))} A{R_OUT},{R_OUT} 0 {large} 0 {f(pt(a['head'], R_OUT))} "
            f"L{f(o)} L{f(t)} L{f(i)} L{f(pt(a['head'], R_IN))} "
            f"A{R_IN},{R_IN} 0 {large} 1 {f(pt(a['tail'], R_IN))} Z")

out = sys.argv[1]
os.makedirs(out, exist_ok=True)
sizes = [16, 20, 24, 32, 40, 48, 64, 256]
pngs = {s: render(s, 4 if s <= 64 else 2) for s in sizes}
ico = struct.pack("<HHH", 0, 1, len(sizes)); offset = 6 + 16 * len(sizes)
for s in sizes:
    ico += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(pngs[s]), offset); offset += len(pngs[s])
open(os.path.join(out, "UpdateAllWin.ico"), "wb").write(ico + b"".join(pngs[s] for s in sizes))
open(os.path.join(out, "Logo256.png"), "wb").write(pngs[256])
open(os.path.join(out, "Logo48.png"), "wb").write(pngs[48])
with open(os.path.join(out, "logo-paths.txt"), "w") as fh:
    fh.write("BLUE " + arrow_path(BLUE) + "\nTEAL " + arrow_path(TEAL) + "\n")
    fh.write(f"TILES origin={TILE0} size={TILE} gap={GAP} radius={TILE_R}\n")
print("ok")

# ---- Tray icons: logo + status dot (bottom-right) ----
STATUS = {
    "green": (0x22, 0xC5, 0x5E), "blue": (0x25, 0x63, 0xEB), "orange": (0xF9, 0x73, 0x16),
    "red": (0xEF, 0x44, 0x44), "yellow": (0xEA, 0xB3, 0x08), "grey": (0x94, 0xA3, 0xB8),
}
_base_color_at = color_at
def make_status_color_at(dot):
    DX, DY, DR = 196.0, 196.0, 52.0
    def f(x, y):
        d = math.hypot(x - DX, y - DY)
        if d <= DR - 12: return dot
        if d <= DR: return (255, 255, 255)
        return _base_color_at(x, y)
    return f
tray_sizes = [16, 20, 24, 32, 40, 48]
for name, dot in STATUS.items():
    color_at = make_status_color_at(dot)
    imgs = {s: render(s, 4) for s in tray_sizes}
    data = struct.pack("<HHH", 0, 1, len(tray_sizes)); off = 6 + 16 * len(tray_sizes)
    for s in tray_sizes:
        data += struct.pack("<BBBBHHII", s, s, 0, 0, 1, 32, len(imgs[s]), off); off += len(imgs[s])
    open(os.path.join(out, f"Tray-{name}.ico"), "wb").write(data + b"".join(imgs[s] for s in tray_sizes))
color_at = _base_color_at
print("tray ok")
