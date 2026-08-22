"use strict";

const FORM_FIELDS = [
  { displayName: "Title", name: "title", type: "string", default: "", description: "Stored in run.txt and job status." },
  { displayName: "Orchestration", name: "orchestration", type: "options", default: "direct", options: [
    { name: "Direct", value: "direct" },
    { name: "Handoff", value: "handoff" },
    { name: "Sequential", value: "sequential" }
  ]},
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
  { displayName: "Callback URL", name: "callbackUrl", type: "string", default: "", description: "Optional webhook when the job completes or fails." },
  { displayName: "Keep Artifacts", name: "keepArtifacts", type: "boolean", default: true }
];

function startOnly(property) {
  return {
    ...property,
    displayOptions: { show: { operation: ["start"] } }
  };
}

class NarrationStudio {
  constructor() {
    this.description = {
      displayName: "Narration Studio",
      name: "narrationStudio",
      icon: "file:narrationStudio.svg",
      group: ["transform"],
      version: [2, 3],
      subtitle: "={{$parameter[\"operation\"]}}",
      description: "Start and monitor Narration Studio jobs on the Windows host",
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
          default: "start",
          options: [
            { name: "Start Job", value: "start", action: "Start a production job" },
            { name: "Get Status", value: "status", action: "Get job status" },
            { name: "Wait Until Complete", value: "wait", action: "Poll until the job finishes" },
            { name: "Download Merged Video", value: "download", action: "Download the merged MP4" }
          ]
        },
        {
          displayName: "Job ID",
          name: "jobId",
          type: "string",
          default: "",
          required: true,
          displayOptions: { show: { operation: ["status", "wait", "download"] } }
        },
        {
          displayName: "Poll Interval Seconds",
          name: "pollInterval",
          type: "number",
          default: 5,
          displayOptions: { show: { operation: ["wait"] } }
        },
        {
          displayName: "Timeout Minutes",
          name: "waitTimeoutMinutes",
          type: "number",
          default: 120,
          displayOptions: { show: { operation: ["wait"] } }
        },
        ...FORM_FIELDS.map(startOnly)
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
        returnData.push({ json: await startJob.call(this, i, items[i], baseUrl, credentials) });
      } else if (operation === "status") {
        const jobId = this.getNodeParameter("jobId", i);
        returnData.push({ json: await requestJson.call(this, "GET", `${baseUrl}/v1/jobs/${jobId}`, credentials) });
      } else if (operation === "wait") {
        const jobId = this.getNodeParameter("jobId", i);
        const interval = Number(this.getNodeParameter("pollInterval", i)) * 1000;
        const timeoutMs = Number(this.getNodeParameter("waitTimeoutMinutes", i)) * 60 * 1000;
        returnData.push({ json: await waitForJob.call(this, jobId, baseUrl, credentials, interval, timeoutMs) });
      } else if (operation === "download") {
        const jobId = this.getNodeParameter("jobId", i);
        const binary = await downloadVideo.call(this, jobId, baseUrl, credentials);
        returnData.push({ json: { jobId }, binary: { data: binary } });
      }
    }

    return [returnData];
  }
}

async function startJob(index, item, baseUrl, credentials) {
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
    url: `${baseUrl}/v1/jobs`,
    headers: { ...credentialHeaders(credentials), "Content-Type": "application/json" },
    body
  });
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

async function waitForJob(jobId, baseUrl, credentials, interval, timeoutMs) {
  const started = Date.now();
  while (true) {
    const status = await requestJson.call(this, "GET", `${baseUrl}/v1/jobs/${jobId}`, credentials);
    if (status.status === "complete" || status.status === "failed") {
      return status;
    }
    if (Date.now() - started > timeoutMs) {
      throw new Error(`Timed out waiting for job ${jobId} (last status: ${status.status}).`);
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
