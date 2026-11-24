"""
List string literals that look player-facing (letters; not in logs, comments or attributes), one per line:
    file:line: text
Used while extracting strings to Resources/Loc/en.json (docs/localization.md).

    python -P tools/loc/list_literals.py Assets/ChibiClash/Scripts/UI/Hud.cs
"""
import re
import sys

LIT = re.compile(r'(?<![@$\\])"((?:[^"\\\n]|\\.)*)"')
SKIP = ('Debug.Log', '[Header', 'Tooltip(', 'Loc.T(', 'Resources.Load', 'GetComponent', 'Shader.Find',
        'SetNextControlName', 'FocusControl', 'GetNameOfFocusedControl', 'PlayerPrefs', 'case "', 'new GameObject(',
        'Application.OpenURL', 'LocCache(')


def main():
    for f in sys.argv[1:]:
        for i, line in enumerate(open(f, encoding='utf-8-sig'), 1):
            s = line.strip()
            if s.startswith('//') or any(k in line for k in SKIP):
                continue
            for m in LIT.finditer(line):
                t = m.group(1)
                if re.search(r'[A-Za-z]{2}', t) and not re.fullmatch(r'[a-z_.0-9/]+', t):
                    print("%s:%d: %s" % (f.replace('\\', '/').split('/')[-1], i, t))


if __name__ == '__main__':
    main()
