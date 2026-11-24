# 0003 — Brand asset pipeline and draft icon mark

- **Status:** accepted (icon artwork itself awaits owner approval)
- **Date:** 2026-11-24
- **Phase:** 0A
- **Blueprint sections:** §0.3–§0.6

## Context

The detailed logo symbol is unreadable below about 48 px. BLUEPRINT §0.6 allows Claude Code to
draft a simplified icon mark as SVG for owner approval. The generation tool must not add licensing
risk. The default WiX installer UI draws black text over its banner and dialog bitmaps, while the
logos may only appear on dark backgrounds (§0.3).

## Decision

- Sources live in `assets/brand/source/`:
  - `irisaura-mark.svg`: an Aura-gradient iris ring on `Aura.Surface` with the person-in-brackets glyph in `Aura.Frost`.
  - `irisaura-tray-mono.svg`: a white person-in-brackets only.
  - The glyph path data is identical to `IrisAura.Theme/Assets/Glyphs.xaml`.
- `scripts/build-brand-assets.ps1` (ImageMagick 7, build-time only) writes to
  `assets/brand/generated/` (git-ignored): app/tray `.ico`, sparse-package logos, About art, README header.
- WiX banner/dialog bitmaps are **not** generated yet. They will be designed with the installer UI in
  Phase 9 (options include a custom dark WiX theme or a light layout where the logo sits in a dark
  panel) — KNOWN_ISSUES KI-005.

## Consequences

- Icons must be regenerated after any SVG change; they are not committed.
- Preview at 16 px shows the colour mark becomes muddy; a designer may hand-tune 16/20 px sizes.
- The SVGs and `Glyphs.xaml` must be kept in sync by hand (the Theme test checks glyph bounds only).

## Alternatives considered

| Option | Why not |
|---|---|
| SixLabors.ImageSharp in `IrisAura.Tools` | Split licence may require a paid commercial licence |
| Commit generated PNG/ICO files | Binary churn; easy to go stale |
| Generate WiX BMPs now as specified | Default WiX text would be black-on-black or force the logo onto white |
