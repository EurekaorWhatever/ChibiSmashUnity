---
paths:
  - "Assets/ChibiClash/Scripts/Netcode/**/*.cs"
  - "Assets/Tests/EditMode/Editor/NetcodeContractTests.cs"
---

# Chibi Clash Netcode Core Rules

This project is P2P deterministic rollback (not server-authoritative): both peers simulate from inputs only.
Full guide: `docs/architecture/netcode/DEVELOPER-GUIDE.md`.

- `Netcode/View/` is presentation: it may read `ChibiSimState` and `ChibiSimEvents`, never write them
- Inside `ChibiSimulation` and everything it calls: integers only (no float/Mathf/Vector2), only the in-state RNG, no Time/DateTime, no statics or caches, no Dictionary iteration order, no UnityEngine objects
- Every mutable match field lives in `ChibiSimState` and is covered by `CopyFrom` and `ComputeChecksum`
- Any gameplay or balance change bumps `ChibiMatchConfig.RulesVersion`; then re-record the golden values in `NetcodeContractTests` on purpose
- Any wire format change bumps `ChibiPacketCodec.ProtocolVersion` (optional trailing fields excepted, readers must accept their absence)
- Never make a failing `NetcodeContractTests` / `ChibiSyncVerificationTests` pass by editing only the expected value: find the cause first
- Network input is untrusted: size and range checks, no throw, no per-packet allocation; names drawn only through `ChibiSocialRules.SafeDisplayName` / `PeerDisplayName`
- Changes to the red zone (rollback, protocol, transport, rooms, social security) get a `network-programmer` and a `security-engineer` review before commit
- Any netcode or online-play change (versions, rollback numbers, transport, rooms, matchmaking, results, limits; no "problems fixed" section) also updates `docs/Network Engineering Summary.md` in the same change: keep it short and clean, simple English, with emoji (user request 2026-11-22; it is the single summary file)
