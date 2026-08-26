using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using TokiAi.Providers;

namespace TokiAi
{
    /// <summary>
    /// What the engine needs from the window. Every method is called from the worker thread, so
    /// the implementation is responsible for marshalling - in particular ExecuteTool must run on
    /// the UI thread, because it touches the live WZ tree.
    /// </summary>
    public interface IAiConversationHost
    {
        void PostStatus(string text);
        void PostAssistantText(string text);
        void PostToolCall(string toolName, string argumentPreview);
        void PostToolResult(string toolName, bool isError, string preview);
        string ExecuteTool(string toolName, JObject input, out bool isError);
    }

    /// <summary>
    /// The tool-use loop: send the conversation, run whatever tools come back, send the results,
    /// repeat until the model answers with plain text or the round limit is hit.
    /// </summary>
    public class AiConversation
    {
        readonly List<ChatTurn> history = new List<ChatTurn>();

        public List<ChatTurn> History { get { return history; } }

        public void Reset()
        {
            history.Clear();
        }

        /// <summary>
        /// Rolls back an exchange that failed part-way, back to the question that started it.
        ///
        /// Tool results ride in "user" turns too, so walking back to the most recent user turn
        /// is wrong: after a failure in round three that lands on a tool-result turn, leaving the
        /// assistant tool_use before it stranded. The next request then carries a tool_use with
        /// no tool_result after it and every provider rejects it outright.
        /// </summary>
        public void RollbackFailedTurn()
        {
            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (history[i].Role != "user" || CarriesToolResults(history[i]))
                    continue;
                history.RemoveRange(i, history.Count - i);
                break;
            }
            EnsureConsistentHistory();
        }

        /// <summary>
        /// Drops anything left dangling at the end of the history - an assistant turn whose tool
        /// calls were never answered, or tool results whose call has already gone. Run before
        /// every send, so a conversation damaged by an earlier failure repairs itself instead of
        /// failing forever until the user starts a new chat.
        /// </summary>
        public int EnsureConsistentHistory()
        {
            int dropped = 0;
            while (history.Count > 0)
            {
                ChatTurn last = history[history.Count - 1];
                if (!last.HasToolUse() && !CarriesToolResults(last))
                    break;
                history.RemoveAt(history.Count - 1);
                dropped++;
            }
            return dropped;
        }

        static bool CarriesToolResults(ChatTurn turn)
        {
            foreach (ContentBlock block in turn.Blocks)
                if (block.Kind == BlockKind.ToolResult)
                    return true;
            return false;
        }

        // Rough character budget for everything the tool results contribute. The whole history is
        // re-sent on every round of a tool loop, so without a cap a handful of big reads makes
        // each request slower than the last until it trips the timeout.
        const int ToolResultBudget = 24000;
        const int KeptToolResultLength = 240;

        /// <summary>
        /// Shrinks older tool results so a long tool loop does not re-send everything it has ever
        /// read. The blocks stay in place - Claude and OpenAI both reject a tool_use with no
        /// matching result - only their text is replaced, newest kept whole.
        /// </summary>
        int TrimToolResults()
        {
            int budget = ToolResultBudget;
            int trimmed = 0;
            for (int i = history.Count - 1; i >= 0; i--)
            {
                foreach (ContentBlock block in history[i].Blocks)
                {
                    if (block.Kind != BlockKind.ToolResult || string.IsNullOrEmpty(block.ToolResult))
                        continue;
                    if (block.ToolResult.Length <= KeptToolResultLength)
                    {
                        budget -= block.ToolResult.Length;
                        continue;
                    }
                    if (budget > 0)
                    {
                        budget -= block.ToolResult.Length;
                        continue;
                    }
                    block.ToolResult = block.ToolResult.Substring(0, KeptToolResultLength)
                        + "\n…(較早的查詢結果已省略以維持對話速度。需要完整內容請重新查一次。)";
                    trimmed++;
                }
            }
            return trimmed;
        }

        public void Run(string userText, AiSettings settings, bool writesAvailable,
            IAiConversationHost host, CancellationToken cancel)
        {
            // Repair anything an earlier failure left dangling before adding to it.
            EnsureConsistentHistory();
            history.Add(ChatTurn.UserText(userText));

            List<ToolDefinition> tools = WzTools.BuildToolList(
                settings.AllowWrites && writesAvailable, settings.DiskAccess, settings.MemoryEnabled);
            ChatProvider provider = ChatProvider.Create(settings.Provider);

            // One snapshot for the whole exchange. A remember() inside this tool loop must not
            // rewrite the prompt mid-loop - the new fact lands in the next conversation instead,
            // which is also what keeps the request identical across rounds.
            AiMemory memory = settings.MemoryEnabled ? AiMemory.Load() : null;
            string systemPrompt = BuildSystemPrompt(settings, memory);

            for (int round = 0; round < settings.MaxToolRounds; round++)
            {
                cancel.ThrowIfCancellationRequested();
                int trimmed = TrimToolResults();
                host.PostStatus(round == 0
                    ? "思考中…"
                    : "思考中…(第 " + (round + 1) + " 輪"
                      + (trimmed > 0 ? ",已省略 " + trimmed + " 筆較早的查詢結果" : "") + ")");

                ChatTurn assistant = provider.Send(systemPrompt, history, tools, settings, cancel);
                history.Add(assistant);

                string text = assistant.JoinedText();
                if (!string.IsNullOrEmpty(text))
                    host.PostAssistantText(text);

                if (!assistant.HasToolUse())
                    return;

                ChatTurn results = new ChatTurn("user");
                foreach (ContentBlock block in assistant.Blocks)
                {
                    if (block.Kind != BlockKind.ToolUse)
                        continue;
                    cancel.ThrowIfCancellationRequested();

                    host.PostToolCall(block.ToolName, SummariseArguments(block.ToolInput));
                    host.PostStatus("執行 " + block.ToolName + "…");

                    bool isError;
                    string result = host.ExecuteTool(block.ToolName, block.ToolInput, out isError);
                    host.PostToolResult(block.ToolName, isError, FirstLine(result));

                    results.Blocks.Add(ContentBlock.MakeToolResult(block.ToolId, block.ToolName, result, isError));
                }
                history.Add(results);
            }

            host.PostAssistantText("(已達到工具呼叫上限 " + settings.MaxToolRounds
                + " 輪就停下來了,避免無限迴圈。可以再問一次讓它接著查,或到設定調高上限。)");
        }

        static string SummariseArguments(JObject input)
        {
            if (input == null || input.Count == 0)
                return "";
            StringBuilder builder = new StringBuilder();
            foreach (JProperty property in input.Properties())
            {
                if (builder.Length > 0)
                    builder.Append(", ");
                string value = property.Value == null ? "" : property.Value.ToString();
                if (value.Length > 80)
                    value = value.Substring(0, 80) + "…";
                builder.Append(property.Name).Append('=').Append(value);
            }
            return builder.ToString();
        }

        static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            int newline = text.IndexOf('\n');
            string line = newline < 0 ? text : text.Substring(0, newline);
            return line.Length > 120 ? line.Substring(0, 120) + "…" : line;
        }

        public static string BuildSystemPrompt(AiSettings settings)
        {
            return BuildSystemPrompt(settings, settings.MemoryEnabled ? AiMemory.Load() : null);
        }

        /// <summary>
        /// Builds the prompt against an explicit memory store. Split out so tests can pass their
        /// own store instead of whatever this machine happens to have learned.
        /// </summary>
        public static string BuildSystemPrompt(AiSettings settings, AiMemory memory)
        {
            string learned = settings.MemoryEnabled && memory != null ? memory.RenderForPrompt() : "";

            if (!string.IsNullOrWhiteSpace(settings.SystemPromptOverride))
            {
                // A custom prompt replaces the instructions, not the accumulated facts - dropping
                // them here would silently switch memory off for anyone who wrote one.
                string custom = settings.SystemPromptOverride.Trim();
                if (learned.Length == 0)
                    return custom;
                return custom + "\n\n" + MemoryInstructions() + "\n" + learned.TrimEnd();
            }

            StringBuilder prompt = new StringBuilder();
            prompt.Append("你是 HaRepacker(Shui改)內嵌的 AI 助手。這是一個 MapleStory WZ 檔案編輯器,");
            prompt.Append("你有工具可以搜尋、讀取、以及「提議」修改使用者目前開啟的 WZ 樹。\n\n");

            prompt.Append("## 資料規則\n");
            prompt.Append("1. 一律用工具讀取實際的 WZ 資料,絕對不要憑記憶編造數值、路徑或 ID。\n");
            prompt.Append("2. 工具查不到就如實說查不到,不要猜。\n");
            prompt.Append("3. 路徑一律從 WZ 檔名開始,例如 Skill.wz/112.img/skill/1120017/level/1/damage。\n\n");

            prompt.Append("## 分頁\n");
            prompt.Append("編輯器可以同時開多個分頁,每個分頁是一份獨立載入的 WZ(常常是不同版本)。\n");
            prompt.Append("路徑前面加 [N] 指定分頁,例如 [2]Skill_000.wz/112.img。不加 [N] 時會在所有分頁裡找同名檔案,\n");
            prompt.Append("找到多個會請你指定。使用者要求「把 A 搬到 B」時,通常就是分頁之間的複製 —— 用 propose_copy。\n\n");

            if (settings.DiskAccess != DiskAccessMode.Off)
            {
                prompt.Append("## 電腦上的檔案\n");
                prompt.Append("你可以用 list_folder / find_files 瀏覽電腦上的資料夾,但只看得到檔名、大小和圖片尺寸,\n");
                prompt.Append("看不到任何檔案的內容 —— 不要假裝讀得到,也不要猜檔案裡面寫什麼。\n");
                if (settings.AllowWrites)
                {
                    prompt.Append("要把整個資料夾的圖片放進 WZ 用 propose_import_images —— 它會依序試「相對路徑比對」、\n");
                    prompt.Append("「檔名比對(忽略前導零,所以 2450000.png 對得到節點 02450000)」,以及在對上的節點底下找 icon / iconRaw。\n");
                    prompt.Append("自動比對對不上時,用 propose_set_image 一張一張指定確切的節點路徑。\n");
                    prompt.Append("**絕對不要叫使用者去改檔名、搬檔案或重建資料夾結構。** 你讀得到資料夾也讀得到 WZ 樹,\n");
                    prompt.Append("對應關係由你自己判斷並用 propose_set_image 明確寫出來,讓使用者在待確認清單裡檢查就好。\n");
                }
                prompt.Append("使用者提到某個資料夾但沒給完整路徑時,先用 find_files 找,不要直接說做不到。\n\n");
            }

            prompt.Append("## 查詢流程\n");
            prompt.Append("1. 一開始先 list_files,確認有幾個分頁、各載入了什麼。\n");
            prompt.Append("2. 用 search 找名稱或數值,務必帶 root 把範圍縮到單一檔案或子樹 —— 全樹搜尋會很慢而且可能被保護上限截斷。\n");
            prompt.Append("3. 用 get_node 讀實際的型別與數值;子節點多就用 offset 翻頁。\n");
            prompt.Append("4. 省工具呼叫:先 get_node 讀父節點,一次就看得到所有子節點的名稱、型別和純量值 —— \n");
            prompt.Append("   不要一個一個子節點分開讀。每一次查詢的結果都會累積在對話裡並重送,查太多次會變慢甚至逾時。\n");
            prompt.Append("   search 只用來「找位置」;找到之後改用 get_node 讀內容。\n");
            prompt.Append("5. 跨版本比對時,兩邊都要實際讀過再下結論 —— 同一個路徑在不同版本結構可能完全不同。\n");
            prompt.Append("6. 用 Markdown 表格呈現結果,並講清楚每個數字是從哪個分頁讀到的。\n\n");

            if (settings.AllowWrites)
            {
                prompt.Append("## 修改規則(非常重要)\n");
                prompt.Append("1. 所有 propose_* 工具都「只是提議」。它們把修改放進待確認清單,使用者要自己在清單裡勾選並按套用才會真的寫進 WZ。\n");
                prompt.Append("2. 因此回報時只能說「已提議」「已加入待確認清單」,絕對不可以說「已修改」「已儲存」「已完成」。\n");
                prompt.Append("3. 提議之前一定要先用 get_node 確認目標節點存在、型別正確、目前的值是什麼。\n");
                prompt.Append("4. 提議後告訴使用者:改了哪些路徑、從什麼變成什麼、總共幾項,並提醒他們到「待確認修改」分頁檢查。\n");
                prompt.Append("5. 使用者沒有明確要求就不要提議修改。\n");
                prompt.Append("6. 移植(跨分頁複製)前一定要先做這三步:讀來源的完整結構、讀目標同位置現在有什麼、\n");
                prompt.Append("   把結構差異和會被覆蓋的東西列出來給使用者看。確認之後才用 propose_copy。\n\n");
            }
            else
            {
                prompt.Append("## 修改規則\n");
                prompt.Append("目前是唯讀模式,沒有任何修改工具。使用者要求改東西時,告訴他們到設定裡打開「允許提議修改」。\n\n");
            }

            prompt.Append("## 輸出格式\n");
            prompt.Append("- 用繁體中文(台灣用語)回覆。\n");
            prompt.Append("- 資料用 Markdown 表格。\n");
            prompt.Append("- ID、路徑、重要名稱用 **粗體**。\n");
            prompt.Append("- 簡潔,不要重複使用者已經知道的事。\n");

            if (settings.MemoryEnabled)
            {
                prompt.Append('\n').Append(MemoryInstructions());
                if (learned.Length > 0)
                    prompt.Append('\n').Append(learned.TrimEnd()).Append('\n');
            }
            return prompt.ToString();
        }

        /// <summary>
        /// How to use the memory tools. Kept separate from the fact list so a custom system
        /// prompt still gets the rules, and so the growing part of the prompt stays at the end
        /// where it disturbs the least of the request.
        /// </summary>
        static string MemoryInstructions()
        {
            StringBuilder rules = new StringBuilder();
            rules.Append("## 長期記憶\n");
            rules.Append("你有一份跨對話、跨關閉編輯器都會保留的記憶。使用者的目標是讓你「做久了就熟悉這套流程」,\n");
            rules.Append("所以每次做完事,回頭想一下這次有沒有學到以後還用得到的東西。\n");
            rules.Append("- 值得記:某個節點路徑代表什麼、這台電腦上哪個資料夾放哪個版本、一個操作的正確步驟、\n");
            rules.Append("  使用者的偏好與要求、踩過的坑。用 remember 記下來。\n");
            rules.Append("- 不值得記:這次對話才有意義的中間結果、你隨時能用 get_node 查到的具體數值、還沒確認的猜測。\n");
            rules.Append("- 記之前先看下面的清單,已經有的不要重複記;內容變了用 update_memory 更正,記錯了用 forget 刪掉。\n");
            rules.Append("- 一次對話通常只有 0~3 件事值得記,寧可少記也不要為了記而記。\n");
            rules.Append("- 使用者說「記住…」「以後都…」「不要再…」的時候,一定要 remember 下來。\n");
            rules.Append("- 記憶只是筆記,不是事實來源。跟工具實際讀到的資料衝突時,一律以工具讀到的為準。\n");
            return rules.ToString();
        }
    }
}
