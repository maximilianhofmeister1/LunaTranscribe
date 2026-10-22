import sys, os
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont
src, out = sys.argv[1], sys.argv[2]
def make(path, axes, family, sub, weightclass, dest):
    f = TTFont(path)
    inst = instantiateVariableFont(f, axes)
    name = inst['name']
    for rec in list(name.names):
        if rec.nameID in (1,2,3,4,6,16,17,21,22,25):
            name.removeNames(nameID=rec.nameID)
    legacy_sub = 'Bold' if weightclass == 700 else 'Regular'
    full = family if sub == 'Regular' else f'{family} {sub}'
    ps = full.replace(' ', '') if sub=='Regular' else f"{family.replace(' ','')}-{sub}"
    for nid, val in ((1, family), (2, legacy_sub), (3, ps + ';LunaTranscribe'), (4, full), (6, ps), (16, family), (17, sub)):
        name.setName(val, nid, 3, 1, 0x409)
        name.setName(val, nid, 1, 0, 0)
    inst['OS/2'].usWeightClass = weightclass
    fs = inst['OS/2'].fsSelection
    fs &= ~((1<<0)|(1<<5)|(1<<6))
    fs |= (1<<5) if weightclass == 700 else (1<<6)
    inst['OS/2'].fsSelection = fs
    inst['head'].macStyle = 1 if weightclass == 700 else 0
    for t in ('STAT','fvar','avar','gvar','HVAR','MVAR'):
        if t in inst: del inst[t]
    inst.save(os.path.join(out, dest))
    print(dest)
fr = os.path.join(src, 'Fraunces.ttf'); fg = os.path.join(src, 'Figtree.ttf')
make(fr, {'wght':520,'SOFT':100,'WONK':1,'opsz':28}, 'Luna Display', 'Regular', 400, 'LunaDisplay.ttf')
make(fr, {'wght':600,'SOFT':60,'WONK':1,'opsz':16}, 'Luna Title', 'Regular', 400, 'LunaTitle.ttf')
for w, sub in ((400,'Regular'),(500,'Medium'),(600,'SemiBold'),(700,'Bold')):
    make(fg, {'wght':w}, 'Luna Text', sub, w, f'LunaText-{sub}.ttf')
