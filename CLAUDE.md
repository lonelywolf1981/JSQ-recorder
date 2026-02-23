# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Restore NuGet packages
dotnet restore JSQ/JSQ.slnx

# Build entire solution
dotnet build JSQ/JSQ.slnx

# Run the WPF application
dotnet run --project JSQ/JSQ.UI.WPF/JSQ.UI.WPF.csproj

# Run tests
dotnet test JSQ/JSQ.Tests/JSQ.Tests.csproj

# Run tests with coverage
dotnet test JSQ/JSQ.Tests/JSQ.Tests.csproj --collect:"XPlat Code Coverage"
```

Solution file format is `.slnx` (modern Visual Studio format), not `.sln`.

## Architecture

The solution (`JSQ/JSQ.slnx`) has 8 projects with strict layering:

```
JSQ.Core        → Domain models, interfaces, configuration (no dependencies)
JSQ.Capture     → TCP client + 3-stage pipeline (IngestQueue → DecodeQueue → PersistQueue)
JSQ.Decode      → Protocol decoder (WIP/stub)
JSQ.Storage     → SQLite + Dapper, WAL mode, batch writes
JSQ.Rules       → Anomaly detection engine + 20-second aggregation
JSQ.Export      → DBF legacy format export
JSQ.UI.WPF      → WPF app, MVVM with CommunityToolkit.MVVM, DI via Microsoft.Extensions.DI
JSQ.Tests       → xUnit tests
```

**Key principle**: data flows one-way through the pipeline — TCP socket → IngestQueue → DecodeQueue → PersistQueue → SQLite. Never bypass the queue chain.

## Key Domain Concepts

**Channels**: 134 fixed measurement channels across posts A (45), B (45), C (45), Common (10), System (4). Canonical definitions in `CHANNEL_SPEC.md` and `JSQ.Core/Models/ChannelDefinition.cs`.

**Experiment state machine**: `Idle → Running ↔ Paused → Stopped → Finalized`. A `RECOVERED` state exists for crash restart. See `JSQ.Core/Models/Experiment.cs`.

**Anomaly rules** per channel: min/max limits, delta spike, no-data timeout, with hysteresis and debounce. See `JSQ.Rules/AnomalyDetector.cs`.

**Sampling**: ~2 Hz (200ms intervals). Aggregation window: 20 seconds. DB batch size: 500 rows, flush every 1s.

## Configuration Defaults

Defined in `JSQ.Core/Configuration/AppConfig.cs`:
- Transmitter: `192.168.0.214:55555`
- Database: `data\experiments.db`
- Logs: `logs\jsq-.log`
- Export output: `export\`

## UI Entry Point

`JSQ.UI.WPF/App.xaml.cs` → `OnStartup` registers DI services (`MainViewModel`, `IExperimentService`) and opens `MainWindow.xaml`. The main window contains: toolbar (Start/Pause/Stop/Export), channel matrix grid (live status), and event log panel.

## Development Status

Stages completed: 1–6 (skeleton, TCP capture, SQLite, anomaly engine, UI v1, channel selection UI).
**Stage 7 in progress**: TCP connection to real transmitter (`TcpCaptureService.cs` modified, `ExperimentService.cs` newly added but untracked).
Remaining: Stage 8 (DBF export), Stage 9 (hardening/service), Stage 10 (integration testing).

## Important Reference Docs

- `CHANNEL_SPEC.md` — canonical 134-channel list with units and groups
- `PROTOCOL_ANALYSIS.md` — binary TCP protocol reverse-engineering
- `FULL_APP_TODO.md` — detailed checklist for all 10 stages
- `QWEN.md` — AI assistant context overview
