using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Text;
using HaRepacker;
using HaRepacker.GUI.Panels;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using Newtonsoft.Json.Linq;

namespace TokiAi
{
    /// <summary>A node plus the tab whose tree it lives in.</summary>
    public class NodeRef
    {
        public WzNode Node;
        public WzTab Tab;

        public NodeRef(WzNode node, WzTab tab)
        {
            Node = node;
            Tab = tab;
        }
    }

    /// <summary>
    /// The WZ side of the assistant: the tool schemas handed to the model, and the code that
    /// runs them against the editor's live tree.
    ///
    /// Reads happen immediately. Writes never do - every write tool records a PendingChange and
    /// returns "queued", and nothing touches the WZ until the user ticks it in the review list
    /// and presses apply. That is the whole safety model, so the tool descriptions say it too:
    /// a model that believes it already saved will tell the user it did.
    /// </summary>
    public class WzTools
    {
        // The tab the window was opened from. Used only to reach MainForm; every lookup goes
        // through the live tab list so all open tabs are visible, which is what makes porting
        // between two loaded versions possible.
        readonly MainPanel anchor;
        readonly PendingChangeSet changes;
        readonly AiSettings settings;

        // Guard rails. A full-tree search parses every image it walks, which on a big client is
        // millions of nodes, so every walk is bounded three ways and reports that it stopped.
        const int MaxVisitedNodes = 200000;
        const int SearchBudgetMs = 15000;
        const int MaxResultChars = 8000;

        // Each queued import holds the replaced bitmap so it can be undone, so the batch size is
        // bounded. Bigger jobs belong in the editor's own folder-import tool.
        const int MaxImportImages = 200;

        // How deep to look when matching a file name against the tree. Item icons sit at
        // <id>/info/icon, so three levels covers the real layouts without scanning a whole file.
        const int NameSearchDepth = 4;

        public WzTools(MainPanel panel, PendingChangeSet changes)
            : this(panel, changes, new AiSettings())
        {
        }

        public WzTools(MainPanel panel, PendingChangeSet changes, AiSettings settings)
        {
            this.anchor = panel;
            this.changes = changes;
            this.settings = settings ?? new AiSettings();
        }

        List<WzTab> Tabs()
        {
            return WzTabs.Enumerate(anchor);
        }

        #region schema

        public static List<ToolDefinition> BuildToolList(bool allowWrites)
        {
            return BuildToolList(allowWrites, DiskAccessMode.Off);
        }

        /// <summary>Without a memory flag, memory is off - this shape covers the WZ tools only.</summary>
        public static List<ToolDefinition> BuildToolList(bool allowWrites, DiskAccessMode diskAccess)
        {
            return BuildToolList(allowWrites, diskAccess, false);
        }

        public static List<ToolDefinition> BuildToolList(bool allowWrites, DiskAccessMode diskAccess,
            bool memoryEnabled)
        {
            List<ToolDefinition> tools = new List<ToolDefinition>();

            tools.Add(new ToolDefinition("list_files",
                "列出編輯器目前所有分頁、以及每個分頁載入的 WZ 檔案。"
                + "開始查任何資料前先用這個,尤其是要在兩個版本之間比對或移植時。",
                Schema(null, null), false));

            tools.Add(new ToolDefinition("get_node",
                "讀取一個節點:它的型別、數值、子節點數量,以及一頁子節點(含各自的型別與純量值)。"
                + "路徑用反斜線或斜線分隔,例如 Skill_000.wz/112.img/skill/1120004/level/1。"
                + "路徑前面可以加分頁編號 [N] 指定分頁,例如 [2]Skill_000.wz/112.img。"
                + "不加 [N] 時會在所有分頁裡找同名檔案,找到多個會回報請你指定。"
                + "子節點很多時用 offset 翻頁。",
                Schema(new string[] { "path" }, new object[] {
                    "path", "string", "節點路徑,從 WZ 檔名開始,可加 [N] 分頁前綴。留空或 / 代表列出所有分頁與檔案。",
                    "max_children", "integer", "這一頁最多回傳幾個子節點,預設 60,上限 300。",
                    "offset", "integer", "從第幾個子節點開始,預設 0。"
                }), false));

            tools.Add(new ToolDefinition("search",
                "在 WZ 樹裡搜尋。可以比對節點名稱、字串/數值內容,或兩者。"
                + "搜尋會強制解析途中的 .img,範圍越大越慢,務必用 root 把範圍縮到單一檔案或單一子樹。",
                Schema(new string[] { "query" }, new object[] {
                    "query", "string", "要找的文字。不分大小寫。",
                    "root", "string", "只在這個子樹底下找,例如 [1]String_000.wz 或 Skill_000.wz/112.img。"
                        + "只寫 [N] 代表整個分頁。留空 = 所有分頁的所有檔案(很慢)。",
                    "mode", "string", "name = 只比對節點名稱;value = 只比對節點的值;both = 兩者都比對(預設)。",
                    "exact", "boolean", "true = 必須完全相同,false = 包含即可(預設)。",
                    "limit", "integer", "最多回傳幾筆,預設 40,上限 200。"
                }), false));

            if (diskAccess != DiskAccessMode.Off)
            {
                tools.Add(new ToolDefinition("list_folder",
                    "列出電腦上某個資料夾裡有什麼(子資料夾、檔案名稱、大小,圖片會附上尺寸)。"
                    + "注意:你只看得到檔名和大小,看不到任何檔案的內容。",
                    Schema(new string[] { "path" }, new object[] {
                        "path", "string", "資料夾的完整路徑,例如 C:\\Users\\66\\Desktop\\修改工具\\新增資料夾。",
                        "recursive", "boolean", "true = 連子資料夾一起列出。預設 false。",
                        "limit", "integer", "最多列幾筆,預設 100,上限 500。"
                    }), false));

                tools.Add(new ToolDefinition("find_files",
                    "在某個資料夾底下遞迴找檔名符合的檔案。找素材資料夾在哪很好用。"
                    + "一樣只回傳路徑和大小,不回傳內容。",
                    Schema(new string[] { "root", "pattern" }, new object[] {
                        "root", "string", "從哪個資料夾開始找,例如 D:\\3.私服檔案。",
                        "pattern", "string", "檔名要包含的文字,或用 * 的萬用字元,例如 0245 或 *.png。",
                        "limit", "integer", "最多回傳幾筆,預設 50,上限 300。"
                    }), false));
            }

            if (!allowWrites)
            {
                // Memory is not a WZ write, so it stays available in read-only mode.
                if (memoryEnabled)
                    AddMemoryTools(tools);
                return tools;
            }

            if (diskAccess != DiskAccessMode.Off)
            {
                tools.Add(new ToolDefinition("propose_import_images",
                    "【提議】把一個資料夾裡的圖片匯入 WZ 節點底下。"
                    + "檔名(去掉副檔名)就是節點名稱,子資料夾會對應成子節點 —— 例如 "
                    + "icon\\0.png 會對應到 <目標節點>\\icon\\0。"
                    + "同名的 canvas 節點存在就換掉它的圖,不存在就新建(除非 only_replace=true)。"
                    + "只進待確認清單,不會立即生效。",
                    Schema(new string[] { "folder", "target_path" }, new object[] {
                        "folder", "string", "來源資料夾的完整路徑。",
                        "target_path", "string", "要匯入到哪個 WZ 節點底下,例如 Consume_000.wz/0245.img,可加 [N] 分頁前綴。",
                        "recursive", "boolean", "true = 連子資料夾的圖一起匯入(預設 true)。",
                        "only_replace", "boolean", "true = 只換掉已存在的圖,不新建節點。預設 false。",
                        "canvas_names", "string", "當檔名對應到的是容器節點(例如道具 02450000)而不是圖片本身時,"
                            + "要換它底下哪幾個圖片節點。逗號分隔,預設 icon,iconRaw。"
                    }), true));

                tools.Add(new ToolDefinition("propose_set_image",
                    "【提議】把「一個」圖片檔放到「一個」指定的節點上。"
                    + "當資料夾結構跟 WZ 樹對不起來時就用這個 —— 你已經讀得到兩邊,直接自己指定對應關係,"
                    + "不需要叫使用者去改檔名或搬資料夾。節點不存在會自動建立(含中間的 sub property)。"
                    + "只進待確認清單,不會立即生效。",
                    Schema(new string[] { "file", "target_path" }, new object[] {
                        "file", "string", "圖片檔的完整路徑。",
                        "target_path", "string", "要放到哪個節點,寫到 canvas 本身,"
                            + "例如 Consume_000.wz/0245.img/02450000/info/icon,可加 [N] 分頁前綴。"
                    }), true));
            }

            tools.Add(new ToolDefinition("propose_set_value",
                "【提議】修改一個節點的值。不會立即生效 —— 只會加進「待確認清單」,等使用者在清單裡勾選並按套用才真的寫入。"
                + "回報時必須說「已提議」,不可以說「已修改」或「已儲存」。",
                Schema(new string[] { "path", "value" }, new object[] {
                    "path", "string", "要修改的節點路徑。",
                    "value", "string", "新的值,用文字表示;數值型別會自動轉換,轉不過去就會回報錯誤。"
                }), true));

            tools.Add(new ToolDefinition("propose_rename",
                "【提議】把節點改名。同樣只進待確認清單,不會立即生效。",
                Schema(new string[] { "path", "new_name" }, new object[] {
                    "path", "string", "要改名的節點路徑。",
                    "new_name", "string", "新名稱。"
                }), true));

            tools.Add(new ToolDefinition("propose_delete",
                "【提議】刪除一個節點(連同它底下的所有子節點)。只進待確認清單,不會立即生效;套用之後仍可以用視窗裡的「還原本次套用」復原。",
                Schema(new string[] { "path" }, new object[] {
                    "path", "string", "要刪除的節點路徑。"
                }), true));

            tools.Add(new ToolDefinition("propose_copy",
                "【提議】把一個節點連同它底下的整棵子樹複製到另一個位置 —— 這是跨分頁移植用的工具,"
                + "來源和目標可以在不同分頁,例如從 [1] 複製到 [2]。"
                + "複製的是深層副本,兩邊之後互不影響。只進待確認清單,不會立即生效。"
                + "注意:整個 WZ 檔案本身(最上層節點)不能複製,只能複製 .img 或它底下的屬性節點。",
                Schema(new string[] { "source_path", "target_parent_path" }, new object[] {
                    "source_path", "string", "要複製的來源節點路徑,建議加 [N] 講明是哪個分頁。",
                    "target_parent_path", "string", "要複製到哪個節點底下(父節點),建議加 [N]。",
                    "new_name", "string", "複製過去之後要叫什麼名字。留空 = 沿用來源的名稱。"
                }), true));

            tools.Add(new ToolDefinition("propose_add",
                "【提議】在某個節點底下新增一個子節點。只進待確認清單,不會立即生效。",
                Schema(new string[] { "parent_path", "name", "type" }, new object[] {
                    "parent_path", "string", "要新增在哪個節點底下。",
                    "name", "string", "新節點的名稱。",
                    "type", "string", "節點型別:string、int、long、short、float、double、uol、vector、sub(SubProperty)、null。",
                    "value", "string", "初始值。sub 和 null 不需要;vector 用 \"x,y\" 格式。"
                }), true));

            if (memoryEnabled)
                AddMemoryTools(tools);

            return tools;
        }

        /// <summary>
        /// The memory tools. Separated out because they are not WZ tools: they never touch the
        /// tree, they work in read-only mode too, and they are the only tools whose effect
        /// outlives the conversation.
        /// </summary>
        static void AddMemoryTools(List<ToolDefinition> tools)
        {
            tools.Add(new ToolDefinition("remember",
                "把一件「之後的對話還會再用到」的事實寫進長期記憶。這份記憶跨對話、跨關閉編輯器都會留著,"
                + "每次開新對話都會自動出現在你的系統提示裡。"
                + "值得記的例子:某個節點路徑代表什麼、這台電腦上哪個資料夾放什麼版本、"
                + "某個操作的正確步驟、使用者的偏好、踩過的坑。"
                + "不要記:這次對話才有意義的中間結果、你隨時可以用 get_node 查到的具體數值、"
                + "已經記過的事(先看系統提示裡的清單)。"
                + "一次對話通常只有 0~3 件事值得記。",
                Schema(new string[] { "category", "text" }, new object[] {
                    "category", "string", "分類,只能是這五個之一:節點、流程、環境、偏好、坑。",
                    "text", "string", "一句話講完,要具體到以後單獨看也看得懂 —— "
                        + "寫「技術谷4.0 的 String 要同時改 Lang\\zh_TW 和 Lang\\zh_CN」,"
                        + "不要寫「String 要改兩個地方」。最多 400 字。"
                }), false));

            tools.Add(new ToolDefinition("update_memory",
                "更正一筆已經記下來的事實。發現記憶跟實際讀到的資料衝突時用這個,不要新增一筆相反的。",
                Schema(new string[] { "id", "text" }, new object[] {
                    "id", "integer", "要更正哪一筆,就是系統提示裡 #N 的那個數字。",
                    "text", "string", "更正後的完整內容(整筆取代,不是附加)。",
                    "category", "string", "要一併換分類時才填。"
                }), false));

            tools.Add(new ToolDefinition("forget",
                "刪掉一筆已經沒用或記錯的記憶。記憶接近上限時也用這個清掉過時的。",
                Schema(new string[] { "id" }, new object[] {
                    "id", "integer", "要刪哪一筆,就是系統提示裡 #N 的那個數字。",
                    "reason", "string", "為什麼要刪,會顯示給使用者看。"
                }), false));
        }

        /// <summary>
        /// Builds a JSON Schema object from a flat name/type/description triple list. Written this
        /// way so the tool table above reads as a table instead of forty lines of JObject setup.
        /// </summary>
        static JObject Schema(string[] required, object[] propertyTriples)
        {
            JObject properties = new JObject();
            if (propertyTriples != null)
            {
                for (int i = 0; i + 2 < propertyTriples.Length; i += 3)
                {
                    JObject property = new JObject();
                    property["type"] = (string)propertyTriples[i + 1];
                    property["description"] = (string)propertyTriples[i + 2];
                    properties[(string)propertyTriples[i]] = property;
                }
            }
            JObject schema = new JObject();
            schema["type"] = "object";
            schema["properties"] = properties;
            JArray requiredArray = new JArray();
            if (required != null)
                foreach (string name in required)
                    requiredArray.Add(name);
            schema["required"] = requiredArray;
            return schema;
        }

        #endregion

        #region dispatch

        public string Execute(string toolName, JObject input, out bool isError)
        {
            isError = false;
            try
            {
                if (input != null && input["__invalid_arguments"] != null)
                {
                    isError = true;
                    return "工具參數不是合法的 JSON,請重新呼叫一次。";
                }
                switch (toolName)
                {
                    case "list_files": return Clip(ListFiles());
                    case "get_node": return Clip(GetNode(input));
                    case "search": return Clip(Search(input));
                    case "list_folder": return Clip(ListFolder(input));
                    case "find_files": return Clip(FindFiles(input));
                    case "propose_import_images": return Clip(ProposeImportImages(input));
                    case "propose_set_image": return Clip(ProposeSetImage(input));
                    case "propose_copy": return Clip(ProposeCopy(input));
                    case "propose_set_value": return Clip(ProposeSetValue(input));
                    case "propose_rename": return Clip(ProposeRename(input));
                    case "propose_delete": return Clip(ProposeDelete(input));
                    case "propose_add": return Clip(ProposeAdd(input));
                    case "remember": return Clip(Remember(input));
                    case "update_memory": return Clip(UpdateMemory(input));
                    case "forget": return Clip(Forget(input));
                    default:
                        isError = true;
                        return "沒有這個工具:" + toolName;
                }
            }
            catch (ToolInputException expected)
            {
                isError = true;
                return expected.Message;
            }
            catch (Exception unexpected)
            {
                isError = true;
                return "工具執行失敗:" + unexpected.Message;
            }
        }

        static string Clip(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= MaxResultChars)
                return text ?? "";
            return text.Substring(0, MaxResultChars)
                + "\n…(結果過長已截斷,共 " + text.Length + " 字。請縮小查詢範圍或用 offset 翻頁。)";
        }

        #endregion

        #region read tools

        string ListFiles()
        {
            List<WzTab> tabs = Tabs();
            StringBuilder builder = new StringBuilder();
            builder.Append("編輯器目前開了 ").Append(tabs.Count).Append(" 個分頁:\n");

            int total = 0;
            foreach (WzTab tab in tabs)
            {
                builder.Append("\n[").Append(tab.Number).Append("] ").Append(tab.Name).Append('\n');
                int count = 0;
                foreach (object item in tab.Panel.DataTree.Nodes)
                {
                    if (item is not WzNode node)
                        continue;
                    count++;
                    total++;
                    builder.Append("    ").Append(node.Text)
                           .Append("  (型別 ").Append(node.Tag == null ? "?" : node.Tag.GetType().Name)
                           .Append(", 子節點 ").Append(node.Nodes.Count).Append(")\n");
                }
                if (count == 0)
                    builder.Append("    (這個分頁沒有載入檔案)\n");
            }

            if (total == 0)
                return "所有分頁都沒有載入 WZ 檔案。請先在編輯器裡開啟 WZ 檔。";

            if (tabs.Count > 1)
                builder.Append("\n路徑要指定分頁時在前面加 [N],例如 [2]Skill_000.wz/112.img。\n");
            return builder.ToString();
        }

        string GetNode(JObject input)
        {
            string path = ReadString(input, "path", "");
            if (string.IsNullOrWhiteSpace(path) || path == "/" || path == "\\")
                return ListFiles();

            NodeRef reference = Resolve(path);
            WzNode node = reference.Node;
            int max = Clamp(ReadInt(input, "max_children", 60), 1, 300);
            int offset = Math.Max(0, ReadInt(input, "offset", 0));

            EnsureParsed(node);

            StringBuilder builder = new StringBuilder();
            builder.Append("路徑: ").Append(Describe(reference)).Append('\n');
            if (Tabs().Count > 1)
                builder.Append("分頁: [").Append(reference.Tab.Number).Append("] ").Append(reference.Tab.Name).Append('\n');
            builder.Append("型別: ").Append(node.Tag == null ? "?" : node.Tag.GetType().Name).Append('\n');
            string value = DescribeValue(node.Tag as WzObject);
            if (value != null)
                builder.Append("值: ").Append(value).Append('\n');
            builder.Append("子節點總數: ").Append(node.Nodes.Count).Append('\n');

            if (node.Nodes.Count == 0)
                return builder.ToString();

            int end = Math.Min(node.Nodes.Count, offset + max);
            builder.Append("子節點 [").Append(offset).Append('-').Append(end - 1).Append("]:\n");
            for (int i = offset; i < end; i++)
            {
                if (node.Nodes[i] is not WzNode child)
                    continue;
                builder.Append("  ").Append(child.Text);
                WzObject childObject = child.Tag as WzObject;
                builder.Append("  <").Append(childObject == null ? "?" : childObject.GetType().Name).Append('>');
                string childValue = DescribeValue(childObject);
                if (childValue != null)
                    builder.Append(" = ").Append(childValue);
                else if (child.Nodes.Count > 0)
                    builder.Append(" (").Append(child.Nodes.Count).Append(" 個子節點)");
                builder.Append('\n');
            }
            if (end < node.Nodes.Count)
                builder.Append("…還有 ").Append(node.Nodes.Count - end).Append(" 個,用 offset=").Append(end).Append(" 繼續讀。\n");
            return builder.ToString();
        }

        string Search(JObject input)
        {
            string query = ReadString(input, "query", "");
            if (string.IsNullOrWhiteSpace(query))
                throw new ToolInputException("search 需要 query。");

            string rootPath = ReadString(input, "root", "");
            string mode = ReadString(input, "mode", "both").ToLowerInvariant();
            bool matchName = mode != "value";
            bool matchValue = mode != "name";
            bool exact = ReadBool(input, "exact", false);
            int limit = Clamp(ReadInt(input, "limit", 40), 1, 200);

            List<NodeRef> roots = new List<NodeRef>();
            if (string.IsNullOrWhiteSpace(rootPath) || rootPath == "/" || rootPath == "\\")
            {
                // No root given: every file in every open tab.
                foreach (WzTab tab in Tabs())
                    foreach (object item in tab.Panel.DataTree.Nodes)
                        if (item is WzNode node)
                            roots.Add(new NodeRef(node, tab));
            }
            else
            {
                int wholeTab;
                string remainder = StripTabPrefix(rootPath, out wholeTab);
                if (wholeTab > 0 && SplitPath(remainder).Length == 0)
                {
                    // "[2]" alone scopes the search to that entire tab.
                    foreach (WzTab tab in Tabs())
                        if (tab.Number == wholeTab)
                            foreach (object item in tab.Panel.DataTree.Nodes)
                                if (item is WzNode node)
                                    roots.Add(new NodeRef(node, tab));
                    if (roots.Count == 0)
                        throw new ToolInputException("分頁 [" + wholeTab + "] 不存在或沒有載入檔案。");
                }
                else
                {
                    roots.Add(Resolve(rootPath));
                }
            }
            if (roots.Count == 0)
                return "目前沒有載入任何 WZ 檔案。";

            List<string> hits = new List<string>();
            Stopwatch clock = Stopwatch.StartNew();
            int visited = 0;
            bool stoppedEarly = false;

            bool multiTab = Tabs().Count > 1;
            foreach (NodeRef root in roots)
            {
                string tabPrefix = multiTab && root.Tab != null ? "[" + root.Tab.Number + "]" : "";
                if (SearchWalk(root.Node, query, matchName, matchValue, exact, limit, hits, clock, tabPrefix, ref visited))
                    continue;
                stoppedEarly = true;
                break;
            }

            StringBuilder builder = new StringBuilder();
            builder.Append("搜尋 \"").Append(query).Append("\"(mode=").Append(mode)
                   .Append(exact ? ", 完全相符" : ", 包含").Append("),找到 ").Append(hits.Count).Append(" 筆");
            if (hits.Count >= limit)
                builder.Append("(已達上限 ").Append(limit).Append(",可能還有更多)");
            builder.Append(",掃過 ").Append(visited).Append(" 個節點。\n");
            if (stoppedEarly)
                builder.Append("注意:搜尋在達到上限前就停了(時間或節點數超過保護上限),結果不完整。請用 root 縮小範圍。\n");
            foreach (string hit in hits)
                builder.Append(hit).Append('\n');
            if (hits.Count == 0)
                builder.Append("(沒有相符的結果)\n");
            return builder.ToString();
        }

        /// <summary>
        /// Depth-first walk. Returns false when a guard rail tripped, so the caller can say the
        /// results are partial rather than quietly reporting "not found".
        /// </summary>
        bool SearchWalk(WzNode node, string query, bool matchName, bool matchValue, bool exact,
            int limit, List<string> hits, Stopwatch clock, string tabPrefix, ref int visited)
        {
            if (hits.Count >= limit)
                return true;
            if (visited >= MaxVisitedNodes || clock.ElapsedMilliseconds > SearchBudgetMs)
                return false;

            visited++;

            WzObject wzObject = node.Tag as WzObject;
            bool nameHit = matchName && Matches(node.Text, query, exact);
            string value = matchValue ? RawValue(wzObject) : null;
            bool valueHit = value != null && Matches(value, query, exact);

            if (nameHit || valueHit)
            {
                StringBuilder line = new StringBuilder();
                line.Append("  ").Append(tabPrefix).Append(NodePath(node));
                line.Append("  <").Append(wzObject == null ? "?" : wzObject.GetType().Name).Append('>');
                string described = DescribeValue(wzObject);
                if (described != null)
                    line.Append(" = ").Append(described);
                hits.Add(line.ToString());
                if (hits.Count >= limit)
                    return true;
            }

            EnsureParsed(node);
            foreach (object item in node.Nodes)
            {
                if (item is not WzNode child)
                    continue;
                if (!SearchWalk(child, query, matchName, matchValue, exact, limit, hits, clock, tabPrefix, ref visited))
                    return false;
                if (hits.Count >= limit)
                    return true;
            }
            return true;
        }

        static bool Matches(string candidate, string query, bool exact)
        {
            if (candidate == null)
                return false;
            return exact
                ? string.Equals(candidate, query, StringComparison.OrdinalIgnoreCase)
                : candidate.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        #endregion

        #region disk tools

        string RequireAllowedFolder(JObject input, string field)
        {
            string requested = RequireString(input, field);
            string full;
            string refusal = DiskAccess.CheckAllowed(settings, requested, out full);
            if (refusal != null)
                throw new ToolInputException(refusal);
            if (!Directory.Exists(full))
                throw new ToolInputException("找不到這個資料夾:" + full);
            return full;
        }

        string ListFolder(JObject input)
        {
            string folder = RequireAllowedFolder(input, "path");
            bool recursive = ReadBool(input, "recursive", false);
            int limit = Clamp(ReadInt(input, "limit", 100), 1, 500);

            StringBuilder builder = new StringBuilder();
            builder.Append("資料夾: ").Append(folder).Append('\n');

            try
            {
                string[] directories = Directory.GetDirectories(folder);
                Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
                builder.Append("子資料夾 (").Append(directories.Length).Append("):\n");
                int shown = 0;
                foreach (string directory in directories)
                {
                    if (shown++ >= limit) { builder.Append("  …還有更多\n"); break; }
                    builder.Append("  [DIR] ").Append(Path.GetFileName(directory)).Append('\n');
                }
            }
            catch (Exception error)
            {
                builder.Append("  (無法列出子資料夾:").Append(error.Message).Append(")\n");
            }

            if (recursive)
            {
                List<KeyValuePair<string, string>> images = DiskAccess.CollectImages(folder, true, limit);
                builder.Append("圖片檔(含子資料夾,").Append(images.Count).Append("):\n");
                foreach (KeyValuePair<string, string> image in images)
                    builder.Append("  ").Append(image.Key)
                           .Append("  ").Append(DiskAccess.DescribeImage(image.Value)).Append('\n');
                return builder.ToString();
            }

            try
            {
                string[] files = Directory.GetFiles(folder);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                builder.Append("檔案 (").Append(files.Length).Append("):\n");
                int shown = 0;
                foreach (string file in files)
                {
                    if (shown++ >= limit) { builder.Append("  …還有更多,用 limit 調整\n"); break; }
                    FileInfo info = new FileInfo(file);
                    builder.Append("  ").Append(info.Name).Append("  ").Append(DiskAccess.HumanSize(info.Length));
                    if (DiskAccess.IsImageFile(file))
                        builder.Append("  ").Append(DiskAccess.DescribeImage(file));
                    builder.Append('\n');
                }
            }
            catch (Exception error)
            {
                builder.Append("  (無法列出檔案:").Append(error.Message).Append(")\n");
            }
            return builder.ToString();
        }

        string FindFiles(JObject input)
        {
            string root = RequireAllowedFolder(input, "root");
            string pattern = RequireString(input, "pattern").Trim();
            int limit = Clamp(ReadInt(input, "limit", 50), 1, 300);

            // A bare word means "name contains this"; anything with a wildcard is used as-is.
            string searchPattern = pattern.IndexOf('*') >= 0 || pattern.IndexOf('?') >= 0
                ? pattern
                : "*" + pattern + "*";

            List<string> hits = new List<string>();
            Stopwatch clock = Stopwatch.StartNew();
            FindWalk(root, searchPattern, limit, hits, clock);

            StringBuilder builder = new StringBuilder();
            builder.Append("在 ").Append(root).Append(" 底下找 \"").Append(pattern)
                   .Append("\",找到 ").Append(hits.Count).Append(" 筆");
            if (hits.Count >= limit)
                builder.Append("(已達上限)");
            if (clock.ElapsedMilliseconds > SearchBudgetMs)
                builder.Append(",搜尋逾時提前停止,結果不完整");
            builder.Append("。\n");
            foreach (string hit in hits)
                builder.Append("  ").Append(hit).Append('\n');
            if (hits.Count == 0)
                builder.Append("(沒有符合的檔案或資料夾)\n");
            return builder.ToString();
        }

        static void FindWalk(string folder, string pattern, int limit, List<string> hits, Stopwatch clock)
        {
            if (hits.Count >= limit || clock.ElapsedMilliseconds > SearchBudgetMs)
                return;
            try
            {
                foreach (string file in Directory.GetFiles(folder, pattern))
                {
                    if (hits.Count >= limit) return;
                    FileInfo info = new FileInfo(file);
                    hits.Add(file + "  " + DiskAccess.HumanSize(info.Length));
                }
                foreach (string directory in Directory.GetDirectories(folder, pattern))
                {
                    if (hits.Count >= limit) return;
                    hits.Add(directory + "  [DIR]");
                }
                foreach (string directory in Directory.GetDirectories(folder))
                    FindWalk(directory, pattern, limit, hits, clock);
            }
            catch
            {
                // Unreadable folder (permissions, a locked system directory): skip it.
            }
        }

        string ProposeImportImages(JObject input)
        {
            string folder = RequireAllowedFolder(input, "folder");
            string targetPath = RequireString(input, "target_path");
            bool recursive = ReadBool(input, "recursive", true);
            bool onlyReplace = ReadBool(input, "only_replace", false);

            NodeRef target = Resolve(targetPath);
            EnsureParsed(target.Node);
            if (!target.Node.CanHaveChilds)
                throw new ToolInputException("目標節點不能有子節點(型別 "
                    + (target.Node.Tag == null ? "?" : target.Node.Tag.GetType().Name) + ")。");

            List<KeyValuePair<string, string>> images = DiskAccess.CollectImages(folder, recursive, MaxImportImages + 1);
            if (images.Count == 0)
                throw new ToolInputException("這個資料夾裡沒有支援的圖片檔("
                    + string.Join("、", DiskAccess.ImageExtensions) + ")。");
            if (images.Count > MaxImportImages)
                throw new ToolInputException("一次最多匯入 " + MaxImportImages
                    + " 張圖,這個資料夾超過了。請分批,或指定更深的子資料夾。");

            string[] canvasNames = SplitCanvasNames(ReadString(input, "canvas_names", "icon,iconRaw"));

            int replacing = 0;
            int creating = 0;
            StringBuilder preview = new StringBuilder();
            List<PendingChange> queued = new List<PendingChange>();
            List<string> misses = new List<string>();

            foreach (KeyValuePair<string, string> image in images)
            {
                string[] parts = SplitPath(StripExtension(image.Key));
                if (parts.Length == 0)
                    continue;

                // 1. The folder mirrors the tree - the layout an export produces.
                WzNode existing = FindByParts(target.Node, parts);

                // 2. It does not, so go looking for the name inside the subtree instead. This is
                //    what makes a flat folder of "2450000.png" land on 02450000's icon canvases.
                if (existing == null || existing.Tag is not WzCanvasProperty)
                {
                    List<WzNode> canvases = FindCanvasesByName(target.Node,
                        parts[parts.Length - 1], canvasNames, NameSearchDepth);
                    if (canvases.Count > 0)
                    {
                        foreach (WzNode canvas in canvases)
                        {
                            string[] canvasParts = PathFrom(target.Node, canvas);
                            if (canvasParts == null)
                                continue;
                            replacing++;
                            PendingChange matched = new PendingChange(PendingChangeKind.ImportImage,
                                Describe(target) + "\\" + string.Join("\\", canvasParts), target.Node, target.Tab);
                            matched.OldValue = "(換圖,保留 " + DescribeCanvasFormat(canvas) + ")";
                            matched.NewValue = image.Key + "  " + DiskAccess.DescribeImage(image.Value);
                            matched.ImportFile = image.Value;
                            matched.ImportParts = canvasParts;
                            matched.ImportReplaces = true;
                            queued.Add(matched);
                            if (preview.Length < 1500)
                                preview.Append("  換圖 ").Append(string.Join("\\", canvasParts))
                                       .Append("  ← ").Append(image.Key)
                                       .Append("  [保留 ").Append(DescribeCanvasFormat(canvas)).Append("]\n");
                        }
                        continue;
                    }
                }

                bool replaces = existing != null;
                if (!replaces && onlyReplace)
                {
                    misses.Add(image.Key);
                    continue;
                }
                if (replaces && existing.Tag is not WzCanvasProperty)
                {
                    preview.Append("  ⚠ 跳過 ").Append(image.Key)
                           .Append(" —— 目標同名節點不是圖片(")
                           .Append(existing.Tag == null ? "?" : existing.Tag.GetType().Name).Append(")\n");
                    continue;
                }

                if (replaces) replacing++; else creating++;

                PendingChange change = new PendingChange(PendingChangeKind.ImportImage,
                    Describe(target) + "\\" + string.Join("\\", parts), target.Node, target.Tab);
                change.OldValue = replaces ? "(換掉現有圖片)" : "(新建)";
                change.NewValue = image.Key + "  " + DiskAccess.DescribeImage(image.Value);
                change.ImportFile = image.Value;
                change.ImportParts = parts;
                change.ImportReplaces = replaces;
                queued.Add(change);

                if (preview.Length < 1500)
                    preview.Append("  ").Append(replaces ? "換圖 " : "新建 ")
                           .Append(string.Join("\\", parts)).Append("  ← ").Append(image.Key).Append('\n');
            }

            if (queued.Count == 0)
            {
                // Say what was actually tried and show real node names, so the model can correct
                // itself instead of bouncing the problem back to the user.
                StringBuilder why = new StringBuilder();
                why.Append("找不到任何對得上的圖片節點。已經試過:相對路徑比對、檔名比對(忽略前導零)、");
                why.Append("以及在符合的節點底下找 ").Append(string.Join("/", canvasNames)).Append("。\n");
                why.Append(Describe(target)).Append(" 底下的節點是:\n");
                int shown = 0;
                foreach (object item in target.Node.Nodes)
                {
                    if (item is not WzNode child) continue;
                    if (shown++ >= 15) { why.Append("  …\n"); break; }
                    why.Append("  ").Append(child.Text).Append("  <")
                       .Append(child.Tag == null ? "?" : child.Tag.GetType().Name).Append(">\n");
                }
                why.Append("資料夾裡的檔案:").Append(string.Join("、", misses.Count > 0 ? misses : new List<string>())).Append('\n');
                why.Append("如果自動比對對不上,改用 propose_set_image 一張一張指定確切的節點路徑。");
                throw new ToolInputException(why.ToString());
            }

            foreach (PendingChange change in queued)
                changes.Add(change);

            StringBuilder report = new StringBuilder();
            report.Append("已提議從 ").Append(folder).Append(" 匯入 ").Append(queued.Count).Append(" 張圖到 ")
                  .Append(Describe(target)).Append('\n');
            report.Append("  換掉現有圖片:").Append(replacing).Append(" 張,新建節點:").Append(creating).Append(" 張\n");
            report.Append(preview);
            if (queued.Count > 25)
                report.Append("  …(清單很長,完整內容請在「待確認修改」分頁看)\n");
            report.Append("(尚未套用。待確認清單目前有 ").Append(changes.Count).Append(" 項。)");
            return report.ToString();
        }

        /// <summary>
        /// The surface format a canvas is stored in. Replacements keep it, because the game reads
        /// the canvas with the format it expects - changing it draws garbage in-game even though
        /// the editor previews it correctly.
        /// </summary>
        static string DescribeCanvasFormat(WzNode node)
        {
            if (node == null || node.Tag is not WzCanvasProperty canvas || canvas.PngProperty == null)
                return "?";
            return CanvasWriter.Describe(canvas.PngProperty.Format);
        }

        static string[] SplitCanvasNames(string value)
        {
            List<string> names = new List<string>();
            foreach (string part in (value ?? "").Split(','))
                if (!string.IsNullOrWhiteSpace(part))
                    names.Add(part.Trim());
            if (names.Count == 0)
            {
                names.Add("icon");
                names.Add("iconRaw");
            }
            return names.ToArray();
        }

        /// <summary>
        /// One image file onto one exact node. The escape hatch for when the folder layout does
        /// not line up with the tree at all - the model can read both sides and state the mapping
        /// itself instead of asking the user to rename files.
        /// </summary>
        string ProposeSetImage(JObject input)
        {
            string requested = RequireString(input, "file");
            string full;
            string refusal = DiskAccess.CheckAllowed(settings, requested, out full);
            if (refusal != null)
                throw new ToolInputException(refusal);
            if (!File.Exists(full))
                throw new ToolInputException("找不到這個檔案:" + full);
            if (!DiskAccess.IsImageFile(full))
                throw new ToolInputException("這不是支援的圖片格式(" + string.Join("、", DiskAccess.ImageExtensions) + ")。");

            string targetPath = RequireString(input, "target_path");
            NodeRef anchor;
            string[] remaining;
            ResolvePartial(targetPath, out anchor, out remaining);

            if (remaining.Length == 0)
            {
                // The node already exists - it has to be a canvas to receive a bitmap.
                if (anchor.Node.Tag is not WzCanvasProperty)
                    throw new ToolInputException("目標節點不是圖片節點(型別 "
                        + (anchor.Node.Tag == null ? "?" : anchor.Node.Tag.GetType().Name)
                        + ")。請指到 canvas 節點,例如 …\\info\\icon。");

                WzNode parent = anchor.Node.Parent as WzNode;
                if (parent == null)
                    throw new ToolInputException("這個節點沒有父節點,無法處理。");
                remaining = new string[] { anchor.Node.Text };
                anchor = new NodeRef(parent, anchor.Tab);
            }
            else
            {
                EnsureParsed(anchor.Node);
                if (!anchor.Node.CanHaveChilds)
                    throw new ToolInputException(Describe(anchor) + " 不能有子節點,無法在底下建立圖片。");
            }

            bool replaces = remaining.Length == 1
                && WzNode.GetChildNode(anchor.Node, remaining[0]) != null;

            PendingChange change = new PendingChange(PendingChangeKind.ImportImage,
                Describe(anchor) + "\\" + string.Join("\\", remaining), anchor.Node, anchor.Tab);
            change.OldValue = replaces ? "(換掉現有圖片)" : "(新建)";
            change.NewValue = Path.GetFileName(full) + "  " + DiskAccess.DescribeImage(full);
            change.ImportFile = full;
            change.ImportParts = remaining;
            change.ImportReplaces = replaces;
            changes.Add(change);

            return "已提議" + (replaces ? "換圖" : "新建圖片") + ":\n  "
                + Describe(anchor) + "\\" + string.Join("\\", remaining)
                + "  ← " + full + "  " + DiskAccess.DescribeImage(full)
                + "\n(尚未套用。待確認清單目前有 " + changes.Count + " 項。)";
        }

        /// <summary>
        /// Resolves as much of a path as exists, handing back the deepest node found and the
        /// parts that would still have to be created underneath it.
        /// </summary>
        void ResolvePartial(string path, out NodeRef anchor, out string[] remaining)
        {
            int tabNumber;
            string withoutTab = StripTabPrefix(path, out tabNumber);
            string[] parts = SplitPath(withoutTab);
            if (parts.Length == 0)
                throw new ToolInputException("目標路徑不能是空的。");

            string prefix = tabNumber > 0 ? "[" + tabNumber + "]" : "";
            anchor = Resolve(prefix + parts[0]);

            int index = 1;
            for (; index < parts.Length; index++)
            {
                EnsureParsed(anchor.Node);
                WzNode next = FindChild(anchor.Node, parts[index]);
                if (next == null)
                    break;
                anchor = new NodeRef(next, anchor.Tab);
            }

            List<string> rest = new List<string>();
            for (int i = index; i < parts.Length; i++)
                rest.Add(parts[i]);
            remaining = rest.ToArray();
        }

        static string StripExtension(string relativePath)
        {
            int dot = relativePath.LastIndexOf('.');
            int separator = Math.Max(relativePath.LastIndexOf('\\'), relativePath.LastIndexOf('/'));
            return dot > separator ? relativePath.Substring(0, dot) : relativePath;
        }

        /// <summary>
        /// Walks a relative name path under a node. Falls back to matching just the file name, so
        /// a flat folder that was not produced by an export still lands on the right canvas.
        /// </summary>
        static WzNode FindByParts(WzNode root, string[] parts)
        {
            WzNode current = root;
            foreach (string part in parts)
            {
                EnsureParsed(current);
                WzNode next = FindChild(current, part);
                if (next == null)
                {
                    if (parts.Length > 1)
                    {
                        EnsureParsed(root);
                        return FindChild(root, parts[parts.Length - 1]);
                    }
                    return null;
                }
                current = next;
            }
            return current;
        }

        /// <summary>
        /// Two node/file names that mean the same thing. WZ item ids are zero-padded to a fixed
        /// width but the exported png rarely is, so "2450000.png" has to find node "02450000".
        /// </summary>
        public static bool NamesMatch(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!IsAllDigits(a) || !IsAllDigits(b))
                return false;
            return string.Equals(a.TrimStart('0'), b.TrimStart('0'), StringComparison.Ordinal);
        }

        static bool IsAllDigits(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            foreach (char character in text)
                if (character < '0' || character > '9')
                    return false;
            return true;
        }

        /// <summary>
        /// Finds the canvases a single image file should land on when the folder layout does not
        /// mirror the WZ tree - the usual case for a flat folder of exported icons.
        ///
        /// Looks through the target subtree for a node matching the file name (leading zeros
        /// ignored). If that node is itself a canvas it is the target; if it is a container, the
        /// canvases named in canvasNames beneath it are, which is what turns one 2450000.png into
        /// both 02450000\info\icon and 02450000\info\iconRaw.
        /// </summary>
        static List<WzNode> FindCanvasesByName(WzNode root, string name, string[] canvasNames, int depthLimit)
        {
            List<WzNode> found = new List<WzNode>();
            WzNode match = FindNodeNamed(root, name, depthLimit);
            if (match == null)
                return found;

            if (match.Tag is WzCanvasProperty)
            {
                found.Add(match);
                return found;
            }
            foreach (string canvasName in canvasNames)
            {
                WzNode canvas = FindNodeNamed(match, canvasName, depthLimit);
                if (canvas != null && canvas.Tag is WzCanvasProperty && !found.Contains(canvas))
                    found.Add(canvas);
            }
            return found;
        }

        /// <summary>Breadth-first so the shallowest match wins, which is the least surprising one.</summary>
        static WzNode FindNodeNamed(WzNode root, string name, int depthLimit)
        {
            List<WzNode> level = new List<WzNode>();
            level.Add(root);
            for (int depth = 0; depth <= depthLimit && level.Count > 0; depth++)
            {
                List<WzNode> next = new List<WzNode>();
                foreach (WzNode node in level)
                {
                    EnsureParsed(node);
                    foreach (object item in node.Nodes)
                    {
                        if (item is not WzNode child)
                            continue;
                        if (NamesMatch(child.Text, name))
                            return child;
                        next.Add(child);
                    }
                }
                level = next;
            }
            return null;
        }

        /// <summary>The name path from an ancestor down to a descendant, as ImportParts wants it.</summary>
        static string[] PathFrom(WzNode ancestor, WzNode descendant)
        {
            List<string> parts = new List<string>();
            System.Windows.Forms.TreeNode current = descendant;
            while (current != null && !ReferenceEquals(current, ancestor))
            {
                parts.Add(current.Text);
                current = current.Parent;
            }
            if (current == null)
                return null;
            parts.Reverse();
            return parts.ToArray();
        }

        #endregion

        #region write tools (proposals only)

        string ProposeSetValue(JObject input)
        {
            string path = RequireString(input, "path");
            string value = RequireString(input, "value");
            NodeRef reference = Resolve(path);
            WzNode node = reference.Node;

            string current = RawValue(node.Tag as WzObject);
            if (current == null)
                throw new ToolInputException("這個節點沒有可直接修改的純量值(型別 "
                    + (node.Tag == null ? "?" : node.Tag.GetType().Name) + ")。可修改的是 string/int/long/short/float/double/uol。");

            // Validate now rather than at apply time: the model should get told immediately that
            // "很快" is not an int, while it can still fix the call.
            string parseError;
            if (!WzValueWriter.CanApply(node.Tag as WzObject, value, out parseError))
                throw new ToolInputException(parseError);

            changes.Add(new PendingChange(PendingChangeKind.SetValue, Describe(reference), node, reference.Tab)
            {
                OldValue = current,
                NewValue = value
            });
            return "已提議:" + Describe(reference) + "  " + current + " → " + value
                + "\n(尚未套用。待確認清單目前有 " + changes.Count + " 項。)";
        }

        string ProposeRename(JObject input)
        {
            string path = RequireString(input, "path");
            string newName = RequireString(input, "new_name");
            NodeRef reference = Resolve(path);
            WzNode node = reference.Node;

            if (node.Parent == null)
                throw new ToolInputException("不能改 WZ 檔案最上層節點的名稱。");
            if (string.Equals(node.Text, newName, StringComparison.Ordinal))
                throw new ToolInputException("新名稱和原本一樣,不需要修改。");
            if (WzNode.GetChildNode((WzNode)node.Parent, newName) != null)
                throw new ToolInputException("同一層底下已經有叫 " + newName + " 的節點了。");

            changes.Add(new PendingChange(PendingChangeKind.Rename, Describe(reference), node, reference.Tab)
            {
                OldValue = node.Text,
                NewValue = newName
            });
            return "已提議改名:" + Describe(reference) + " → " + newName
                + "\n(尚未套用。待確認清單目前有 " + changes.Count + " 項。)";
        }

        string ProposeDelete(JObject input)
        {
            string path = RequireString(input, "path");
            NodeRef reference = Resolve(path);
            WzNode node = reference.Node;
            if (node.Parent == null)
                throw new ToolInputException("不能刪除 WZ 檔案最上層節點。要關檔請用編輯器的 Unload。");

            changes.Add(new PendingChange(PendingChangeKind.Delete, Describe(reference), node, reference.Tab)
            {
                OldValue = DescribeValue(node.Tag as WzObject) ?? ("(" + node.Nodes.Count + " 個子節點)"),
                NewValue = "(刪除)"
            });
            return "已提議刪除:" + Describe(reference)
                + "\n(尚未套用。待確認清單目前有 " + changes.Count + " 項。)";
        }

        string ProposeAdd(JObject input)
        {
            string parentPath = RequireString(input, "parent_path");
            string name = RequireString(input, "name");
            string type = RequireString(input, "type").Trim().ToLowerInvariant();
            string value = ReadString(input, "value", "");

            NodeRef parentRef = Resolve(parentPath);
            WzNode parent = parentRef.Node;
            EnsureParsed(parent);
            if (!parent.CanHaveChilds)
                throw new ToolInputException("這個節點不能有子節點(型別 "
                    + (parent.Tag == null ? "?" : parent.Tag.GetType().Name) + ")。");
            if (WzNode.GetChildNode(parent, name) != null)
                throw new ToolInputException(parentPath + " 底下已經有叫 " + name + " 的節點了。");

            string error;
            if (!WzValueWriter.CanCreate(type, value, out error))
                throw new ToolInputException(error);

            changes.Add(new PendingChange(PendingChangeKind.Add, Describe(parentRef) + "\\" + name, parent, parentRef.Tab)
            {
                OldValue = "(不存在)",
                NewValue = type + (string.IsNullOrEmpty(value) ? "" : " = " + value),
                AddName = name,
                AddType = type,
                AddValue = value
            });
            return "已提議新增:" + Describe(parentRef) + "\\" + name + "  <" + type + ">"
                + (string.IsNullOrEmpty(value) ? "" : " = " + value)
                + "\n(尚未套用。待確認清單目前有 " + changes.Count + " 項。)";
        }

        /// <summary>
        /// The porting workhorse: deep-copies a subtree, usually from one tab's version into
        /// another's. The clone is taken at apply time, not now, so the proposal reflects the
        /// source as it stands when the user actually agrees to it.
        /// </summary>
        string ProposeCopy(JObject input)
        {
            string sourcePath = RequireString(input, "source_path");
            string targetPath = RequireString(input, "target_parent_path");
            string newName = ReadString(input, "new_name", "");

            NodeRef source = Resolve(sourcePath);
            NodeRef target = Resolve(targetPath);

            if (source.Node.Parent == null || source.Node.Tag is WzFile)
                throw new ToolInputException("不能複製整個 WZ 檔案(最上層節點),請指定底下的 .img 或屬性節點。");
            if (source.Node.Tag is WzDirectory)
                throw new ToolInputException("不能複製 WZ 目錄節點,只能複製 .img 或它底下的屬性節點。");
            if (source.Node.Tag is not WzObject)
                throw new ToolInputException("來源節點沒有可複製的內容。");

            EnsureParsed(source.Node);
            EnsureParsed(target.Node);

            if (!target.Node.CanHaveChilds)
                throw new ToolInputException("目標節點不能有子節點(型別 "
                    + (target.Node.Tag == null ? "?" : target.Node.Tag.GetType().Name) + ")。");
            if (ReferenceEquals(source.Node, target.Node))
                throw new ToolInputException("來源和目標是同一個節點。");
            if (IsAncestorOf(source.Node, target.Node))
                throw new ToolInputException("目標在來源底下,複製會變成無限巢狀。");

            string finalName = string.IsNullOrWhiteSpace(newName) ? source.Node.Text : newName.Trim();
            bool overwrite = WzNode.GetChildNode(target.Node, finalName) != null;

            int descendants = CountDescendants(source.Node);
            changes.Add(new PendingChange(PendingChangeKind.Copy,
                Describe(target) + "\\" + finalName, target.Node, target.Tab)
            {
                OldValue = overwrite ? "(已存在,會被覆蓋)" : "(不存在)",
                NewValue = "複製自 " + Describe(source) + "(" + descendants + " 個節點)",
                AddName = finalName,
                CopySource = source.Node,
                CopyOverwrites = overwrite
            });

            StringBuilder report = new StringBuilder();
            report.Append("已提議複製:\n  來源 ").Append(Describe(source))
                  .Append("  <").Append(source.Node.Tag.GetType().Name).Append(">,連同底下 ")
                  .Append(descendants).Append(" 個節點\n  目標 ").Append(Describe(target))
                  .Append('\\').Append(finalName).Append('\n');
            if (overwrite)
                report.Append("  ⚠ 目標已經有同名節點,套用時會先刪除舊的再放入複本。\n");
            report.Append("(尚未套用。待確認清單目前有 ").Append(changes.Count).Append(" 項。)");
            return report.ToString();
        }

        static bool IsAncestorOf(WzNode ancestor, WzNode node)
        {
            System.Windows.Forms.TreeNode current = node;
            while (current != null)
            {
                if (ReferenceEquals(current, ancestor))
                    return true;
                current = current.Parent;
            }
            return false;
        }

        static int CountDescendants(WzNode node)
        {
            int count = 1;
            foreach (object item in node.Nodes)
                if (item is WzNode child)
                    count += CountDescendants(child);
            return count;
        }

        #endregion

        #region memory tools

        /// <summary>
        /// Raised after any memory tool changed the store, so the window can refresh the manager
        /// without polling. Fires on the UI thread, because tools already run there.
        /// </summary>
        public event EventHandler MemoryChanged;

        void RaiseMemoryChanged()
        {
            EventHandler handler = MemoryChanged;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        /// <summary>
        /// Every memory tool reloads from the file, mutates, and writes back. Holding a cached
        /// list instead would let a second editor window silently overwrite what this one learned.
        /// </summary>
        static AiMemory OpenMemory()
        {
            return AiMemory.Load();
        }

        string Remember(JObject input)
        {
            if (!settings.MemoryEnabled)
                return "長期記憶目前是關閉的,沒有記下來。";

            string category = ReadString(input, "category", AiMemory.DefaultCategory);
            string text = RequireString(input, "text");

            AiMemory memory = OpenMemory();
            MemoryEntry duplicate;
            string refusal;
            MemoryEntry added = memory.Add(category, text, false, out duplicate, out refusal);

            if (duplicate != null)
                return "這件事已經記過了(#" + duplicate.Id + " " + duplicate.Text + ")。"
                     + "沒有重複記。內容有變的話用 update_memory 更新那一筆。";
            if (added == null)
                return refusal ?? "沒有記下來。";

            memory.Save();
            RaiseMemoryChanged();

            string report = "已記住 #" + added.Id + " [" + added.Category + "] " + added.Text;
            if (memory.NearCapacity())
                report += "\n(提醒:記憶快滿了,目前 " + memory.Count + " 筆。"
                        + "看到過時或重複的請用 forget 清掉,或合併成一筆。)";
            return report;
        }

        string UpdateMemory(JObject input)
        {
            if (!settings.MemoryEnabled)
                return "長期記憶目前是關閉的,沒有更新。";

            int id = ReadInt(input, "id", 0);
            string text = RequireString(input, "text");
            string category = ReadString(input, "category", "");

            AiMemory memory = OpenMemory();
            MemoryEntry before = memory.Find(id);
            if (before == null)
                return "找不到記憶 #" + id + "。請看系統提示裡的清單確認編號。";

            string previous = before.Text;
            if (!memory.Update(id, text, category))
                return "更新失敗:新的內容是空的。";

            memory.Save();
            RaiseMemoryChanged();
            return "已更正 #" + id + "\n  原本:" + previous + "\n  改成:" + before.Text;
        }

        string Forget(JObject input)
        {
            if (!settings.MemoryEnabled)
                return "長期記憶目前是關閉的,沒有刪除。";

            int id = ReadInt(input, "id", 0);
            string reason = ReadString(input, "reason", "");

            AiMemory memory = OpenMemory();
            MemoryEntry entry = memory.Find(id);
            if (entry == null)
                return "找不到記憶 #" + id + ",不用刪。";

            memory.Remove(id);
            memory.Save();
            RaiseMemoryChanged();
            return "已刪除 #" + id + " " + entry.Text
                 + (string.IsNullOrWhiteSpace(reason) ? "" : "\n  原因:" + reason.Trim());
        }

        #endregion

        #region tree helpers

        /// <summary>
        /// A WzImage only materialises its children once parsed; every walk has to force that or
        /// it silently sees an empty subtree.
        /// </summary>
        public static void EnsureParsed(WzNode node)
        {
            if (node.Tag is not WzImage image)
                return;
            if (!image.Parsed)
                image.ParseImage();
            if (node.Nodes.Count == 0 && image.WzProperties.Count > 0)
                node.Reparse();
        }

        public WzNode ResolveNode(string path)
        {
            return Resolve(path).Node;
        }

        /// <summary>
        /// Resolves a tool path to a node and the tab it belongs to. An optional leading [N]
        /// picks a tab; without it the file name is looked up across every open tab, and an
        /// ambiguous name is reported with the candidates rather than silently picking one.
        /// </summary>
        public NodeRef Resolve(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ToolInputException("路徑不能是空的。");

            int requestedTab;
            string remainder = StripTabPrefix(path, out requestedTab);
            List<WzTab> tabs = Tabs();

            if (requestedTab > 0)
            {
                WzTab named = null;
                foreach (WzTab tab in tabs)
                    if (tab.Number == requestedTab)
                        named = tab;
                if (named == null)
                    throw new ToolInputException("沒有第 " + requestedTab + " 個分頁。目前只有 "
                        + tabs.Count + " 個,用 list_files 看清單。");
                tabs = new List<WzTab>();
                tabs.Add(named);
            }

            string[] parts = SplitPath(remainder);
            if (parts.Length == 0)
            {
                if (requestedTab <= 0)
                    throw new ToolInputException("路徑不能是空的。");
                // "[2]" on its own means the tab itself; hand back its first root so callers
                // that only need a tab anchor have something to work with.
                foreach (object item in tabs[0].Panel.DataTree.Nodes)
                    if (item is WzNode first)
                        return new NodeRef(first, tabs[0]);
                throw new ToolInputException("分頁 [" + requestedTab + "] 沒有載入任何檔案。");
            }

            List<NodeRef> matches = new List<NodeRef>();
            foreach (WzTab tab in tabs)
            {
                WzNode root = FindRoot(tab, parts[0]);
                if (root != null)
                    matches.Add(new NodeRef(root, tab));
            }

            if (matches.Count == 0)
                throw new ToolInputException("找不到已載入的 WZ 檔案:" + parts[0]
                    + "。先用 list_files 看有哪些分頁和檔案。");
            if (matches.Count > 1)
            {
                StringBuilder candidates = new StringBuilder();
                foreach (NodeRef match in matches)
                {
                    if (candidates.Length > 0)
                        candidates.Append("、");
                    candidates.Append('[').Append(match.Tab.Number).Append(']').Append(match.Node.Text);
                }
                throw new ToolInputException("有 " + matches.Count + " 個分頁都載入了 " + parts[0]
                    + ",請用 [N] 指定是哪一個:" + candidates);
            }

            NodeRef result = matches[0];
            WzNode current = result.Node;
            for (int i = 1; i < parts.Length; i++)
            {
                EnsureParsed(current);
                WzNode next = FindChild(current, parts[i]);
                if (next == null)
                    throw new ToolInputException("在 " + Describe(new NodeRef(current, result.Tab))
                        + " 底下找不到 " + parts[i] + "。用 get_node 讀它看有哪些子節點。");
                current = next;
            }
            return new NodeRef(current, result.Tab);
        }

        /// <summary>Pulls a leading "[N]" off a path, returning the rest.</summary>
        public static string StripTabPrefix(string path, out int tabNumber)
        {
            tabNumber = 0;
            string trimmed = (path ?? "").Trim();
            if (!trimmed.StartsWith("[", StringComparison.Ordinal))
                return trimmed;
            int close = trimmed.IndexOf(']');
            if (close < 2)
                return trimmed;
            int parsed;
            if (!int.TryParse(trimmed.Substring(1, close - 1).Trim(), out parsed) || parsed < 1)
                return trimmed;
            tabNumber = parsed;
            return trimmed.Substring(close + 1);
        }

        /// <summary>The path as the model should quote it back - prefixed by tab when several are open.</summary>
        public string Describe(NodeRef reference)
        {
            if (reference == null || reference.Node == null)
                return "";
            string path = NodePath(reference.Node);
            return Tabs().Count > 1 && reference.Tab != null
                ? "[" + reference.Tab.Number + "]" + path
                : path;
        }

        public static string[] SplitPath(string path)
        {
            string[] raw = path.Replace('/', '\\').Split('\\');
            List<string> parts = new List<string>();
            foreach (string part in raw)
                if (!string.IsNullOrWhiteSpace(part))
                    parts.Add(part.Trim());
            return parts.ToArray();
        }

        static WzNode FindRoot(WzTab tab, string name)
        {
            foreach (object item in tab.Panel.DataTree.Nodes)
                if (item is WzNode node && string.Equals(node.Text, name, StringComparison.OrdinalIgnoreCase))
                    return node;

            // The tree shows "Skill" for Skill.wz on some clients and "Skill.wz" on others; accept
            // either spelling from the model rather than making it guess.
            string trimmed = StripWzExtension(name);
            foreach (object item in tab.Panel.DataTree.Nodes)
                if (item is WzNode node && string.Equals(StripWzExtension(node.Text), trimmed, StringComparison.OrdinalIgnoreCase))
                    return node;
            return null;
        }

        static string StripWzExtension(string name)
        {
            if (name == null)
                return "";
            if (name.EndsWith(".wz", StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - 3);
            if (name.EndsWith(".ms", StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - 3);
            return name;
        }

        static WzNode FindChild(WzNode parent, string name)
        {
            foreach (object item in parent.Nodes)
                if (item is WzNode node && string.Equals(node.Text, name, StringComparison.Ordinal))
                    return node;
            foreach (object item in parent.Nodes)
                if (item is WzNode node && string.Equals(node.Text, name, StringComparison.OrdinalIgnoreCase))
                    return node;
            return null;
        }

        public static string NodePath(WzNode node)
        {
            List<string> parts = new List<string>();
            System.Windows.Forms.TreeNode current = node;
            while (current != null)
            {
                parts.Add(current.Text);
                current = current.Parent;
            }
            parts.Reverse();
            return string.Join("\\", parts);
        }

        /// <summary>The value as the model should see it, or null when the node has no scalar.</summary>
        public static string RawValue(WzObject wzObject)
        {
            if (wzObject == null)
                return null;
            if (wzObject is WzStringProperty text) return text.Value;
            if (wzObject is WzIntProperty integer) return integer.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (wzObject is WzLongProperty big) return big.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (wzObject is WzShortProperty small) return small.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (wzObject is WzFloatProperty single) return single.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (wzObject is WzDoubleProperty dbl) return dbl.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (wzObject is WzUOLProperty uol) return uol.Value;
            return null;
        }

        /// <summary>
        /// A short human/model readable description. Unlike RawValue this also covers the
        /// non-editable types, so the model can tell a canvas from an empty sub property.
        /// </summary>
        public static string DescribeValue(WzObject wzObject)
        {
            string raw = RawValue(wzObject);
            if (raw != null)
                return raw.Length > 300 ? raw.Substring(0, 300) + "…" : raw;
            if (wzObject is WzCanvasProperty canvas)
            {
                try
                {
                    System.Drawing.Bitmap bitmap = canvas.GetLinkedWzCanvasBitmap();
                    if (bitmap != null)
                        return "(圖片 " + bitmap.Width + "x" + bitmap.Height + ")";
                }
                catch
                {
                    // A broken link must not take the whole listing down.
                }
                return "(圖片)";
            }
            if (wzObject is WzVectorProperty vector)
                return "(" + vector.X.Value + ", " + vector.Y.Value + ")";
            if (wzObject is WzBinaryProperty sound)
                return "(音效 " + sound.Length + "ms)";
            if (wzObject is WzNullProperty)
                return "(null)";
            return null;
        }

        static int Clamp(int value, int low, int high)
        {
            return value < low ? low : (value > high ? high : value);
        }

        static string ReadString(JObject input, string name, string fallback)
        {
            JToken token = input == null ? null : input[name];
            return token == null || token.Type == JTokenType.Null ? fallback : token.ToString();
        }

        static string RequireString(JObject input, string name)
        {
            string value = ReadString(input, name, null);
            if (string.IsNullOrWhiteSpace(value))
                throw new ToolInputException("缺少必要參數:" + name);
            return value;
        }

        static int ReadInt(JObject input, string name, int fallback)
        {
            JToken token = input == null ? null : input[name];
            int parsed;
            if (token == null) return fallback;
            return int.TryParse(token.ToString(), out parsed) ? parsed : fallback;
        }

        static bool ReadBool(JObject input, string name, bool fallback)
        {
            JToken token = input == null ? null : input[name];
            bool parsed;
            if (token == null) return fallback;
            return bool.TryParse(token.ToString(), out parsed) ? parsed : fallback;
        }

        #endregion
    }

    /// <summary>Something the model got wrong and can retry - reported back, never thrown at the user.</summary>
    public class ToolInputException : Exception
    {
        public ToolInputException(string message) : base(message) { }
    }
}
