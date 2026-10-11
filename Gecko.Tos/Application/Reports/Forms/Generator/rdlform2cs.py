"""Emit a C# FormItem list for an RDL's body: textboxes, lines and rectangles at absolute inch positions.
Tablixes are reported as TABLE placeholders (hand-written per form). Expressions over the form's dataset are
translated to C# over a `f` (BookingForm) parameter; anything not translated is flagged with TODO."""
import sys, re
import xml.etree.ElementTree as ET
sys.stdout.reconfigure(encoding='utf-8')

def inch(v):
    if v is None: return 0.0
    v=v.strip()
    for unit,k in (('in',1.0),('cm',1/2.54),('mm',1/25.4),('pt',1/72.0)):
        if v.endswith(unit): return float(v[:-len(unit)])*k
    return float(v)

t=ET.parse(sys.argv[1]).getroot(); ns=t.tag.split('}')[0]+'}'
def g(e,n):
    x=e.find(ns+n); return x.text if x is not None else None

def expr(v):
    if v is None: return '""'
    if not v.startswith('='):
        return 'S(' + csstr(v) + ')'
    e=v[1:].strip()
    e=re.sub(r'First\(Fields!(\w+)\.Value\s*,\s*"[^"]*"\)', r'f.\1', e)
    e=re.sub(r'Fields!(\w+)\.Value', r'f.\1', e)
    e=re.sub(r'(?i)\bFormat\(', 'D(', e)
    e=re.sub(r'(?i)\bvbcrlf\b', '"\\n"', e)
    e=e.replace(' & ', ' + ').replace('&', '+')
    e=re.sub(r'\s+', ' ', e)
    if re.search(r'[A-Za-z_]+\(', re.sub(r'\b(D|S)\(', '', e)) or '!' in e or 'Parameters' in e or 'Globals' in e:
        return 'S(' + e + ') /* TODO */'
    return 'S(' + e + ')'

def csstr(s):
    return '"' + s.replace('\\','\\\\').replace('"','\\"').replace('\r','').replace('\n','\\n') + '"'

def style(tb):
    sizes=[x.text for x in tb.iter(ns+'FontSize')]
    size=float(sizes[0].replace('pt','')) if sizes else 10.0
    bold=any(x.text and x.text.lower()=='bold' for x in tb.iter(ns+'FontWeight'))
    aligns=[x.text for x in tb.iter(ns+'TextAlign') if x.text]
    align=aligns[0] if aligns else 'General'
    st=tb.find(ns+'Style')
    borders={}
    if st is not None:
        for side in ('Border','TopBorder','BottomBorder','LeftBorder','RightBorder'):
            b=st.find(ns+side)
            if b is not None:
                s=b.find(ns+'Style')
                borders[side]=s.text if s is not None else 'Solid' if side!='Border' else None
    return size,bold,align,borders

def text_of(tb):
    paras=[]
    for p in tb.iter(ns+'Paragraph'):
        runs=[ (r.find(ns+'Value').text if r.find(ns+'Value') is not None else '') for r in p.iter(ns+'TextRun')]
        paras.append(runs)
    # constant if every run is literal
    if all(not (r or '').startswith('=') for rs in paras for r in rs):
        return 'S(' + csstr('\n'.join(''.join(r or '' for r in rs) for rs in paras)) + ')'
    parts=[]
    for i,rs in enumerate(paras):
        if i>0: parts.append('S("\\n")')
        for r in rs: parts.append(expr(r or ''))
    return ' + '.join(parts) if len(parts)>1 else parts[0]

out=[]
def walk(items, ox, oy, where):
    for c in items:
        tag=c.tag.replace(ns,'')
        top=oy+inch(g(c,'Top')); left=ox+inch(g(c,'Left')); w=inch(g(c,'Width')); h=inch(g(c,'Height'))
        name=c.get('Name')
        if tag=='Textbox':
            size,bold,align,borders=style(c)
            b=''.join(k[0] if k!='Border' else 'A' for k,v in borders.items() if v and v!='None')
            dotted=any(v=='Dotted' for v in borders.values())
            out.append((top, f'        T({top:.3f}, {left:.3f}, {w:.3f}, {h:.3f}, {text_of(c)}, {size:g}f, Align.{align}' +
                       (', bold: true' if bold else '') + (f', border: "{b}"' if b else '') + (', dotted: true' if dotted else '') + f'),   // {name}'))
        elif tag=='Line':
            st=[x.text for x in c.iter(ns+'Style')]
            dotted='Dotted' in st or 'Dashed' in st
            out.append((top, f'        L({top:.3f}, {left:.3f}, {w:.3f}, {h:.3f}' + (', dotted: true' if dotted else '') + f'),   // {name}'))
        elif tag=='Rectangle':
            size,bold,align,borders=style(c)
            b=''.join(k[0] if k!='Border' else 'A' for k,v in borders.items() if v and v!='None')
            if b: out.append((top, f'        R({top:.3f}, {left:.3f}, {w:.3f}, {h:.3f}, "{b}"),   // {name}'))
            ri=c.find(ns+'ReportItems')
            if ri is not None: walk(ri, left, top, name)
        elif tag=='Tablix':
            out.append((top, f'        // TABLE {name} top={top:.3f} left={left:.3f} w={w:.3f} h={h:.3f} bottom={top+h:.3f}'))
        else:
            out.append((top, f'        // {tag} {name} at {top:.3f},{left:.3f} (not handled)'))

body=t.find('.//'+ns+'Body')
print(f'// {sys.argv[1]}: body height {inch(g(body,"Height")):.3f}in')
walk(body.find(ns+'ReportItems'), 0, 0, 'Body')
for top,line in sorted(out,key=lambda x:x[0]): print(line)
