using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace OdriveWheel.SimHubPlugin
{
    /// <summary>Floats as the firmware writes them: %.9g, "inf", "-inf", "nan", "-0". Always invariant culture.</summary>
    public static class OdriveFloatText
    {
        public static bool IsNonFinite(string text) => TryNonFinite(text, out _);

        /// <summary>The float32 the text stands for. False for anything that is no float or does not fit one.</summary>
        public static bool TryParseFloat32(string text, out float value)
        {
            if (TryNonFinite(text, out value)) return true;
            value = 0.0f;
            if (string.IsNullOrEmpty(text) || text.Trim() != text) return false;
            if (!double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                                 CultureInfo.InvariantCulture, out double parsed))
                return false;
            value = (float)parsed;
            // .NET Framework parses "-0" as +0, the firmware means the negative zero.
            if (value == 0.0f && text[0] == '-') value = -0.0f;
            return !float.IsInfinity(value);
        }

        /// <summary>The value of the last digit the text shows: what rounding or cutting there can have changed.</summary>
        internal static double LastDigit(string text)
        {
            int e = text.IndexOfAny(new[] { 'e', 'E' });
            var mantissa = e < 0 ? text : text.Substring(0, e);
            int exponent = 0;
            if (e >= 0) int.TryParse(text.Substring(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent);
            int point = mantissa.IndexOf('.');
            int decimals = point < 0 ? 0 : mantissa.Length - point - 1;
            return Math.Pow(10.0, exponent - decimals);
        }

        private static bool TryNonFinite(string text, out float value)
        {
            value = 0.0f;
            if (text == null) return false;
            switch (text.ToLowerInvariant())
            {
                case "inf": case "+inf": case "infinity": case "+infinity":
                    value = float.PositiveInfinity;
                    return true;
                case "-inf": case "-infinity":
                    value = float.NegativeInfinity;
                    return true;
                case "nan": case "-nan": case "+nan":
                    value = float.NaN;
                    return true;
                default:
                    return false;
            }
        }
    }

    public enum ComparisonVerdict
    {
        /// <summary>The same value, by type, and both sides are certain of it.</summary>
        Same,
        /// <summary>The same text, but at least one side is not certain: equal as text, nothing more.</summary>
        SameText,
        /// <summary>Different values, and that holds whatever rounding or cutting the replies went through.</summary>
        Differs,
        /// <summary>Different text, but they may be the same value on the board.</summary>
        Unclear,
        OnlyLeft,
        OnlyRight,
        /// <summary>A side tried and has no value: its read failed.</summary>
        NotCompared,
    }

    /// <summary>
    /// Compares two replies of one parameter by its catalog type. The rules, also in docs/packages/PKG1.md:
    /// <list type="bullet">
    /// <item>Floats are float32 values. They compare by value only where both replies are exact. +0 and -0 are the same
    /// value, both NaN are the same (no payload is compared), +inf and -inf differ from each other and from every number.</item>
    /// <item>Integers, bitmasks, enums and axis states compare as whole numbers of any size, nothing goes through a double.</item>
    /// <item>Booleans take 0 and 1, and the True and False of older web client files.</item>
    /// <item>Where a side is not certain, equal text means equal text and no more. Different text is a difference only where
    /// no rounding or cutting can explain it, otherwise it is unclear.</item>
    /// <item>A path the catalog does not know is compared as text.</item>
    /// </list>
    /// </summary>
    public static class OdriveValueComparer
    {
        public static ComparisonVerdict Compare(ParameterDefinition definition, string left, ReplyCertainty leftCertainty,
                                                string right, ReplyCertainty rightCertainty, out string note)
        {
            note = null;
            if (definition == null)
            {
                note = "not in the plugin's catalog, compared as text";
                return left == right ? ComparisonVerdict.SameText : ComparisonVerdict.Unclear;
            }

            switch (definition.Type)
            {
                case ParameterType.Text:
                    return left == right ? ComparisonVerdict.Same : ComparisonVerdict.Differs;
                case ParameterType.Boolean:
                    return CompareBoolean(left, right, out note);
                case ParameterType.Float:
                    return CompareFloat(left, leftCertainty, right, rightCertainty, out note);
                default:
                    return CompareInteger(definition, left, leftCertainty, right, rightCertainty, out note);
            }
        }

        private static ComparisonVerdict CompareBoolean(string left, string right, out string note)
        {
            note = null;
            if (!TryBoolean(left, out bool a) || !TryBoolean(right, out bool b))
                return Invalid("boolean", left, right, out note);
            return a == b ? ComparisonVerdict.Same : ComparisonVerdict.Differs;
        }

        internal static bool TryBoolean(string text, out bool value)
        {
            switch (text)
            {
                case "1": case "True": case "true":  value = true;  return true;
                case "0": case "False": case "false": value = false; return true;
                default: value = false; return false;
            }
        }

        private static ComparisonVerdict CompareFloat(string left, ReplyCertainty leftCertainty, string right, ReplyCertainty rightCertainty, out string note)
        {
            note = null;
            if (!OdriveFloatText.TryParseFloat32(left, out float a) || !OdriveFloatText.TryParseFloat32(right, out float b))
                return Invalid("float32", left, right, out note);

            if (leftCertainty == ReplyCertainty.Exact && rightCertainty == ReplyCertainty.Exact)
            {
                if (float.IsNaN(a) || float.IsNaN(b))
                {
                    if (float.IsNaN(a) && float.IsNaN(b))
                    {
                        note = "both NaN";
                        return ComparisonVerdict.Same;
                    }
                    return ComparisonVerdict.Differs;
                }
                if (a != b) return ComparisonVerdict.Differs;
                if (a == 0.0f && IsNegative(a) != IsNegative(b)) note = "+0 and -0, the same value";
                else if (left != right) note = "the same float32, written differently";
                return ComparisonVerdict.Same;
            }

            if (left == right)
            {
                note = UncertainNote(leftCertainty, rightCertainty);
                return ComparisonVerdict.SameText;
            }
            // A non-finite reply is exact, so a number on the other side cannot be it.
            if (float.IsNaN(a) != float.IsNaN(b)) return ComparisonVerdict.Differs;
            if (float.IsInfinity(a) || float.IsInfinity(b) || float.IsNaN(a))
                return a.Equals(b) ? ComparisonVerdict.Same : ComparisonVerdict.Differs;
            if (leftCertainty == ReplyCertainty.MayBeCutOff || rightCertainty == ReplyCertainty.MayBeCutOff)
            {
                note = "a reply may be cut off";
                return ComparisonVerdict.Unclear;
            }

            double room = Room(left, leftCertainty) + Room(right, rightCertainty);
            if (Math.Abs((double)a - b) > room) return ComparisonVerdict.Differs;
            note = UncertainNote(leftCertainty, rightCertainty) + ", the difference is within the rounding";
            return ComparisonVerdict.Unclear;
        }

        private static double Room(string text, ReplyCertainty certainty)
            => certainty == ReplyCertainty.Exact ? 0.0 : OdriveFloatText.LastDigit(text);

        private static bool IsNegative(float value) => BitConverter.ToInt32(BitConverter.GetBytes(value), 0) < 0;

        private static ComparisonVerdict CompareInteger(ParameterDefinition definition, string left, ReplyCertainty leftCertainty,
                                                        string right, ReplyCertainty rightCertainty, out string note)
        {
            note = null;
            if (!TryInteger(left, out var a) || !TryInteger(right, out var b))
                return Invalid(definition.Type.ToString().ToLowerInvariant(), left, right, out note);

            bool leftCut = leftCertainty != ReplyCertainty.Exact, rightCut = rightCertainty != ReplyCertainty.Exact;
            if (!leftCut && !rightCut)
            {
                if (a != b) return ComparisonVerdict.Differs;
                if (left != right) note = "the same number, written differently";
                return ComparisonVerdict.Same;
            }

            if (left == right)
            {
                note = UncertainNote(leftCertainty, rightCertainty);
                return ComparisonVerdict.SameText;
            }
            // A prefix only says something about digits as the firmware writes them.
            if (left != a.ToString(CultureInfo.InvariantCulture) || right != b.ToString(CultureInfo.InvariantCulture))
            {
                note = "precision not proven, and a reply is written unusually";
                return ComparisonVerdict.Unclear;
            }
            // Older firmware kept the first 9 characters, so the whole number starts with a reply that may be cut. Where
            // both may be, either can be the start of the other.
            bool possible = leftCut && rightCut ? left.StartsWith(right, StringComparison.Ordinal) || right.StartsWith(left, StringComparison.Ordinal)
                          : leftCut ? right.StartsWith(left, StringComparison.Ordinal)
                          : left.StartsWith(right, StringComparison.Ordinal);
            if (!possible) return ComparisonVerdict.Differs;
            note = "a reply may be cut off, and one starts with the other";
            return ComparisonVerdict.Unclear;
        }

        internal static bool TryInteger(string text, out BigInteger value)
            => BigInteger.TryParse(text ?? "", NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

        private static ComparisonVerdict Invalid(string type, string left, string right, out string note)
        {
            note = "not a valid " + type + " on both sides, compared as text";
            return left == right ? ComparisonVerdict.SameText : ComparisonVerdict.Unclear;
        }

        private static string UncertainNote(ReplyCertainty left, ReplyCertainty right)
        {
            if (left == ReplyCertainty.MayBeCutOff || right == ReplyCertainty.MayBeCutOff) return "precision not proven, a reply may be cut off";
            return "precision not proven, a reply may be rounded";
        }
    }

    /// <summary>One side of a comparison: a fresh snapshot of the board, or a file. Plain data, nothing here talks to the board.</summary>
    public sealed class SnapshotSide
    {
        internal SnapshotSide(string name, IDictionary<string, string> values, IDictionary<string, string> notRead,
                              bool asciiFull, bool float32RoundTrip, string precision, IList<string> notes)
        {
            Name             = name;
            Values           = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(values, StringComparer.Ordinal));
            NotRead          = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(notRead, StringComparer.Ordinal));
            KeyOrder         = new ReadOnlyCollection<string>(values.Keys.ToList());
            AsciiFull        = asciiFull;
            Float32RoundTrip = float32RoundTrip;
            Precision        = precision;
            Notes            = new ReadOnlyCollection<string>(notes.ToList());
        }

        public string Name { get; }
        /// <summary>Path to the reply as it came, settings and status values together.</summary>
        public IReadOnlyDictionary<string, string> Values { get; }
        /// <summary>Paths this side tried and has no value for, with the reason.</summary>
        public IReadOnlyDictionary<string, string> NotRead { get; }
        internal IReadOnlyList<string> KeyOrder { get; }
        public bool AsciiFull { get; }
        public bool Float32RoundTrip { get; }
        /// <summary>What the precision of this side rests on, in words.</summary>
        public string Precision { get; }
        /// <summary>What else to know about this side: partial, metadata missing, and so on.</summary>
        public IReadOnlyList<string> Notes { get; }

        public ReplyCertainty CertaintyOf(ParameterDefinition definition, string reply)
            => OdriveReplyCertainty.Of(definition, reply, AsciiFull, Float32RoundTrip);

        public static SnapshotSide FromSnapshot(CatalogSnapshot snapshot, string name)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var notRead = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in snapshot.Entries)
            {
                if (entry.HasValue) values[entry.Path] = entry.Reply;
                else notRead[entry.Path] = OdriveSnapshotText.Outcome(entry);
            }
            var notes = new List<string>();
            if (snapshot.Completeness == SnapshotCompleteness.Partial) notes.Add("partial snapshot: " + snapshot.AbortReason);
            return new SnapshotSide(name, values, notRead, snapshot.Capabilities.AsciiFull, snapshot.Capabilities.Float32RoundTrip,
                                    OdriveSnapshotText.Precision(snapshot.Capabilities), notes);
        }
    }

    public sealed class ComparedValue
    {
        internal ComparedValue(string path, ParameterDefinition definition, SnapshotSection section, string left, string right,
                               ComparisonVerdict verdict, string note)
        {
            Path       = path;
            Definition = definition;
            Section    = section;
            Left       = left;
            Right      = right;
            Verdict    = verdict;
            Note       = note;
        }

        public string Path { get; }
        /// <summary>Null for a path the catalog does not know.</summary>
        public ParameterDefinition Definition { get; }
        public SnapshotSection Section { get; }
        public string Left { get; }
        public string Right { get; }
        public ComparisonVerdict Verdict { get; }
        public string Note { get; }
        public bool IsDifference => Verdict != ComparisonVerdict.Same && Verdict != ComparisonVerdict.SameText;
    }

    /// <summary>The result of comparing two sides. Settings and status values are kept apart: status values change on their own.</summary>
    public sealed class SnapshotComparison
    {
        private SnapshotComparison(SnapshotSide left, SnapshotSide right, IList<ComparedValue> values)
        {
            Left   = left;
            Right  = right;
            Values = new ReadOnlyCollection<ComparedValue>(values.ToList());
        }

        public SnapshotSide Left { get; }
        public SnapshotSide Right { get; }
        public IReadOnlyList<ComparedValue> Values { get; }
        public IEnumerable<ComparedValue> Configuration => Values.Where(v => v.Section == SnapshotSection.Configuration);
        public IEnumerable<ComparedValue> Status => Values.Where(v => v.Section == SnapshotSection.Status);

        /// <summary>Compares by path. The paths are data: nothing here is ever sent anywhere.</summary>
        public static SnapshotComparison Compare(SnapshotSide left, SnapshotSide right, OdriveParameterCatalog catalog)
        {
            var known = new HashSet<string>(catalog.Parameters.Select(p => p.Path), StringComparer.Ordinal);
            var paths = catalog.Parameters.Select(p => p.Path)
                .Where(p => left.Values.ContainsKey(p) || right.Values.ContainsKey(p) || left.NotRead.ContainsKey(p) || right.NotRead.ContainsKey(p))
                .Concat(left.KeyOrder.Concat(right.KeyOrder).Concat(left.NotRead.Keys).Concat(right.NotRead.Keys)
                                     .Where(p => !known.Contains(p)).Distinct(StringComparer.Ordinal))
                .ToList();

            var values = new List<ComparedValue>();
            foreach (var path in paths)
            {
                var definition = catalog.Find(path);
                var section = definition != null ? OdriveSnapshotCapture.SectionOf(definition) : SnapshotSection.Configuration;
                left.Values.TryGetValue(path, out var a);
                right.Values.TryGetValue(path, out var b);

                ComparisonVerdict verdict;
                string note = null;
                if (a != null && b != null)
                {
                    verdict = OdriveValueComparer.Compare(definition, a, left.CertaintyOf(definition, a), b, right.CertaintyOf(definition, b), out note);
                }
                else if (a == null && b == null)
                {
                    verdict = ComparisonVerdict.NotCompared;
                    note = string.Join("; ", new[] { left, right }.Where(s => s.NotRead.ContainsKey(path)).Select(s => s.Name + ": " + s.NotRead[path]));
                }
                else
                {
                    var missingSide = a == null ? left : right;
                    if (missingSide.NotRead.TryGetValue(path, out var why))
                    {
                        verdict = ComparisonVerdict.NotCompared;
                        note = missingSide.Name + ": " + why;
                    }
                    else
                    {
                        verdict = a == null ? ComparisonVerdict.OnlyRight : ComparisonVerdict.OnlyLeft;
                        if (definition == null) note = "not in the plugin's catalog";
                    }
                }
                values.Add(new ComparedValue(path, definition, section, a, b, verdict, note));
            }
            return new SnapshotComparison(left, right, values);
        }
    }
}
