using System.Text.Json;

namespace WiiCompiled.SimpleUI;

/// <summary>One "My Stuff" slot: a folder under MyStuffService.RootDirectory whose files replace
/// the game's own by filename (see runtime/src/hle/storage/riivolution.cpp's RiivoDiscoverRoots).
/// FolderId is the stable, never-renamed folder name on disk; Name is the user-facing label shown
/// in MyStuffForm's list, editable independently so a rename never needs to touch (or risk failing
/// to rename) a folder that might be open in Explorer or mid-copy.</summary>
internal sealed class MyStuffSlot
{
    public string Name { get; set; } = "";
    public string FolderId { get; set; } = "";
    public bool Enabled { get; set; }
}

internal sealed class MyStuffLibrary
{
    /// <summary>Master switch: when false, no slot is layered in even if individually enabled -
    /// a single kill-switch for the whole feature without unchecking every slot.</summary>
    public bool Enabled { get; set; }
    public List<MyStuffSlot> Slots { get; set; } = new();
}

/// <summary>
/// "My Stuff" - SimpleUI's own Riivolution-style file layering, always highest priority (above
/// Config.toml's own overlay_roots, above Retro Rewind's own pack, above the base disc - see
/// riivolution.cpp). A file dropped anywhere under a slot's folder replaces the game's own file
/// that shares its filename, regardless of which folder that file actually lives in on the disc -
/// no need to mirror the disc's own folder structure. Purely a boot-time runtime read; unlike a
/// code-target cheat this never needs a recompile, only relaunching the game.
/// </summary>
internal static class MyStuffService
{
    public static string RootDirectory(string installDirectory) =>
        Path.Combine(installDirectory, "MyStuff");

    public static string SlotDirectory(string installDirectory, MyStuffSlot slot) =>
        Path.Combine(RootDirectory(installDirectory), slot.FolderId);

    private static string LibraryPath(string installDirectory) =>
        Path.Combine(installDirectory, "mystuff-library.json");

    private static readonly JsonSerializerOptions LibraryJsonOptions = new() { WriteIndented = true };

    public static MyStuffLibrary Load(string installDirectory)
    {
        var path = LibraryPath(installDirectory);
        if (!File.Exists(path))
        {
            return new MyStuffLibrary();
        }
        try
        {
            return JsonSerializer.Deserialize<MyStuffLibrary>(File.ReadAllText(path)) ?? new MyStuffLibrary();
        }
        catch (JsonException)
        {
            return new MyStuffLibrary();
        }
    }

    public static void Save(string installDirectory, MyStuffLibrary library)
    {
        var path = LibraryPath(installDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(library, LibraryJsonOptions));
    }

    /// <summary>Next "SlotN" folder id that doesn't collide with an existing slot - stable and
    /// never reused once assigned, so removing then re-adding a slot never silently inherits an
    /// old folder's leftover contents (Remove never deletes the folder - see MyStuffForm).</summary>
    public static string NextFolderId(IReadOnlyList<MyStuffSlot> existing)
    {
        var taken = new HashSet<string>(existing.Select(s => s.FolderId), StringComparer.OrdinalIgnoreCase);
        var n = 1;
        while (taken.Contains($"Slot{n}"))
        {
            n++;
        }
        return $"Slot{n}";
    }

    public static string NextDefaultName(IReadOnlyList<MyStuffSlot> existing)
    {
        var taken = new HashSet<string>(existing.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        var n = 1;
        while (taken.Contains($"My Stuff {n}"))
        {
            n++;
        }
        return $"My Stuff {n}";
    }

    /// <summary>Writes [my_stuff] roots = [...] as one line - mirrors CheatsService.WriteDataCodes'
    /// approach for [cheats] codes exactly, leaving every other section/key untouched. Only slots
    /// that are both individually enabled and covered by the library's own master switch are
    /// written, in list order (index 0 is highest priority on the runtime side).</summary>
    public static void WriteConfig(string configTomlPath, string installDirectory, MyStuffLibrary library)
    {
        var activePaths = library.Enabled
            ? library.Slots.Where(s => s.Enabled).Select(s => SlotDirectory(installDirectory, s)).ToList()
            : [];

        var lines = File.Exists(configTomlPath) ? File.ReadAllLines(configTomlPath).ToList() : [];
        var sectionStart = lines.FindIndex(l => l.Trim() == "[my_stuff]");
        var arrayText = "roots = [" + string.Join(", ", activePaths.Select(p => $"\"{EscapeToml(p)}\"")) + "]";

        if (sectionStart < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length != 0)
            {
                lines.Add("");
            }
            lines.Add("[my_stuff]");
            lines.Add(arrayText);
        }
        else
        {
            var sectionEnd = lines.Count;
            for (var i = sectionStart + 1; i < lines.Count; i++)
            {
                if (lines[i].TrimStart().StartsWith('['))
                {
                    sectionEnd = i;
                    break;
                }
            }
            var rootsLine = -1;
            for (var i = sectionStart + 1; i < sectionEnd; i++)
            {
                if (lines[i].TrimStart().StartsWith("roots", StringComparison.Ordinal))
                {
                    rootsLine = i;
                    break;
                }
            }
            if (rootsLine >= 0)
            {
                lines[rootsLine] = arrayText;
            }
            else
            {
                lines.Insert(sectionEnd, arrayText);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(configTomlPath)!);
        File.WriteAllLines(configTomlPath, lines);
    }

    private static string EscapeToml(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
