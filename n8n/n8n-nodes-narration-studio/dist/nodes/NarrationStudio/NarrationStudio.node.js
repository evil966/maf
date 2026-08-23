"use strict";

const FORM_FIELDS = [
  { displayName: "Title", name: "title", type: "string", default: "", description: "Stored in run.txt and job status." },
  { displayName: "Orchestration", name: "orchestration", type: "options", default: "direct", options: [
    { name: "Direct", value: "direct" },
    { name: "Handoff", value: "handoff" },
    { name: "Sequential", value: "sequential" }
  ], description: "Used by Start Job only. Create Session always runs the explicit n8n stage chain (direct)." },
  { displayName: "Use Stub Narration", name: "useStubNarration", type: "boolean", default: true },
  { displayName: "Use Stub Slide Phrases", name: "useStubSlidePhrases", type: "boolean", default: false },
  { displayName: "Transcript Binary Field", name: "transcriptBinary", type: "string", default: "transcript", description: "Binary property name. Also accepts Transcript_file from the Form Trigger." },
  { displayName: "Stub Narration Binary Field", name: "stubNarrationBinary", type: "string", default: "stubNarration", description: "Binary property name. Also accepts Stub_narration_file from the Form Trigger." },
  { displayName: "Segment Count", name: "segmentCount", type: "number", default: 8, typeOptions: { minValue: 1 } },
  { displayName: "Max Slides Per Segment", name: "maxSlidesPerSegment", type: "number", default: 5, typeOptions: { minValue: 1, maxValue: 5 } },
  { displayName: "Target Talk Minutes", name: "targetTalkMinutes", type: "number", default: 7, typeOptions: { minValue: 1 } },
  { displayName: "Ollama Endpoint", name: "endpoint", type: "string", default: "http://localhost:11434" },
  { displayName: "Ollama Model", name: "model", type: "string", default: "qwen3.8:latest" },
  { displayName: "Ollama Timeout Minutes", name: "timeoutMinutes", type: "number", default: 30 },
  { displayName: "OpenAI Model", name: "openAIModel", type: "string", default: "gpt-4o-mini" },
  { displayName: "Azure OpenAI Endpoint", name: "azureOpenAIEndpoint", type: "string", default: "" },
  { displayName: "Azure OpenAI Deployment", name: "azureOpenAIDeployment", type: "string", default: "gpt-4o-mini" },
  { displayName: "ElevenLabs Voice ID", name: "elevenLabsVoiceId", type: "string", default: "UPXQvvSDDubCtocmbltQ" },
  { displayName: "ElevenLabs Model ID", name: "elevenLabsModelId", type: "string", default: "eleven_multilingual_v2" },
  { displayName: "Video Width", name: "videoWidth", type: "number", default: 1920 },
  { displayName: "Video Height", name: "videoHeight", type: "number", default: 1080 },
  { displayName: "Video Frame Rate", name: "videoFrameRate", type: "number", default: 30 },
  { displayName: "Rewrite Extra Instructions", name: "rewriteExtraInstructions", type: "string", typeOptions: { rows: 4 }, default: "" },
  { displayName: "Callback URL", name: "callbackUrl", type: "string", default: "", description: "Optional webhook when a full Start Job completes or fails." },
  { displayName: "Keep Artifacts", name: "keepArtifacts", type: "boolean", default: true }
];

const STEP_OPERATIONS = {
  rewrite: "rewrite",
  segment: "segment",
  planSlides: "plan-slides",
  render: "render",
  tts: "tts",
  compose: "compose",
  merge: "merge",
  verify: "verify"
};

const JOB_ID_OPERATIONS = [
  "rewrite", "segment", "planSlides", "render", "tts", "compose", "merge", "verify",
  "status", "wait", "waitForStep", "download"
];

function showFor(property, operations) {
  return {
    ...property,
    displayOptions: { show: { operation: operations } }
  };
}

class NarrationStudio {
  constructor() {
    this.description = {
      displayName: "Narration Studio",
      name: "narrationStudio",
      icon: "file:narrationStudio.svg",
      group: ["transform"],
      version: [2, 3, 4],
      subtitle: "={{$parameter[\"operation\"]}}",
      description: "Drive Narration Studio sessions and jobs on the Windows host",
      defaults: { name: "Narration Studio" },
      inputs: ["main"],
      outputs: ["main"],
      credentials: [{ name: "narrationStudioApi", required: false }],
      properties: [
        {
          displayName: "Operation",
          name: "operation",
          type: "options",
          noDataExpression: true,
          default: "createSession",
          options: [
            { name: "Create Session", value: "createSession", action: "Allocate a session without starting production" },
            { name: "Rewrite", value: "rewrite", action: "Rewrite the transcript into narration" },
            { name: "Segment", value: "segment", action: "Split narration into scripts" },
            { name: "Plan Slides", value: "planSlides", action: "Plan slides for every segment" },
            { name: "Render", value: "render", action: "Render PNG slides" },
            { name: "TTS", value: "tts", action: "Synthesize segment audio" },
            { name: "Compose", value: "compose", action: "Compose segment MP4s" },
            { name: "Merge", value: "merge", action: "Merge segment videos" },
            { name: "Verify", value: "verify", action: "Run HarnessChecks" },
            { name: "Get Status", value: "status", action: "Get job or session status" },
            { name: "Wait Until Complete", value: "wait", action: "Poll until the full job finishes" },
            { name: "Wait For Step", value: "waitForStep", action: "Poll until one session step finishes" },
            { name: "Download Merged Video", value: "download", action: "Download the merged MP4" },
            { name: "Start Job", value: "start", action: "Start a full production job (compatibility)" }
          ]
        },
        {
          displayName: "Job ID",
          name: "jobId",
          type: "string",
          default: "",
          required: true,
          displayOptions: { show: { operation: JOB_ID_OPERATIONS } }
        },
        {
          displayName: "Step Name",
          name: "waitStep",
          type: "options",
          default: "plan-slides",
          options: [
            { name: "Plan Slides", value: "plan-slides" },
            { name: "TTS", value: "tts" },
            { name: "Rewrite", value: "rewrite" },
            { name: "Segment", value: "segment" },
            { name: "Render", value: "render" },
            { name: "Compose", value: "compose" },
            { name: "Merge", value: "merge" },
            { name: "Verify", value: "verify" }
          ],
          displayOptions: { show: { operation: ["waitForStep"] } }
        },
        {
          displayName: "Poll Interval Seconds",
          name: "pollInterval",
          type: "number",
          default: 5,
          displayOptions: { show: { operation: ["wait", "waitForStep"] } }
        },
        {
          displayName: "Timeout Minutes",
          name: "waitTimeoutMinutes",
          type: "number",
          default: 120,
          displayOptions: { show: { operation: ["wait", "waitForStep"] } }
        },
        ...FORM_FIELDS.map((property) => showFor(property, ["start", "createSession"]))
      ]
    };
  }

  async execute() {
    const items = this.getInputData();
    const operation = this.getNodeParameter("operation", 0);
    const credentials = await loadCredentials.call(this);
    const baseUrl = String(credentials.baseUrl || "http://host.docker.internal:5088").replace(/\/$/, "");
    const returnData = [];

    for (let i = 0; i < items.length; i++) {
      if (operation === "start") {
        returnData.push({ json: await createProduction.call(this, i, items[i], baseUrl, credentials, "/v1/jobs") });
      } else if (operation === "createSession") {
        returnData.push({ json: await createProduction.call(this, i, items[i], baseUrl, credentials, "/v1/sessions") });
      } else if (STEP_OPERATIONS[operation]) {
        const jobId = this.getNodeParameter("jobId", i);
        returnData.push({ json: await runStep.call(this, jobId, STEP_OPERATIONS[operation], baseUrl, credentials) });
      } else if (operation === "status") {
        const jobId = this.getNodeParameter("jobId", i);
        returnData.push({ json: await requestJson.call(this, "GET", `${baseUrl}/v1/jobs/${jobId}`, credentials) });
      } else if (operation === "wait") {
        const jobId = this.getNodeParameter("jobId", i);
        const interval = Number(this.getNodeParameter("pollInterval", i)) * 1000;
        const timeoutMs = Number(this.getNodeParameter("waitTimeoutMinutes", i)) * 60 * 1000;
        returnData.push({ json: await waitForJob.call(this, jobId, baseUrl, credentials, interval, timeoutMs) });
      } else if (operation === "waitForStep") {
        const jobId = this.getNodeParameter("jobId", i);
        const step = this.getNodeParameter("waitStep", i);
        const interval = Number(this.getNodeParameter("pollInterval", i)) * 1000;
        const timeoutMs = Number(this.getNodeParameter("waitTimeoutMinutes", i)) * 60 * 1000;
        returnData.push({ json: await waitForStep.call(this, jobId, step, baseUrl, credentials, interval, timeoutMs) });
      } else if (operation === "download") {
        const jobId = this.getNodeParameter("jobId", i);
        const binary = await downloadVideo.call(this, jobId, baseUrl, credentials);
        returnData.push({ json: { jobId }, binary: { data: binary } });
      }
    }

    return [returnData];
  }
}

async function createProduction(index, item, baseUrl, credentials, path) {
  const body = {
    title: cleanString(this.getNodeParameter("title", index, "")),
    orchestration: this.getNodeParameter("orchestration", index) || "direct",
    useStubNarration: toBool(this.getNodeParameter("useStubNarration", index), true),
    useStubSlidePhrases: toBool(this.getNodeParameter("useStubSlidePhrases", index), false),
    segmentCount: toNumber(this.getNodeParameter("segmentCount", index), 8),
    maxSlidesPerSegment: toNumber(this.getNodeParameter("maxSlidesPerSegment", index), 5),
    targetTalkMinutes: toNumber(this.getNodeParameter("targetTalkMinutes", index), 7),
    endpoint: cleanString(this.getNodeParameter("endpoint", index, "")),
    model: cleanString(this.getNodeParameter("model", index, "")),
    timeoutMinutes: toNumber(this.getNodeParameter("timeoutMinutes", index), 30),
    openAIModel: cleanString(this.getNodeParameter("openAIModel", index, "")),
    azureOpenAIEndpoint: cleanString(this.getNodeParameter("azureOpenAIEndpoint", index, "")),
    azureOpenAIDeployment: cleanString(this.getNodeParameter("azureOpenAIDeployment", index, "")),
    elevenLabsVoiceId: cleanString(this.getNodeParameter("elevenLabsVoiceId", index, "")),
    elevenLabsModelId: cleanString(this.getNodeParameter("elevenLabsModelId", index, "")),
    videoWidth: toNumber(this.getNodeParameter("videoWidth", index), 1920),
    videoHeight: toNumber(this.getNodeParameter("videoHeight", index), 1080),
    videoFrameRate: toNumber(this.getNodeParameter("videoFrameRate", index), 30),
    rewriteExtraInstructions: cleanString(this.getNodeParameter("rewriteExtraInstructions", index, "")),
    callbackUrl: cleanString(this.getNodeParameter("callbackUrl", index, "")),
    keepArtifacts: toBool(this.getNodeParameter("keepArtifacts", index), true),
    transcriptText: await readBinaryText.call(
      this,
      index,
      item,
      this.getNodeParameter("transcriptBinary", index),
      ["transcript", "Transcript_file", "Transcript file", "transcript_file"]
    ),
    stubNarrationText: await readBinaryText.call(
      this,
      index,
      item,
      this.getNodeParameter("stubNarrationBinary", index),
      ["stubNarration", "Stub_narration_file", "Stub narration file", "Dub_narration_file", "dubNarration"]
    )
  };

  return studioRequest.call(this, {
    method: "POST",
    url: `${baseUrl}${path}`,
    headers: { ...credentialHeaders(credentials), "Content-Type": "application/json" },
    body
  });
}

async function runStep(jobId, step, baseUrl, credentials) {
  const status = await studioRequest.call(this, {
    method: "POST",
    url: `${baseUrl}/v1/sessions/${jobId}/steps/${step}`,
    headers: credentialHeaders(credentials)
  });
  throwIfFailed(status, `Session ${jobId} step ${step} failed.`, step);
  return status;
}

async function readBinaryText(index, item, preferredName, aliases) {
  const key = resolveBinaryKey(item, [preferredName, ...aliases]);
  if (!key) {
    return "";
  }
  const buffer = await this.helpers.getBinaryDataBuffer(index, key);
  return Buffer.from(buffer).toString("utf8");
}

function resolveBinaryKey(item, names) {
  const binary = item.binary || {};
  for (const name of names) {
    if (name && binary[name]) {
      return name;
    }
  }
  return "";
}

function cleanString(value) {
  if (value === undefined || value === null) {
    return "";
  }
  const text = String(value).trim();
  return text === "undefined" || text === "null" ? "" : text;
}

function toBool(value, fallback) {
  if (typeof value === "boolean") {
    return value;
  }
  const text = String(value ?? "").trim().toLowerCase();
  if (text === "true" || text === "1" || text === "yes") {
    return true;
  }
  if (text === "false" || text === "0" || text === "no") {
    return false;
  }
  return fallback;
}

function toNumber(value, fallback) {
  const number = Number(value);
  return Number.isFinite(number) ? number : fallback;
}

function stepStatusOf(status, step) {
  if (status && status.steps && status.steps[step]) {
    return status.steps[step];
  }
  if (status && status.currentStep === step) {
    return status.stepStatus;
  }
  return "";
}

function stepErrorOf(status, step) {
  if (status && status.stepErrors && status.stepErrors[step]) {
    return status.stepErrors[step];
  }
  return status && status.error ? status.error : "";
}

function throwIfFailed(status, fallback, step) {
  if (!status) {
    return;
  }
  if (step) {
    if (stepStatusOf(status, step) === "failed") {
      throw new Error(stepErrorOf(status, step) || fallback);
    }
    return;
  }
  if (status.stepStatus === "failed" || status.status === "failed") {
    throw new Error(status.error || fallback);
  }
}

async function waitForJob(jobId, baseUrl, credentials, interval, timeoutMs) {
  const started = Date.now();
  while (true) {
    const status = await requestJson.call(this, "GET", `${baseUrl}/v1/jobs/${jobId}`, credentials);
    if (status.status === "complete" || status.status === "failed") {
      throwIfFailed(status, `Job ${jobId} failed.`);
      return status;
    }
    if (Date.now() - started > timeoutMs) {
      throw new Error(`Timed out waiting for job ${jobId} (last status: ${status.status}).`);
    }
    await new Promise((resolve) => setTimeout(resolve, interval));
  }
}

async function waitForStep(jobId, step, baseUrl, credentials, interval, timeoutMs) {
  const started = Date.now();
  while (true) {
    const status = await requestJson.call(this, "GET", `${baseUrl}/v1/jobs/${jobId}`, credentials);
    const stepStatus = stepStatusOf(status, step);
    if (stepStatus === "failed") {
      throwIfFailed(status, `Session ${jobId} step ${step} failed.`, step);
    }
    if (stepStatus === "complete") {
      return status;
    }
    if (Date.now() - started > timeoutMs) {
      throw new Error(`Timed out waiting for session ${jobId} step ${step} (last stepStatus: ${stepStatus || status.stepStatus}).`);
    }
    await new Promise((resolve) => setTimeout(resolve, interval));
  }
}

async function downloadVideo(jobId, baseUrl, credentials) {
  const body = await studioRequest.call(this, {
    method: "GET",
    url: `${baseUrl}/v1/jobs/${jobId}/artifacts/merged`,
    headers: credentialHeaders(credentials),
    encoding: "arraybuffer"
  });
  return this.helpers.prepareBinaryData(body, `video_${jobId}.mp4`, "video/mp4");
}

async function requestJson(method, url, credentials) {
  return studioRequest.call(this, {
    method,
    url,
    headers: credentialHeaders(credentials)
  });
}

async function loadCredentials() {
  try {
    return (await this.getCredentials("narrationStudioApi")) || {};
  } catch {
    return {};
  }
}

async function studioRequest(options) {
  if (this.helpers.httpRequest) {
    const request = {
      method: options.method,
      url: options.url,
      headers: options.headers || {}
    };
    if (options.body) {
      request.body = options.body;
      request.headers = { ...request.headers, "Content-Type": "application/json" };
    }
    if (options.encoding === "arraybuffer") {
      request.encoding = "arraybuffer";
    }
    return this.helpers.httpRequest(request);
  }

  return this.helpers.request({
    method: options.method,
    url: options.url,
    headers: options.headers,
    body: options.body,
    encoding: options.encoding === "arraybuffer" ? null : undefined,
    json: true
  });
}

function credentialHeaders(credentials) {
  const headers = {};
  if (credentials && credentials.apiKey) {
    headers["X-Api-Key"] = credentials.apiKey;
  }
  if (credentials.elevenLabsApiKey) {
    headers["X-ElevenLabs-Api-Key"] = credentials.elevenLabsApiKey;
  }
  if (credentials.openAIApiKey) {
    headers["X-OpenAI-Api-Key"] = credentials.openAIApiKey;
  }
  if (credentials.azureOpenAIApiKey) {
    headers["X-Azure-OpenAI-Api-Key"] = credentials.azureOpenAIApiKey;
  }
  return headers;
}

module.exports = { NarrationStudio };
