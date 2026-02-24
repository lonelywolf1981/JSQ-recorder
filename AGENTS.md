# AGENTS.md

## Purpose
- This file is the operational guide for coding agents working in this repository.
- It consolidates build/test commands and coding conventions inferred from the codebase.
- Scope: entire repo rooted at `C:\Users\a.baidenko\Downloads\JSQ-recording`.

## Rule Sources Checked
- `CLAUDE.md` (primary project workflow and architecture guidance).
- `QWEN.md` (project context and process expectations).
- `.cursor/rules/` and `.cursorrules`: not present.
- `.github/copilot-instructions.md`: not present.
- No additional Cursor/Copilot rule files were found at scan time.

## Repository Layout
- Main solution file: `JSQ/JSQ.slnx` (use `.slnx`, not `.sln`).
- Projects and layering:
  - `JSQ.Core` -> domain models/interfaces/config.
  - `JSQ.Capture` -> TCP intake and queue pipeline.
  - `JSQ.Decode` -> protocol decoder.
  - `JSQ.Storage` -> SQLite + Dapper persistence.
  - `JSQ.Rules` -> anomalies + aggregation.
  - `JSQ.Export` -> DBF export.
  - `JSQ.UI.WPF` -> WPF MVVM app.
  - `JSQ.Tests` -> xUnit tests.
- Architectural invariant: keep one-way data flow:
  - TCP -> IngestQueue -> DecodeQueue -> PersistQueue -> SQLite.
  - Do not bypass queue stages.

## Build, Lint, and Test Commands
- Run commands from repo root unless stated otherwise.
- Restore all projects:
  - `dotnet restore "JSQ/JSQ.slnx"`
- Build full solution:
  - `dotnet build "JSQ/JSQ.slnx"`
- Run WPF application:
  - `dotnet run --project "JSQ/JSQ.UI.WPF/JSQ.UI.WPF.csproj"`
- Run all tests:
  - `dotnet test "JSQ/JSQ.Tests/JSQ.Tests.csproj"`
- Run tests with coverage:
  - `dotnet test "JSQ/JSQ.Tests/JSQ.Tests.csproj" --collect:"XPlat Code Coverage"`

## Single-Test Execution (Important)
- List available tests first:
  - `dotnet test "JSQ/JSQ.Tests/JSQ.Tests.csproj" --list-tests`
- Run one test class:
  - `dotnet test "JSQ/JSQ.Tests/JSQ.Tests.csproj" --filter "FullyQualifiedName~JSQ.Tests.ExperimentTests"`
- Run one exact test method:
  - `dotnet test "JSQ/JSQ.Tests/JSQ.Tests.csproj" --filter "FullyQualifiedName~JSQ.Tests.ExperimentTests.Experiment_DefaultState_IsIdle"`
- Run tests by method-name substring:
  - `dotnet test "JSQ/JSQ.Tests/JSQ.Tests.csproj" --filter "FullyQualifiedName~Sample_InvalidValue_IsValidReturnsFalse"`

## Linting Status
- No dedicated lint command/config (`.editorconfig`, StyleCop config) was found.
- Practical lint gate in this repo is compiler/analyzer warnings during build/test.
- Optional strict check:
  - `dotnet build "JSQ/JSQ.slnx" -warnaserror`
- Note: current codebase may emit existing warnings; do not silently broaden warning suppressions.

## Alternative Build Profile
- There is a custom debug profile used by local script:
  - Script: `build_debug_qwen.bat`
  - Manual equivalent:
    - `dotnet build "JSQ/JSQ.UI.WPF/JSQ.UI.WPF.csproj" -c Debug_Qwen -o ..\\..\\debug_qwen -v q`

## Language and Framework Baseline
- All projects target `.NET Framework 4.8`.
- `LangVersion` is set to `latest` and `Nullable` is enabled.
- Keep compatibility with `net48` APIs and runtime behavior.

## Code Style: Imports and Namespaces
- Prefer file-scoped namespaces (`namespace X.Y;`).
- Place `using` directives at file top.
- Keep `System.*` usings first, then third-party, then `JSQ.*`.
- Avoid unnecessary usings; remove dead imports when touching files.
- Respect existing `GlobalUsings.cs` in projects that have it.

## Code Style: Formatting
- Use 4-space indentation, no tabs.
- Opening braces on new lines for types/methods/control blocks.
- Keep methods short when possible; extract helpers for complex flows.
- Preserve existing comment language/style in touched files (many comments are Russian).
- Use expression-bodied members for trivial getters/to-string only when readable.

## Code Style: Types and Nullability
- Treat nullable annotations seriously; avoid `!` unless unavoidable.
- Initialize non-nullable reference properties (`= string.Empty`, `= new()`).
- Use `?` for truly optional values (`DateTime?`, `double?`, nullable events).
- Prefer explicit DTO/model types over anonymous nested structures in public APIs.

## Naming Conventions
- Types, properties, methods, events: PascalCase.
- Interfaces: `I` prefix (`IExperimentRepository`, `IDatabaseService`).
- Private fields: `_camelCase`.
- Locals/parameters: camelCase.
- Async methods must end with `Async`.
- Tests follow `TypeOrFeature_Scenario_ExpectedResult` naming.

## Error Handling and Reliability
- Guard critical boundaries (network, disk, DB, UI startup) with try/catch.
- Do not swallow exceptions in core domain logic without fallback/telemetry intent.
- If catching and continuing, keep behavior explicit and safe (no crash loops).
- Prefer specific exceptions for validation/state errors (`InvalidOperationException`, etc.).
- For long-running loops/services, fail one iteration safely rather than kill the process.

## Async, Threading, and Concurrency
- Use `Task`-based async for I/O and background work.
- Avoid blocking calls on UI thread.
- Protect shared mutable state with `lock` where already established.
- Use concurrent collections for producer/consumer queues.
- Pass `CancellationToken` through async boundaries when available.

## Data and Domain Rules
- Channel registry is canonical in `JSQ.Core/Models/ChannelRegistry.cs`.
- Channel count is fixed (134 logical channels across A/B/C/Common/System).
- Invalid sample sentinel is `-99` (see `Sample.IsValid`).
- Experiment lifecycle states:
  - `Idle -> Running <-> Paused -> Stopped -> Finalized` (+ `Recovered` for recovery).
- Aggregation window is 20s by default; do not change semantics casually.

## Persistence and SQL
- Use Dapper with parameterized SQL; never build SQL via string concatenation of input.
- Keep SQLite WAL assumptions intact (`journal_mode=WAL`, `synchronous=FULL`).
- Batch writes and checkpoints are reliability-critical; preserve write ordering guarantees.
- Persist timestamps in round-trip format where used (`DateTime.ToString("O")`).

## WPF/MVVM Conventions
- UI layer uses MVVM with CommunityToolkit.Mvvm.
- Prefer `[ObservableProperty]` and `[RelayCommand]` patterns for view models.
- Keep view model logic UI-thread-safe (`Dispatcher` where required).
- Register dependencies in `JSQ.UI.WPF/App.xaml.cs` DI setup.

## Testing Conventions
- Test framework: xUnit.
- Use `[Fact]` for single-case tests, `[Theory]` + `[InlineData]` for variants.
- Keep Arrange/Act/Assert structure and readable assertions.
- Add or update tests when changing domain/storage/rules behavior.
- For targeted validation during development, run single-class or single-method filters first.

## Agent Workflow Checklist
- Before coding: locate impacted layer(s) and preserve architecture boundaries.
- While coding: keep changes minimal, cohesive, and compatible with `net48`.
- Before finishing:
  - `dotnet build "JSQ/JSQ.slnx"`
  - `dotnet test "JSQ/JSQ.Tests/JSQ.Tests.csproj"` (or targeted filter during iteration)
- If tests cannot be run locally, state this explicitly in handoff notes.

## Files Worth Reading Before Major Changes
- `CLAUDE.md`
- `CHANNEL_SPEC.md`
- `PROTOCOL_ANALYSIS.md`
- `FULL_APP_TODO.md`
- `JSQ/JSQ.Core/Models/ChannelRegistry.cs`
