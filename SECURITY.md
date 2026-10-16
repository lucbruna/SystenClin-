# Security Policy

## Reporting a vulnerability

Please do not open a public issue for security problems. Use GitHub's private
vulnerability reporting (Security tab → "Report a vulnerability") or contact
the maintainer directly. You can expect an acknowledgment within a few days.

## Scope

Systenclin deletes files and changes system state by design — all of that is
documented in the README and confirmed by the user before it happens. Reports
that are **in scope**:

- bypasses of the safety guard (`IsSafeToDelete`) or of the
  junction/reparse-point protections;
- deletion or modification of paths outside those shown to and confirmed by
  the user;
- elevation or privilege issues;
- arbitrary command/code execution via scanned file names, browser data, or
  history content.

When reporting, include steps to reproduce and the affected version.
