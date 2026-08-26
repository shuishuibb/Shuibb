using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace TokiAi.Providers
{
    /// <summary>
    /// Google Gemini generateContent. The assistant role is called "model", and function calls
    /// carry no id - a functionResponse is matched back to its call by tool name.
    /// </summary>
    public class GeminiProvider : ChatProvider
    {
        const string DefaultBase = "https://generativelanguage.googleapis.com";

        public override ChatTurn Send(string systemPrompt, List<ChatTurn> history,
            List<ToolDefinition> tools, AiSettings settings, CancellationToken cancel)
        {
            JObject body = BuildRequestBody(systemPrompt, history, tools, settings);
            string url = TrimBase(settings.CurrentBaseUrl, DefaultBase)
                + "/v1beta/models/" + Uri.EscapeDataString(settings.CurrentModel) + ":generateContent";
            string key = settings.CurrentKey;
            JObject response = PostJson(url, body, delegate (HttpRequestMessage request)
            {
                // Header form rather than ?key= so the key never lands in a proxy access log.
                request.Headers.TryAddWithoutValidation("x-goog-api-key", key);
            }, settings, cancel);
            return ParseResponse(response);
        }

        public override JObject BuildRequestBody(string systemPrompt, List<ChatTurn> history,
            List<ToolDefinition> tools, AiSettings settings)
        {
            JObject body = new JObject();

            if (!string.IsNullOrEmpty(systemPrompt))
            {
                JObject part = new JObject();
                part["text"] = systemPrompt;
                JArray parts = new JArray();
                parts.Add(part);
                JObject instruction = new JObject();
                instruction["parts"] = parts;
                body["systemInstruction"] = instruction;
            }

            JArray contents = new JArray();
            foreach (ChatTurn turn in history)
            {
                JArray parts = new JArray();
                foreach (ContentBlock block in turn.Blocks)
                {
                    switch (block.Kind)
                    {
                        case BlockKind.Text:
                            if (string.IsNullOrEmpty(block.Text))
                                break;
                            JObject text = new JObject();
                            text["text"] = block.Text;
                            parts.Add(text);
                            break;

                        case BlockKind.ToolUse:
                            JObject call = new JObject();
                            call["name"] = block.ToolName;
                            call["args"] = block.ToolInput ?? new JObject();
                            JObject callPart = new JObject();
                            callPart["functionCall"] = call;
                            parts.Add(callPart);
                            break;

                        case BlockKind.ToolResult:
                            // The response object has to be a JSON object, so the tool's text is
                            // wrapped rather than sent bare.
                            JObject payload = new JObject();
                            payload[block.IsError ? "error" : "result"] = block.ToolResult ?? "";
                            JObject functionResponse = new JObject();
                            functionResponse["name"] = block.ToolName;
                            functionResponse["response"] = payload;
                            JObject responsePart = new JObject();
                            responsePart["functionResponse"] = functionResponse;
                            parts.Add(responsePart);
                            break;
                    }
                }
                if (parts.Count == 0)
                    continue;
                JObject content = new JObject();
                content["role"] = turn.Role == "assistant" ? "model" : "user";
                content["parts"] = parts;
                contents.Add(content);
            }
            body["contents"] = contents;

            if (tools != null && tools.Count > 0)
            {
                JArray declarations = new JArray();
                foreach (ToolDefinition tool in tools)
                {
                    JObject entry = new JObject();
                    entry["name"] = tool.Name;
                    entry["description"] = tool.Description;
                    entry["parameters"] = SanitiseSchema(tool.Parameters);
                    declarations.Add(entry);
                }
                JObject toolEntry = new JObject();
                toolEntry["functionDeclarations"] = declarations;
                JArray toolArray = new JArray();
                toolArray.Add(toolEntry);
                body["tools"] = toolArray;
            }

            JObject generationConfig = new JObject();
            generationConfig["maxOutputTokens"] = settings.MaxOutputTokens;
            body["generationConfig"] = generationConfig;
            return body;
        }

        public override List<string> ListModels(AiSettings settings, CancellationToken cancel)
        {
            string key = settings.CurrentKey;
            JObject response = GetJson(TrimBase(settings.CurrentBaseUrl, DefaultBase) + "/v1beta/models?pageSize=200",
                delegate (HttpRequestMessage request)
                {
                    request.Headers.TryAddWithoutValidation("x-goog-api-key", key);
                }, settings, cancel);
            return ParseModelList(response);
        }

        public override List<string> ParseModelList(JObject response)
        {
            List<string> models = new List<string>();
            if (response["models"] is JArray entries)
                foreach (JToken entry in entries)
                {
                    // Embedding and image models appear in the same list but reject
                    // generateContent, so they are filtered out by supported method.
                    bool generates = false;
                    if (entry["supportedGenerationMethods"] is JArray methods)
                        foreach (JToken method in methods)
                            if (string.Equals((string)method, "generateContent", StringComparison.Ordinal))
                                generates = true;
                    if (!generates)
                        continue;

                    string name = (string)entry["name"];
                    if (string.IsNullOrEmpty(name))
                        continue;
                    // The API returns "models/gemini-2.0-flash"; the request path adds that prefix
                    // back itself, so it is stripped here.
                    models.Add(name.StartsWith("models/", StringComparison.Ordinal) ? name.Substring(7) : name);
                }
            models.Sort(StringComparer.OrdinalIgnoreCase);
            return models;
        }

        public override ChatTurn ParseResponse(JObject response)
        {
            ChatTurn assistant = new ChatTurn("assistant");
            if (response.SelectToken("candidates[0].content.parts") is JArray parts2)
            {
                int callIndex = 0;
                foreach (JToken part in parts2)
                {
                    JToken textToken = part["text"];
                    if (textToken != null && textToken.Type != JTokenType.Null)
                    {
                        string value = (string)textToken;
                        if (!string.IsNullOrEmpty(value))
                            assistant.Blocks.Add(ContentBlock.MakeText(value));
                        continue;
                    }
                    if (part["functionCall"] is JObject functionCall)
                    {
                        string name = (string)functionCall["name"];
                        // No id from the API; a synthetic one keeps the neutral model uniform and
                        // the name is what actually gets sent back.
                        assistant.Blocks.Add(ContentBlock.MakeToolUse(
                            name + "#" + callIndex, name, functionCall["args"] as JObject));
                        callIndex++;
                    }
                }
            }

            if (assistant.Blocks.Count == 0)
            {
                string blockReason = (string)response.SelectToken("promptFeedback.blockReason");
                string finish = (string)response.SelectToken("candidates[0].finishReason");
                if (!string.IsNullOrEmpty(blockReason))
                    assistant.Blocks.Add(ContentBlock.MakeText("(請求被 Gemini 的安全過濾擋下:" + blockReason + ")"));
                else if (finish == "MAX_TOKENS")
                    assistant.Blocks.Add(ContentBlock.MakeText("(回應被長度上限截斷,沒有產生內容。請到設定調高「單次回應上限」。)"));
                else
                    assistant.Blocks.Add(ContentBlock.MakeText("(模型沒有回傳任何內容。)"));
            }
            return assistant;
        }

        /// <summary>
        /// Gemini takes an OpenAPI 3.0 schema subset and rejects the request outright on any
        /// keyword outside it, so unsupported ones are dropped rather than passed through.
        /// </summary>
        static JToken SanitiseSchema(JToken schema)
        {
            if (schema is JArray array)
            {
                JArray copy = new JArray();
                foreach (JToken item in array)
                    copy.Add(SanitiseSchema(item));
                return copy;
            }
            if (schema is not JObject source)
                return schema == null ? null : schema.DeepClone();

            JObject result = new JObject();
            foreach (JProperty property in source.Properties())
            {
                switch (property.Name)
                {
                    case "type":
                    case "description":
                    case "enum":
                    case "required":
                    case "format":
                    case "nullable":
                        result[property.Name] = property.Value.DeepClone();
                        break;
                    case "items":
                        result["items"] = SanitiseSchema(property.Value);
                        break;
                    case "properties":
                        JObject properties = new JObject();
                        if (property.Value is JObject propertyBag)
                            foreach (JProperty child in propertyBag.Properties())
                                properties[child.Name] = SanitiseSchema(child.Value);
                        result["properties"] = properties;
                        break;
                    default:
                        // additionalProperties, $schema, default, examples: silently dropped.
                        break;
                }
            }
            return result;
        }
    }
}
