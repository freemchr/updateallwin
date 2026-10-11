"""Reduces an SVG to plain shapes (no metadata, scripts, links or editor data) so WinUI's
SvgImageSource renders it reliably. Usage: python3 -I sanitize_svg.py <in.svg> <out.svg>"""
import re, sys
import xml.etree.ElementTree as ET

SVG = "{http://www.w3.org/2000/svg}"
SHAPES = {"g", "path", "rect", "circle", "ellipse", "polygon", "polyline", "line"}
KEEP = {"d", "x", "y", "width", "height", "rx", "ry", "cx", "cy", "r", "points", "x1", "y1", "x2", "y2",
        "transform", "fill", "fill-rule", "fill-opacity", "stroke", "stroke-width", "stroke-linejoin",
        "stroke-linecap", "stroke-miterlimit", "opacity", "clip-rule"}

def num(v):
    m = re.match(r"[\d.]+", v or "")
    return float(m.group()) if m else None

def copy(src, dst_parent):
    for child in src:
        tag = child.tag.replace(SVG, "")
        if tag not in SHAPES:
            continue
        el = ET.SubElement(dst_parent, tag)
        for k, v in child.attrib.items():
            if k in KEEP and "url(" not in v:
                el.set(k, v)
        for decl in (child.get("style") or "").split(";"):
            if ":" in decl:
                k, v = (s.strip() for s in decl.split(":", 1))
                if k in KEEP and "url(" not in v and k not in el.attrib:
                    el.set(k, v)
        if tag == "g":
            copy(child, el)
            if len(el) == 0:
                dst_parent.remove(el)

root = ET.parse(sys.argv[1]).getroot()
vb = root.get("viewBox") or f"0 0 {num(root.get('width'))} {num(root.get('height'))}"
ET.register_namespace("", "http://www.w3.org/2000/svg")
out = ET.Element(f"{SVG}svg", {"viewBox": vb})
w, h = vb.split()[2:4]
out.set("width", w); out.set("height", h)
copy(root, out)
ET.ElementTree(out).write(sys.argv[2], encoding="utf-8", xml_declaration=True)
print(sys.argv[2], "shapes:", sum(1 for _ in out.iter()) - 1, "viewBox:", vb)
