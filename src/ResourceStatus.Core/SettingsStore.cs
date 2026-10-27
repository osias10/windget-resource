using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResourceStatus.Core;

/// <summary>Local settings written by replacing a file on the same volume.</summary>
public sealed class SettingsStore(string path)
{
    private readonly string _path = Path.GetFullPath(path);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new TolerantEnumConverterFactory() }
    };

    /// <summary>True after an unreadable Load until the next Save preserves the existing file.</summary>
    public bool LastLoadFailed { get; private set; }

    public AppSettings Load()
    {
        if (!File.Exists(_path)) return new AppSettings();
        string json;
        try { json = ReadWithRetry(); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new AppSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LastLoadFailed = true;
            return new AppSettings();
        }
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                ?? throw new JsonException("Settings must contain a JSON object.");
            DisplayCatalog.Normalize(settings);
            // Missing profile properties use constructor defaults during deserialization.
            // Inspect the saved document too, so even partial legacy settings keep Disk hidden.
            using var document = JsonDocument.Parse(json);
            foreach (var mode in Enum.GetValues<WidgetMode>())
            {
                var items = Property(Property(Property(document.RootElement, "DisplayProfiles"), mode.ToString()), "Items");
                if (Property(items, "Disk").ValueKind == JsonValueKind.Undefined && Property(items, ((int)MetricKind.Disk).ToString(CultureInfo.InvariantCulture)).ValueKind == JsonValueKind.Undefined)
                {
                    var profile = settings.DisplayProfiles?.For(mode);
                    if (profile?.Items is not null && profile.Items.TryGetValue(MetricKind.Disk, out var disk) && disk is not null) disk.Visible = false;
                }
            }
            return settings;
        }
        catch (JsonException)
        {
            BackupMalformedFile();
            return new AppSettings();
        }
    }

    private static JsonElement Property(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }

    private string ReadWithRetry()
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return File.ReadAllText(_path); }
            catch (Exception exception) when (attempt < 3 && exception is (IOException or UnauthorizedAccessException)
                and not (FileNotFoundException or DirectoryNotFoundException))
            {
                Thread.Sleep(100);
            }
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }
            // After an unreadable Load the existing file is kept beside the new one instead of being lost.
            var backup = LastLoadFailed ? $"{_path}.unreadable-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json" : null;
            if (File.Exists(_path)) File.Replace(temporary, _path, backup);
            else File.Move(temporary, _path);
            LastLoadFailed = false;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void BackupMalformedFile()
    {
        try
        {
            File.Copy(_path, $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json", overwrite: false);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Writes enum names; reads names case-insensitively or numbers, and maps unknown names to an
    /// undefined value so <see cref="DisplayCatalog.Normalize"/> restores that value or drops that key.</summary>
    private sealed class TolerantEnumConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(TolerantEnumConverter<>).MakeGenericType(typeToConvert))!;
    }

    private sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        private static readonly T Unknown = (T)Enum.ToObject(typeof(T), -1);

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.String => Parse(reader.GetString()),
                JsonTokenType.Number when reader.TryGetInt64(out var number) => (T)Enum.ToObject(typeof(T), number),
                _ => throw new JsonException($"Expected a {typeof(T).Name} name or number.")
            };

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            if (Enum.IsDefined(value)) writer.WriteStringValue(value.ToString());
            else writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
        }

        public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Parse(reader.GetString());

        public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WritePropertyName(Enum.IsDefined(value) ? value.ToString()
                : Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));

        private static T Parse(string? text) =>
            text is not null && !text.Contains(',') && Enum.TryParse<T>(text, ignoreCase: true, out var value) ? value : Unknown;
    }
}
