using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Nexus.Editor.Ios
{
    /// <summary>
    /// A small reader and text editor for XML property lists (the Xcode export's <c>Info.plist</c>, a <c>PrivacyInfo.xcprivacy</c>),
    /// free of UnityEditor and of the Xcode API so the rules built on it run in the host harness (#436, #434). Values read back are
    /// <c>Dictionary&lt;string, object&gt;</c>, <c>List&lt;object&gt;</c>, <c>string</c>, <c>bool</c> and <c>long</c>; anything else
    /// (date, data, real) is kept as its text. Edits are done on the text, so the file's DOCTYPE and layout stay as Unity wrote them.
    /// A binary plist is not supported: convert it first (<c>plutil -convert xml1</c>).
    /// </summary>
    public static class PlistXml
    {
        /// <summary>The root dictionary of a property list; throws <see cref="FormatException"/> when the text is not an XML plist with a dict root.</summary>
        public static Dictionary<string, object> ReadRoot(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) throw new FormatException("The property list is empty.");
            var document = new XmlDocument { XmlResolver = null };
            try
            {
                using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
                {
                    document.Load(reader);
                }
            }
            catch (XmlException ex)
            {
                throw new FormatException("The property list is not XML (a binary plist must be converted with plutil first): " + ex.Message, ex);
            }

            var root = document.DocumentElement;
            if (root == null || root.Name != "plist") throw new FormatException("The XML has no <plist> root.");
            var dict = FirstElement(root);
            if (dict == null || dict.Name != "dict") throw new FormatException("The <plist> root does not hold a <dict>.");
            return (Dictionary<string, object>)ReadValue(dict);
        }

        private static XmlElement FirstElement(XmlElement parent)
        {
            foreach (XmlNode child in parent.ChildNodes)
            {
                if (child is XmlElement element) return element;
            }
            return null;
        }

        private static object ReadValue(XmlElement element)
        {
            switch (element.Name)
            {
                case "dict":
                {
                    var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                    string key = null;
                    foreach (XmlNode child in element.ChildNodes)
                    {
                        if (!(child is XmlElement item)) continue;
                        if (item.Name == "key") key = item.InnerText;
                        else if (key != null) { dict[key] = ReadValue(item); key = null; }
                    }
                    return dict;
                }
                case "array":
                {
                    var list = new List<object>();
                    foreach (XmlNode child in element.ChildNodes)
                    {
                        if (child is XmlElement item) list.Add(ReadValue(item));
                    }
                    return list;
                }
                case "true": return true;
                case "false": return false;
                case "integer": return long.TryParse(element.InnerText.Trim(), out long number) ? number : (object)element.InnerText;
                default: return element.InnerText;
            }
        }

        /// <summary>True when the root dictionary of <paramref name="xml"/> holds <paramref name="key"/>.</summary>
        public static bool HasKey(string xml, string key) => ReadRoot(xml).ContainsKey(key);

        /// <summary>Adds <c>&lt;key&gt;k&lt;/key&gt;&lt;true/&gt;</c> or <c>&lt;false/&gt;</c> to the root dictionary unless the key exists; returns whether it added.</summary>
        public static bool AddBoolIfAbsent(ref string xml, string key, bool value)
            => AddIfAbsent(ref xml, key, value ? "<true/>" : "<false/>");

        /// <summary>Adds a string entry unless the key exists; returns whether it added.</summary>
        public static bool AddStringIfAbsent(ref string xml, string key, string value)
            => AddIfAbsent(ref xml, key, "<string>" + Escape(value) + "</string>");

        /// <summary>Sets a string entry, replacing the existing value; returns whether the text changed.</summary>
        public static bool SetString(ref string xml, string key, string value)
        {
            var root = ReadRoot(xml);
            if (root.TryGetValue(key, out object current) && current is string text && text == value) return false;
            RemoveKey(ref xml, key);
            AddIfAbsent(ref xml, key, "<string>" + Escape(value) + "</string>");
            return true;
        }

        /// <summary>Sets an array of strings, replacing the existing array; returns whether the text changed.</summary>
        public static bool SetStringArray(ref string xml, string key, IReadOnlyList<string> values)
        {
            var root = ReadRoot(xml);
            if (root.TryGetValue(key, out object current) && current is List<object> list && list.Count == values.Count)
            {
                bool same = true;
                for (int i = 0; i < values.Count; i++) same &= list[i] is string s && s == values[i];
                if (same) return false;
            }
            RemoveKey(ref xml, key);
            var builder = new StringBuilder("<array>");
            foreach (string value in values) builder.Append("<string>").Append(Escape(value)).Append("</string>");
            AddIfAbsent(ref xml, key, builder.Append("</array>").ToString());
            return true;
        }

        private static readonly string[] ScalarPatterns =
        {
            @"<string>[^<]*</string>", @"<string\s*/>", @"<true\s*/>", @"<false\s*/>", @"<integer>[^<]*</integer>", @"<real>[^<]*</real>",
            @"<array\s*/>", @"<array>(?:\s*<string>[^<]*</string>|\s*<string\s*/>)*\s*</array>"
        };

        /// <summary>
        /// Removes a root entry whose value is a scalar or an array of strings; returns whether the key was there. Any other value shape
        /// (a nested dict) throws <see cref="NotSupportedException"/> rather than cut the wrong text.
        /// </summary>
        public static bool RemoveKey(ref string xml, string key)
        {
            if (!ReadRoot(xml).ContainsKey(key)) return false;
            string keyPattern = @"[ \t]*<key>" + Regex.Escape(Escape(key)) + @"</key>\s*";
            foreach (string value in ScalarPatterns)
            {
                var match = Regex.Match(xml, keyPattern + value + @"[ \t]*\r?\n?");
                if (match.Success)
                {
                    xml = xml.Remove(match.Index, match.Length);
                    return true;
                }
            }
            throw new NotSupportedException("The value of " + key + " is a shape this editor does not remove (only scalars and arrays of strings).");
        }

        private static bool AddIfAbsent(ref string xml, string key, string valueXml)
        {
            if (ReadRoot(xml).ContainsKey(key)) return false;
            // The root dictionary closes at the last </dict> of the file (nested dicts close earlier).
            int close = xml.LastIndexOf("</dict>", StringComparison.Ordinal);
            if (close < 0) throw new FormatException("The property list has no closing </dict>.");
            int lineStart = xml.LastIndexOf('\n', close) + 1;
            string indent = xml.Substring(lineStart, close - lineStart).Trim().Length == 0 ? xml.Substring(lineStart, close - lineStart) : string.Empty;
            string newline = xml.Contains("\r\n") ? "\r\n" : "\n";
            string entry = indent + "\t<key>" + Escape(key) + "</key>" + newline + indent + "\t" + valueXml + newline;
            xml = indent.Length > 0 || lineStart == close
                ? xml.Insert(lineStart, entry)
                : xml.Insert(close, newline + entry.TrimEnd('\r', '\n') + newline);
            return true;
        }

        /// <summary>XML text escaping for a plist key or string.</summary>
        public static string Escape(string text) => System.Security.SecurityElement.Escape(text ?? string.Empty);
    }
}
