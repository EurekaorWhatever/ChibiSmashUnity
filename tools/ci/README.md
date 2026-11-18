# CI

| Workflow | Needs Unity licence | Does |
|---|---|---|
| `.github/workflows/fast-checks.yml` | no | `gen_movesets.py --check`, `check_stage_geometry.py`, Python syntax check of `tools/` |
| `.github/workflows/tests.yml` | yes | EditMode tests via game-ci/unity-test-runner |
| `.github/workflows/web-build.yml` | yes | WebGL build via game-ci/unity-builder, published to GitHub Pages |

`tests.yml` skips (green, with a notice annotation) when `UNITY_LICENSE` is not set, so a skipped run verifies nothing:
check the annotation.

## Adding the Unity secrets (repo owner, one time)

GitHub > Settings > Secrets and variables > Actions > New repository secret:

- `UNITY_LICENSE`: contents of the `.ulf` file (personal licence: follow https://game.ci/docs/github/activation to
  generate it with the game-ci activation workflow).
- `UNITY_EMAIL`: Unity account email.
- `UNITY_PASSWORD`: Unity account password.

Secrets are not exposed to pull requests from forks; those runs skip the Unity job.

## Web build / GitHub Pages

`.github/workflows/web-build.yml` builds the WebGL version on every push to `main` (and on demand from Actions >
web-build > Run workflow), then publishes it to **https://eurekaorwhatever.github.io/ChibiSmashUnity/**. Whatever
reaches `main` (local edits, Claude Code with Unity MCP, merged pull requests) shows up on the link after the build:
expect about 30-60 minutes, longer the first time. Pushes that change only `docs/`, `design/`, `production/` or
Markdown do not rebuild.

One-time setup by the repository owner:

1. Add the three Unity secrets above (`UNITY_LICENSE`, `UNITY_EMAIL`, `UNITY_PASSWORD`). Without them the job is
   skipped with a notice and the link keeps the last published version.
2. Settings > Pages > Build and deployment > Source: **GitHub Actions**.
3. Run it once: Actions > web-build > Run workflow. The link works when the `deploy` job is green.

The build itself is `ChibiClash.EditorTools.WebBuild.BuildFromCommandLine` (`Assets/ChibiClash/Editor/WebBuild.cs`).
It uses the scenes enabled in Build Settings. It also uses Gzip with the decompression fallback, because GitHub
Pages cannot send Brotli headers. It sets that only for the build and restores the project settings afterwards. For
a local build, use the menu **Chibi Clash > Build Web Version (GitHub Pages)**, which writes to `build/WebGL/WebGL/`
(git-ignored).

Limits in the browser:
- Online play needs Relay over WebSockets and a linked Unity Gaming Services project.
- The webcam pose panel asks for camera permission.
- GitHub Pages sites are capped at 1 GB.

## Local tools

- `python tools/ci/compile_check.py [--runtime]`: Roslyn compile of the C# using the local editor. The editor is found
  from `ProjectSettings/ProjectVersion.txt` under the standard Hub paths (Windows, macOS); override with
  `UNITY_EDITOR_DATA` (the editor's `Data` folder; on macOS `.../Unity.app/Contents`).
- `python tools/ci/check_stage_geometry.py [--tol 2]`: art JSON vs `ChibiStageTable.cs`, stages 3..7.

## Why there is no Linux C# compile job

Compiling needs UnityEngine/UnityEditor reference assemblies and the Unity-generated csproj files (gitignored). They
ship only with a licensed editor install and are not redistributable, and no maintained NuGet package matches Unity 6.
The compile is covered locally by `compile_check.py` and in CI by the Unity test job once licence secrets exist.
