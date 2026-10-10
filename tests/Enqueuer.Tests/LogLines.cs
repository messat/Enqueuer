using System.Collections.Generic;
using System.Text.Json;
using NUnit.Framework;

namespace Enqueuer.Tests
{
    // The enqueuer's console output outside Development, one CLEF JSON object per line (#112, ENQ-ADR-002), read back so a
    // test can check a line's level, message and properties as a Log Analytics search would. Copied from RepoScanner's
    // tests/RepoScanner.Scanner.Tests/LogLines.cs, as ConsoleLogFormat is (Issue 4 design, decision 4).
    internal static class LogLines
    {
        // Every non-empty line, parsed. A line that is not one JSON object fails the test, naming its number, since
        // Log Analytics could not unpack it either.
        public static IReadOnlyList<JsonElement> Read(string output)
        {
            List<JsonElement> lines = new List<JsonElement>();
            string[] texts = output.Split('\n');

            for (int index = 0; index < texts.Length; index++)
            {
                string text = texts[index].TrimEnd('\r');
                if (text.Length == 0)
                {
                    continue;
                }

                try
                {
                    using (JsonDocument document = JsonDocument.Parse(text))
                    {
                        if (document.RootElement.ValueKind != JsonValueKind.Object)
                        {
                            Assert.Fail("Line " + (index + 1) + " is JSON but not an object: " + text);
                        }

                        lines.Add(document.RootElement.Clone());
                    }
                }
                catch (JsonException exception)
                {
                    Assert.Fail("Line " + (index + 1) + " is not JSON (" + exception.Message + "): " + text);
                }
            }

            return lines;
        }

        // CLEF leaves the level out for Information, the most common one
        public static string Level(JsonElement line)
        {
            return line.TryGetProperty("@l", out JsonElement level) ? level.GetString()! : "Information";
        }

        // The finished message, with string values in quotes, as Serilog renders it
        public static string Message(JsonElement line)
        {
            return line.GetProperty("@m").GetString()!;
        }

        // A top-level property: a string as it is, a number or object as its JSON. Null when the line has no such
        // property, or it is JSON null.
        public static string? Value(JsonElement line, string name)
        {
            if (!line.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        }

        // Every string value anywhere in the line, unescaped, for checks that must not be fooled by JSON's escaping
        public static IEnumerable<string> Strings(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                yield return element.GetString()!;
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    foreach (string text in Strings(property.Value))
                    {
                        yield return text;
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    foreach (string text in Strings(item))
                    {
                        yield return text;
                    }
                }
            }
        }
    }
}
