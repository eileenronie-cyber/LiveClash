# Live Clash

*by John Ronie B. Torres*

A Revit add-in that runs clash detection **in the background while you model**, against the current model *and every loaded link* — the Navisworks "append all the NWCs into one set" idea, but live, inside Revit, scoped to whatever you just touched.

> **Status:** built, deployed, and run against a real 310k-element model across Revit 2024/2025/2026. Live checking, full sweeps, host-vs-link, multi-set clash matrices, the rule-based filter, per-link category control, the provisional-geometry audit, and the triage record are all working and measured — the numbers quoted below come from that model, not estimates. The known limits are listed at the end of this file.
>
> Build with `dotnet build -p:RevitVersion=2026` (also `2025`, `2024`) with Revit closed — the output lands in `bin\Debug<version>\`, which the installed `.addin` manifests reference directly, so a build *is* a deploy.

---

## How it works

### The live trigger

Revit's `IUpdater` (Dynamic Model Update) framework is the mechanism that makes this live rather than on-demand. Revit calls `ClashUpdater.Execute()` **inside the transaction that changed the model**, before it commits, handing you exactly the element ids that were added, modified, or deleted.

That's the whole trick: you never re-check the model, you check the four elements that just moved.

It comes with rules that shape every design decision in this project:

| Rule | Why | Where it's handled |
|---|---|---|
| Never throw | An exception gets your updater disabled and shows the user a scary dialog | `Execute()` wraps everything and logs instead |
| Never open a transaction | One is already open | The updater only reads |
| Never show UI | Modal dialogs inside a transaction deadlock Revit | Results go to a store; the panel reads it later |
| Be fast | This runs on the main thread — every ms is a frozen cursor | Time budget + deferred queue |

### Two-phase intersection

Per changed element:

1. **Broad phase** — `TargetIndex`, a uniform spatial hash over the target bounding boxes, held warm in `ClashContext` and patched incrementally rather than rebuilt. Returns a handful of candidate ids. This replaced a Revit collector query, which was correct but cost 60.8 ms per element against 0.14 ms here.
2. **Narrow phase** — the exact `ElementIntersectsElementFilter` (host) or `ElementIntersectsSolidFilter` (links), run **only over those candidate ids** via `new FilteredElementCollector(doc, candidateIds)`. This is the expensive part, and it never sees more than a few dozen elements.
3. **Verification** — `ClashVerify` puts a boolean volume test over every reported *hard* hit, because the intersection filter also matches things with no solid to speak of: light cones, detection zones. A pair it cannot measure becomes `Unverified` and stays on the list. Nothing is dropped for being unmeasurable — under-reporting a clash is the one failure this tool must not have.
4. Anything that passed (1) but failed (2) is inside the clearance envelope without physically touching — a **clearance** hit rather than a **hard** hit.

The warm context is cached per scope, keyed by everything that changes what gets collected: target categories, the host-vs-host and host-vs-links switches, and the active set's per-link scoping. Change any of them and the next check builds a fresh one.

### Links

`ElementIntersectsSolidFilter` can't cross documents, so for each loaded link:

```
host element solids  --(link.ToLink = GetTotalTransform().Inverse)-->  link space
                     --> query runs inside the link's own document
```

Running the query inside the link document is what lets you use *that model's* spatial index instead of brute-forcing it. `LinkContext` wraps each `RevitLinkInstance` with its document, name, and both transforms.

Note that the bounding box transform uses all **eight corners**, not just Min and Max — transforming only two corners silently breaks the moment a link is rotated, which is a classic and very hard-to-spot bug.

### Not freezing Revit

Two safety valves in `ClashUpdater`:

- **`MaxElementsPerUpdate`** (default 40) — a paste, an array, or a model-wide parameter change dumps its ids into `DeferredQueue` instead of checking them inline.
- **`TimeBudgetMs`** (default 120) — if the inline pass overruns, whatever it didn't reach goes to the same queue.

`LiveClashApp.OnIdling` drains that queue in 25-element slices whenever Revit has nothing else to do, calling `SetRaiseWithoutDelay()` while work remains.

### Noise suppression

This is what separates a usable tool from one you switch off after ten minutes. Every duct touches its own fitting; every sprinkler touches the branch it hangs from. `PairSuppressor` drops pairs that are:

- joined (`JoinGeometryUtils.AreElementsJoined`)
- host/hosted (a light in a ceiling, a sprinkler on a pipe)
- MEP-connected (walks `ConnectorManager` → `Connector.AllRefs`)
- parts of the same nested family (`SuperComponent`)

plus whole categories that are never real clashes — duct/pipe insulation and lining (they wrap the very element they'd clash with), rooms, spaces, lines, and anything in a **non-primary design option**.

Suppression only applies within the host document. Nothing in a link can be joined or connected to host geometry, so every link hit is a genuine coordination issue.

---

## Files

Sixty-odd source files; this is the map, not the inventory.

```
LiveClash.csproj              multi-targets net48 / net8.0-windows / net10.0-windows by Revit version
LiveClash.addin               manifest
LiveClashApp.cs               IExternalApplication — ribbon, registration, idle draining,
                              background re-verification, document adoption
Commands/Commands.cs          ribbon commands (show panel, pause/resume, clearance, glance)

Core/  — detection
  ClashUpdater.cs             the IUpdater — live trigger, budget, deferral
  ClashEngine.cs              broad/narrow phase, host-vs-host and host-vs-link, multi-set sweep
  ClashContext.cs             the per-scan warm context, cached by scope signature
  TargetIndex.cs              uniform spatial hash over target boxes, patched incrementally
  ClashVerify.cs              boolean volume test — confirms every reported hard hit
  PairVerifier.cs             "do these two still overlap?" with no broad phase
  ClashGeometryKind.cs        how they meet: through / ends inside / enclosed
  GeometryUtil.cs             solid extraction, 8-corner box transform
  ClashFocus.cs               section box that frames a clash you cannot select
  ClashScopeEstimate.cs       full-check cost estimate, measured not guessed
  PairSuppressor.cs           noise rules
  ProvisionalAudit.cs         finds openings, reserved space, placeholders, connectorless plant

Core/  — scope and filtering
  ClashSettings.cs            everything persisted, per model; the multi-set line format
  ClashSet.cs                 one clash set: two sides, rules, per-link scoping
  ParameterFilter.cs          the rule list — OR/AND groups over picked values
  ParameterFilter.Serialization.cs   the shared line format (Revit-free, gated headless)
  ParameterCatalog.cs         what parameters this model actually has
  CategoryCatalog.cs          what categories this model actually has, with counts
  ValueScan.cs                distinct values, for Browse rather than typing
  LinkContext.cs              one loaded link + its transforms
  LinkScope.cs                what one link contains, per category
  FilterPreset.cs             the filter as a file you can hand to someone

Core/  — results and record
  ClashResult.cs              result + key normalisation + ElementId shim
  ClashResultStore.cs         locked, de-duplicated live result set
  ClashGroup.cs               proximity clustering, so one duct run is one issue
  ClashStatusStore.cs         append-only triage decisions, per model
  ResolvedLog.cs              append-only record of what was fixed
  ClashRecord.cs              the unified row behind the Record window
  CsvExport.cs                the register, for a coordination meeting
  CsvStamp.cs                 which model, when, who and which rules, on every exported row
  CsvText.cs                  CSV quoting and Excel's formula guard, shared by every export
  DeferredQueue.cs            overflow buffer
  Log.cs                      file logger (%LOCALAPPDATA%\LiveClash\liveclash.log, rolls at 4 MB)

UI/
  ClashPanel.xaml(.cs)        dockable results panel
  FilterWindow.xaml(.cs)      clash sets, categories, rule builder
  LinkScopeWindow.xaml(.cs)   per-link category control
  AuditWindow.xaml(.cs)       provisional-geometry audit
  RecordWindow.xaml(.cs)      resolved / coordinated / ignored / pending
  GlanceWindow.xaml(.cs)      read-only summary by severity and category
  ValueBrowserWindow.xaml(.cs)   pick values instead of typing them
  NoteWindow.xaml(.cs)        why a clash was accepted
  Theme.xaml                  shared Fluent styling
  *Handler.cs                 ExternalEvents — show, focus, full check, recheck, filter, audit
```

---

## Build & install

**Just want to use it?** Download the zip for your Revit version from [Releases](../../releases), unzip it, and copy `LiveClash.dll` and `LiveClash.addin` together into `%APPDATA%\Autodesk\Revit\Addins\<year>\`. Start Revit, and a **Live Clash** tab appears.

To build it yourself:

```powershell
dotnet build -p:RevitVersion=2026 -c Release
# or 2025, 2024 — the csproj picks the runtime automatically:
#   2026+ net10.0-windows, 2025 net8.0-windows, 2024 net48
```

**2024, 2025 and 2026 only.** This used to say "or 2025, 2024, 2023, 2022", which was a promise nothing kept: `ci/gate.ps1` compiles exactly these three, so 2022 and 2023 were never built by anything.

The reason has since changed, and the answer has not. It used to be that 2023 *could not* compile — `ElementId(long)` was constructed in ten places and that overload arrives in 2024. `IdUtil.From` centralised it, so **2023 now builds clean in Release and Debug**. It is still not supported, and that is a deliberate choice rather than an oversight: no gate leg builds it, no self-test has run in it, and a version nobody exercises is one you find out about from a user. Compiling is the cheap half.

If Revit isn't in the default location:

```powershell
dotnet build -p:RevitVersion=2026 -p:RevitInstallDir="D:\Autodesk\Revit 2026\"
```

Then copy `LiveClash.dll` and `LiveClash.addin` to:

```
%APPDATA%\Autodesk\Revit\Addins\2026\
```

`.addin` expects the DLL beside it. If you put the DLL elsewhere, make `<Assembly>` an absolute path.

**Prefer NuGet to `HintPath`?** Swap the `<Reference>` block for:

```xml
<PackageReference Include="Nice3point.Revit.Api.RevitAPI" Version="$(RevitVersion).*" PrivateAssets="all"/>
<PackageReference Include="Nice3point.Revit.Api.RevitAPIUI" Version="$(RevitVersion).*" PrivateAssets="all"/>
```

That builds on a machine without Revit installed (useful for CI).

---

## Testing & CI

Two tiers, split by what can run where:

**Headless (no Revit) — gated in CI.** The Revit-free logic — filter serialization and the legacy→rules migration, where the "dispatch switch eats new keys" bug lived four times — runs as a plain console app:

```powershell
dotnet run --project tests\LiveClash.LogicTests    # exit code = failed-check count
```

The code under test is `tests\LiveClash.Logic` (the Revit-free Core files compiled as their own assembly, so coverage and mutation tools can measure product code separately from the checks); `dotnet test` runs the same list as one MSTest test, which is how Stryker.NET drives it.

The whole gate (compile every runtime from NuGet reference assemblies, then the logic tests) is one script, runnable locally before a push and identical to what CI runs:

```powershell
.\ci\gate.ps1
```

It builds `net48 / net8 / net10` (Revit 2024 / 2025 / 2026) against the Nice3point packaged reference assemblies via `-p:RevitApiSource=NuGet`, so **no Revit install is needed** — that's what catches a refactor breaking one target while another still compiles. `.github/workflows/ci.yml` runs the same script on `windows-latest` (it runs on every push and pull request). Pushing a `v*` tag runs `.github/workflows/release.yml`, which runs the gate, builds each Revit version and publishes the zips as a release.

**In-Revit checks.** Geometry verification (`BooleanOperationsUtils`, `Solid`), `ClashResult.MakeKey`, the `TargetIndex` grid, `PairSuppressor` and the sweep's de-duplication can't run headless, because those types only exist inside Revit's process. They are checked inside Revit against real models and against a fixture generated from Revit's own templates on 2024, 2025 and 2026. That harness isn't part of this repository. The serialization and migration assertions it shares with the headless runner (`Testing/FilterLogicChecks.cs` and the other files in `Testing/`) are.

*Why not just run everything in CI?* You can't `dotnet test` a clash engine — its core operation is a Revit API call. The split is the honest shape: compile + pure logic gated automatically, geometry proven in Revit.

`Testing/` is never compiled into the add-in; only `tests/LiveClash.LogicTests` links it.

**Two of those have since moved into the headless tier too.** The *rules* inside `MakeKey` and `ParameterFilter.Matches` now live in Revit-free methods (`MakeKey(long, long?, long)`, and a matcher that reads values through `ParameterFilter.IValueSource`); the public Revit-typed methods are thin wrappers marked `[RevitBound]`. The in-Revit checks still run them end to end on real elements.

### Quality gates beyond the tests

| What | Command | Gate |
|---|---|---|
| Coverage of the Revit-free logic | `.\ci\coverage.ps1` | line and branch coverage above a ratchet (part of `ci\gate.ps1`); `[RevitBound]` members excluded and kept thin |
| Static analysis | `.\ci\analyze.ps1` | .NET analyzers: zero findings in reliability/security, nothing new beyond `ci\analyzers-baseline.txt` (part of `ci\gate.ps1`) |
| Mutation testing | `.\ci\mutation.ps1` | Stryker.NET plants defects in the Revit-free logic; weekly in CI (`.github\workflows\mutation.yml`) |
| Fuzz / property checks | inside the logic tests | seeded random filters, ledger histories (shuffled), CSV fields and search queries (`Testing\FuzzChecks.cs`) |

---

## Using it

**Live Clash** tab → **Clash Panel**.

- **Live** — pause/resume without unloading the add-in. The setting persists, and the ribbon button and the panel checkbox are the same switch.
- **Full check** — one-off sweep of every source element. Run this after opening a model so the panel starts from a real baseline instead of only knowing about what you've touched this session. It blocks Revit while it runs; that's why it's a button and not automatic. A green/amber/red hint beside it estimates the cost first, and for a big scope it *measures* a 250-element sample rather than guessing.
- **Filter** — clash sets (up to four), each with its own two sides, rule list, and per-link category control. Export the lot to a `.lcfilter` file to hand to a colleague.
- **Audit** — finds provisional geometry (modelled openings, reserved space, placeholders, connectorless plant) and lets you drop family+type pairs that are never a real obstruction. Drops are global, both sides, every set.
- **Record** — what has been resolved, coordinated, ignored, and what is still pending. Exports to CSV.
- **Export (CSV)** — the panel's register and Record's full history both export to CSV. Every row ends with four stamp columns — *Exported from*, *Exported*, *Exported by* and *Rules* (the same fingerprint the panel shows) — so a file left in an ACC folder or pasted beside another modeller's still says which model, when, who, and under which rules. Two exports with different *Rules* were not produced by the same filter, so a difference between them is not a disagreement about the model.
- **Clearance** — soft-clash tolerance (off / 25 / 50 / 100 mm).
- **Include insulation** (per set) — also tests by pipe and duct insulation, filed against the pipe or duct itself: a source pipe by its own insulation, and a pipe or duct in a **linked** model by *its* insulation, so a host pipe 30 mm from a linked pipe still clashes with its 40 mm of insulation (proved for pipes against a linked pipe and a linked duct). The same goes for a pipe or duct that is only a *target* in the model itself: a duct 30 mm off an insulated pipe still clashes with its insulation. One limit: duct lining is never touched.
- **Single-click** a row to select the pair. **Double-click** to frame it in a section box in a dedicated 3D view — which is how you look at a clash whose elements cannot be selected from the host, both sides in links included. **This is the one thing LiveClash writes to your model:** the first double-click creates a 3D view named `LiveClash — Clash Focus` (in one transaction, "LiveClash: focus clash"), and every later click reuses and re-frames that same view rather than adding more. It stays in the project until you delete it, and it will be saved with the model, so delete it before issuing a file if you don't want it there.

Red dot = hard clash. Amber = clearance only. Rows group by proximity, so one duct run through one wall is one issue rather than fourteen.

When the list is empty it says *why* — never checked, checked and clean, filter changed, drops changed, or you switched model. An empty list means completely different things, and guessing wrong is how people conclude the tool is broken.

---

## Tuning

Scope is set in the **Filter** window and saved per model; the settings below are the ones with no UI.

The two sides of each clash set are **the single biggest lever on both speed and noise**. The defaults are MEP-vs-structure-and-MEP. Narrow them to your actual discipline before you judge performance — checking everything against everything will be slow and unusable regardless of how good the code is, which is what the scope guard on the Full check button is there to tell you before you wait for it.

The rest lives in `ClashSettings`:

`MaxElementsPerUpdate` / `TimeBudgetMs` trade responsiveness against how much gets deferred. If modelling feels sticky, lower the budget; work moves to idle time rather than being lost.

After changing categories in code, call `ClashUpdater.AddTriggers()` — the triggers are built from that list at registration, so new categories won't fire until they're rebuilt. The Filter window already does this for you.

Settings, triage and resolutions are stored per model under `%LOCALAPPDATA%\LiveClash\`, keyed by `ModelKey`. Switching between two open models swaps all three to follow the active one.

---

## Known limits

**Soft clash accuracy.** Clearance is evaluated on **bounding boxes**, not true offset solids. Revit has no public solid-offset operation, so this is conservative: it flags some things that are actually clear. For a round duct in particular the box corners overstate the envelope. If you need exact clearances you'd have to build offset geometry yourself (sweep the solid's faces outward, or approximate with a scaled solid about the centroid) — meaningfully more work and much slower.

**Links only refresh when reloaded.** `LinkContext` reads the link document as currently loaded. If a colleague moves a beam, you won't see it until the link is reloaded. Same limitation any in-Revit approach has — it's why this complements a Navisworks/Revizto round rather than replacing it.

**Not an ACC / BCF issue tracker, on purpose.** LiveClash is the modeller's tool: each seat checks its own model against its links, and ACC Model Coordination is where the whole project is checked model against model. The CSV exports travel as files (they upload to Autodesk Docs like any spreadsheet), but they do not become ACC issues: ACC's issue import takes its own template and cannot pin an issue to the model, its Issues API does not create pinned issues, and BCF import is not available in Model Coordination. Each register is also one modeller's slice, so the same clash appears in two seats' files. Autodesk's own Revit Issues add-in is the way to raise a pinned ACC issue for a clash found here. What *is* shared is the rules: a `.lcfilter` preset in a project folder, with the rules fingerprint to confirm every seat imported it.

**Clashes themselves are recomputed, not stored.** What survives a restart is the *decisions* — accepted, coordinated, ignored, and what has been fixed — not the geometry hits. Reopen a model and the panel starts empty until you sweep it; the banner says so rather than leaving you to guess.

**`ChangePriority`.** Set to `FreeStandingComponents` so geometry is seen after Revit finishes regenerating connectors, joins, and fittings. If you see clashes against stale geometry, this is the dial to turn.

**Worksharing.** Only your local model is checked. Other users' unsynced work is invisible, as it is to every other tool.

---

## If you ever target an older Revit

The gate builds 2024, 2025 and 2026, and nothing else is tested. **2023 does now compile clean in both Release and Debug** — `IdUtil.From` centralised the one construction that used to break it — but compiling is not support: no gate leg builds it, no self-test has ever run in it, and nothing here has been opened in a 2023 Revit. Treat the list below as where to start if you take it further back, not as a promise that it works.

- `ElementId(long)` — **2024+**. `IdUtil.From` is the only place that constructs one from a number, and it carries the `#if`. Do not write `new ElementId(someLong)` anywhere else; that is exactly what silently broke the pre-2024 build before.
- `Category.BuiltInCategory` — Revit 2023+. The `#else` branch covers older versions.
- `ElementId.Value` vs `.IntegerValue` — handled by the `R24_PLUS` constant in `IdUtil` and `DeferredQueue.NewId`.
- `UnitTypeId` — Revit 2021+. Pre-2021 needs `DisplayUnitType`.
- `DockablePaneProviderData.VisibleByDefault` — confirm it exists in your target version; drop the line if not.
- `ExternalEvent.Create` in `OnStartup` — valid, but if your Revit version complains, move `InitialiseEvents()` to the first `Idling` tick.

---

## License

MIT. See [LICENSE](LICENSE).
