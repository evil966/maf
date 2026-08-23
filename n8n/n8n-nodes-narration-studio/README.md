# n8n node: Narration Studio

Community node for the Windows Narration Studio session and job API. This package lives in this repo. It is **not** on the public npm registry — do not use n8n’s **Install community nodes** dialog for this name.

The sample workflow is n8n as the loop: Form → Create Session → Rewrite → Segment → Plan Slides → Wait → Render and TTS in parallel → Join → Compose → Merge → Verify → Download. Windows `ProductionTools` still writes every PNG / MP3 / MP4. **Start Job** remains as a compatibility “run all” against `POST /v1/jobs`.

Printable operator guide (including Docker Compose YAML): `docs/n8n-service.pdf`. Architecture: `docs/NarrationStudio-Internal-Workings.pdf` §19.

## Credentials

**Narration Studio API** (optional until `Service:ApiKey` is set)

| Field | Meaning |
| --- | --- |
| Base URL | `http://host.docker.internal:5088` from Docker — not `localhost` in the container |
| API Key | Same string as Studio `Service:ApiKey`; sent as `X-Api-Key` |
| ElevenLabs / OpenAI / Azure | Other headers. Not `Service:ApiKey` |

Create it: Credentials → Add credential → **Narration Studio API**. **Test** hits `/health` (does not prove the job key). Attach the same record on every Narration Studio node except the Form Trigger (Create Session, each stage, Wait, Download). Do not attach it to the Form Trigger. If only Create Session has it, later stage nodes return 401.

With no credential selected the node still calls `http://host.docker.internal:5088`. That works while `Service:ApiKey` is empty. After you set a key: restart Studio, paste the same string into the credential, save the workflow. Full click-path: `docs/n8n-service.pdf` §3.1.

## Install into the existing Docker n8n stack

Added to the `n8n` service in `E:\icountera\n8n-api_icountera\n8n\docker-compose.yml`:

```yaml
      N8N_CUSTOM_EXTENSIONS: /custom-nodes/n8n-nodes-narration-studio
      N8N_UNVERIFIED_PACKAGES_ENABLED: "true"
    extra_hosts:
      - "host.docker.internal:host-gateway"
    volumes:
      - n8n_data:/home/node/.n8n
      - E:/exercises/maf/n8n/n8n-nodes-narration-studio:/custom-nodes/n8n-nodes-narration-studio:ro
```

```powershell
cd E:\icountera\n8n-api_icountera\n8n
docker compose -p n8n-api_icountera up -d
docker compose -p n8n-api_icountera exec n8n sh -c "mkdir -p /home/node/.n8n/nodes && cd /home/node/.n8n/nodes && npm install /custom-nodes/n8n-nodes-narration-studio"
docker compose -p n8n-api_icountera restart n8n
```

Always use `-p n8n-api_icountera`. After node JS edits, restart n8n and hard-refresh the editor (Ctrl+Shift+R). Re-run `npm install` only if `package.json` changed.

Sanitized compose (what was added vs already there): `n8n/docker-compose.n8n-excerpt.yml`. To add another custom node, append a volume and colon-separate `N8N_CUSTOM_EXTENSIONS` — see `docs/n8n-service.pdf` §2.5. That is an n8n package, not a MAF specialist.
