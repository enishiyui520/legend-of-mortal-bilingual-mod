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

jp = {}
with open(jp_path, encoding='utf-8-sig', newline='') as f:
    for row in csv.reader(f):
        if len(row) >= 2:
            jp[row[0]] = row[1]

BS = chr(0x5c)
def esc(s):
    return s.replace(BS, BS + BS).replace(NL, BS + 'n').replace(chr(0x09), ' ')

seen = {}
for k, jpv in jp.items():
    if k not in base:
        continue
    cn = conv(base[k]).strip()
    jv = conv(jpv).strip()
    if not (1 <= len(cn) <= 6):
        continue
    if cn == jv:
        continue
    if cn not in seen:
        seen[cn] = jv

out = os.path.join(SC, "pkg", "活俠傳_繁中日文雙語MOD", "Mods", "JP", "short_labels.tsv")
os.makedirs(os.path.dirname(out), exist_ok=True)
with open(out, "w", encoding='utf-8') as w:
    for cn, jv in seen.items():
        w.write(esc(cn) + chr(0x09) + esc(jv) + NL)
print("short_labels entries:", len(seen), "->", out, os.path.getsize(out), "bytes")
