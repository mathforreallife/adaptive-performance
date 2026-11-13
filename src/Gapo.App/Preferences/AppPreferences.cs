using System.Text.Json;
using Gapo.App.Overlay;
using Microsoft.UI.Xaml;

namespace Gapo.App.Preferences;

public sealed record AppPreferences
{
    public bool OverlayEnabled { get; init; } = true;

    public OverlayCorner OverlayCorner { get; init; } = OverlayCorner.TopLeft;

    public bool OverlayShowsBeforeAndAfter { get; init; } = true;

    public bool NotifyWhenAway { get; init; } = true;

    public bool FlashTaskbarWhenAway { get; init; } = true;

    public bool StartAgentAutomatically { get; init; } = true;

    public ElementTheme Theme { get; init; } = ElementTheme.Default;

    public bool AskBeforeEachChange { get; init; }

    public bool AllowFrameCap { get; init; } = true;

    public int TargetFps { get; init; }

    public int MostRestarts { get; init; } = 6;
}

public sealed class PreferenceStore
{
    private readonly string path;

    public PreferenceStore(string path)
    {
        this.path = path;
        Current = Load();
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GAPO", "preferences.json");

    public AppPreferences Current { get; private set; }

    public void Save(AppPreferences preferences)
    {
        Current = preferences;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
                json.WriteStartObject();
                json.WriteBoolean("overlayEnabled", preferences.OverlayEnabled);
                json.WriteString("overlayCorner", preferences.OverlayCorner.ToString());
                json.WriteBoolean("overlayShowsBeforeAndAfter", preferences.OverlayShowsBeforeAndAfter);
                json.WriteBoolean("notifyWhenAway", preferences.NotifyWhenAway);
                json.WriteBoolean("flashTaskbarWhenAway", preferences.FlashTaskbarWhenAway);
                json.WriteBoolean("startAgentAutomatically", preferences.StartAgentAutomatically);
                json.WriteString("theme", preferences.Theme.ToString());
                json.WriteBoolean("askBeforeEachChange", preferences.AskBeforeEachChange);
                json.WriteBoolean("allowFrameCap", preferences.AllowFrameCap);
                json.WriteNumber("targetFps", preferences.TargetFps);
                json.WriteNumber("mostRestarts", preferences.MostRestarts);
                json.WriteEndObject();
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private AppPreferences Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return new AppPreferences();
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var defaults = new AppPreferences();
            return new AppPreferences
            {
                OverlayEnabled = Flag(root, "overlayEnabled", defaults.OverlayEnabled),
                OverlayCorner = Enum.TryParse<OverlayCorner>(Text(root, "overlayCorner"), out var corner) ? corner : defaults.OverlayCorner,
                OverlayShowsBeforeAndAfter = Flag(root, "overlayShowsBeforeAndAfter", defaults.OverlayShowsBeforeAndAfter),
                NotifyWhenAway = Flag(root, "notifyWhenAway", defaults.NotifyWhenAway),
                FlashTaskbarWhenAway = Flag(root, "flashTaskbarWhenAway", defaults.FlashTaskbarWhenAway),
                StartAgentAutomatically = Flag(root, "startAgentAutomatically", defaults.StartAgentAutomatically),
                Theme = Enum.TryParse<ElementTheme>(Text(root, "theme"), out var theme) ? theme : defaults.Theme,
                AskBeforeEachChange = Flag(root, "askBeforeEachChange", defaults.AskBeforeEachChange),
                AllowFrameCap = Flag(root, "allowFrameCap", defaults.AllowFrameCap),
                TargetFps = Whole(root, "targetFps", defaults.TargetFps),
                MostRestarts = Whole(root, "mostRestarts", defaults.MostRestarts),
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppPreferences();
        }
    }

    private static bool Flag(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;

    private static int Whole(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 0 ? number : fallback;

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
}
