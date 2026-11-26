# 0011 — Phase 0B outcome: capture in the user session only (proposed)

- **Status:** proposed — owner decision required (GO/NO-GO gate)
- **Date:** 2026-11-26
- **Phase:** 0B
- **Blueprint sections:** §1.3, §3.6, §7.1, §10 Phase 0B, §11.2

## Context

Phase 0B must choose (A) pre-logon capture proven on the supported matrix, (B) capture only in a user session, or
(C) unsupported. The owner limited testing on DEV-01 to the user session; LocalSystem/Session 0 and pre-logon tests
need a disposable VM or a spare device with a recovery plan (CLAUDE.md), and none is available. DEV-01 is an
RGB-only AMD laptop without a Windows Hello face sensor (tier T3). Evidence: [PHASE_0B_CAMERA_REPORT.md](../PHASE_0B_CAMERA_REPORT.md).

## Decision (proposed)

Choose **(B)**: camera capture is supported in the interactive user session (proven on DEV-01: exclusive and shared
read-only, camera-in-use handled through shared mode). (A) is not proven, so per the §3.6 gate the **custom pre-logon
unlock track stops**; IrisAura Island and in-session IrisAura Face Lab continue; Windows Hello stays the sign-in
method. Phase 0C (credential provider spike) is then not needed unless the decision is revisited.

## Consequences

- Phase 6 (Face Lab) proceeds in-session only, using `IrisAura.Face.Camera` with the no-frame-timeout → shared-mode
  fallback found here.
- No LocalSystem service, broker or credential provider is built.
- The decision can be reopened with test devices for the other §11.2 classes and a safe pre-logon test setup.

## Alternatives considered

| Option | Why not (now) |
|---|---|
| Run LocalSystem / pre-logon experiments on DEV-01 | Primary machine; owner excluded it; recovery risk |
| Keep the gate open and start Phase 0C | 0C needs a VM; it only matters if (A) is reachable |
| (C) unsupported | Too strong: in-session capture works |
