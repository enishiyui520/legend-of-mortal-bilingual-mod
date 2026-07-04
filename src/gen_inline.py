# -*- coding: utf-8 -*-
import os, re, csv, json
SC = r"C:\Users\User\AppData\Local\Temp\claude\C--Users-User\b219f5be-09f7-4a66-ae5b-3673849a9c14\scratchpad"
base = json.load(open(os.path.join(SC, "base_zhtw.json"), encoding='utf-8'))
jp_path = os.path.join(SC, "jpmod_v2", "extracted", "LOM_JP_Mod_v2.0", "Mods", "JP", "Stringtable.csv")
BSN = chr(0x5c) + chr(0x6e)
NL = chr(0x0a)
TMP = re.compile(r'</?(?:color|size|b|i|u|s|alpha|material|font|sprite|style|mark|sub|sup|nobr|indent|align|voffset|cspace|mspace|pos|space|width|line-height|noparse|gradient|rotate|link|lowercase|uppercase|smallcaps)(?:=[^>]*)?>', re.I)

def conv(s):
    s = s.replace(chr(0x0d) + NL, NL).replace(chr(0x0d), NL).replace(BSN, NL)
    return TMP.sub('', s)

# exclude: placeholders, identical, and tight calligraphy nameplate names/titles
NAME_NS = ('Character/', 'CharacterTitle/', 'CombatCharacter/',
           'PlayerInfo/Title', 'Position/title', 'System/')
def should_double(k, tw, jp):
    if '{' in tw or '}' in tw or '{' in jp or '}' in jp:
        return False
    if tw.strip() == jp.strip():
        return False
    if k.startswith(NAME_NS):
        return False
    return True

jp = {}
with open(jp_path, encoding='utf-8-sig', newline='') as f:
    for row in csv.reader(f):
        if len(row) >= 2:
            jp[row[0]] = row[1]

# NOTE: do NOT embed <color> tags in the CSV. The game's own typewriter/reveal
# effect inserts its own <color=#FFFFFF00> tag at a raw character index into the
# string as it types, and that insertion can land INSIDE our tag and corrupt it
# (e.g. "<color=#FFD54A>" splits into "<col" + inserted-tag + "or=#FFD54A>").
# Coloring the JP line is done post-hoc by the plugin, only after the text has
# fully stopped changing (reveal finished), which sidesteps the corruption.
rows = []; dbl = 0; single = 0
for k, v in jp.items():
    jpv = conv(v).lstrip(NL)
    tw = conv(base[k]) if k in base else None
    # some LegendInfo entries start with a stylistic leading blank line in the
    # vanilla text (translators preserved it too); harmless single-language,
    # but wastes a line once we stack JP underneath in a fixed-height panel
    # (e.g. CG_Panel), causing overflow that gets clipped. Strip before joining.
    if tw is not None:
        tw = tw.lstrip(NL)
    if tw is not None and should_double(k, tw, jpv):
        rows.append([k, tw + NL + jpv]); dbl += 1
    else:
        rows.append([k, tw if tw is not None else jpv]); single += 1

out = os.path.join(SC, "Stringtable.inline.csv")
with open(out, "w", encoding='utf-8-sig', newline='') as w:
    csv.writer(w, quoting=csv.QUOTE_MINIMAL).writerows(rows)
print("inline everywhere: doubled=%d single=%d size=%d" % (dbl, single, os.path.getsize(out)))

from collections import Counter
db = Counter(r[0].split('/')[0] for r in rows if NL in r[1])
print("doubled namespaces:", db.most_common(20))
