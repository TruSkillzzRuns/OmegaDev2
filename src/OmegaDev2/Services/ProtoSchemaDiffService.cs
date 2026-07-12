using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace OmegaDev2.Services;

// Parses MHServerEmu's generated GameServerToClient.cs (Google.ProtocolBuffers-style)
// and extracts each NetMessage's fields with their tag numbers and types.
//
// The generated layout (verified by reading MHServerEmu's actual output):
//
//   public sealed partial class NetMessageFoo : ...
//   {
//       private static readonly string[] _netMessageFooFieldNames = new string[] { "fieldA", "fieldB" };
//       private static readonly uint[] _netMessageFooFieldTags = new uint[] { 8, 16 };
//       ...
//   }
//
// Tag = (fieldNumber << 3) | wireType. So tag 8 = field 1 wire 0, tag 16 = field 2 wire 0,
// tag 24 = field 3 wire 0, tag 10 = field 1 wire 2 (length-delim), etc.
public static class ProtoSchemaDiffService
{
    public sealed class MessageSchema
    {
        public string Name { get; set; } = "";
        public List<FieldSchema> Fields { get; set; } = new();
    }

    public sealed class FieldSchema
    {
        public string Name { get; set; } = "";
        public uint Tag { get; set; }
        public int FieldNumber => (int)(Tag >> 3);
        public int WireType => (int)(Tag & 7);
        public string WireTypeName => WireType switch
        {
            0 => "varint",
            1 => "fixed64",
            2 => "len-delim",
            3 => "start-group",
            4 => "end-group",
            5 => "fixed32",
            _ => $"?{WireType}",
        };
    }

    public sealed class FieldDiff
    {
        public string FieldName { get; set; } = "";
        public uint? OldTag { get; set; }
        public uint? NewTag { get; set; }
        public string ChangeKind { get; set; } = "";  // "added" / "removed" / "renumbered" / "wire-type-changed" / "renamed"
    }

    public sealed class MessageDiff
    {
        public string MessageName { get; set; } = "";
        public string ChangeKind { get; set; } = "";  // "added" / "removed" / "modified" / "unchanged"
        public List<FieldDiff> FieldChanges { get; set; } = new();
    }

    public static Dictionary<string, MessageSchema> Parse(string filePath)
    {
        var result = new Dictionary<string, MessageSchema>(StringComparer.Ordinal);
        var text = File.ReadAllText(filePath);

        // Find each NetMessage class
        var classRegex = new Regex(@"public\s+sealed\s+partial\s+class\s+(NetMessage\w+)\b", RegexOptions.Compiled);
        foreach (Match m in classRegex.Matches(text))
        {
            var name = m.Groups[1].Value;
            // Find the FieldNames + FieldTags arrays that follow
            var nameArrayRegex = new Regex(@"_" + Regex.Escape(LowerFirst(name)) + @"FieldNames\s*=\s*new\s+string\[\]\s*\{\s*([^}]*)\}", RegexOptions.Compiled);
            var tagArrayRegex  = new Regex(@"_" + Regex.Escape(LowerFirst(name)) + @"FieldTags\s*=\s*new\s+uint\[\]\s*\{\s*([^}]*)\}", RegexOptions.Compiled);

            var nameMatch = nameArrayRegex.Match(text, m.Index);
            var tagMatch  = tagArrayRegex.Match(text, m.Index);
            if (!nameMatch.Success || !tagMatch.Success) continue;

            var fieldNames = SplitStringLiterals(nameMatch.Groups[1].Value);
            var tags       = SplitUInts(tagMatch.Groups[1].Value);
            if (fieldNames.Count != tags.Count) continue;

            var schema = new MessageSchema { Name = name };
            for (int i = 0; i < fieldNames.Count; i++)
                schema.Fields.Add(new FieldSchema { Name = fieldNames[i], Tag = tags[i] });
            result[name] = schema;
        }
        return result;
    }

    public static List<MessageDiff> Diff(Dictionary<string, MessageSchema> oldSet, Dictionary<string, MessageSchema> newSet, bool includeUnchanged = false)
    {
        var diffs = new List<MessageDiff>();
        var allNames = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var k in oldSet.Keys) allNames.Add(k);
        foreach (var k in newSet.Keys) allNames.Add(k);

        foreach (var name in allNames)
        {
            var inOld = oldSet.TryGetValue(name, out var oldMsg);
            var inNew = newSet.TryGetValue(name, out var newMsg);
            if (!inOld && inNew) { diffs.Add(new MessageDiff { MessageName = name, ChangeKind = "added" }); continue; }
            if (inOld && !inNew) { diffs.Add(new MessageDiff { MessageName = name, ChangeKind = "removed" }); continue; }

            var fieldChanges = DiffFields(oldMsg!, newMsg!);
            if (fieldChanges.Count > 0)
                diffs.Add(new MessageDiff { MessageName = name, ChangeKind = "modified", FieldChanges = fieldChanges });
            else if (includeUnchanged)
                diffs.Add(new MessageDiff { MessageName = name, ChangeKind = "unchanged" });
        }
        return diffs;
    }

    private static List<FieldDiff> DiffFields(MessageSchema oldMsg, MessageSchema newMsg)
    {
        var changes = new List<FieldDiff>();
        var oldByName = new Dictionary<string, FieldSchema>(StringComparer.Ordinal);
        var newByName = new Dictionary<string, FieldSchema>(StringComparer.Ordinal);
        foreach (var f in oldMsg.Fields) oldByName[f.Name] = f;
        foreach (var f in newMsg.Fields) newByName[f.Name] = f;

        foreach (var f in oldMsg.Fields)
        {
            if (!newByName.TryGetValue(f.Name, out var nf))
                changes.Add(new FieldDiff { FieldName = f.Name, OldTag = f.Tag, ChangeKind = "removed" });
            else if (nf.FieldNumber != f.FieldNumber)
                changes.Add(new FieldDiff { FieldName = f.Name, OldTag = f.Tag, NewTag = nf.Tag, ChangeKind = "renumbered" });
            else if (nf.WireType != f.WireType)
                changes.Add(new FieldDiff { FieldName = f.Name, OldTag = f.Tag, NewTag = nf.Tag, ChangeKind = "wire-type-changed" });
        }
        foreach (var f in newMsg.Fields)
        {
            if (!oldByName.ContainsKey(f.Name))
                changes.Add(new FieldDiff { FieldName = f.Name, NewTag = f.Tag, ChangeKind = "added" });
        }
        return changes;
    }

    private static string LowerFirst(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s[1..];

    private static List<string> SplitStringLiterals(string body)
    {
        var list = new List<string>();
        foreach (Match m in Regex.Matches(body, @"""([^""]*)"""))
            list.Add(m.Groups[1].Value);
        return list;
    }

    private static List<uint> SplitUInts(string body)
    {
        var list = new List<uint>();
        foreach (var token in body.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            // Tags can appear as "8", "8u", "0x10", etc.
            var t = token.TrimEnd('u', 'U', 'l', 'L');
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (uint.TryParse(t[2..], System.Globalization.NumberStyles.HexNumber, null, out var hv)) list.Add(hv);
            }
            else if (uint.TryParse(t, out var v)) list.Add(v);
        }
        return list;
    }
}
