using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace TokiAi.Providers
{
    /// <summary>
    /// Anthropic Messages API. Tool results go back as content blocks inside a user turn,
    /// keyed by the tool_use id.
    /// </summary>
    public class ClaudeProvider : ChatProvider
    {
        const string DefaultBase = "https://api.anthropic.com";

        public override ChatTurn Send(string systemPrompt, List<ChatTurn> history,
            List<ToolDefinition> tools, AiSettings settings, CancellationToken cancel)
        {
            JObject body = BuildRequestBody(systemPrompt, history, tools, settings);
            string url = TrimBase(settings.CurrentBaseUrl, DefaultBase) + "/v1/messages";
            string key = settings.CurrentKey;
            JObject response = PostJson(url, body, delegate (HttpRequestMessage request)
            {
                request.Headers.TryAddWithoutValidation("x-api-key", key);
                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            }, settings, cancel);
            return ParseResponse(response);
        }

        public override JObject BuildRequestBody(string systemPrompt, List<ChatTurn> history,
            List<ToolDefinition> tools, AiSettings settings)
        {
            JObject body = new JObject();
            body["model"] = settings.CurrentModel;
            body["max_tokens"] = settings.MaxOutputTokens;
            if (!string.IsNullOrEmpty(systemPrompt))
                body["system"] = systemPrompt;

            JArray messages = new JArray();
            foreach (ChatTurn turn in history)
            {
                JArray content = new JArray();
                foreach (ContentBlock block in turn.Blocks)
                {
                    switch (block.Kind)
                    {
                        case BlockKind.Text:
                            if (string.IsNullOrEmpty(block.Text))
                                break;
                            JObject text = new JObject();
                            text["type"] = "text";
                            text["text"] = block.Text;
                            content.Add(text);
                            break;

                        case BlockKind.ToolUse:
                            JObject use = new JObject();
                            use["type"] = "tool_use";
                            use["id"] = block.ToolId;
                            use["name"] = block.ToolName;
                            use["input"] = block.ToolInput ?? new JObject();
                            content.Add(use);
                            break;

                        case BlockKind.ToolResult:
                            JObject result = new JObject();
                            result["type"] = "tool_result";
                            result["tool_use_id"] = block.ToolId;
                            result["content"] = block.ToolResult ?? "";
                            if (block.IsError)
                                result["is_error"] = true;
                            content.Add(result);
                            break;
                    }
                }
                if (content.Count == 0)
                    continue;
                JObject message = new JObject();
                message["role"] = turn.Role;
                message["content"] = content;
                messages.Add(message);
            }
            body["messages"] = messages;

            if (tools != null && tools.Count > 0)
            {
                JArray toolArray = new JArray();
                foreach (ToolDefinition tool in tools)
                {
                    JObject entry = new JObject();
                    entry["name"] = tool.Name;
                    entry["description"] = tool.Description;
                    entry["input_schema"] = tool.Parameters;
                    toolArray.Add(entry);
                }
                body["tools"] = toolArray;
            }
            return body;
        }

        public override List<string> ListModels(AiSettings settings, CancellationToken cancel)
        {
            string key = settings.CurrentKey;
            JObject response = GetJson(TrimBase(settings.CurrentBaseUrl, DefaultBase) + "/v1/models?limit=100",
                delegate (HttpRequestMessage request)
                {
                    request.Headers.TryAddWithoutValidation("x-api-key", key);
                    request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                }, settings, cancel);
            return ParseModelList(response);
        }

        public override List<string> ParseModelList(JObject response)
        {
            List<string> models = new List<string>();
            if (response["data"] is JArray entries)
                foreach (JToken entry in entries)
                {
                    string id = (string)entry["id"];
                    if (!string.IsNullOrEmpty(id))
                        models.Add(id);
                }
            return models;
        }

        public override ChatTurn ParseResponse(JObject response)
        {
            ChatTurn assistant = new ChatTurn("assistant");
            JToken contentToken = response["content"];
            if (contentToken is JArray blocks)
            {
                foreach (JToken item in blocks)
                {
                    string type = (string)item["type"];
                    if (type == "text")
                    {
                        string value = (string)item["text"];
                        if (!string.IsNullOrEmpty(value))
                            assistant.Blocks.Add(ContentBlock.MakeText(value));
                    }
                    else if (type == "tool_use")
                    {
                        assistant.Blocks.Add(ContentBlock.MakeToolUse(
                            (string)item["id"],
                            (string)item["name"],
                            item["input"] as JObject));
                    }
                }
            }

            if (assistant.Blocks.Count == 0)
            {
                string stopReason = (string)response["stop_reason"];
                assistant.Blocks.Add(ContentBlock.MakeText(stopReason == "max_tokens"
                    ? "(回應被 max_tokens 截斷,沒有產生內容。請到設定調高「單次回應上限」。)"
                    : "(模型沒有回傳任何內容。)"));
            }
            return assistant;
        }
    }
}
