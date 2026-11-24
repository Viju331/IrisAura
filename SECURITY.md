# Security Policy

## Reporting a vulnerability

Please **do not** open a public issue for security problems.

Use GitHub's private vulnerability reporting:
**Security → Report a vulnerability** on https://github.com/Viju331/IrisAura.

Include the affected component, Windows build, steps to reproduce and impact.
You should receive an acknowledgement within 7 days.

## Scope notes

- **IrisAura Face Lab** is a research tool. Its face matching is not an authentication factor, and
  reports that RGB face matching can be spoofed are expected; they are still welcome when they show
  a gap in our documented limits.
- **Custom Windows sign-in** is experimental and is never enabled by default. Any issue that could
  lock a user out of Windows or bypass Windows sign-in is treated as critical.
- Please never send real face images, embeddings, passwords or other personal data in a report.

## Supported versions

No release has been published yet. This section will list supported versions from Release 1.
