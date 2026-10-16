# Contributing

Thanks for your interest in improving Systenclin.

## Ground rules

- **No new dependencies.** The app ships as a single self-contained exe by
  design; keep it dependency-free.
- **The safety guard is sacred.** `IsSafeToDelete` and the fail-closed checks
  around reparse points/junctions must never be weakened. When in doubt,
  refuse the operation and explain the reason to the user.
- **The UI is PT-BR on purpose.** User-facing strings stay in Portuguese
  (comments in Portuguese are fine too). README and docs are in English.
- **Minimal diffs.** Fix the problem at hand; avoid drive-by refactors.

## Requirements

- Windows 10/11
- .NET 10 SDK

## Build and run

```powershell
dotnet build Systenclin/Systenclin.csproj -c Release
dotnet run -c Release --project Testes/Systenclin.Testes/Systenclin.Testes.csproj
```

The second command runs the core verification harness (safety guard, scanner,
cleaner, privacy, history). It compiles `Systenclin/Core/*.cs` directly — no
app UI involved.

## Pull requests

- One topic per PR with a short description of **what** and **why**.
- `dotnet build` must be clean (0 warnings, 0 errors) and the harness must
  pass with 0 failures.
- Add harness checks for new Core behavior whenever feasible.
- CI runs the build and the harness on every PR.
