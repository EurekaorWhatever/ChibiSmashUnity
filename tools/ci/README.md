# CI

| Workflow | Needs Unity licence | Does |
|---|---|---|
| `.github/workflows/fast-checks.yml` | no | `gen_movesets.py --check`, `check_stage_geometry.py`, Python syntax check of `tools/` |
| `.github/workflows/tests.yml` | yes | EditMode tests via game-ci/unity-test-runner |

`tests.yml` skips (green, with a notice annotation) when `UNITY_LICENSE` is not set, so a skipped run verifies nothing:
check the annotation.

## Adding the Unity secrets (repo owner, one time)

GitHub > Settings > Secrets and variables > Actions > New repository secret:

- `UNITY_LICENSE`: contents of the `.ulf` file (personal licence: follow https://game.ci/docs/github/activation to
  generate it with the game-ci activation workflow).
- `UNITY_EMAIL`: Unity account email.
- `UNITY_PASSWORD`: Unity account password.

Secrets are not exposed to pull requests from forks; those runs skip the Unity job.

## Local tools

- `python tools/ci/compile_check.py [--runtime]`: Roslyn compile of the C# using the local editor. The editor is found
  from `ProjectSettings/ProjectVersion.txt` under the standard Hub paths (Windows, macOS); override with
  `UNITY_EDITOR_DATA` (the editor's `Data` folder; on macOS `.../Unity.app/Contents`).
- `python tools/ci/check_stage_geometry.py [--tol 2]`: art JSON vs `ChibiStageTable.cs`, stages 3..7.

## Why there is no Linux C# compile job

Compiling needs UnityEngine/UnityEditor reference assemblies and the Unity-generated csproj files (gitignored). They
ship only with a licensed editor install and are not redistributable, and no maintained NuGet package matches Unity 6.
The compile is covered locally by `compile_check.py` and in CI by the Unity test job once licence secrets exist.
