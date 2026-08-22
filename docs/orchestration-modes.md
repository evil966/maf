# Narration Studio orchestration modes

This note describes the three `dotnet run` modes (`handoff`, `sequential`, `direct`): what Microsoft Agent Framework (MAF) actually requires, what this app added, **why** each path exists, and **who put it there**.

**Confirmed** means the behavior is in this repository’s source or git history. **Inferred** means MAF package behavior that this repo calls but does not implement.

## Short answers

| Question | Answer |
| --- | --- |
| Are the three modes a MAF requisite? | **No.** MAF ships **handoff** and **sequential** as workflow builders. It does not require a CLI with three modes, and it has no “direct” orchestration type. |
| Why are they in this app? | **By design** of this boilerplate: demonstrate two MAF graphs, plus a no-agent factory that still produces the video. |
| Did this documentation pass insert them? | **No.** `--orchestration`, default `handoff`, `DirectExecutor`, and failover to direct were in the **first commit** (`0436a60`, 20 August 2026). Later commits only changed *how* those modes behave (stub skips the rewriter agent; direct always overwrites media). |

## How to invoke

Working directory should be the **repo root**. The `--` after the project path is required so `dotnet` does not swallow the flag.

Empty args (no `--orchestration` and no transcript path) start the **HTTP job API** for n8n, not a CLI handoff run. See `docs/n8n-service.pdf`.

```powershell
dotnet run --project src\NarrationStudio
dotnet run --project src\NarrationStudio -- --orchestration handoff
dotnet run --project src\NarrationStudio -- --orchestration sequential
dotnet run --project src\NarrationStudio -- --orchestration direct
dotnet run --project src\NarrationStudio -- path\to\transcript.txt --orchestration direct
```

Parsing is in `ProductionApp.ResolveOrchestration`:

1. `--orchestration=value` (equals form)
2. else `--orchestration value` (two tokens)
3. else default **`handoff`**

The value is lowercased. There is no `--help`. A token that does not start with `--` is a transcript path, not a mode.

If the string is anything other than `direct` or `sequential` (including typos), the app does **not** error. It treats the run as **handoff** (`orchestration == "sequential"` is false → `CreateHandoffWorkflow`). **Confirmed** in `ProductionApp.RunAsync`.

## What is MAF vs what is this app

| Piece | Origin | Why it exists |
| --- | --- | --- |
| `AgentWorkflowBuilder.CreateHandoffBuilderWith` / `WithHandoff` / `WithAutonomousMode` | MAF (`Microsoft.Agents.AI.Workflows`) | Library pattern: a coordinator routes to specialists using natural-language handoff conditions. **Inferred:** conditions are hints to the model, not compiled predicates. |
| `AgentWorkflowBuilder.BuildSequential` | MAF | Library pattern: run a fixed list of agents in order. |
| `InProcessExecution.RunStreamingAsync` + `TurnToken` | MAF | In-process workflow runtime. |
| `AIAgent` / `AsAIAgent` / `AIFunctionFactory.Create` | MAF + `Microsoft.Extensions.AI` | Wrap an `IChatClient` as an agent with tools. |
| `--orchestration` CLI | This app | Expose the two MAF graphs and the no-agent path without recompiling. |
| Default `handoff` | This app | The intended “MAF demo” path. |
| `DirectExecutor` | This app | Deterministic factory: call `ProductionTools` in order. Not a MAF type. |
| Failover: if agents do not produce a merged MP4, call direct | This app | Video is the product; the LLM is optional. |
| Fall through to direct if `TryCreate` fails | This app | No Ollama/OpenAI/Azure client → still produce media. |
| Stub rewrite before `TryCreate`; omit `narration_rewriter` from the graph when stubbing | This app (later change) | Avoid connecting to Ollama just to copy `voice_transcript_ollama.txt`. |

MAF requisites for **handoff or sequential only** are: an `IChatClient`, agents, tools, and a workflow runner. ffmpeg, ElevenLabs, the mutex, `PipelineStore`, and direct mode are **not** MAF requisites.

## Mode-by-mode

### `handoff` (default)

**What.** `WorkflowHost.CreateHandoffWorkflow`. Coordinator agent (`GetPipelineStatus` only) plus specialists. Each specialist is granted a small tool set. Handoff edges include coordinator → each specialist, rewriter → segmenter (if stubbing is off), slide_planner → image_renderer, and every specialist → coordinator (“return after tools”).

**Autonomous mode (confirmed).** `WithAutonomousMode(turnLimit: 8, continuationPrompt: "Continue from GetPipelineStatus…", agents: [coordinator])`. After eight coordinator turns the MAF runtime is expected to stop (**inferred** exact stop semantics).

**Why.** This is the Microsoft-style multi-agent demo: a router decides who runs next. It exists to exercise MAF handoff, not because the factory needs a router. The pipeline order is already known.

**When it still becomes direct.**

- `--orchestration direct`, or
- `AgentFactory.TryCreate` returns false (no chat client), or
- workflow returns and `MergedVideoPath` is missing / file not on disk → log warning and `DirectExecutor.RunAsync()`.

### `sequential`

**What.** `WorkflowHost.CreateSequentialWorkflow` → `BuildSequential("narration-studio-sequential", agents)`. Order:

- Stubbing **on:** segmenter → slide_planner → image_renderer → tts → video_composer
- Stubbing **off:** rewriter first, then the same list

No coordinator. **Inferred:** MAF runs that list in order; it does not consult handoff conditions.

**Why.** Second MAF sample path: “do not make the model choose the next specialist.” Useful when handoff routing is unreliable. Still requires a live LLM for each agent’s tool-calling turn.

**Failover.** Same as handoff: missing merged file → direct.

### `direct`

**What.** `DirectExecutor.RunAsync` calls tools in a fixed C# order:

1. `GetPipelineStatus`
2. `RephraseTranscriptIntoNarration` only if `NarrationPath` is empty/missing (ProductionApp usually already stub-copied it)
3. `SegmentTranscriptIntoEightScripts` (always, current code)
4. `PlanSlidesForAllSegments` (regex split + Ollama one-sentence per slide)
5. `RenderAllSlideImages`
6. `SynthesizeAllAudioAsync`
7. `ComposeAllSegmentVideos`
8. `MergeAllVideos`

No `FileMemoryProvider`, no `ProductionGuardrails`. Snapshots (`run.txt`, `pipeline-status.txt`, `notes.txt`) still write because `PipelineStore.Mutate` always snapshots.

**Why.** The video must ship even when:

- Ollama/OpenAI is down (`TryCreate` fails → this path)
- The model skips a specialist or burns the 8-turn limit (failover)
- You want a repeatable factory for development (`--orchestration direct`)

Direct still talks to Ollama for **slide sentences** and to ElevenLabs/SAPI for TTS. “No agents” is not “no LLMs.”

## Control flow (confirmed)

```
ProductionApp.RunAsync
  → mutex NarrationStudio.Production
  → ResolveOrchestration  (default handoff)
  → Initialize (wipe media; keep old memory folders)
  → ProductionHarness.RunAsync
      → if UseStubNarration: RephraseTranscriptIntoNarration (copy stub)
      → if mode == direct OR TryCreate fails
            → DirectExecutor
        else
            → sequential ? CreateSequentialWorkflow : CreateHandoffWorkflow
            → WorkflowHost.RunAsync (MAF streaming)
            → if no merged MP4 → DirectExecutor
      → HarnessChecks.Verify (throw if artifacts missing)
  → print Done: path
```

## Why this shape (design, not a MAF rule)

The app is a **MAF boilerplate around a real pipeline**. The durable contract is `ProductionTools`. Agents are optional drivers of those tools.

That is why three modes exist together:

1. **Handoff** — show MAF routing.
2. **Sequential** — show MAF ordered crew without a coordinator.
3. **Direct** — show that the same tools work with zero agents; also the safety net.

Microsoft’s docs describe handoff and sequential as **alternative orchestration styles**. They do not say an application must expose both plus a third non-agent runner. That combination is this repo’s teaching/production design.

## Provenance (git and this project’s history)

**First commit `0436a60` (20 August 2026, author Edwin Villapando)** already contained:

- `ProductionApp.ResolveOrchestration` and default `handoff`
- `WorkflowHost.CreateHandoffWorkflow` and `CreateSequentialWorkflow`
- `DirectExecutor`
- Failover to direct when merge is missing
- Fall through to direct when `TryCreate` fails

So the three-mode CLI is **original application design**, not a late documentation-session patch.

That first commit is the Narration Studio boilerplate as first built in this workspace (Cursor agent + your git author). It is **not** a file copied unchanged from the MAF NuGet samples. MAF contributed the **APIs**; this app contributed the **CLI, crew, tools, and direct runner**.

**Later commits (same repo, not a MAF upgrade):**

| Change | Effect on modes |
| --- | --- |
| Stub narration / skip `narration_rewriter` in the graph | Handoff and sequential omit the rewriter when `UseStubNarration` is true. Direct still no-ops rephrase if the stub file is already copied. |
| Direct always re-segments / re-plans / overwrites media | Direct used to skip segmentation if `Segments.Count` already matched. Current code always regenerates after wipe. |
| Ollama slide sentences | All three modes hit Ollama during plan/render. Independent of orchestration. |
| Timestamped memory snapshots | All modes write `production-notes-{RunId}`. Only handoff/sequential construct MAF `FileMemoryProvider`. |

**This documentation session** (harness / agentic / internal-workings PDFs) did **not** add `--orchestration` or `DirectExecutor`. It only described them.

If “inserted by you” means “did an AI invent a fake MAF requirement?”: **no.** If it means “did an AI add a no-agent path when building the boilerplate?”: **yes — as a deliberate app design**, present from the first commit, so the factory is not hostage to the coordinator.

## What each mode needs at runtime

| Need | handoff | sequential | direct |
| --- | --- | --- | --- |
| `IChatClient` (Ollama / OpenAI / Azure) for agents | Yes, else falls to direct | Same | No |
| Ollama for slide sentences (current default) | Yes (via tools) | Yes | Yes |
| Ollama for 7-minute rewrite | Only if stubbing off | Same | Same |
| ElevenLabs or SAPI | Yes | Yes | Yes |
| ffmpeg | Yes | Yes | Yes |
| Guardrail middleware | Yes | Yes | No |
| MAF `FileMemoryProvider` | Yes | Yes | No |
| Failover to direct | Yes | Yes | Already there |

## Key files

| File | Role |
| --- | --- |
| `src/NarrationStudio/ProductionApp.cs` | CLI parse, default, TryCreate gate, failover |
| `src/NarrationStudio/Agents/WorkflowHost.cs` | Handoff graph, sequential list, user prompt, streaming run |
| `src/NarrationStudio/Agents/AgentFactory.cs` | Crew, tools, chat client, guardrails, file memory |
| `src/NarrationStudio/Harness/ProductionHarness.cs` | Run policy, failover, HarnessChecks |
| `src/NarrationStudio/Orchestration/DirectExecutor.cs` | No-agent tool loop |
| `src/NarrationStudio/Tools/ProductionTools.cs` | Shared I/O for every mode |
