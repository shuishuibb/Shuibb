using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace TokiAi
{
    /// <summary>
    /// Just enough Markdown for what the models actually emit here: headings, bullet lists,
    /// tables, fenced code, inline bold and inline code. Rendered straight into a RichTextBox
    /// through the selection API - no HTML, no browser control, no extra dependency.
    ///
    /// Tables are laid out by padding to a fixed column width in a CJK-monospaced font, and the
    /// padding counts a full-width character as two columns. Consolas would break the moment a
    /// table held any Chinese, which for this tool is every table.
    /// </summary>
    public static class MarkdownRenderer
    {
        public static Font BaseFont = new Font(PickFont("Microsoft JhengHei UI", "Microsoft JhengHei", "Segoe UI"), 10f);
        public static Font BoldFont = new Font(BaseFont, FontStyle.Bold);
        public static Font HeadingFont = new Font(PickFont("Microsoft JhengHei UI", "Microsoft JhengHei", "Segoe UI"), 11.5f, FontStyle.Bold);
        public static Font MonoFont = new Font(PickFont("MingLiU", "MS Gothic", "NSimSun", "Consolas"), 10f);
        public static Font MonoBoldFont = new Font(MonoFont, FontStyle.Bold);

        static string PickFont(params string[] candidates)
        {
            foreach (string candidate in candidates)
            {
                try
                {
                    using (Font probe = new Font(candidate, 10f))
                    {
                        if (string.Equals(probe.Name, candidate, StringComparison.OrdinalIgnoreCase))
                            return candidate;
                    }
                }
                catch
                {
                    // Font not installed - try the next one.
                }
            }
            return candidates[candidates.Length - 1];
        }

        public static void Append(RichTextBox box, string markdown, Color color)
        {
            if (string.IsNullOrEmpty(markdown))
                return;

            string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int index = 0;
            bool inCodeFence = false;

            while (index < lines.Length)
            {
                string line = lines[index];

                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    inCodeFence = !inCodeFence;
                    index++;
                    continue;
                }

                if (inCodeFence)
                {
                    AppendRun(box, line + "\n", MonoFont, color);
                    index++;
                    continue;
                }

                if (IsTableRow(line) && index + 1 < lines.Length && IsTableSeparator(lines[index + 1]))
                {
                    index = AppendTable(box, lines, index, color);
                    continue;
                }

                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    string heading = line.TrimStart('#').Trim();
                    AppendInline(box, heading + "\n", HeadingFont, HeadingFont, color);
                    index++;
                    continue;
                }

                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
                {
                    int indent = line.Length - trimmed.Length;
                    AppendRun(box, new string(' ', indent) + "• ", BaseFont, color);
                    AppendInline(box, trimmed.Substring(2) + "\n", BaseFont, BoldFont, color);
                    index++;
                    continue;
                }

                AppendInline(box, line + "\n", BaseFont, BoldFont, color);
                index++;
            }
        }

        #region tables

        static bool IsTableRow(string line)
        {
            string trimmed = (line ?? "").Trim();
            return trimmed.StartsWith("|", StringComparison.Ordinal) && trimmed.Length > 1;
        }

        static bool IsTableSeparator(string line)
        {
            string trimmed = (line ?? "").Trim();
            if (!trimmed.StartsWith("|", StringComparison.Ordinal))
                return false;
            foreach (char character in trimmed)
                if (character != '|' && character != '-' && character != ':' && character != ' ')
                    return false;
            return trimmed.IndexOf('-') >= 0;
        }

        static string[] SplitRow(string line)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("|", StringComparison.Ordinal))
                trimmed = trimmed.Substring(1);
            if (trimmed.EndsWith("|", StringComparison.Ordinal))
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            string[] cells = trimmed.Split('|');
            for (int i = 0; i < cells.Length; i++)
                cells[i] = StripInlineMarkers(cells[i].Trim());
            return cells;
        }

        static int AppendTable(RichTextBox box, string[] lines, int start, Color color)
        {
            List<string[]> rows = new List<string[]>();
            string[] header = SplitRow(lines[start]);
            rows.Add(header);

            int index = start + 2; // skip the separator row
            while (index < lines.Length && IsTableRow(lines[index]))
            {
                rows.Add(SplitRow(lines[index]));
                index++;
            }

            int columns = 0;
            foreach (string[] row in rows)
                columns = Math.Max(columns, row.Length);

            int[] widths = new int[columns];
            foreach (string[] row in rows)
                for (int c = 0; c < row.Length; c++)
                    widths[c] = Math.Max(widths[c], DisplayWidth(row[c]));

            for (int r = 0; r < rows.Count; r++)
            {
                StringBuilder builder = new StringBuilder();
                for (int c = 0; c < columns; c++)
                {
                    string cell = c < rows[r].Length ? rows[r][c] : "";
                    builder.Append(cell);
                    builder.Append(new string(' ', Math.Max(0, widths[c] - DisplayWidth(cell))));
                    if (c < columns - 1)
                        builder.Append("  ");
                }
                AppendRun(box, builder.ToString() + "\n", r == 0 ? MonoBoldFont : MonoFont, color);

                if (r == 0)
                {
                    StringBuilder rule = new StringBuilder();
                    for (int c = 0; c < columns; c++)
                    {
                        rule.Append(new string('-', widths[c]));
                        if (c < columns - 1)
                            rule.Append("  ");
                    }
                    AppendRun(box, rule.ToString() + "\n", MonoFont, Blend(color));
                }
            }
            return index;
        }

        /// <summary>
        /// Columns are padded in character cells, and CJK / full-width forms occupy two of them
        /// in the monospaced font used for tables.
        /// </summary>
        public static int DisplayWidth(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;
            int width = 0;
            foreach (char character in text)
                width += IsFullWidth(character) ? 2 : 1;
            return width;
        }

        static bool IsFullWidth(char character)
        {
            return (character >= 0x1100 && character <= 0x115F)      // Hangul Jamo
                || (character >= 0x2E80 && character <= 0xA4CF)      // CJK radicals .. Yi
                || (character >= 0xAC00 && character <= 0xD7A3)      // Hangul syllables
                || (character >= 0xF900 && character <= 0xFAFF)      // CJK compatibility
                || (character >= 0xFE30 && character <= 0xFE6F)      // CJK compatibility forms
                || (character >= 0xFF00 && character <= 0xFF60)      // Full-width forms
                || (character >= 0xFFE0 && character <= 0xFFE6);
        }

        #endregion

        #region inline

        /// <summary>Renders one line, switching fonts for **bold** and `code`.</summary>
        static void AppendInline(RichTextBox box, string text, Font normal, Font bold, Color color)
        {
            int index = 0;
            StringBuilder plain = new StringBuilder();

            while (index < text.Length)
            {
                if (index + 1 < text.Length && text[index] == '*' && text[index + 1] == '*')
                {
                    int end = text.IndexOf("**", index + 2, StringComparison.Ordinal);
                    if (end > 0)
                    {
                        Flush(box, plain, normal, color);
                        AppendRun(box, text.Substring(index + 2, end - index - 2), bold, color);
                        index = end + 2;
                        continue;
                    }
                }
                if (text[index] == '`')
                {
                    int end = text.IndexOf('`', index + 1);
                    if (end > 0)
                    {
                        Flush(box, plain, normal, color);
                        AppendRun(box, text.Substring(index + 1, end - index - 1), MonoFont, color);
                        index = end + 1;
                        continue;
                    }
                }
                plain.Append(text[index]);
                index++;
            }
            Flush(box, plain, normal, color);
        }

        static void Flush(RichTextBox box, StringBuilder plain, Font font, Color color)
        {
            if (plain.Length == 0)
                return;
            AppendRun(box, plain.ToString(), font, color);
            plain.Clear();
        }

        static string StripInlineMarkers(string text)
        {
            return (text ?? "").Replace("**", "").Replace("`", "");
        }

        public static void AppendRun(RichTextBox box, string text, Font font, Color color)
        {
            box.SelectionStart = box.TextLength;
            box.SelectionLength = 0;
            box.SelectionFont = font;
            box.SelectionColor = color;
            box.AppendText(text);
        }

        static Color Blend(Color color)
        {
            return Color.FromArgb((color.R + 128) / 2, (color.G + 128) / 2, (color.B + 128) / 2);
        }

        #endregion
    }
}
