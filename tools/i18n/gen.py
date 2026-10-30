# Собирает src\PulseBar\L.Strings2.cs из tools\i18n\<язык>.json. Новая строка интерфейса: добавить английский текст
# в strings.json, перевод — в каждый <язык>.json, запустить: python tools\i18n\gen.py
import json, os, re, sys

here = os.path.dirname(os.path.abspath(__file__))
strings = json.load(open(os.path.join(here, "strings.json"), encoding="utf-8"))
order = ["ru", "uk", "de", "fr", "es", "pt", "it", "pl", "zh", "ja"]
problems = []


def cs(s):
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


out = ["using System.Collections.Generic;", "", "namespace PulseBar;", "",
       "// Строки интерфейса 2.x. Создано tools/i18n/gen.py из tools/i18n/*.json — правьте JSON, а не этот файл.",
       "// Ключ — английская строка; чего нет в языке — показывается по-английски.",
       "internal static partial class L", "{",
       "\tprivate static readonly Dictionary<string, Dictionary<string, string>> Strings2 = new Dictionary<string, Dictionary<string, string>>",
       "\t{"]
for code in order:
    path = os.path.join(here, code + ".json")
    if not os.path.exists(path):
        problems.append(f"{code}: no file")
        continue
    data = json.load(open(path, encoding="utf-8"))
    out.append(f"\t\t[{cs(code)}] = new Dictionary<string, string>")
    out.append("\t\t{")
    rows = []
    for en in strings:
        tr = data.get(en)
        if not isinstance(tr, str) or not tr.strip():
            problems.append(f"{code}: missing {en!r}")
            continue
        if sorted(re.findall(r"\{\d\}", en)) != sorted(re.findall(r"\{\d\}", tr)):
            problems.append(f"{code}: placeholder mismatch {en!r} -> {tr!r}")
            continue
        rows.append(f"\t\t\t[{cs(en)}] = {cs(tr.strip())}")
    out.append(",\n".join(rows))
    out.append("\t\t},")
out[-1] = out[-1].rstrip(",")
out += ["\t};", "}", ""]
dst = os.path.join(here, "..", "..", "src", "PulseBar", "L.Strings2.cs")
open(dst, "w", encoding="utf-8", newline="").write("\r\n".join(out))
print("written", dst)
print("\n".join(problems) if problems else "no problems")
