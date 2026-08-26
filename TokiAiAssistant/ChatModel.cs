using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace TokiAi
{
    public enum BlockKind
    {
        Text,
        ToolUse,
        ToolResult
    }

    /// <summary>
    /// One piece of a turn. Claude, OpenAI and Gemini all express "assistant said some text and
    /// also wants to call these tools" differently; the conversation is kept in this neutral
    /// shape and each provider translates it on the way out and back.
    /// </summary>
    public class ContentBlock
    {
        public BlockKind Kind;

        public string Text;          // Text
        public string ToolId;        // ToolUse / ToolResult - Gemini has no ids, so it gets the name
        public string ToolName;      // ToolUse / ToolResult
        public JObject ToolInput;    // ToolUse
        public string ToolResult;    // ToolResult
        public bool IsError;         // ToolResult

        public static ContentBlock MakeText(string text)
        {
            return new ContentBlock { Kind = BlockKind.Text, Text = text };
        }

        public static ContentBlock MakeToolUse(string id, string name, JObject input)
        {
            return new ContentBlock { Kind = BlockKind.ToolUse, ToolId = id, ToolName = name, ToolInput = input ?? new JObject() };
        }

        public static ContentBlock MakeToolResult(string id, string name, string result, bool isError)
        {
            return new ContentBlock { Kind = BlockKind.ToolResult, ToolId = id, ToolName = name, ToolResult = result, IsError = isError };
        }
    }

    public class ChatTurn
    {
        // "user" or "assistant".
        public string Role;
        public List<ContentBlock> Blocks = new List<ContentBlock>();

        public ChatTurn(string role)
        {
            Role = role;
        }

        public static ChatTurn UserText(string text)
        {
            ChatTurn turn = new ChatTurn("user");
            turn.Blocks.Add(ContentBlock.MakeText(text));
            return turn;
        }

        public string JoinedText()
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            foreach (ContentBlock block in Blocks)
            {
                if (block.Kind != BlockKind.Text || string.IsNullOrEmpty(block.Text))
                    continue;
                if (builder.Length > 0)
                    builder.Append('\n');
                builder.Append(block.Text);
            }
            return builder.ToString();
        }

        public bool HasToolUse()
        {
            foreach (ContentBlock block in Blocks)
                if (block.Kind == BlockKind.ToolUse)
                    return true;
            return false;
        }
    }

    /// <summary>
    /// A tool the model may call. Parameters is a JSON Schema object, which is what all three
    /// providers want - they just nest it under a different property name.
    /// </summary>
    public class ToolDefinition
    {
        public string Name;
        public string Description;
        public JObject Parameters;
        public bool IsWrite;

        public ToolDefinition(string name, string description, JObject parameters, bool isWrite)
        {
            Name = name;
            Description = description;
            Parameters = parameters;
            IsWrite = isWrite;
        }
    }

    public class ProviderException : Exception
    {
        public ProviderException(string message) : base(message) { }
        public ProviderException(string message, Exception inner) : base(message, inner) { }
    }
}
