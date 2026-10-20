"""Writes the button glyph SVGs of src/Trw.Game/Content/Glyphs (docs/BUTTON_GLYPHS.md).

The SVG files are the source the game rasterizes (Glyphs/GlyphRaster.cs); this script only keeps their style consistent. Edit a file by hand, or
change the style here and run it again:  python tools/glyphs/make_svgs.py

Every glyph is 64 units tall (any width). Text uses font-family 'Peaceful Wars' (the game's font, Content/Fonts/Peaceful Wars.ttf), which the
rasterizer supplies to the SVG renderer. keyboard/keycap.svg is a template: {{W}}, {{FACE_W}}, {{CX}}, {{FONT_SIZE}} and {{LABEL}} are filled in for
each key label. Shapes are generic drawings in the controllers' usual colours, no logos.
"""
import os

ROOT = os.path.join(os.path.dirname(__file__), '..', '..', 'src', 'Trw.Game', 'Content', 'Glyphs')

# the game's menu look: dark bronze metal, gold rims, warm white labels
INK = '#F6EAD0'
GOLD = '#E0A040'
DARK = '#221C17'
FACE0, FACE1 = '#5E5246', '#352D26'
FONT = "font-family=\"'Peaceful Wars'\""


def svg(w, body, h=64):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" viewBox="0 0 {w} {h}">\n'
            f'<defs><linearGradient id="face" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{FACE0}"/><stop offset="1" stop-color="{FACE1}"/></linearGradient>'
            f'<radialGradient id="ball" cx="0.4" cy="0.35" r="0.7"><stop offset="0" stop-color="#FFFFFF" stop-opacity="0.35"/><stop offset="1" stop-color="#000000" stop-opacity="0.25"/></radialGradient></defs>\n'
            f'{body}\n</svg>\n')


def text(x, y, s, size, fill=INK, anchor='middle', extra=''):
    return f'<text x="{x}" y="{y}" {FONT} font-size="{size}" fill="{fill}" text-anchor="{anchor}" {extra}>{s}</text>'


def write(rel, content):
    p = os.path.join(ROOT, rel)
    os.makedirs(os.path.dirname(p), exist_ok=True)
    with open(p, 'w', newline='\n') as f:
        f.write(content)


# ------------------------------------------------------------------ shared shapes
def keycap_body(w, inner=''):
    return (f'<rect x="1.5" y="3" width="{w - 3}" height="59" rx="11" fill="#0E0B09"/>'
            f'<rect x="1.5" y="1.5" width="{w - 3}" height="57" rx="11" fill="{DARK}" stroke="{GOLD}" stroke-width="3"/>'
            f'<rect x="7" y="6" width="{w - 14}" height="44" rx="7" fill="url(#face)"/>' + inner)


def arrow(cx, cy, size, direction, fill=INK):
    # a filled triangle pointing up / down / left / right
    s = size
    pts = {'up': [(0, -s), (s * 0.9, s * 0.6), (-s * 0.9, s * 0.6)],
           'down': [(0, s), (s * 0.9, -s * 0.6), (-s * 0.9, -s * 0.6)],
           'left': [(-s, 0), (s * 0.6, s * 0.9), (s * 0.6, -s * 0.9)],
           'right': [(s, 0), (-s * 0.6, s * 0.9), (-s * 0.6, -s * 0.9)]}[direction]
    return '<polygon points="' + ' '.join(f'{cx + x:.1f},{cy + y:.1f}' for x, y in pts) + f'" fill="{fill}"/>'


def round_button(fill, label=None, label_fill=INK, size=34, rim='#0E0B09', inner=''):
    b = (f'<circle cx="32" cy="33" r="29" fill="#0E0B09"/>'
         f'<circle cx="32" cy="32" r="28.5" fill="{rim}" stroke="{GOLD}" stroke-width="2.5"/>'
         f'<circle cx="32" cy="32" r="23" fill="{fill}"/><circle cx="32" cy="32" r="23" fill="url(#ball)"/>')
    if label:
        b += text(32, 32 + size * 0.36, label, size, label_fill)
    return b + inner


def shoulder(w, label, fill='#3A332C', tall=False, size=26):
    if tall:   # a trigger: rounded top, flat bottom
        body = (f'<path d="M4,60 L4,26 Q4,4 {w / 2},4 Q{w - 4},4 {w - 4},26 L{w - 4},60 Z" fill="#0E0B09" transform="translate(0,1.5)"/>'
                f'<path d="M4,60 L4,26 Q4,4 {w / 2},4 Q{w - 4},4 {w - 4},26 L{w - 4},60 Z" fill="{fill}" stroke="{GOLD}" stroke-width="3"/>')
        ty = 46
    else:      # a bumper: a wide rounded bar
        body = (f'<rect x="2" y="13" width="{w - 4}" height="40" rx="17" fill="#0E0B09"/>'
                f'<rect x="2" y="11" width="{w - 4}" height="40" rx="17" fill="{fill}" stroke="{GOLD}" stroke-width="3"/>')
        ty = 40
    return body + text(w / 2, ty, label, size)


def dpad(direction, fill='#3A332C', hi=GOLD):
    arms = {'up': (24, 4, 16, 24), 'down': (24, 36, 16, 24), 'left': (4, 24, 24, 16), 'right': (36, 24, 24, 16)}
    b = (f'<path d="M24,4 H40 V24 H60 V40 H40 V60 H24 V40 H4 V24 H24 Z" fill="#0E0B09" transform="translate(0,1.5)"/>'
         f'<path d="M24,4 H40 V24 H60 V40 H40 V60 H24 V40 H4 V24 H24 Z" fill="{fill}" stroke="{GOLD}" stroke-width="2.5"/>')
    if direction:
        x, y, w, h = arms[direction]
        b += f'<rect x="{x + 2}" y="{y + 2}" width="{w - 4}" height="{h - 4}" fill="{hi}"/>'
        cx, cy = x + w / 2, y + h / 2
        b += arrow(cx, cy, 5, direction, DARK)
    return b


def stick(label=None, direction=None, fill='#3A332C', cap='#5A5048'):
    b = (f'<circle cx="32" cy="33" r="29" fill="#0E0B09"/>'
         f'<circle cx="32" cy="32" r="28.5" fill="{fill}" stroke="{GOLD}" stroke-width="2.5"/>'
         f'<circle cx="32" cy="32" r="17" fill="{cap}" stroke="#0E0B09" stroke-width="2"/><circle cx="32" cy="32" r="17" fill="url(#ball)"/>')
    if label:
        b += text(32, 41, label, 24)
    if direction:
        pos = {'up': (32, 9), 'down': (32, 55), 'left': (9, 32), 'right': (55, 32)}[direction]
        b += arrow(pos[0], pos[1], 6, direction, GOLD)
    return b


# ------------------------------------------------------------------ keyboard and mouse
def keyboard():
    # template: the rasterizer measures the label and fills in the width
    write('keyboard/keycap.svg',
          '<svg xmlns="http://www.w3.org/2000/svg" width="{{W}}" height="64" viewBox="0 0 {{W}} 64">\n'
          f'<defs><linearGradient id="face" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{FACE0}"/><stop offset="1" stop-color="{FACE1}"/></linearGradient></defs>\n'
          f'<rect x="1.5" y="3" width="{{{{FACE_W}}}}" height="59" rx="11" fill="#0E0B09"/>'
          f'<rect x="1.5" y="1.5" width="{{{{FACE_W}}}}" height="57" rx="11" fill="{DARK}" stroke="{GOLD}" stroke-width="3"/>'
          f'<rect x="7" y="6" width="{{{{INNER_W}}}}" height="44" rx="7" fill="url(#face)"/>\n'
          f'<text x="{{{{CX}}}}" y="{{{{BASE}}}}" {FONT} font-size="{{{{FONT_SIZE}}}}" fill="{INK}" text-anchor="middle">{{{{LABEL}}}}</text>\n'
          '</svg>\n')
    for d in ('up', 'down', 'left', 'right'):
        write(f'keyboard/key_{d}.svg', svg(64, keycap_body(64, arrow(32, 28, 12, d))))
    # the four arrow keys (the stick on the keyboard)
    cluster = ''
    for d, (x, y) in {'up': (36, 0), 'left': (0, 34), 'down': (36, 34), 'right': (72, 34)}.items():
        cluster += (f'<g transform="translate({x},{y}) scale(0.5)">' + keycap_body(64, arrow(32, 28, 12, d)) + '</g>')
    write('keyboard/arrows.svg', svg(104, cluster, 66))

    def mouse(hi):
        body = (f'<path d="M12,22 Q12,3 32,3 Q52,3 52,22 L52,42 Q52,61 32,61 Q12,61 12,42 Z" fill="#0E0B09" transform="translate(0,1.5)"/>'
                f'<path d="M12,22 Q12,3 32,3 Q52,3 52,22 L52,42 Q52,61 32,61 Q12,61 12,42 Z" fill="{DARK}" stroke="{GOLD}" stroke-width="3"/>')
        if hi == 'left':
            body += f'<path d="M14.5,25 L14.5,22 Q14.5,6 30.5,5.5 L30.5,25 Z" fill="{GOLD}"/>'
        elif hi == 'right':
            body += f'<path d="M49.5,25 L49.5,22 Q49.5,6 33.5,5.5 L33.5,25 Z" fill="{GOLD}"/>'
        body += f'<line x1="12" y1="26.5" x2="52" y2="26.5" stroke="{GOLD}" stroke-width="2"/><line x1="32" y1="4" x2="32" y2="26" stroke="{GOLD}" stroke-width="2"/>'
        wheel = GOLD if hi in ('middle', 'wheel_up', 'wheel_down') else '#5A5048'
        body += f'<rect x="28" y="9" width="8" height="13" rx="4" fill="{wheel}" stroke="#0E0B09" stroke-width="1.5"/>'
        if hi == 'wheel_up':
            body += arrow(32, 44, 8, 'up', GOLD)
        if hi == 'wheel_down':
            body += arrow(32, 44, 8, 'down', GOLD)
        if hi in ('x1', 'x2'):
            body += f'<rect x="7" y="{30 if hi == "x2" else 40}" width="9" height="8" rx="2" fill="{GOLD}"/>'
            body += text(34, 51, '4' if hi == 'x1' else '5', 18)
        return svg(64, body)
    for b in ('left', 'right', 'middle', 'x1', 'x2', 'wheel_up', 'wheel_down'):
        write(f'keyboard/mouse_{b}.svg', mouse(b))


# ------------------------------------------------------------------ Xbox-style pad
def xbox():
    for k, c in {'a': '#5DB13C', 'b': '#D8403A', 'x': '#2F80D6', 'y': '#E8B820'}.items():
        write(f'xbox/{k}.svg', svg(64, round_button('#1A1A1A', k.upper(), c, 34)))
    write('xbox/lb.svg', svg(88, shoulder(88, 'LB')))
    write('xbox/rb.svg', svg(88, shoulder(88, 'RB')))
    write('xbox/lt.svg', svg(64, shoulder(64, 'LT', tall=True, size=22)))
    write('xbox/rt.svg', svg(64, shoulder(64, 'RT', tall=True, size=22)))
    # Start = the menu button (three lines), Back = the view button (two windows)
    menu = ''.join(f'<rect x="21" y="{y}" width="22" height="3.5" rx="1.5" fill="{INK}"/>' for y in (23, 30, 37))
    write('xbox/start.svg', svg(64, round_button('#1A1A1A', inner=menu)))
    view = f'<rect x="20" y="22" width="16" height="13" rx="2" fill="none" stroke="{INK}" stroke-width="3"/><rect x="28" y="29" width="16" height="13" rx="2" fill="#1A1A1A" stroke="{INK}" stroke-width="3"/>'
    write('xbox/back.svg', svg(64, round_button('#1A1A1A', inner=view)))
    for d in ('up', 'down', 'left', 'right'):
        write(f'xbox/dpad_{d}.svg', svg(64, dpad(d)))
    for s in ('ls', 'rs'):
        write(f'xbox/{s}.svg', svg(64, stick(s[0].upper())))
        for d in ('up', 'down', 'left', 'right'):
            write(f'xbox/{s}_{d}.svg', svg(64, stick(s[0].upper(), d)))


# ------------------------------------------------------------------ PlayStation-style pad
def playstation():
    blue, red, pink, teal = '#7DA7E8', '#E86A6A', '#D98AD9', '#4FC9A8'
    write('playstation/cross.svg', svg(64, round_button('#1A1A1A', inner=f'<path d="M22,22 L42,42 M42,22 L22,42" stroke="{blue}" stroke-width="5" stroke-linecap="round"/>')))
    write('playstation/circle.svg', svg(64, round_button('#1A1A1A', inner=f'<circle cx="32" cy="32" r="11" fill="none" stroke="{red}" stroke-width="4.5"/>')))
    write('playstation/square.svg', svg(64, round_button('#1A1A1A', inner=f'<rect x="21.5" y="21.5" width="21" height="21" fill="none" stroke="{pink}" stroke-width="4.5"/>')))
    write('playstation/triangle.svg', svg(64, round_button('#1A1A1A', inner=f'<path d="M32,19 L44.5,41 L19.5,41 Z" fill="none" stroke="{teal}" stroke-width="4.5" stroke-linejoin="round"/>')))
    write('playstation/l1.svg', svg(88, shoulder(88, 'L1')))
    write('playstation/r1.svg', svg(88, shoulder(88, 'R1')))
    write('playstation/l2.svg', svg(64, shoulder(64, 'L2', tall=True, size=22)))
    write('playstation/r2.svg', svg(64, shoulder(64, 'R2', tall=True, size=22)))

    def pill(inner):
        return (f'<rect x="4" y="15" width="56" height="36" rx="18" fill="#0E0B09"/>'
                f'<rect x="4" y="13" width="56" height="36" rx="18" fill="#3A332C" stroke="{GOLD}" stroke-width="3"/>' + inner)
    write('playstation/options.svg', svg(64, pill(''.join(f'<rect x="22" y="{y}" width="20" height="3.5" rx="1.5" fill="{INK}"/>' for y in (22, 29.5, 37)))))
    write('playstation/share.svg', svg(64, pill(f'<path d="M32,21 L32,41 M25,28 L32,21 L39,28" stroke="{INK}" stroke-width="3.5" fill="none" stroke-linecap="round" stroke-linejoin="round"/>')))
    for d in ('up', 'down', 'left', 'right'):
        write(f'playstation/dpad_{d}.svg', svg(64, dpad(d)))
    for s, lab in (('ls', 'L3'), ('rs', 'R3')):
        write(f'playstation/{s}.svg', svg(64, stick(lab)))
        for d in ('up', 'down', 'left', 'right'):
            write(f'playstation/{s}_{d}.svg', svg(64, stick(lab, d)))


# ------------------------------------------------------------------ the N64 controller (the game's own prompts)
def n64():
    write('n64/a.svg', svg(64, round_button('#2B4FD8', 'A', INK, 34)))
    write('n64/b.svg', svg(64, round_button('#2E9A3E', 'B', INK, 34)))
    write('n64/start.svg', svg(64, round_button('#D2302A', 'START', INK, 13)))
    write('n64/z.svg', svg(64, shoulder(64, 'Z', fill='#5A5A5A', tall=True, size=28)))
    write('n64/l.svg', svg(88, shoulder(88, 'L', fill='#5A5A5A')))
    write('n64/r.svg', svg(88, shoulder(88, 'R', fill='#5A5A5A')))
    for d in ('up', 'down', 'left', 'right'):
        write(f'n64/c_{d}.svg', svg(64, round_button('#F0C020', inner=arrow(32, 32, 11, d, '#3A2A00'))))
        write(f'n64/dpad_{d}.svg', svg(64, dpad(d, '#5A5A5A')))
        write(f'n64/stick_{d}.svg', svg(64, n64_stick(d)))
    write('n64/stick.svg', svg(64, n64_stick(None)))


def n64_stick(direction):
    gate = ' '.join(f'{32 + 28 * c:.1f},{32 + 28 * s:.1f}' for c, s in
                    [(1, 0), (0.71, 0.71), (0, 1), (-0.71, 0.71), (-1, 0), (-0.71, -0.71), (0, -1), (0.71, -0.71)])
    b = (f'<polygon points="{gate}" fill="#0E0B09" transform="translate(0,1.5)"/>'
         f'<polygon points="{gate}" fill="#4A4A4A" stroke="{GOLD}" stroke-width="2.5"/>'
         f'<circle cx="32" cy="32" r="14" fill="#7A7A7A" stroke="#0E0B09" stroke-width="2"/><circle cx="32" cy="32" r="14" fill="url(#ball)"/>'
         '<circle cx="32" cy="32" r="8" fill="none" stroke="#5A5A5A" stroke-width="2"/>')
    if direction:
        pos = {'up': (32, 10), 'down': (32, 54), 'left': (10, 32), 'right': (54, 32)}[direction]
        b += arrow(pos[0], pos[1], 6, direction, GOLD)
    return b


# ------------------------------------------------------------------ touch screen (the on-screen controls' style: translucent rings)
def touch():
    def ring(inner, w=64):
        return (f'<circle cx="{w / 2}" cy="32" r="29" fill="#FFFFFF" fill-opacity="0.22" stroke="#FFFFFF" stroke-opacity="0.9" stroke-width="3"/>' + inner)
    for k in ('a', 'b', 'z', 'l', 'r'):
        write(f'touch/{k}.svg', svg(64, ring(text(32, 44, k.upper(), 32, '#FFFFFF'))))
    write('touch/start.svg', svg(64, ring(text(32, 37, 'START', 13, '#FFFFFF'))))
    for d in ('up', 'down', 'left', 'right'):
        write(f'touch/c_{d}.svg', svg(64, ring(arrow(32, 32, 11, d, '#F0C020'))))
        write(f'touch/dpad_{d}.svg', svg(64, ring(arrow(32, 32, 11, d, '#FFFFFF'))))
        write(f'touch/stick_{d}.svg', svg(64, touch_stick(d)))
    write('touch/stick.svg', svg(64, touch_stick(None)))


def touch_stick(direction):
    b = ('<circle cx="32" cy="32" r="29" fill="#FFFFFF" fill-opacity="0.12" stroke="#FFFFFF" stroke-opacity="0.9" stroke-width="3"/>'
         '<circle cx="32" cy="32" r="13" fill="#FFFFFF" fill-opacity="0.55"/>')
    if direction:
        pos = {'up': (32, 10), 'down': (32, 54), 'left': (10, 32), 'right': (54, 32)}[direction]
        b += arrow(pos[0], pos[1], 6, direction, '#FFFFFF')
    return b


if __name__ == '__main__':
    keyboard(); xbox(); playstation(); n64(); touch()
    print('written to', os.path.normpath(ROOT))
