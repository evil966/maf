"use strict";

class NarrationStudioApi {
  constructor() {
    this.name = "narrationStudioApi";
    this.displayName = "Narration Studio API";
    this.documentationUrl = "https://github.com";
    this.properties = [
      {
        displayName: "Base URL",
        name: "baseUrl",
        type: "string",
        default: "http://host.docker.internal:5088",
        required: true,
        description: "Windows Studio service. From Docker n8n use host.docker.internal."
      },
      {
        displayName: "API Key",
        name: "apiKey",
        type: "string",
        typeOptions: { password: true },
        default: "",
        description: "Sent as X-Api-Key. Leave empty if Service:ApiKey is empty."
      },
      {
        displayName: "ElevenLabs API Key",
        name: "elevenLabsApiKey",
        type: "string",
        typeOptions: { password: true },
        default: ""
      },
      {
        displayName: "OpenAI API Key",
        name: "openAIApiKey",
        type: "string",
        typeOptions: { password: true },
        default: ""
      },
      {
        displayName: "Azure OpenAI API Key",
        name: "azureOpenAIApiKey",
        type: "string",
        typeOptions: { password: true },
        default: ""
      }
    ];
    this.authenticate = {
      type: "generic",
      properties: {
        headers: {
          "X-Api-Key": "={{$credentials.apiKey}}"
        }
      }
    };
    this.test = {
      request: {
        baseURL: "={{$credentials.baseUrl}}",
        url: "/health"
      }
    };
  }
}

module.exports = { NarrationStudioApi };
