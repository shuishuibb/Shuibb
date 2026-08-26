using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TokiAi
{
    /// <summary>
    /// One durable fact the assistant learned. Deliberately tiny and human-readable: the whole
    /// store is a JSON file the user can open in Notepad, and every entry is shown back to them
    /// in the memory manager so nothing accumulates unseen.
    /// </summary>
    public class MemoryEntry
    {
        public int Id;
        public string Category = AiMemory.DefaultCategory;
        public string Text = "";
        public DateTime Created = DateTime.Now;
        public bool FromUser;

        public MemoryEntry Copy()
        {
            return new MemoryEntry
            {
                Id = Id,
                Category = Category,
                Text = Text,
                Created = Created,
                FromUser = FromUser
            };
        }
    }

    /// <summary>
    /// Long-term memory: facts that survive closing the window and closing the editor.
    ///
    /// The conversation itself is deliberately NOT persisted - replaying an old transcript would
    /// re-send stale WZ readings as if they were current. What persists is only what the model
    /// explicitly decided is durable, written one short line at a time, and rendered back into
    /// the system prompt of every later conversation.
    ///
    /// Every mutation is load-modify-save against the file rather than against a cached list,
    /// so two editor windows open at once cannot silently drop each other's entries.
    /// </summary>
    public class AiMemory
    {
        public const string DefaultCategory = "其他";

        // A fixed vocabulary, because free-text categories fragment ("節點"/"節點結構"/"結構")
        // and the rendered prompt then reads as noise. Anything unrecognised lands in 其他.
        public static readonly string[] Categories = { "節點", "流程", "環境", "偏好", "坑", DefaultCategory };

        // The whole store is re-sent as part of the system prompt on every single request, so it
        // is capped by characters rather than entries. Past the soft cap the tool result starts
        // asking the model to consolidate; at the hard cap it refuses to add until something goes.
        public const int SoftCharBudget = 9000;
        public const int HardCharBudget = 14000;
        public const int MaxEntries = 400;
        public const int MaxEntryLength = 400;

        readonly List<MemoryEntry> entries = new List<MemoryEntry>();
        int nextId = 1;

        public List<MemoryEntry> Entries { get { return entries; } }
        public int Count { get { return entries.Count; } }

        public static string MemoryPath
        {
            get { return Path.Combine(AiSettings.SettingsDirectory, "memory.json"); }
        }

        #region storage

        public static AiMemory Load()
        {
            return LoadFrom(MemoryPath);
        }

        /// <summary>Reads from an explicit file. Split out so the caps and merging are testable.</summary>
        public static AiMemory LoadFrom(string path)
        {
            AiMemory memory = new AiMemory();
            int storedNextId = 0;
            try
            {
                if (!File.Exists(path))
                    return memory;

                JObject root = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                storedNextId = ReadInt(root, "nextId", 0);
                JArray list = root["entries"] as JArray;
                if (list != null)
                {
                    foreach (JToken token in list)
                    {
                        JObject item = token as JObject;
                        if (item == null)
                            continue;
                        string text = ReadString(item, "text", "");
                        if (string.IsNullOrWhiteSpace(text))
                            continue;

                        MemoryEntry entry = new MemoryEntry();
                        entry.Id = ReadInt(item, "id", 0);
                        entry.Category = NormaliseCategory(ReadString(item, "category", DefaultCategory));
                        entry.Text = Tidy(text);
                        entry.Created = ReadDate(item, "created");
                        entry.FromUser = ReadBool(item, "fromUser", false);
                        memory.entries.Add(entry);
                    }
                }
            }
            catch
            {
                // A corrupt memory file must never stop the assistant from opening. Losing the
                // learned facts is recoverable; a window that will not open is not.
                return new AiMemory();
            }
            memory.RenumberBlanks(storedNextId);
            return memory;
        }

        public void Save()
        {
            SaveTo(MemoryPath);
        }

        public void SaveTo(string path)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            JArray list = new JArray();
            foreach (MemoryEntry entry in entries)
            {
                JObject item = new JObject();
                item["id"] = entry.Id;
                item["category"] = entry.Category ?? DefaultCategory;
                item["text"] = entry.Text ?? "";
                item["created"] = entry.Created.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                item["fromUser"] = entry.FromUser;
                list.Add(item);
            }
            JObject root = new JObject();
            root["version"] = 1;
            root["nextId"] = nextId;
            root["entries"] = list;
            File.WriteAllText(path, root.ToString(Formatting.Indented), new UTF8Encoding(false));
        }

        /// <summary>
        /// Assigns ids to any entry that arrived without one, and picks the next id to hand out.
        ///
        /// The counter is persisted rather than derived from the highest surviving id, because
        /// deleting the newest entry would otherwise make the next one reuse its number - and the
        /// model addresses entries by id, so a recycled id makes a later update_memory silently
        /// rewrite a different fact than the one it meant.
        /// </summary>
        void RenumberBlanks(int storedNextId)
        {
            int highest = 0;
            foreach (MemoryEntry entry in entries)
                if (entry.Id > highest)
                    highest = entry.Id;
            foreach (MemoryEntry entry in entries)
                if (entry.Id <= 0)
                    entry.Id = ++highest;
            nextId = Math.Max(storedNextId, highest + 1);
        }

        #endregion

        #region mutation

        public MemoryEntry Find(int id)
        {
            foreach (MemoryEntry entry in entries)
                if (entry.Id == id)
                    return entry;
            return null;
        }

        /// <summary>
        /// Adds a fact, or returns the existing one when it is already known. Returns null and
        /// fills <paramref name="refusal"/> when the store is full or the text is unusable.
        /// </summary>
        public MemoryEntry Add(string category, string text, bool fromUser, out MemoryEntry duplicate, out string refusal)
        {
            duplicate = null;
            refusal = null;

            text = Tidy(text);
            if (string.IsNullOrWhiteSpace(text))
            {
                refusal = "記憶內容是空的,沒有記下來。";
                return null;
            }
            if (text.Length > MaxEntryLength)
                text = text.Substring(0, MaxEntryLength).TrimEnd() + "…";

            duplicate = FindSimilar(text);
            if (duplicate != null)
                return null;

            if (entries.Count >= MaxEntries)
            {
                refusal = "記憶已經滿了(" + MaxEntries + " 筆)。請先用 forget 刪掉過時的,或把幾筆合併成一筆。";
                return null;
            }
            if (TotalChars() + text.Length > HardCharBudget)
            {
                refusal = "記憶已達長度上限。請先用 forget 刪掉過時的,或把重複的合併成一筆再記。";
                return null;
            }

            MemoryEntry entry = new MemoryEntry();
            entry.Id = nextId++;
            entry.Category = NormaliseCategory(category);
            entry.Text = text;
            entry.Created = DateTime.Now;
            entry.FromUser = fromUser;
            entries.Add(entry);
            return entry;
        }

        public bool Update(int id, string text, string category)
        {
            MemoryEntry entry = Find(id);
            if (entry == null)
                return false;
            text = Tidy(text);
            if (string.IsNullOrWhiteSpace(text))
                return false;
            if (text.Length > MaxEntryLength)
                text = text.Substring(0, MaxEntryLength).TrimEnd() + "…";
            entry.Text = text;
            if (!string.IsNullOrWhiteSpace(category))
                entry.Category = NormaliseCategory(category);
            return true;
        }

        public bool Remove(int id)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Id != id)
                    continue;
                entries.RemoveAt(i);
                return true;
            }
            return false;
        }

        public void Clear()
        {
            entries.Clear();
        }

        public int TotalChars()
        {
            int total = 0;
            foreach (MemoryEntry entry in entries)
                total += (entry.Text ?? "").Length;
            return total;
        }

        public bool NearCapacity()
        {
            return TotalChars() > SoftCharBudget || entries.Count > MaxEntries * 3 / 4;
        }

        /// <summary>
        /// Finds an entry that already says this. Catches the common case where the model
        /// re-learns the same fact in a later conversation and would otherwise stack up near
        /// duplicates that all get re-sent forever.
        /// </summary>
        public MemoryEntry FindSimilar(string text)
        {
            string wanted = Normalise(text);
            if (wanted.Length == 0)
                return null;
            foreach (MemoryEntry entry in entries)
            {
                string existing = Normalise(entry.Text);
                if (existing.Length == 0)
                    continue;
                if (existing == wanted)
                    return entry;
                // One being a prefix-free superset of the other means the same fact with extra
                // words on it; keep whichever is already stored and let the model update it.
                if (existing.Length >= 12 && wanted.Length >= 12
                    && (existing.Contains(wanted) || wanted.Contains(existing)))
                    return entry;
            }
            return null;
        }

        #endregion

        #region rendering

        /// <summary>
        /// The block appended to the system prompt. Ids are shown because update_memory and
        /// forget address entries by id, so the model must be able to see them.
        /// </summary>
        public string RenderForPrompt()
        {
            if (entries.Count == 0)
                return "";

            StringBuilder block = new StringBuilder();
            block.Append("## 你已經學到的事情(長期記憶)\n");
            block.Append("以下是先前對話累積下來的,每次開新對話都會帶上。\n");
            block.Append("這些可能已經過時 —— 跟你這次實際讀到的資料衝突時,一律以實際讀到的為準,");
            block.Append("並用 update_memory 把那一筆更正。\n\n");

            foreach (string category in Categories)
            {
                List<MemoryEntry> inCategory = new List<MemoryEntry>();
                foreach (MemoryEntry entry in entries)
                    if (NormaliseCategory(entry.Category) == category)
                        inCategory.Add(entry);
                if (inCategory.Count == 0)
                    continue;

                block.Append("### ").Append(CategoryTitle(category)).Append('\n');
                foreach (MemoryEntry entry in inCategory)
                {
                    block.Append("- #").Append(entry.Id).Append(' ').Append(entry.Text);
                    if (entry.FromUser)
                        block.Append("  (使用者親自交代)");
                    block.Append('\n');
                }
                block.Append('\n');
            }
            return block.ToString();
        }

        public static string CategoryTitle(string category)
        {
            switch (NormaliseCategory(category))
            {
                case "節點": return "節點與 WZ 結構";
                case "流程": return "操作流程";
                case "環境": return "這台電腦上的環境";
                case "偏好": return "使用者的偏好與要求";
                case "坑": return "踩過的坑 / 要避開的事";
                default: return "其他";
            }
        }

        #endregion

        #region text

        public static string NormaliseCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category))
                return DefaultCategory;
            string trimmed = category.Trim();
            foreach (string known in Categories)
                if (string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase))
                    return known;

            // The model writes in whatever wording the conversation used, so map the obvious
            // synonyms instead of dumping everything into 其他.
            if (trimmed.Contains("節點") || trimmed.Contains("結構") || trimmed.Contains("路徑")
                || trimmed.IndexOf("node", StringComparison.OrdinalIgnoreCase) >= 0
                || trimmed.IndexOf("structure", StringComparison.OrdinalIgnoreCase) >= 0)
                return "節點";
            if (trimmed.Contains("流程") || trimmed.Contains("步驟") || trimmed.Contains("做法")
                || trimmed.IndexOf("workflow", StringComparison.OrdinalIgnoreCase) >= 0
                || trimmed.IndexOf("process", StringComparison.OrdinalIgnoreCase) >= 0)
                return "流程";
            if (trimmed.Contains("環境") || trimmed.Contains("版本") || trimmed.Contains("資料夾")
                || trimmed.IndexOf("environment", StringComparison.OrdinalIgnoreCase) >= 0
                || trimmed.IndexOf("path", StringComparison.OrdinalIgnoreCase) >= 0)
                return "環境";
            if (trimmed.Contains("偏好") || trimmed.Contains("習慣") || trimmed.Contains("要求")
                || trimmed.IndexOf("preference", StringComparison.OrdinalIgnoreCase) >= 0)
                return "偏好";
            if (trimmed.Contains("坑") || trimmed.Contains("雷") || trimmed.Contains("錯誤")
                || trimmed.IndexOf("pitfall", StringComparison.OrdinalIgnoreCase) >= 0
                || trimmed.IndexOf("gotcha", StringComparison.OrdinalIgnoreCase) >= 0)
                return "坑";
            return DefaultCategory;
        }

        static string Tidy(string text)
        {
            if (text == null)
                return "";
            // Newlines would break the one-line-per-fact rendering, and a fact that needs several
            // lines is really several facts.
            return text.Replace("\r", " ").Replace("\n", " ").Replace("  ", " ").Trim();
        }

        static string Normalise(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            StringBuilder builder = new StringBuilder(text.Length);
            foreach (char character in text)
            {
                if (char.IsWhiteSpace(character))
                    continue;
                if (char.IsPunctuation(character) || char.IsSymbol(character))
                    continue;
                builder.Append(char.ToLowerInvariant(character));
            }
            return builder.ToString();
        }

        static string ReadString(JObject root, string name, string fallback)
        {
            JToken token = root[name];
            return token == null || token.Type == JTokenType.Null ? fallback : token.ToString();
        }

        static int ReadInt(JObject root, string name, int fallback)
        {
            JToken token = root[name];
            int parsed;
            if (token == null) return fallback;
            return int.TryParse(token.ToString(), out parsed) ? parsed : fallback;
        }

        static bool ReadBool(JObject root, string name, bool fallback)
        {
            JToken token = root[name];
            bool parsed;
            if (token == null) return fallback;
            return bool.TryParse(token.ToString(), out parsed) ? parsed : fallback;
        }

        static DateTime ReadDate(JObject root, string name)
        {
            JToken token = root[name];
            DateTime parsed;
            if (token != null && DateTime.TryParse(token.ToString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out parsed))
                return parsed;
            return DateTime.Now;
        }

        #endregion
    }
}
