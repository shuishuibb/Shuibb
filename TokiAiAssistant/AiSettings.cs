using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TokiAi
{
    public enum AiProvider
    {
        Claude = 0,
        OpenAI = 1,
        Gemini = 2
    }

    public enum DiskAccessMode
    {
        Off = 0,
        Full = 1,
        Folders = 2
    }

    /// <summary>
    /// Per-user settings for the AI assistant. Lives outside the program folder so copying the
    /// editor to a new folder (which this project does every round) does not carry an API key
    /// with it, and so an unwritable install directory does not break the feature.
    /// </summary>
    public class AiSettings
    {
        public AiProvider Provider = AiProvider.Claude;

        // One key per provider, so switching provider does not lose the other keys.
        public string ClaudeKey = "";
        public string OpenAiKey = "";
        public string GeminiKey = "";

        public string ClaudeModel = DefaultClaudeModel;
        public string OpenAiModel = DefaultOpenAiModel;
        public string GeminiModel = DefaultGeminiModel;

        // Current Claude model IDs take no date suffix - "claude-opus-5", never
        // "claude-opus-5-20260101". The settings dialog can pull the live list from each
        // provider, so these are only the starting point before a key is entered.
        public const string DefaultClaudeModel = "claude-opus-5";
        public const string DefaultOpenAiModel = "gpt-4o";
        public const string DefaultGeminiModel = "gemini-2.0-flash";

        // Left blank = the official endpoint. Set it to use a proxy or a compatible gateway.
        public string ClaudeBaseUrl = "";
        public string OpenAiBaseUrl = "";
        public string GeminiBaseUrl = "";

        public bool AllowWrites = true;

        // Whole-machine by default. This is safe here only because no tool ever returns file
        // CONTENTS to the model: listing yields names/sizes/image dimensions, and imported
        // pixels travel disk -> WZ node without passing through the API. Adding any
        // read-file-contents tool later would invalidate that reasoning.
        public DiskAccessMode DiskAccess = DiskAccessMode.Full;
        public List<string> AllowedFolders = new List<string>();

        // Long-term memory. Off means the store is neither injected into the prompt nor
        // writable - the file stays on disk untouched, so turning it back on restores
        // everything that was learned before.
        public bool MemoryEnabled = true;

        public int MaxToolRounds = 12;

        // A WZ question routinely runs a dozen tool rounds with sizeable results. 4096 output
        // tokens truncated real answers mid-sentence and 120s tripped on the later, larger
        // rounds, so both defaults are higher and the old ones are migrated up on load.
        public int MaxOutputTokens = 8192;
        public int TimeoutSeconds = 300;

        // Bumped whenever a default changes in a way existing files should pick up.
        public const int CurrentVersion = 2;
        public int Version = CurrentVersion;

        public string SystemPromptOverride = "";

        public string CurrentKey
        {
            get
            {
                switch (Provider)
                {
                    case AiProvider.OpenAI: return OpenAiKey;
                    case AiProvider.Gemini: return GeminiKey;
                    default: return ClaudeKey;
                }
            }
        }

        public string CurrentModel
        {
            get
            {
                switch (Provider)
                {
                    case AiProvider.OpenAI: return string.IsNullOrWhiteSpace(OpenAiModel) ? DefaultOpenAiModel : OpenAiModel.Trim();
                    case AiProvider.Gemini: return string.IsNullOrWhiteSpace(GeminiModel) ? DefaultGeminiModel : GeminiModel.Trim();
                    default: return string.IsNullOrWhiteSpace(ClaudeModel) ? DefaultClaudeModel : ClaudeModel.Trim();
                }
            }
        }

        public string CurrentBaseUrl
        {
            get
            {
                switch (Provider)
                {
                    case AiProvider.OpenAI: return (OpenAiBaseUrl ?? "").Trim();
                    case AiProvider.Gemini: return (GeminiBaseUrl ?? "").Trim();
                    default: return (ClaudeBaseUrl ?? "").Trim();
                }
            }
        }

        public static string ProviderDisplayName(AiProvider provider)
        {
            switch (provider)
            {
                case AiProvider.OpenAI: return "OpenAI";
                case AiProvider.Gemini: return "Gemini";
                default: return "Claude";
            }
        }

        #region storage

        public static string SettingsDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "TOKI_HaRepacker_AI");
            }
        }

        public static string SettingsPath
        {
            get { return Path.Combine(SettingsDirectory, "ai_settings.json"); }
        }

        public static AiSettings Load()
        {
            return LoadFrom(SettingsPath);
        }

        /// <summary>Reads settings from an explicit file. Split out so the migration is testable.</summary>
        public static AiSettings LoadFrom(string path)
        {
            AiSettings settings = new AiSettings();
            try
            {
                if (!File.Exists(path))
                    return settings;

                JObject root = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));

                settings.Provider = (AiProvider)ReadInt(root, "provider", (int)AiProvider.Claude);
                settings.ClaudeKey = Unprotect(ReadString(root, "claudeKey", ""));
                settings.OpenAiKey = Unprotect(ReadString(root, "openAiKey", ""));
                settings.GeminiKey = Unprotect(ReadString(root, "geminiKey", ""));
                settings.ClaudeModel = ReadString(root, "claudeModel", settings.ClaudeModel);
                settings.OpenAiModel = ReadString(root, "openAiModel", settings.OpenAiModel);
                settings.GeminiModel = ReadString(root, "geminiModel", settings.GeminiModel);
                settings.ClaudeBaseUrl = ReadString(root, "claudeBaseUrl", "");
                settings.OpenAiBaseUrl = ReadString(root, "openAiBaseUrl", "");
                settings.GeminiBaseUrl = ReadString(root, "geminiBaseUrl", "");
                settings.AllowWrites = ReadBool(root, "allowWrites", true);
                settings.DiskAccess = (DiskAccessMode)ReadInt(root, "diskAccess", (int)DiskAccessMode.Full);
                settings.AllowedFolders = new List<string>();
                if (root["allowedFolders"] is JArray folders)
                    foreach (JToken folder in folders)
                    {
                        string value = folder == null ? null : folder.ToString();
                        if (!string.IsNullOrWhiteSpace(value))
                            settings.AllowedFolders.Add(value.Trim());
                    }
                settings.MemoryEnabled = ReadBool(root, "memoryEnabled", true);
                settings.MaxToolRounds = ReadInt(root, "maxToolRounds", 12);
                settings.MaxOutputTokens = ReadInt(root, "maxOutputTokens", 8192);
                settings.TimeoutSeconds = ReadInt(root, "timeoutSeconds", 300);
                settings.SystemPromptOverride = ReadString(root, "systemPromptOverride", "");
                settings.Version = ReadInt(root, "version", 1);

                if (settings.Version < 2)
                {
                    // v1 shipped 4096/120, which truncated answers and timed out on long tool
                    // loops. Raise those two only if they were left at the old defaults - a
                    // deliberately lowered value is the user's choice and stays.
                    if (settings.MaxOutputTokens == 4096) settings.MaxOutputTokens = 8192;
                    if (settings.TimeoutSeconds == 120) settings.TimeoutSeconds = 300;
                    settings.Version = CurrentVersion;
                }
            }
            catch
            {
                // A corrupt settings file must not stop the editor from opening the window.
                return new AiSettings();
            }
            if (settings.MaxToolRounds < 1) settings.MaxToolRounds = 1;
            if (settings.MaxToolRounds > 40) settings.MaxToolRounds = 40;
            if (settings.MaxOutputTokens < 256) settings.MaxOutputTokens = 256;
            if (settings.TimeoutSeconds < 15) settings.TimeoutSeconds = 15;
            return settings;
        }

        public void Save()
        {
            Directory.CreateDirectory(SettingsDirectory);
            JObject root = new JObject();
            root["provider"] = (int)Provider;
            root["claudeKey"] = Protect(ClaudeKey);
            root["openAiKey"] = Protect(OpenAiKey);
            root["geminiKey"] = Protect(GeminiKey);
            root["claudeModel"] = ClaudeModel ?? "";
            root["openAiModel"] = OpenAiModel ?? "";
            root["geminiModel"] = GeminiModel ?? "";
            root["claudeBaseUrl"] = ClaudeBaseUrl ?? "";
            root["openAiBaseUrl"] = OpenAiBaseUrl ?? "";
            root["geminiBaseUrl"] = GeminiBaseUrl ?? "";
            root["allowWrites"] = AllowWrites;
            root["diskAccess"] = (int)DiskAccess;
            JArray folders = new JArray();
            if (AllowedFolders != null)
                foreach (string folder in AllowedFolders)
                    if (!string.IsNullOrWhiteSpace(folder))
                        folders.Add(folder.Trim());
            root["allowedFolders"] = folders;
            root["memoryEnabled"] = MemoryEnabled;
            root["maxToolRounds"] = MaxToolRounds;
            root["maxOutputTokens"] = MaxOutputTokens;
            root["timeoutSeconds"] = TimeoutSeconds;
            root["systemPromptOverride"] = SystemPromptOverride ?? "";
            root["version"] = CurrentVersion;
            File.WriteAllText(SettingsPath, root.ToString(Formatting.Indented), new UTF8Encoding(false));
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

        #endregion

        #region DPAPI

        // API keys are real credentials, so they are encrypted to the Windows user account
        // rather than written in the clear. P/Invoke instead of the ProtectedData package so
        // this assembly stays dependency-free and drops into the program folder on its own.
        // If DPAPI is unavailable the value round-trips as plain text.

        const string ProtectedPrefix = "dpapi:";

        [StructLayout(LayoutKind.Sequential)]
        struct DataBlob
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptProtectData(ref DataBlob pDataIn, string szDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr hMem);

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain))
                return "";
            try
            {
                byte[] input = Encoding.UTF8.GetBytes(plain);
                DataBlob inBlob = new DataBlob();
                DataBlob outBlob = new DataBlob();
                inBlob.pbData = Marshal.AllocHGlobal(input.Length);
                inBlob.cbData = input.Length;
                try
                {
                    Marshal.Copy(input, 0, inBlob.pbData, input.Length);
                    // 0x4 = CRYPTPROTECT_UI_FORBIDDEN: never prompt.
                    if (!CryptProtectData(ref inBlob, "TOKI HaRepacker AI key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x4, ref outBlob))
                        return plain;
                    byte[] encrypted = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, encrypted, 0, outBlob.cbData);
                    return ProtectedPrefix + Convert.ToBase64String(encrypted);
                }
                finally
                {
                    if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
                    if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
                }
            }
            catch
            {
                return plain;
            }
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored))
                return "";
            if (!stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
                return stored;
            try
            {
                byte[] encrypted = Convert.FromBase64String(stored.Substring(ProtectedPrefix.Length));
                DataBlob inBlob = new DataBlob();
                DataBlob outBlob = new DataBlob();
                inBlob.pbData = Marshal.AllocHGlobal(encrypted.Length);
                inBlob.cbData = encrypted.Length;
                try
                {
                    Marshal.Copy(encrypted, 0, inBlob.pbData, encrypted.Length);
                    if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x4, ref outBlob))
                        return "";
                    byte[] plain = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, plain, 0, outBlob.cbData);
                    return Encoding.UTF8.GetString(plain);
                }
                finally
                {
                    if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
                    if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
                }
            }
            catch
            {
                return "";
            }
        }

        #endregion
    }
}
