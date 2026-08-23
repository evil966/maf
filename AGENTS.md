# Narration Studio

Windows console app. Microsoft Agent Framework (MAF) is the inner agent runtime. This repo’s **production harness** owns I/O.

## Working directory

Repo root (`E:\exercises\maf` or clone root). Launch profile and `dotnet run` expect transcripts and `output/` here.

## Do not invert the factory

- All PNG / MP3 / MP4 writes go through `ProductionTools`. Do not give specialists extra disk APIs.
- Caps (`SegmentCount`, `MaxSlidesPerSegment`, `TargetTalkMinutes`) come from `Studio:`. Do not hardcode `8` in prompts or docs.
- Success is files on disk plus `HarnessChecks` (`status == complete`). Not the coordinator’s last sentence.

## Commands

```powershell
dotnet test
dotnet run --project src\NarrationStudio
dotnet run --project src\NarrationStudio -- --orchestration direct
dotnet run --project src\NarrationStudio -- --orchestration handoff
dotnet run --project src\NarrationStudio -- --orchestration sequential
```

No args (or `--service`) starts the HTTP API on `http://0.0.0.0:5088` for n8n. n8n is the loop (`POST /v1/sessions` + per-step `/v1/sessions/{id}/steps/{step}`); `POST /v1/jobs` is the compatibility run-all. `--orchestration` still runs the CLI and exits. Optional `Service:ApiKey` (or `Service__ApiKey`) is sent as `X-Api-Key`; mint a random string — there is no generate endpoint. See `docs/n8n-service.pdf` §3.1 and `docs/NarrationStudio-Internal-Workings.pdf` §19.

`--orchestration` default is `handoff` (MAF). `direct` is the reference executor and the failover path.

## Config

`src/NarrationStudio/appsettings.json` is copied to the bin folder; `dotnet run` from repo root binds the **bin** copy. `UseStubNarration` skips the 7-minute Ollama rewrite. `UseStubSlidePhrases` skips per-slide Ollama. Do not commit API keys.

## Layout

- `src/NarrationStudio/Harness/` — `ProductionHarness`, `HarnessChecks`, `HarnessEvents`
- `src/NarrationStudio/Tools/` — only I/O surface
- `src/NarrationStudio/Agents/` — MAF crew and workflows
- `src/NarrationStudio/Orchestration/DirectExecutor.cs` — no-agent tool loop
- `docs/` — architecture notes (harness, orchestration, guardrails, memory, n8n service PDF)
- `docs/diagrams/` — factory and n8n mermaid (`.mmd` / `.svg`)
- `n8n/` — custom node + Form Trigger workflow for Docker n8n
