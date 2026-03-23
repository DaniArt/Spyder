using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using Spy.Core;

namespace Spy.UI;

internal static class LocatorStorage
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null,
        DictionaryKeyPolicy = null
    };
    static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    static readonly string LocatorLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Spyder",
        "logs",
        "locator-save-errors.log");

    internal class LocatorFileModel
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; } = 1;

        [JsonPropertyName("elements")]
        public Dictionary<string, LocatorElement> Elements { get; set; } = new();
    }

    internal class LocatorElement
    {
        // Новый формат
        [JsonPropertyName("human_selector")]
        public string? HumanSelector { get; set; }

        [JsonPropertyName("locator")]
        public StructuredLocator? Locator { get; set; }

        // Обратная совместимость со старым форматом
        [JsonPropertyName("selector")]
        public string? Selector { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTime UpdatedAt { get; set; }
    }

    public static LocatorFileModel Load(string path)
    {
        if (!File.Exists(path))
            return new LocatorFileModel();

        try
        {
            var json = File.ReadAllText(path);
            var model = JsonSerializer.Deserialize<LocatorFileModel>(json, Options);
            return model ?? new LocatorFileModel();
        }
        catch
        {
            MessageBox.Show("Failed to read locator file.", "Locator file", MessageBoxButton.OK, MessageBoxImage.Warning);
            return new LocatorFileModel();
        }
    }

    public static void Save(string path, LocatorFileModel model)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var tmp = path + ".tmp";
            var json = JsonSerializer.Serialize(model, Options);
            try
            {
                using var doc = JsonDocument.Parse(json);
                var roundTrip = JsonSerializer.Deserialize<LocatorFileModel>(json, Options);
                if (roundTrip == null || roundTrip.Elements == null)
                    throw new JsonException("Round-trip validation failed: deserialized model is null.");
            }
            catch (JsonException jex)
            {
                LogLocatorSaveError(path, jex);
                MessageBox.Show(
                    $"Locator JSON validation failed. File was not written.\n\n{FormatJsonException(jex)}",
                    "Locator file",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            File.WriteAllText(tmp, json, Utf8NoBom);
            File.Move(tmp, path, true);
        }
        catch (Exception ex)
        {
            LogLocatorSaveError(path, ex);
            MessageBox.Show(ex.Message, "Save locator file error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    static string FormatJsonException(JsonException ex)
    {
        var loc = ex.LineNumber.HasValue && ex.BytePositionInLine.HasValue
            ? $"(line {ex.LineNumber.Value + 1}, byte {ex.BytePositionInLine.Value + 1})"
            : "";
        var path = string.IsNullOrWhiteSpace(ex.Path) ? "" : $" path={ex.Path}";
        return $"{ex.Message} {loc}{path}".Trim();
    }

    static void LogLocatorSaveError(string locatorPath, Exception ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(LocatorLogPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var msg =
                $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z [locator-save] path={locatorPath}\n" +
                ex + "\n\n";
            File.AppendAllText(LocatorLogPath, msg, Utf8NoBom);
        }
        catch
        {
        }
    }

    public static string SerializeLocatorOnly(StructuredLocator locator)
        => JsonSerializer.Serialize(locator, Options);

    public static string SerializeLocatorElement(LocatorElement element)
        => JsonSerializer.Serialize(element, Options);

    public static void RunLocatorJsonSelfCheck()
    {
        var model = new LocatorFileModel();

        var a = new LocatorElement
        {
            HumanSelector = "Process(\"A\") -> VCL(\"MainForm\") -> VCL(\"btn\\\"Login\\\"\")",
            Locator = new StructuredLocator(
                BackendPriority: new[] { "vcl", "uia", "win32", "msaa" },
                Process: "ExampleApp \"quotes\" \\ slash \n line",
                Window: null,
                Chain: null,
                VclChain: new[] { "MainForm", "pnlClient", "btn\"Login\"" },
                UIA: new LocatorUia("Button", "id\"1", "TButton", "Login\nName"),
                Win32: new LocatorWin32("Button", "Text \"x\""),
                Leaf: new LocatorLeaf("Window", "TButton", "t", 1)
            ),
            Selector = null,
            Description = "desc: unicode ✓, tabs\t, newlines\n",
            UpdatedAt = DateTime.UtcNow
        };

        var b = new LocatorElement
        {
            HumanSelector = "Window(TitleContains(\"[test]\")) -> UIA(Name(\"x\"))",
            Locator = new StructuredLocator(
                BackendPriority: new[] { "uia", "win32", "msaa" },
                Process: "ExampleApp",
                Window: new LocatorWindow("Title \"X\"", "TForm", "Contains \"Y\""),
                Chain: new[]
                {
                    new SlStep("uia", "a\"b", "n\\m", "cls", "Button", null, null, null),
                    new SlStep("win32", null, "caption", "Button", null, null, null, 0)
                }
            ),
            Selector = "legacy",
            Description = null,
            UpdatedAt = DateTime.UtcNow
        };

        model.Elements["a"] = a;
        model.Elements["b"] = b;

        var json = JsonSerializer.Serialize(model, Options);
        using var doc = JsonDocument.Parse(json);
        var roundTrip = JsonSerializer.Deserialize<LocatorFileModel>(json, Options);
        if (roundTrip == null || roundTrip.Elements.Count != 2)
            throw new InvalidOperationException("Locator JSON self-check failed: round-trip mismatch.");
    }
}

