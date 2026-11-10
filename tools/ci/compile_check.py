"""
Fast C# compile check for Chibi Clash WITHOUT the Unity editor (it may be open; this never touches the project lock).

    python tools/ci/compile_check.py            # runtime (Assembly-CSharp) + editor/tests (Assembly-CSharp-Editor)
    python tools/ci/compile_check.py --runtime  # runtime only

Uses the Roslyn compiler that ships inside the Unity editor install, with the exact references, defines and language
version from the Unity-generated Assembly-CSharp*.csproj files. Source files are discovered fresh from Assets/ (so new
files are included even though the generated .csproj lists only old ones). Prints compiler errors, exit code 0 = clean.

It only checks that the code compiles. Unity still has to import, and tests still have to run in Unity
(Test Runner, or the unity-mcp TestRunnerApi call). Output DLLs go to Temp/compile_check/ and are never loaded by Unity.
"""
import os
import re
import subprocess
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
UNITY = r"C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Data"
DOTNET = os.path.join(UNITY, "NetCoreRuntime", "dotnet.exe")
CSC = os.path.join(UNITY, "DotNetSdk", "sdk", "8.0.318", "Roslyn", "bincore", "csc.dll")
OUT = os.path.join(ROOT, "Temp", "compile_check")
SCRIPT_ASSEMBLIES = os.path.join(ROOT, "Library", "ScriptAssemblies")


def parse_csproj(name):
    text = open(os.path.join(ROOT, name), encoding="utf-8-sig").read()
    refs = re.findall(r"<HintPath>(.*?)</HintPath>", text)
    for proj in re.findall(r'<ProjectReference Include="([^"]+)\.csproj"', text):
        dll = os.path.join(SCRIPT_ASSEMBLIES, os.path.basename(proj) + ".dll")
        if os.path.exists(dll):
            refs.append(dll)
    defines = re.search(r"<DefineConstants>(.*?)</DefineConstants>", text).group(1)
    lang = re.search(r"<LangVersion>(.*?)</LangVersion>", text).group(1)
    return refs, defines, lang


def sources(editor):
    """Assembly-CSharp = Assets/**/*.cs outside Editor folders and outside folders that own an asmdef;
    Assembly-CSharp-Editor = the Editor folders."""
    asmdef_dirs = set()
    for d, _, files in os.walk(os.path.join(ROOT, "Assets")):
        if any(f.endswith(".asmdef") for f in files):
            asmdef_dirs.add(os.path.normcase(d))
    out = []
    for d, _, files in os.walk(os.path.join(ROOT, "Assets")):
        nd = os.path.normcase(d)
        if any(nd == a or nd.startswith(a + os.sep) for a in asmdef_dirs):
            continue
        parts = [p.lower() for p in os.path.relpath(d, ROOT).split(os.sep)]
        if ("editor" in parts) != editor:
            continue
        out += [os.path.join(d, f) for f in files if f.endswith(".cs")]
    return sorted(out)


def compile_one(csproj, editor, extra_refs, out_name):
    refs, defines, lang = parse_csproj(csproj)
    refs = [r for r in refs if not os.path.basename(r).startswith("Assembly-CSharp")] + extra_refs
    os.makedirs(OUT, exist_ok=True)
    rsp = os.path.join(OUT, out_name + ".rsp")
    dll = os.path.join(OUT, out_name + ".dll")
    with open(rsp, "w", encoding="utf-8") as f:
        f.write("-target:library\n-nologo\n-nostdlib+\n-noconfig\n-nowarn:0169,0414,0649,1591,0618,0067,0219,0168\n")
        f.write("-langversion:%s\n-define:%s\n-out:\"%s\"\n" % (lang, defines, dll))
        for r in refs:
            f.write('-r:"%s"\n' % r)
        for s in sources(editor):
            f.write('"%s"\n' % s)
    p = subprocess.run([DOTNET, CSC, "@" + rsp], capture_output=True, text=True)
    errors = [l for l in (p.stdout + p.stderr).splitlines() if ": error " in l]
    return p.returncode, errors, dll


def main():
    rc, errs, runtime_dll = compile_one("Assembly-CSharp.csproj", False, [], "Assembly-CSharp")
    for e in errs:
        print(e.replace(ROOT + os.sep, ""))
    print("Assembly-CSharp: %s (%d errors)" % ("OK" if rc == 0 else "FAILED", len(errs)))
    if rc != 0 or "--runtime" in sys.argv:
        sys.exit(rc)
    rc2, errs2, _ = compile_one("Assembly-CSharp-Editor.csproj", True, [runtime_dll], "Assembly-CSharp-Editor")
    for e in errs2:
        print(e.replace(ROOT + os.sep, ""))
    print("Assembly-CSharp-Editor (tools + tests): %s (%d errors)" % ("OK" if rc2 == 0 else "FAILED", len(errs2)))
    sys.exit(rc2)


if __name__ == "__main__":
    main()
