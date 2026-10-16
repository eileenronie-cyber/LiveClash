# Live Clash

A Revit add-in (2024/2025/2026) that clash-checks what you model, as you model it, against the host and every loaded link. `README.md` explains the design: the live trigger, broad and narrow phase, links, budgets and noise rules.

## Layout

- `Core/`: the engine (updater, `ClashEngine`, `TargetIndex`, `ClashVerify`, settings, ledgers, `Log`).
- `UI/`: WPF windows and the dockable panel. `Commands/`: the ribbon. `LiveClashApp.cs`: start-up and idle work.
- `Testing/`: Revit-free checks, listed once in `SharedChecks.cs`.
- `tests/LiveClash.Logic`: the Revit-free Core files compiled on their own (measured for coverage and mutation). `tests/LiveClash.LogicTests`: the headless runner (`dotnet run`) and an MSTest wrapper (`dotnet test`, used by Stryker).
- `ci/`: the gate and its legs. `.github/workflows/`: CI on push, mutation weekly, release on `v*` tags.

## Build and test

```
dotnet build -p:RevitVersion=2026                              # also 2025, 2024; output bin\Debug<version>
dotnet build -p:RevitVersion=2026 -p:RevitApiSource=NuGet      # no Revit installed
.\ci\gate.ps1                                                  # what CI runs: 3 Release builds, Debug, doc checks, logic checks, coverage, analyzers
.\ci\gate.ps1 -Quick                                           # seconds; the pre-push hook (git config core.hooksPath ci/hooks)
.\ci\mutation.ps1                                              # Stryker.NET, slow
```

## Code rules

- **Inside the updater:** never throw, never open a transaction, never show UI. Results go to the store, and the panel reads them later.
- **Never under-report:** an unmeasurable pair stays listed as `Unverified`; never drop a clash for being hard to measure.
- **New Revit-free logic:** add its checks to `Testing/SharedChecks.cs`. Keep members that need a live document thin, and mark them `[RevitBound]` so coverage leaves them out.
- **`ci/analyzers-baseline.txt` is a ratchet:** regenerate it (`ci\analyze.ps1 -UpdateBaseline`) only after fixing findings.
- **`.ps1` files stay ASCII:** Windows PowerShell 5.1 reads them as ANSI.

## Note for maintainers

This repository is a published copy of the author's working repository and is refreshed from it. A change made only here can be overwritten by the next refresh unless it is also made upstream.
