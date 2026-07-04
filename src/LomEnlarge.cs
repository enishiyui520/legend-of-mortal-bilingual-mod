using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Unity.Mono;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace LomBilingual
{
    // v3: fixed big font + box auto-grows UPWARD to fit, with hard safety clamps.
    [BepInPlugin("local.lom.enlarge", "LOM Text Enlarge", "3.0")]
    public class Enlarge : BaseUnityPlugin
    {
        int size = 32;             // fixed dialogue font (F6/F7), default bigger
        const int MIN_SIZE = 28;   // never below previous default
        bool on = true;
        bool forceWhite = true;    // F9 (default ON: dialogue white)
        const float MAX_EXTRA_STORY = 360f;    // overworld dialogue: more empty screen space above
        const float MAX_EXTRA_BATTLE = 160f;   // battle screen: HUD/health bars sit right above, less room
        const int SHRINK_FLOOR = 20;           // last-resort shrink never goes below this
        float nextScan = 0f;
        float nextRich = 0f;
        List<Text> targets = new List<Text>();
        Dictionary<int, Vector2> oSize = new Dictionary<int, Vector2>();
        Dictionary<int, Vector2> oPos = new Dictionary<int, Vector2>();

        // JP-yellow colorization: only applied once text stops changing (typewriter reveal finished),
        // to avoid the game's own reveal effect (which inserts a tag at a raw char index) corrupting our tag.
        const float SETTLE = 0.15f;
        const string JP_COLOR_DIALOGUE = "<color=#FFD54A>";   // overworld story dialogue: yellow
        const string JP_COLOR_BATTLE = "<color=#FF4040>";     // battle screen (MainUI): red
        Dictionary<int, string> lastRaw = new Dictionary<int, string>();
        Dictionary<int, float> lastChangeAt = new Dictionary<int, float>();

        // Vertical status-tab labels (e.g. StatusButton_Home "門派"): add a JP column
        // to the LEFT of the existing CN column instead of stacking JP under CN,
        // since these narrow (width~20) rects render CJK vertically one char per line.
        Dictionary<string, string> shortMap = new Dictionary<string, string>();
        Dictionary<int, TMP_Text> jpSiblings = new Dictionary<int, TMP_Text>();
        float nextStatusScan = 0f;

        void Awake()
        {
            LoadShortMap();
        }

        void LoadShortMap()
        {
            try
            {
                string path = Path.Combine(Paths.GameRootPath, "Mods\\JP\\short_labels.tsv");
                if (!File.Exists(path)) { Logger.LogWarning("short_labels.tsv not found: " + path); return; }
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    string cn = Unesc(line.Substring(0, tab));
                    string jv = Unesc(line.Substring(tab + 1));
                    if (!shortMap.ContainsKey(cn)) shortMap[cn] = jv;
                }
                Logger.LogInfo("LOM shortMap loaded: " + shortMap.Count);
            }
            catch (Exception e) { Logger.LogError("LoadShortMap error: " + e); }
        }

        static string Unesc(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[i + 1];
                    if (n == 'n') { sb.Append('\n'); i++; continue; }
                    if (n == '\\') { sb.Append('\\'); i++; continue; }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        void UpdateStatusLabels()
        {
            foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(typeof(TMP_Text)))
            {
                TMP_Text t = o as TMP_Text;
                if (t == null || !t.gameObject.activeInHierarchy) continue;
                string p = PathOf(t.transform);
                if (p.IndexOf("StatusButton_", StringComparison.Ordinal) < 0) continue;
                string cn = t.text != null ? t.text.Trim() : "";
                if (cn.Length == 0) continue;
                string jv;
                if (!shortMap.TryGetValue(cn, out jv)) continue;

                int id = t.GetInstanceID();
                TMP_Text sib;
                if (!jpSiblings.TryGetValue(id, out sib) || sib == null)
                {
                    sib = CreateJpSibling(t);
                    jpSiblings[id] = sib;
                }
                if (sib != null && sib.text != jv) sib.text = jv;
            }
        }

        TMP_Text CreateJpSibling(TMP_Text src)
        {
            try
            {
                GameObject go = new GameObject("LOM_JP_" + src.gameObject.name);
                go.transform.SetParent(src.transform.parent, false);
                TextMeshProUGUI sib = go.AddComponent<TextMeshProUGUI>();
                RectTransform rt = sib.rectTransform;
                RectTransform srt = src.rectTransform;
                rt.anchorMin = srt.anchorMin; rt.anchorMax = srt.anchorMax; rt.pivot = srt.pivot;
                rt.sizeDelta = srt.sizeDelta;
                rt.anchoredPosition = srt.anchoredPosition + new Vector2(-24f, 0f);
                sib.font = src.font;
                sib.fontSize = Mathf.Max(10f, src.fontSize * 0.7f);
                sib.color = new Color32(0xFF, 0xD5, 0x4A, 0xFF);
                sib.alignment = src.alignment;
                sib.enableWordWrapping = true;
                sib.raycastTarget = false;
                return sib;
            }
            catch (Exception e) { Logger.LogError("CreateJpSibling error: " + e.Message); return null; }
        }

        void Update()
        {
            try
            {
                if (Input.GetKeyDown(KeyCode.F7)) { size += 2; Log(); }
                if (Input.GetKeyDown(KeyCode.F6)) { size -= 2; if (size < MIN_SIZE) size = MIN_SIZE; Log(); }
                if (Input.GetKeyDown(KeyCode.F5)) { on = !on; Logger.LogInfo("LOM on=" + on); if (!on) RestoreAll(); }
                if (Input.GetKeyDown(KeyCode.F9)) { forceWhite = !forceWhite; Logger.LogInfo("LOM white=" + forceWhite); }
                if (Input.GetKeyDown(KeyCode.F8)) Dump();

                if (Time.unscaledTime >= nextScan) { nextScan = Time.unscaledTime + 0.5f; Rescan(); }
                if (Time.unscaledTime >= nextRich) { nextRich = Time.unscaledTime + 0.25f; EnableRichWhereTagged(); }
                if (Time.unscaledTime >= nextStatusScan) { nextStatusScan = Time.unscaledTime + 0.3f; UpdateStatusLabels(); }
                if (!on) return;
                if (Time.timeSinceLevelLoad < 1.5f) return;   // skip load/transition frames

                for (int i = 0; i < targets.Count; i++)
                {
                    Text t = targets[i];
                    if (t == null || !t.gameObject.activeInHierarchy) continue;
                    if (string.IsNullOrEmpty(t.text)) continue;

                    TryColorizeJp(t);
                    if (!t.supportRichText) t.supportRichText = true;   // let <color> tags (yellow JP) render
                    if (t.resizeTextForBestFit) t.resizeTextForBestFit = false;
                    bool isBattle = PathOf(t.transform).IndexOf("MainUI", StringComparison.Ordinal) >= 0;
                    if (t.fontSize != size) t.fontSize = size;
                    if (t.verticalOverflow != VerticalWrapMode.Overflow) t.verticalOverflow = VerticalWrapMode.Overflow;
                    if (t.horizontalOverflow != HorizontalWrapMode.Wrap) t.horizontalOverflow = HorizontalWrapMode.Wrap;

                    if (forceWhite)
                    {
                        Color c = t.color;
                        if (c.r < 0.98f || c.g < 0.98f || c.b < 0.98f) t.color = new Color(1f, 1f, 1f, c.a);
                    }

                    RectTransform tr = t.rectTransform;
                    if (tr.rect.width < 50f) continue;   // invalid width -> skip (avoids preferredHeight blowup)
                    float maxExtra = isBattle ? MAX_EXTRA_BATTLE : MAX_EXTRA_STORY;
                    float ph = t.preferredHeight;
                    if (ph <= 0f || ph > 1500f) continue; // sanity guard
                    float need = ph + 20f;

                    float extra = GrowUp(tr, need, maxExtra);
                    RectTransform box = t.transform.parent as RectTransform;
                    if (box != null) SetExtra(box, extra, maxExtra);

                    // Last resort: if growing the box to its cap still isn't enough room
                    // (very long line + tight battle screen), shrink THIS text's font a
                    // little instead of letting it overflow and get covered by other UI.
                    float allowedHeight = tr.sizeDelta.y > 0f ? tr.sizeDelta.y : need;
                    int guard = 0;
                    while (t.preferredHeight > allowedHeight + 2f && t.fontSize > SHRINK_FLOOR && guard < 12)
                    {
                        t.fontSize -= 1;
                        guard++;
                    }
                }
            }
            catch (Exception e) { Logger.LogError("LOM err: " + e.Message); }
        }

        float GrowUp(RectTransform rt, float need, float maxExtra)
        {
            int id = rt.GetInstanceID();
            if (!oSize.ContainsKey(id)) { oSize[id] = rt.sizeDelta; oPos[id] = rt.anchoredPosition; }
            Vector2 os = oSize[id], op = oPos[id];
            float target = Mathf.Max(os.y, need);
            float extra = Mathf.Clamp(target - os.y, 0f, maxExtra);
            rt.sizeDelta = new Vector2(os.x, os.y + extra);
            rt.anchoredPosition = new Vector2(op.x, op.y + extra * rt.pivot.y);
            return extra;
        }

        void SetExtra(RectTransform rt, float extra, float maxExtra)
        {
            extra = Mathf.Clamp(extra, 0f, maxExtra);
            int id = rt.GetInstanceID();
            if (!oSize.ContainsKey(id)) { oSize[id] = rt.sizeDelta; oPos[id] = rt.anchoredPosition; }
            Vector2 os = oSize[id], op = oPos[id];
            rt.sizeDelta = new Vector2(os.x, os.y + extra);
            rt.anchoredPosition = new Vector2(op.x, op.y + extra * rt.pivot.y);
        }

        void RestoreAll()
        {
            foreach (Text t in targets)
            {
                if (t == null) continue;
                Restore(t.rectTransform);
                Restore(t.transform.parent as RectTransform);
            }
        }
        void Restore(RectTransform rt)
        {
            if (rt == null) return;
            int id = rt.GetInstanceID();
            if (oSize.ContainsKey(id)) { rt.sizeDelta = oSize[id]; rt.anchoredPosition = oPos[id]; }
        }

        // Applies JP-yellow coloring only after the text has stopped changing for SETTLE
        // seconds (i.e. the typewriter reveal is done), and only once (guarded by checking
        // for an existing tag). This never touches text while the game is still mutating it.
        void TryColorizeJp(Text t)
        {
            string cur = t.text;
            if (string.IsNullOrEmpty(cur)) return;
            if (cur.IndexOf("<color=", StringComparison.Ordinal) >= 0) return; // already colorized

            int id = t.GetInstanceID();
            string prev;
            if (!lastRaw.TryGetValue(id, out prev) || prev != cur)
            {
                lastRaw[id] = cur;
                lastChangeAt[id] = Time.unscaledTime;
                return;
            }
            if (Time.unscaledTime - lastChangeAt[id] < SETTLE) return;

            int b = FindJpBoundary(cur);
            if (b <= 0 || b >= cur.Length) return;

            string path = PathOf(t.transform);
            bool isBattle = path.IndexOf("MainUI", StringComparison.Ordinal) >= 0;
            string color = isBattle ? JP_COLOR_BATTLE : JP_COLOR_DIALOGUE;

            t.text = cur.Substring(0, b) + color + cur.Substring(b) + "</color>";
            t.supportRichText = true;
        }

        static bool IsKana(char c)
        {
            return (c >= '぀' && c <= 'ヿ') || (c >= 'ㇰ' && c <= 'ㇿ');
        }

        // Finds where the Japanese line begins: first kana character, then walk back
        // to the start of that line. Traditional Chinese never contains kana, so this
        // reliably separates the CN block from the JP block regardless of internal
        // line breaks within either block. Returns -1 if no kana found (leave uncolored).
        static int FindJpBoundary(string s)
        {
            int kanaIdx = -1;
            for (int i = 0; i < s.Length; i++) { if (IsKana(s[i])) { kanaIdx = i; break; } }
            if (kanaIdx < 0) return -1;
            int nl = s.LastIndexOf('\n', kanaIdx);
            return nl >= 0 ? nl + 1 : 0;
        }

        // Any text element that contains a <color=...> tag gets rich text enabled,
        // so the tag renders as color instead of showing the literal code anywhere.
        void EnableRichWhereTagged()
        {
            foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(typeof(Text)))
            {
                Text t = o as Text;
                if (t == null || !t.gameObject.activeInHierarchy) continue;
                if (t.supportRichText) continue;
                string s = t.text;
                if (!string.IsNullOrEmpty(s) && s.IndexOf("<color=", StringComparison.Ordinal) >= 0)
                    t.supportRichText = true;
            }
        }

        bool Wanted(Text t)
        {
            string p = PathOf(t.transform);
            if (t.name == "StoryText" && p.IndexOf("SayDialog") >= 0) return true;
            if (t.name == "Text" && (p.IndexOf("EnemyDialog") >= 0 || p.IndexOf("PlayerDialog") >= 0)) return true;
            return false;
        }

        void Rescan()
        {
            bool ok = targets.Count > 0;
            for (int i = 0; i < targets.Count; i++) if (targets[i] == null) { ok = false; break; }
            if (ok) return;
            targets.Clear();
            foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(typeof(Text)))
            {
                Text t = o as Text;
                if (t == null) continue;
                if (Wanted(t)) targets.Add(t);
            }
            Logger.LogInfo("LOM targets=" + targets.Count);
        }

        void Dump()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("\n=== F8 SHORT labels (len<=8) with geometry ===\n");
            foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(typeof(Text)))
            {
                Text t = o as Text;
                if (t == null || !t.gameObject.activeInHierarchy) continue;
                string txt = t.text;
                if (string.IsNullOrEmpty(txt)) continue;
                string tr = txt.Trim();
                if (tr.Length < 1 || tr.Length > 8) continue;
                RectTransform r = t.rectTransform;
                Vector3 wp = r.position;
                sb.Append("UI '" + tr.Replace("\n", "\\n") + "' fs" + t.fontSize + " ap(" + (int)r.anchoredPosition.x + "," + (int)r.anchoredPosition.y + ") sd(" + (int)r.sizeDelta.x + "," + (int)r.sizeDelta.y + ") wp(" + (int)wp.x + "," + (int)wp.y + ") " + PathOf(t.transform) + "\n");
            }
            foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(typeof(TMPro.TMP_Text)))
            {
                TMPro.TMP_Text t = o as TMPro.TMP_Text;
                if (t == null || !t.gameObject.activeInHierarchy) continue;
                string txt = t.text;
                if (string.IsNullOrEmpty(txt)) continue;
                string tr = txt.Trim();
                if (tr.Length < 1 || tr.Length > 8) continue;
                RectTransform r = t.rectTransform;
                Vector3 wp = r.position;
                sb.Append("TMP '" + tr.Replace("\n", "\\n") + "' fs" + t.fontSize.ToString("0") + " ap(" + (int)r.anchoredPosition.x + "," + (int)r.anchoredPosition.y + ") sd(" + (int)r.sizeDelta.x + "," + (int)r.sizeDelta.y + ") wp(" + (int)wp.x + "," + (int)wp.y + ") " + PathOf(t.transform) + "\n");
            }
            Logger.LogInfo(sb.ToString());
        }
        static string Hex(Color c) { return ((int)(c.r * 255)).ToString("X2") + ((int)(c.g * 255)).ToString("X2") + ((int)(c.b * 255)).ToString("X2"); }
        void Log() { Logger.LogInfo("LOM size=" + size + " white=" + forceWhite); }
        static string PathOf(Transform tr) { string s = tr.name; Transform p = tr.parent; while (p != null) { s = p.name + "/" + s; p = p.parent; } return s; }
    }
}
