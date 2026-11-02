using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace OdriveWheel.SimHubPlugin.Tests
{
    /// <summary>A value of odrive-interface.yaml: text, or keys in the order of the file.</summary>
    internal sealed class YamlNode
    {
        public string Text;
        public readonly List<string> Keys = new List<string>();
        private readonly Dictionary<string, YamlNode> _children = new Dictionary<string, YamlNode>(StringComparer.Ordinal);

        public YamlNode this[string key] => _children.TryGetValue(key, out var child) ? child : null;

        public void Add(string key, YamlNode child)
        {
            if (!_children.ContainsKey(key)) Keys.Add(key);
            _children[key] = child;
        }
    }

    /// <summary>
    /// Reads just enough YAML for odrive-interface.yaml, so the test project needs no further
    /// package: block maps by indentation, one-line values, block texts and { } maps. Text in a
    /// doc can leave stray keys behind, which is harmless because they are never looked up.
    /// </summary>
    internal static class YamlSubset
    {
        private static readonly Regex KeyLine = new Regex(@"^(\s*)([A-Za-z0-9_.]+):(?:\s+(.*?))?\s*$", RegexOptions.Compiled);

        public static YamlNode Parse(string text)
        {
            var lines = text.Replace("\r", "").Split('\n');
            var root = new YamlNode();
            var stack = new Stack<(int Indent, YamlNode Node)>();
            stack.Push((-1, root));

            for (int i = 0; i < lines.Length; i++)
            {
                var match = KeyLine.Match(lines[i]);
                if (!match.Success || lines[i].TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;

                int indent = match.Groups[1].Length;
                while (stack.Peek().Indent >= indent) stack.Pop();

                var node = new YamlNode();
                stack.Peek().Node.Add(match.Groups[2].Value, node);

                var rest = match.Groups[3].Success ? StripComment(match.Groups[3].Value) : "";
                if (rest.Length == 0)
                {
                    stack.Push((indent, node));
                }
                else if (rest[0] == '|' || rest[0] == '>')
                {
                    while (i + 1 < lines.Length && (lines[i + 1].Trim().Length == 0 || IndentOf(lines[i + 1]) > indent)) i++;
                }
                else if (rest[0] == '{')
                {
                    while (!IsBalanced(rest) && i + 1 < lines.Length) rest += " " + lines[++i].Trim();
                    int position = 0;
                    ParseFlowMap(rest, ref position, node);
                }
                else
                {
                    node.Text = rest;
                }
            }
            return root;
        }

        private static int IndentOf(string line) => line.Length - line.TrimStart(' ').Length;

        private static string StripComment(string value)
        {
            if (value.StartsWith("#", StringComparison.Ordinal)) return "";
            if (value.StartsWith("'", StringComparison.Ordinal) || value.StartsWith("\"", StringComparison.Ordinal)) return value;
            int comment = value.IndexOf(" #", StringComparison.Ordinal);
            return comment >= 0 ? value.Substring(0, comment).TrimEnd() : value;
        }

        private static bool IsBalanced(string text)
        {
            int depth = 0;
            foreach (char c in text)
            {
                if (c == '{') depth++;
                else if (c == '}') depth--;
            }
            return depth <= 0;
        }

        // { key: value, key: { ... } }
        private static void ParseFlowMap(string text, ref int position, YamlNode node)
        {
            position++;
            while (position < text.Length && text[position] != '}')
            {
                if (char.IsWhiteSpace(text[position]) || text[position] == ',')
                {
                    position++;
                    continue;
                }
                int start = position;
                while (position < text.Length && ":,}".IndexOf(text[position]) < 0) position++;
                var key = text.Substring(start, position - start).Trim();
                var child = new YamlNode();
                node.Add(key, child);
                if (position >= text.Length || text[position] != ':') continue;

                position++;
                while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
                if (position < text.Length && text[position] == '{')
                {
                    ParseFlowMap(text, ref position, child);
                    continue;
                }
                start = position;
                char quote = position < text.Length && (text[position] == '\'' || text[position] == '"') ? text[position] : '\0';
                if (quote != '\0') position = Math.Max(position + 1, text.IndexOf(quote, position + 1));
                while (position < text.Length && ",}".IndexOf(text[position]) < 0) position++;
                child.Text = text.Substring(start, position - start).Trim();
            }
            position++;
        }
    }
}
