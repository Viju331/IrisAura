# Models

ONNX models for IrisAura Face Lab (Phase 6+). **None are included yet.**

Rules (BLUEPRINT §4, §13):

- Only commercially licensed weights: **YuNet (MIT)**, **SFace (Apache-2.0)**, **MiniFASNetV2/V1SE (Apache-2.0)**,
  MediaPipe Face Landmarker (Apache-2.0). InsightFace `buffalo_*` / `antelopev2` weights are **non-commercial — never add them**.
- Before committing a model, add:
  1. its SHA-256 to `SHA256SUMS` (`<hash> *<file>`, the format of `sha256sum`),
  2. its licence text and source URL to `LICENSES/<model>.txt`,
  3. an entry to `THIRD_PARTY_NOTICES.md`, then run `scripts/generate-dependency-inventory.ps1`.
- The build will verify hashes (Phase 6) so a tampered or swapped model fails the build.
