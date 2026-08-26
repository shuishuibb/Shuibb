using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace TokiAi.Providers
{
    /// <summary>
    /// OpenAI Chat Completions. Unlike Claude, a turn cannot mix roles: tool results have to be
    /// split out into their own "tool" messages, and tool arguments travel as a JSON *string*.
    /// </summary>
    public class OpenAiProvider : ChatProvider
    {
        const string DefaultBase = "https://api.openai.com";

        public override ChatTurn Send(string systemPrompt, List<ChatTurn> history,
            List<ToolDefinition> tools, AiSettings settings, CancellationToken cancel)
        {
            JObject body = BuildRequestBody(systemPrompt, history, tools, settings);
            string url = TrimBase(settings.CurrentBaseUrl, DefaultBase) + "/v1/chat/completions";
            string key = settings.CurrentKey;
            JObject response = PostJson(url, body, delegate (HttpRequestMessage request)
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            }, settings, cancel);
            return ParseResponse(response);
        }

        public override JObject BuildRequestBody(string systemPrompt, List<ChatTurn> history,
            List<ToolDefinition> tools, AiSettings settings)
        {
            JObject body = new JObject();
            string model = settings.CurrentModel;
            body["model"] = model;
            body[UsesMaxCompletionTokens(model) ? "max_completion_tokens" : "max_tokens"] = settings.MaxOutputTokens;

            JArray messages = new JArray();
            if (!string.IsNullOrEmpty(systemPrompt))
            {
                JObject system = new JObject();
                system["role"] = "system";
                system["content"] = systemPrompt;
                messages.Add(system);
            }

            foreach (ChatTurn turn in history)
            {
                if (turn.Role == "assistant")
                {
                    JObject message = new JObject();
                    message["role"] = "assistant";
                    string text = turn.JoinedText();
                    JArray toolCalls = new JArray();
                    foreach (ContentBlock block in turn.Blocks)
                    {
                        if (block.Kind != BlockKind.ToolUse)
                            continue;
                        JObject call = new JObject();
                        call["id"] = block.ToolId;
                        call["type"] = "function";
                        JObject function = new JObject();
                        function["name"] = block.ToolName;
                        function["arguments"] = (block.ToolInput ?? new JObject()).ToString(Newtonsoft.Json.Formatting.None);
                        call["function"] = function;
                        toolCalls.Add(call);
                    }
                    // The API rejects an assistant message that has neither content nor tool_calls.
                    message["content"] = string.IsNullOrEmpty(text) ? (JToken)JValue.CreateNull() : text;
                    if (toolCalls.Count > 0)
                        message["tool_calls"] = toolCalls;
                    else if (string.IsNullOrEmpty(text))
                        message["content"] = "";
                    messages.Add(message);
                }
                else
                {
                    // Tool results first: OpenAI wants every tool_call answered before the next
                    // user message.
                    foreach (ContentBlock block in turn.Blocks)
                    {
                        if (block.Kind != BlockKind.ToolResult)
                            continue;
                        JObject toolMessage = new JObject();
                        toolMessage["role"] = "tool";
                        toolMessage["tool_call_id"] = block.ToolId;
                        toolMessage["content"] = block.ToolResult ?? "";
                        messages.Add(toolMessage);
                    }
                    string text = turn.JoinedText();
                    if (!string.IsNullOrEmpty(text))
                    {
                        JObject userMessage = new JObject();
                        userMessage["role"] = "user";
                        userMessage["content"] = text;
                        messages.Add(userMessage);
                    }
                }
            }
            body["messages"] = messages;

            if (tools != null && tools.Count > 0)
            {
                JArray toolArray = new JArray();
                foreach (ToolDefinition tool in tools)
                {
                    JObject function = new JObject();
                    function["name"] = tool.Name;
                    function["description"] = tool.Description;
                    function["parameters"] = tool.Parameters;
                    JObject entry = new JObject();
                    entry["type"] = "function";
                    entry["function"] = function;
                    toolArray.Add(entry);
                }
                body["tools"] = toolArray;
            }
            return body;
        }

        public override List<string> ListModels(AiSettings settings, CancellationToken cancel)
        {
            string key = settings.CurrentKey;
            JObject response = GetJson(TrimBase(settings.CurrentBaseUrl, DefaultBase) + "/v1/models",
                delegate (HttpRequestMessage request)
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
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
                    // The account's model list also carries embeddings, TTS, moderation and
                    // image models, none of which the Chat Completions endpoint accepts.
                    if (string.IsNullOrEmpty(id) || !LooksLikeChatModel(id))
                        continue;
                    models.Add(id);
                }
            models.Sort(StringComparer.OrdinalIgnoreCase);
            return models;
        }

        static bool LooksLikeChatModel(string id)
        {
            string lower = id.ToLowerInvariant();
            if (lower.Contains("embedding") || lower.Contains("whisper") || lower.Contains("tts")
                || lower.Contains("dall-e") || lower.Contains("moderation") || lower.Contains("audio")
                || lower.Contains("realtime") || lower.Contains("transcribe") || lower.Contains("image"))
                return false;
            return lower.StartsWith("gpt-") || lower.StartsWith("o1") || lower.StartsWith("o3")
                || lower.StartsWith("o4") || lower.StartsWith("chatgpt");
        }

        public override ChatTurn ParseResponse(JObject response)
        {
            ChatTurn assistant = new ChatTurn("assistant");
            JToken message2 = response.SelectToken("choices[0].message");
            if (message2 != null)
            {
                string content = (string)message2["content"];
                if (!string.IsNullOrEmpty(content))
                    assistant.Blocks.Add(ContentBlock.MakeText(content));

                if (message2["tool_calls"] is JArray calls)
                {
                    foreach (JToken call in calls)
                    {
                        string name = (string)call.SelectToken("function.name");
                        string arguments = (string)call.SelectToken("function.arguments");
                        JObject input;
                        try
                        {
                            input = string.IsNullOrWhiteSpace(arguments) ? new JObject() : JObject.Parse(arguments);
                        }
                        catch
                        {
                            // A malformed arguments string is the model's mistake, not a crash:
                            // hand it through so the tool layer can report it back as an error.
                            input = new JObject();
                            input["__invalid_arguments"] = arguments ?? "";
                        }
                        assistant.Blocks.Add(ContentBlock.MakeToolUse((string)call["id"], name, input));
                    }
                }
            }

            if (assistant.Blocks.Count == 0)
            {
                string finish = (string)response.SelectToken("choices[0].finish_reason");
                assistant.Blocks.Add(ContentBlock.MakeText(finish == "length"
                    ? "(回應被長度上限截斷,沒有產生內容。請到設定調高「單次回應上限」。)"
                    : "(模型沒有回傳任何內容。)"));
            }
            return assistant;
        }

        // The reasoning models rejected max_tokens outright; they take max_completion_tokens.
        static bool UsesMaxCompletionTokens(string model)
        {
            if (string.IsNullOrEmpty(model))
                return false;
            string lower = model.ToLowerInvariant();
            return lower.StartsWith("o1") || lower.StartsWith("o3") || lower.StartsWith("o4")
                || lower.StartsWith("gpt-5") || lower.StartsWith("gpt-4.1-nano-reasoning");
        }
    }
}
