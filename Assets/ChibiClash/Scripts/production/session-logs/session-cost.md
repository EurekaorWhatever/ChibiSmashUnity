# Session Subagent Cost

**Session:** `76b7f14c-3df9-4af7-817d-bd5d483ecc85`  
**Updated:** 20261106_140845

Subagent spawns this session: **10**

| Agent | Spawns |
|-------|--------|
| network-programmer | 3 |
| unity-specialist | 2 |
| gameplay-programmer | 2 |
| technical-director | 1 |
| qa-tester | 1 |
| ai-programmer | 1 |

Each spawn is a fresh context window that re-reads its own inputs.
To reduce this: lower `modes.review_mode` (`/settings modes.review_mode=solo`)
or `modes.rigor` (`/settings modes.rigor=minimal`).
