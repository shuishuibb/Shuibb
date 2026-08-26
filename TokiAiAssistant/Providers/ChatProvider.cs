using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace TokiAi.Providers
{
    public abstract class ChatProvider
    {
        // One client for the whole process: creating an HttpClient per request exhausts sockets.
        // Timeout is Infinite here and enforced per call through a linked CancellationTokenSource,
        // so the user's Stop button and the settings timeout both work on the same path.
        static readonly HttpClient http = CreateClient();

        static HttpClient CreateClient()
        {
            HttpClientHandler handler = new HttpClientHandler();
            handler.AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate;
            HttpClient client = new HttpClient(handler);
            client.Timeout = Timeout.InfiniteTimeSpan;
            return client;
        }

        public static ChatProvider Create(AiProvider provider)
        {
            switch (provider)
            {
                case AiProvider.OpenAI: return new OpenAiProvider();
                case AiProvider.Gemini: return new GeminiProvider();
                default: return new ClaudeProvider();
            }
        }

        /// <summary>
        /// Sends the whole conversation and returns the assistant turn, which may contain text,
        /// tool calls, or both. Blocking on purpose - the caller already runs on a worker thread.
        /// </summary>
        public abstract ChatTurn Send(string systemPrompt, List<ChatTurn> history,
            List<ToolDefinition> tools, AiSettings settings, CancellationToken cancel);

        /// <summary>
        /// Translates the neutral conversation into this provider's request body. Split out from
        /// Send so the wire format - the part that cannot be exercised without a real API key -
        /// can be asserted offline.
        /// </summary>
        public abstract JObject BuildRequestBody(string systemPrompt, List<ChatTurn> history,
            List<ToolDefinition> tools, AiSettings settings);

        /// <summary>Translates this provider's response body back into a neutral assistant turn.</summary>
        public abstract ChatTurn ParseResponse(JObject response);

        /// <summary>
        /// Asks the provider which models the key can actually use. A hardcoded model list goes
        /// stale the moment a new model ships - and a stale ID is a 404 the user has to diagnose -
        /// so the settings dialog fills its dropdown from here instead.
        /// </summary>
        public abstract List<string> ListModels(AiSettings settings, CancellationToken cancel);

        /// <summary>
        /// Pulls the usable model IDs out of this provider's list response. Split from ListModels
        /// so the filtering rules - which differ per provider and are easy to get subtly wrong -
        /// can be asserted without a key.
        /// </summary>
        public abstract List<string> ParseModelList(JObject response);

        protected JObject GetJson(string url, Action<HttpRequestMessage> addHeaders,
            AiSettings settings, CancellationToken cancel)
        {
            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                linked.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
                try
                {
                    using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        if (addHeaders != null)
                            addHeaders(request);

                        using (HttpResponseMessage response = http.Send(request, HttpCompletionOption.ResponseContentRead, linked.Token))
                        {
                            string text;
                            using (System.IO.StreamReader reader = new System.IO.StreamReader(response.Content.ReadAsStream(linked.Token), Encoding.UTF8))
                                text = reader.ReadToEnd();

                            if (!response.IsSuccessStatusCode)
                                throw new ProviderException(DescribeHttpError((int)response.StatusCode, text));
                            return JObject.Parse(text);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    if (cancel.IsCancellationRequested)
                        throw;
                    throw new ProviderException("請求逾時(" + settings.TimeoutSeconds + " 秒)。");
                }
                catch (HttpRequestException networkError)
                {
                    throw new ProviderException("連線失敗:" + networkError.Message, networkError);
                }
            }
        }

        protected JObject PostJson(string url, JObject body, Action<HttpRequestMessage> addHeaders,
            AiSettings settings, CancellationToken cancel)
        {
            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                linked.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
                try
                {
                    using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        request.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), new UTF8Encoding(false), "application/json");
                        if (addHeaders != null)
                            addHeaders(request);

                        using (HttpResponseMessage response = http.Send(request, HttpCompletionOption.ResponseContentRead, linked.Token))
                        {
                            string text;
                            using (System.IO.StreamReader reader = new System.IO.StreamReader(response.Content.ReadAsStream(linked.Token), Encoding.UTF8))
                                text = reader.ReadToEnd();

                            if (!response.IsSuccessStatusCode)
                                throw new ProviderException(DescribeHttpError((int)response.StatusCode, text));

                            try
                            {
                                return JObject.Parse(text);
                            }
                            catch (Exception parseError)
                            {
                                throw new ProviderException("回應不是合法的 JSON:" + Truncate(text, 500), parseError);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    if (cancel.IsCancellationRequested)
                        throw;
                    throw new ProviderException("請求逾時(" + settings.TimeoutSeconds + " 秒)。\n"
                        + "常見原因是這輪對話已經很長 —— 每一次工具查詢的結果都會跟著重送,查越多次越慢。\n"
                        + "按「新對話」重開一輪通常就好了;也可以到設定調高逾時。");
                }
                catch (HttpRequestException networkError)
                {
                    throw new ProviderException("連線失敗:" + networkError.Message, networkError);
                }
            }
        }

        /// <summary>
        /// Turns the provider's error body into something a user can act on. The common failures
        /// (bad key, no credit, wrong model name, rate limit) all look the same otherwise.
        /// </summary>
        static string DescribeHttpError(int status, string body)
        {
            string detail = "";
            try
            {
                JObject parsed = JObject.Parse(body);
                JToken message = parsed.SelectToken("error.message") ?? parsed.SelectToken("message")
                    ?? parsed.SelectToken("error.status") ?? parsed.SelectToken("error");
                if (message != null)
                    detail = message.ToString();
            }
            catch
            {
                detail = Truncate(body, 400);
            }

            string hint;
            switch (status)
            {
                case 401:
                case 403:
                    hint = "API 金鑰無效或沒有權限。請到「設定」重新填入金鑰。";
                    break;
                case 404:
                    hint = "找不到該模型或端點。請確認「模型名稱」拼寫正確。";
                    break;
                case 429:
                    hint = "被限流或額度不足。稍後再試,或檢查帳戶餘額。";
                    break;
                case 400:
                    hint = "請求被拒絕(400)。多半是模型名稱錯誤,或這個模型不支援工具呼叫。";
                    break;
                default:
                    hint = "伺服器回應 HTTP " + status + "。";
                    break;
            }
            return string.IsNullOrEmpty(detail) ? hint : hint + "\n\n伺服器訊息:" + Truncate(detail, 600);
        }

        protected static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max)
                return text ?? "";
            return text.Substring(0, max) + "…(已截斷)";
        }

        protected static string TrimBase(string baseUrl, string fallback)
        {
            string value = string.IsNullOrWhiteSpace(baseUrl) ? fallback : baseUrl.Trim();
            while (value.EndsWith("/", StringComparison.Ordinal))
                value = value.Substring(0, value.Length - 1);
            return value;
        }
    }
}
