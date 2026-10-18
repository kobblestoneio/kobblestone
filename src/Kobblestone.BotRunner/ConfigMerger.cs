using Tomlyn;
using Tomlyn.Model;

namespace Kobblestone.BotRunner;

/// <summary>
/// Merges the configuration layers of a `Bot` into one MCC <c>MinecraftClient.ini</c> (TOML).
/// </summary>
/// <remarks>
/// Layers are applied in ordinal file name order (<c>00-base.toml</c>, <c>10-inline.toml</c>, <c>20-from-0.toml</c>,
/// ..., <c>99-managed.toml</c>), later layers win:
/// <list type="bullet">
/// <item>Tables are merged recursively.</item>
/// <item>Arrays of tables (e.g. <c>[[ChatBot.ScriptScheduler.TaskList]]</c>) are concatenated, so tasks from several
/// layers are all kept.</item>
/// <item>Everything else is replaced.</item>
/// </list>
/// </remarks>
public static class ConfigMerger
{
    private static readonly TomlSerializerOptions Options = new() { WriteIndented = true };

    public static TomlTable MergeDirectory(string directory)
    {
        var result = new TomlTable();

        if (!Directory.Exists(directory))
        {
            return result;
        }

        foreach (var file in Directory.GetFiles(directory, "*.toml").Order(StringComparer.Ordinal))
        {
            var text = File.ReadAllText(file);

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (!TomlSerializer.TryDeserialize(text, out TomlTable? layer) || layer is null)
            {
                throw new InvalidDataException($"Configuration layer {Path.GetFileName(file)} is not valid TOML.");
            }

            Merge(result, layer);
        }

        return result;
    }

    public static void Merge(TomlTable target, TomlTable source)
    {
        foreach (var (key, value) in source)
        {
            switch (value)
            {
                case TomlTable sourceTable when target.TryGetValue(key, out var existing) &&
                                                existing is TomlTable targetTable:
                    Merge(targetTable, sourceTable);
                    break;
                case TomlTableArray sourceArray when target.TryGetValue(key, out var existing) &&
                                                     existing is TomlTableArray targetArray:
                    foreach (var table in sourceArray)
                    {
                        targetArray.Add(table);
                    }

                    break;
                default:
                    target[key] = value;
                    break;
            }
        }
    }

    public static string Serialize(TomlTable table) => TomlSerializer.Serialize(table, Options);
}
