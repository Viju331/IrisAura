# 0001 — Proprietary licence for a public repository

- **Status:** accepted (owner may revise)
- **Date:** 2026-11-24
- **Phase:** 0A
- **Blueprint sections:** §0.7, §12.6

## Context

The repository is public on GitHub, but IrisAura is meant to be sold to a client (BLUEPRINT §0.7,
§12.6 client licensing). An open-source licence (MIT, Apache-2.0) would let anyone, including
competitors, reuse and resell the code and would be irrevocable for every published version. The
logos are also proprietary brand assets.

## Decision

Use an "all rights reserved" proprietary licence (`LICENSE`). Viewing on GitHub is allowed; any
copying, use or redistribution needs written permission. Third-party components keep their own licences.

## Consequences

- The owner can later relax the licence (for example open-source the Island SDK) without having
  given away earlier versions. The reverse is not possible.
- External contributions are not accepted (`CONTRIBUTING.md`).
- A public repository still exposes the code and plan to anyone who reads it. Making the repository
  private is the stronger protection and is recommended until the trademark and logo provenance are
  settled (docs/BRAND_LICENSE.md).
- The copyright line names the GitHub account `Viju331`; replace it with the owner's legal name or
  company before selling.

## Alternatives considered

| Option | Why not |
|---|---|
| MIT / Apache-2.0 | Permits commercial reuse by anyone; conflicts with selling the product |
| Source-available (e.g. BSL) | More complex; no need yet |
| No licence file | Legally "all rights reserved" by default, but unclear to readers |
