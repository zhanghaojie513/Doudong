# Dougong

<p align="center">
  <strong>Everything is a Plugin · .NET Agent Harness SDK</strong>
</p>

<p align="center">
  <a href="README.md">简体中文</a> · <span>English</span>
</p>

<p align="center">
  A composable plugin kernel that carries agent runtimes, tools, protocols and hosts.
</p>

<p align="center">
  <a href="https://github.com/zhanghaojie513/Dougong"><img src="https://img.shields.io/badge/repository-Dougong-2f6f5e?style=flat-square" alt="Repository"></a>
  <a href="https://github.com/zhanghaojie513/Dougong/actions/workflows/build.yml"><img src="https://img.shields.io/github/actions/workflow/status/zhanghaojie513/Dougong/build.yml?style=flat-square&label=build" alt="Build"></a>
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-10.0-512bd4?style=flat-square" alt=".NET 10"></a>
  <a href="https://github.com/zhanghaojie513/Dougong/releases"><img src="https://img.shields.io/badge/version-0.1.0--alpha.1-orange?style=flat-square" alt="Version 0.1.0-alpha.1"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-1f883d?style=flat-square" alt="MIT License"></a>
</p>

> [!WARNING]
> **Early development — do not depend on this yet.** The M0 kernel has not fully settled; public APIs are experimental drafts whose signatures and behavior may change in any commit. This repository is currently for reading the design and tracking progress, not for production use. See [status](docs/status.md) for the first usable version and API stabilization timeline.

> **Dougong** borrows the "Everything is a Plugin" idea from DeepSeek Harness (dsh) to build an independent Agent Harness SDK for the .NET ecosystem. It borrows architectural ideas only — no code is ported, and this is not an official DeepSeek project.

A *dougong* (斗拱) is the bracket set in traditional Chinese timber architecture: standardized components assembled layer by layer to carry the roof. The project uses the same metaphor — the kernel bears and coordinates, plugins provide capabilities, and components can be composed, replaced, disabled and reloaded.

## Current Status

Dougong is in an **experimental alpha stage**, targeting `net10.0`.

`Dougong.Kernel` has completed a full round of lifecycle, service, event and dynamic-loading work, backed by 156 kernel behavior tests. Blocking issues confirmed across three rounds of M0 review are fixed: generations that failed or drained past their timeout can never be re-activated or unloaded; activation, cancellation, drain, events and cleanup each get an independent budget, and a cleanup timeout halts further cleanup; bare resolution is open only to the async flow of the current activation callback; `ResolveAll` returns every provider; cleanup order follows the actual dependency edges; ordinary unsubscription releases event references immediately; state deltas commit atomically inside the lock and terminate when the kernel is disposed; observer diagnostics keep only shared strings; snapshots no longer carry plugin manifests, so they cannot block dynamic plugin collection; and cross-ALC acceptance tests over a real shared contract were added. M0 was closed on 2026-10-24. For M1, the agent contracts and a deterministic comparison between MAF and the in-house loop are done; the in-house loop was selected as the official implementation, and comparison against real providers is still pending.

The repository currently contains:

- 6 product projects: `Kernel`, `Agent`, `Ai`, `Mcp`, `Acp`, `Cli`
- 1 kernel test project
- 3 sample projects: basic plugin, contract plugin, shared contract
- `Dougong.Telemetry` — not yet created
- `Dougong.Agent` — loop, commit barrier, approval, resource coordination, file-version recheck and context compaction are implemented
- `Dougong.Ai` — three wire-protocol connectors and the `ChatClientFactory` are implemented
- `Dougong.Mcp`, `Dougong.Acp` — still skeletons (M2)
- `Dougong.Cli` — a runnable host: REPL + approval + in-workspace read/write + JSONL sessions

## Why Dougong

Dougong focuses on the **supporting layer** for agents — not on any particular model or UI:

- Plugins get a clear lifecycle and scope
- Services collaborate through generation binding and invocation leases
- Missing dependencies, provider replacement and deactivation all go through one coordinator
- Dynamic plugins use collectible `AssemblyLoadContext`s
- Agents, MCP, ACP, Skills and hosts can all compose as plugins
- Model providers plug in through `Microsoft.Extensions.AI`; no provider matrix is baked into the kernel

## Core Architecture

```text
┌──────────────────────────────────────────────────────┐
│                    Host / Desktop                    │
│              CLI · Avalonia · ACP Client             │
└──────────────────────────┬───────────────────────────┘
                           │
┌──────────────────────────▼───────────────────────────┐
│ Dougong.Agent                                        │
│ agent loop · sessions · tool pipeline · approval ·   │
│ context compaction                                   │
├──────────────────────────────────────────────────────┤
│ Dougong.Ai · Dougong.Mcp · Dougong.Acp               │
│ model routing · MCP bridge · ACP adapter             │
├──────────────────────────────────────────────────────┤
│ Dougong.Kernel                                       │
│ lifecycle · service registry · dependency graph ·    │
│ events · dynamic loading                             │
└──────────────────────────────────────────────────────┘
```

### `Dougong.Kernel` Design Highlights

| Capability | Description |
|---|---|
| Lifecycle | `Registered`, `Activating`, `Active`, `Deactivating`, `Stopped`, `Blocked`, `Failed` states with one coordinator |
| Service collaboration | `(Type, Name?)` service keys, optional requirements, explicit `ProviderId`, priority selection and activation transactions |
| Invocation lease | `IResolver` returns generation-bound service references; the in-flight count is released after the call |
| Deactivation drain | Close new calls, cancel plugin lifetime, wait for in-flight calls and accepted events, then clean up |
| Event bus | Typed subscriptions, dedicated bounded queues, async pumping, unsubscribe-releases-references and overflow diagnostics |
| State view | Revision-numbered deltas committed atomically in the lock, snapshots, cursor renewal, register/remove projections and resync signals |
| Dynamic loading | Collectible `AssemblyLoadContext`, explicit shared contracts, version checks and cooperative unloading |
| External reference hygiene | Snapshots and handles carry only shared data; the full manifest is fetched explicitly |

> The current implementation is a trusted in-process plugin model. The ALC provides dependency isolation and cooperative unloading — not an OS-level permission sandbox or fault isolation.

## Package Structure

| Package | Role | Status |
|---|---|---|
| `Dougong.Kernel` | Lifecycle, services, events, dependency graph, dynamic loading | M0 closed (156 tests + 13 probes) |
| `Dougong.Agent` | Agent runtime contracts, loop, sessions, tools, approval | M1 main body implemented |
| `Dougong.Ai` | Three wire-protocol connectors, `ChatClientFactory` | M1 implemented; usage accounting pending |
| `Dougong.Mcp` | MCP server ↔ agent tool bridge | Skeleton (M2) |
| `Dougong.Acp` | ACP agent-side adapter | Skeleton (M2) |
| `Dougong.Cli` | Headless reference host | Runnable; all three protocols selectable |
| `Dougong.Telemetry` | OpenTelemetry bridge | Planned |

## Getting Started

### Requirements

- .NET SDK `10.0.402`
- Windows, Linux or macOS
- Git

### Build and Test

```bash
git clone https://github.com/zhanghaojie513/Dougong.git
cd Dougong

dotnet restore Dougong.slnx
dotnet build Dougong.slnx -c Release
dotnet test Dougong.slnx -c Release --no-build
```

### Packaging

```bash
dotnet pack Dougong.slnx -c Release --output ./artifacts/packages
```

The API is still experimental. Before using `Dougong.Kernel`, read the status board and the architecture doc, and trust the actual API and test results over this README.

## Documentation

> Categorization and reading paths live in [docs/README.md](docs/README.md). Root-level docs describe the current system; `docs/vision/` holds direction and philosophy, not promises of implemented capability. **The documentation is currently written in Chinese.**

| Document | Contents |
|---|---|
| [Architecture](docs/architecture.md) | Layering, kernel contracts, state machine, dynamic loading, host boundary |
| [Overall design](docs/design.md) | Ecological niche, milestones, MAF vs in-house loop decision gates |
| [Status board](docs/status.md) | What is implemented, test baseline, open acceptance items, recovery entry |
| [M1 candidate comparison](docs/m1-candidate-comparison.md) | Like-for-like comparison of MAF wrapping vs the in-house loop, versions, decision rationale |
| [M0 dev journey](docs/journey/m0-dev-journey.md) | Problems and misjudgments that actually happened during M0, for retrospective writing |
| [Development plan](docs/plan.md) | Milestone order, gap register and acceptance criteria |
| [M1 dev journey](docs/journey/m1-dev-journey.md) | Defects, test flaws and misjudgments during M1, including protocol issues only real providers exposed |
| [Philosophy](docs/vision/philosophy.md) | The dougong metaphor, design principles, explicit non-goals |
| [Frontier survey](docs/vision/frontier-roadmap.md) | Public progress in agent harnesses, protocols, isolation and memory evaluation |
| [Iteration roadmap](docs/vision/iteration-roadmap.md) | Internal ordering and exit criteria beyond M3 |
| [Skill evolution](docs/vision/skills-evolution.md) | A vision for skill retrospection, consolidation, approval and loading |

## Roadmap

```text
M0  plugin kernel and lifecycle counterexample verification   ← done
 │
 ▼
M1  agent runtime comparison and a minimal usable agent       ← current
 │
 ▼
M2  ACP · MCP · telemetry · restricted memory
 │
 ▼
M3  plugin API stabilization and ecosystem tooling
```

M1 does not accept "a loop that runs" as its bar. The plan compares `MAF HarnessAgent` against an in-house `IChatClient`-based loop over the same set of deterministic fake models, fake tools and failure cases before choosing the final implementation path.

## Design Boundaries

Dougong currently **is not**:

- a port of, or a compatible runtime for, dsh
- an in-process security sandbox
- an LLM provider matrix
- a desktop UI or IDE
- a multi-agent orchestration framework
- a finished AI coding assistant

The companion desktop client will be developed as a separate Dougong Desktop host project; this repository owns the SDK, the kernel and the reference CLI.

## Contributing

The project is in a period of rapid API evolution. Before opening an issue or pull request, please read the [status board](docs/status.md) and the [architecture doc](docs/architecture.md), and add deterministic failure-path tests for any behavior change.

## Acknowledgements

Dougong is an independent open-source project. The following projects provided public ideas or technical references:

- [deepseek-harness](https://github.com/deepseek-ai/deepseek-harness) — the "Everything is a Plugin" architectural reference
- [Microsoft Agent Framework](https://learn.microsoft.com/en-us/agent-framework/) — agent runtime capability reference
- [Microsoft.Extensions.AI](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai) — model abstraction and connector foundation
- [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) — MCP protocol foundation
- [Hermes Agent](https://github.com/NousResearch/hermes-agent) — reference for the skill self-evolution idea
- [TKCode](https://github.com/AtomUICommunity/TKCode) — design reference for self-describing permissions, concurrency tiers and commit barriers

## License

[MIT](LICENSE) © 2026 Haojie Zhang (张浩洁)
