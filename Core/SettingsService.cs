using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RGDSCapture.Core
{
    /// <summary>
    /// Source-generated serializer for <see cref="AppSettings"/>.
    ///
    /// The reflection-based JsonSerializer overloads cannot survive trimming:
    /// the linker has no way to see which types they will reach, so it strips
    /// the metadata they need and settings silently stop loading in a trimmed
    /// build. Generating the contract at compile time makes it visible to the
    /// linker, and skips the reflection at startup as well.
    /// </summary>
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(AppSettings))]
    internal sealed partial class SettingsJsonContext : JsonSerializerContext;

    /// <summary>
    /// Loads and saves <see cref="AppSettings"/>. All failures are non-fatal:
    /// a corrupt or missing file simply yields defaults.
    /// </summary>
    public sealed class SettingsService
    {
        public AppSettings Current { get; private set; } = new();

        public AppSettings Load()
        {
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                {
                    var loaded = JsonSerializer.Deserialize(
                        File.ReadAllText(AppPaths.SettingsFile),
                        SettingsJsonContext.Default.AppSettings);
                    if (loaded != null) Current = loaded;
                }
            }
            catch
            {
                Current = new AppSettings();
            }
            return Current;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.SettingsDir);
                // Write to a temp file first so a crash mid-write can't corrupt settings.
                string tmp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(
                    Current, SettingsJsonContext.Default.AppSettings));
                File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
            }
            catch
            {
                // Persistence is best-effort; never crash over settings.
            }
        }
    }
}
