using System.Globalization;
using System.Text;
using System.Text.Json;
using Translator.Core.Mods;
using Translator.Core.Parsing.Dol;
using Translator.Core.Parsing.Rel;

namespace WiiCompiled.SimpleUI;

/// <summary>One named entry in the cheat list, Dolphin-Gecko-manager style: a title, optional
/// creator/description, a body of one or more "AAAAAAAA VVVVVVVV" lines, and whether it's
/// currently checked on. Disabling an entry (or deleting it) removes its lines from both
/// Config.toml and recomp.yml's code_patches the same as never having added them.</summary>
internal sealed class CheatEntry
{
    public string Name { get; set; } = "";
    public string Creator { get; set; } = "";
    public string Description { get; set; } = "";
    public string Codes { get; set; } = "";
    public bool Enabled { get; set; }

    public IEnumerable<string> Lines => Codes
        .Split('\n')
        .Select(l => l.Trim())
        .Where(l => l.Length > 0);
}

internal enum CheatKind
{
    /// <summary>Targets data/RAM - written to Config.toml, applied live, no recompile.</summary>
    Data,
    /// <summary>Targets executable code - written to recomp.yml's code_patches, needs a recompile.</summary>
    Code,
    /// <summary>Parses, but its address is in neither the installed DOL nor REL.</summary>
    InvalidAddress,
    /// <summary>Not "AAAAAAAA VVVVVVVV", an unsupported code type, or a 'po' code (see
    /// DolCodePatcher.ParseLines - pointer-relative addressing needs cross-code state this
    /// architecture does not track).</summary>
    Malformed,
}

/// <summary>
/// One logical code (one raw line, two for a type 08/09 write-and-fill, or 1+ceil(byteCount/8) for a
/// type 06/07 string write), classified against the
/// installed game's own main.dol/StaticR.rel. RawLines is what actually gets written out to
/// Config.toml/code_patches.local.txt - always the untouched original line(s), never the addresses
/// a multi-write code expands into - and Line is RawLines joined with a newline purely for display
/// (both files store flat individual lines; a two-line code occupies two consecutive array/file
/// entries there, never one entry with an embedded newline, which would be invalid TOML).
/// </summary>
internal sealed record ClassifiedCheat(string Line, IReadOnlyList<string> RawLines, CheatKind Kind, string? Detail = null);

/// <summary>
/// Classifies and persists Gecko/AR "simple write" cheat codes for an installed WiiCompiled: a
/// data-target code goes in the runtime Config.toml (live, no rebuild - see
/// runtime/include/cheat_codes.h), a code-target one goes in the installed workspace's own
/// recomp.yml code_patches (see translator's DolCodePatcher/RelCodePatcher) and needs a recompile,
/// since nothing at runtime re-reads guest instruction bytes to decide what executes.
/// </summary>
internal static class CheatsService
{
    public static string GameDataDirectory(string installDirectory) =>
        Path.Combine(installDirectory, "GameAssets", "DATA");

    public static string DolPath(string installDirectory) =>
        Path.Combine(GameDataDirectory(installDirectory), "sys", "main.dol");

    public static string RelPath(string installDirectory) =>
        Path.Combine(GameDataDirectory(installDirectory), "files", "rel", "StaticR.rel");

    /// <summary>
    /// Sidecar next to recomp.yml, one "AAAAAAAA VVVVVVVV" per line - NOT recomp.yml itself.
    /// ToolkitFingerprint.ComputeComponents hashes every file under projects/ as part of the
    /// tamper-check that gates every compile (VerifyToolkitForCompilation); editing recomp.yml in
    /// place made that check fail with "the installed recompilation toolkit was modified" on every
    /// single cheat change. This exact filename is excluded from that hash on the C# host side
    /// (ToolkitFingerprint.cs) and read as an addition to recomp.yml's own code_patches on the
    /// translator side (TranslationProjectConfig.cs) - both sides need to agree on the name.
    /// </summary>
    public static string LocalCodePatchesPath(string installDirectory) =>
        Path.Combine(installDirectory, "BuildWorkspace", "projects", "mkwii", "code_patches.local.txt");

    // Deliberately still under Base\: this is the one file a rebuild is *supposed* to blow away
    // (see ForceRebuildIfCodePatchesPending) - Installation.CheckBaseCore reads it from exactly
    // this path, and its absence is what makes the normal --silent path recompile.
    public static string BaseFingerprintPath(string installDirectory) =>
        Path.Combine(installDirectory, "Base", "build-fingerprint.json");

    /// <summary>Tracks what code_patches were actually baked into the currently-compiled exe, so a
    /// removed line can be told apart from one that was never compiled in - see CheatsForm. Lives at
    /// the install root, NOT under Base\: every recompile replaces Base\'s contents with fresh
    /// build output, which would silently wipe this (and the cheat library below) on every rebuild
    /// otherwise - the install root itself is the stable, long-lived part of an installation
    /// (install-state.json, the setup exe copy) that a same-toolkit rebuild never touches.
    /// </summary>
    public static string CompiledCodePatchesMarkerPath(string installDirectory) =>
        Path.Combine(installDirectory, "compiled-code-patches.txt");

    /// <summary>
    /// Read-only diff between the workspace's current recomp.yml code_patches and what's actually
    /// compiled into the current base product - the single source of truth both the persistent
    /// MainForm banner and CheatsForm's own banner read from, so they never disagree.
    /// </summary>
    public static (IReadOnlyList<string> PendingAdd, IReadOnlyList<string> PendingRemove) GetPendingCodePatches(
        string installDirectory)
    {
        var current = ReadCodePatches(LocalCodePatchesPath(installDirectory));
        var compiled = ReadCompiledCodePatches(installDirectory);
        return (current.Except(compiled).ToArray(), compiled.Except(current).ToArray());
    }

    /// <summary>What's actually baked into the currently-compiled base product right now (see
    /// CompiledCodePatchesMarkerPath) - empty if nothing has ever been compiled with code_patches.
    /// Used both by GetPendingCodePatches' aggregate diff and, per entry, to color-code the Cheats
    /// list (an entry whose own code lines are all in here needs no recompile; one whose lines are
    /// only partly in here, or a disabled entry whose lines are still in here, does).</summary>
    public static List<string> ReadCompiledCodePatches(string installDirectory) =>
        File.Exists(CompiledCodePatchesMarkerPath(installDirectory))
            ? File.ReadAllLines(CompiledCodePatchesMarkerPath(installDirectory)).Where(l => l.Trim().Length > 0).ToList()
            : [];

    /// <summary>
    /// If the workspace's recomp.yml code_patches differ from what's actually compiled into the
    /// current base product, deletes its build-fingerprint.json so the normal --silent
    /// reconciliation path (see Installation.CheckBaseCore/ProductRepairService) treats it as
    /// needing a rebuild - the same "provenance missing" path already used for a broken install.
    /// Without this, a plain Install/Update click on an otherwise-current install would silently
    /// skip cheats someone set up in a previous session, since the fingerprint has no idea
    /// code_patches changed. Safe to call unconditionally before every install/repair attempt.
    /// </summary>
    public static void ForceRebuildIfCodePatchesPending(string installDirectory)
    {
        var (pendingAdd, pendingRemove) = GetPendingCodePatches(installDirectory);
        if (pendingAdd.Count == 0 && pendingRemove.Count == 0)
        {
            return;
        }
        var fingerprintPath = BaseFingerprintPath(installDirectory);
        if (File.Exists(fingerprintPath))
        {
            File.Delete(fingerprintPath);
        }
    }

    /// <summary>The named-entry list itself (title/creator/description/codes/enabled) - UI-owned
    /// state, not read by the translator or runtime, which only ever see the derived Config.toml
    /// [cheats] codes / recomp.yml code_patches lists this expands into. At the install root, not
    /// under Base\ - see CompiledCodePatchesMarkerPath for why.</summary>
    public static string LibraryPath(string installDirectory) =>
        Path.Combine(installDirectory, "cheats-library.json");

    private static readonly JsonSerializerOptions LibraryJsonOptions = new() { WriteIndented = true };

    public static List<CheatEntry> LoadLibrary(string installDirectory)
    {
        var path = LibraryPath(installDirectory);
        if (!File.Exists(path))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<CheatEntry>>(File.ReadAllText(path)) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static void SaveLibrary(string installDirectory, IReadOnlyList<CheatEntry> entries)
    {
        var path = LibraryPath(installDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(entries, LibraryJsonOptions));
    }

    /// <summary>
    /// Re-derives Config.toml's [cheats] codes and code_patches.local.txt from cheats-library.json
    /// itself, from scratch - the one thing every compile can fall back to regardless of whether
    /// whatever last touched the library (CheatsForm, or anything else) definitely flushed its own
    /// derived-file writes first. CheatsForm's own ApplyAndRefreshBanner already calls this after
    /// every check/add/edit/remove, so normally the derived files are never actually stale by the
    /// time this runs again - but calling it unconditionally right before every Install/Update or
    /// Recompile (see MainForm.RunInstallAsync) turns "derived files matched the library" from an
    /// invariant every code path has to individually uphold into a fact this one call guarantees,
    /// which is what actually closes off the whole class of "stale cheat data" bugs, not just the
    /// specific path that happened to cause the last one.
    /// </summary>
    /// <summary>
    /// <paramref name="cheatsEnabled"/> is the main window's master "Cheats" toggle - false writes
    /// both derived files empty regardless of the library's own per-entry Enabled flags, the same
    /// as if every entry were unchecked, without touching cheats-library.json itself (so re-checking
    /// the box brings everything back exactly as it was).
    /// </summary>
    public static (int DataLineCount, int CodeLineCount) SyncDerivedFilesFromLibrary(
        string installDirectory, bool cheatsEnabled = true)
    {
        var dataLines = new List<string>();
        var codeLines = new List<string>();
        if (cheatsEnabled)
        {
            var entries = LoadLibrary(installDirectory);
            var classified = entries.Where(e => e.Enabled)
                .SelectMany(e => Classify(installDirectory, e.Lines))
                .ToList();
            dataLines = classified.Where(c => c.Kind == CheatKind.Data).SelectMany(c => c.RawLines).ToList();
            codeLines = classified.Where(c => c.Kind == CheatKind.Code).SelectMany(c => c.RawLines).ToList();
        }

        WriteDataCodes(DefaultConfigTomlPath(), dataLines);
        WriteCodePatches(LocalCodePatchesPath(installDirectory), codeLines);
        return (dataLines.Count, codeLines.Count);
    }

    // Non-portable installs only: %LOCALAPPDATA%\WiiCompiled\Config.toml, matching
    // RuntimeConfigFile::ApplicationDataDirectory(). A portable install's own UserData\Config.toml
    // is not resolved here - see the file comment on ConfigTomlPath.
    public static string DefaultConfigTomlPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WiiCompiled", "Config.toml");

    /// <summary>
    /// Groups raw lines into logical codes exactly as DolCodePatcher.ParseLines/AdditionalLinesRequired
    /// would (a type 08/09 code consumes two lines, a type 06/07 consumes as many as its own byte
    /// count needs), then decides Data vs Code per code by looking
    /// up every one of its expanded writes in the installed DOL's/REL's own section tables - the
    /// same classification the translator performs, just read-only here. If a code's writes land in
    /// both a data section and a code section (only possible for a multi-write code - 08/09's
    /// address-incrementing writes, or 06/07's byte sequence - straddling a section boundary), the
    /// whole code is classified Code: it moves as one unit into code_patches.local.txt rather than
    /// being silently split across the two application mechanisms.
    /// </summary>
    public static List<ClassifiedCheat> Classify(string installDirectory, IEnumerable<string> lines)
    {
        var results = new List<ClassifiedCheat>();
        var dolPath = DolPath(installDirectory);
        var relPath = RelPath(installDirectory);
        DolFile? dol = File.Exists(dolPath) ? DolFile.Load(dolPath) : null;
        RelFile? relFile = File.Exists(relPath) ? RelFile.Load(relPath) : null;
        const uint relLoadAddress = 0x805102E0u; // Pinned in projects/mkwii/recomp.yml inputs.rel.

        var lineList = lines.ToList();
        var index = 0;
        while (index < lineList.Count)
        {
            var firstLine = lineList[index].Trim();
            index++;
            if (firstLine.Length == 0 || firstLine.StartsWith('#') || firstLine.StartsWith(';'))
            {
                continue;
            }

            var codeType = TryGetCodeType(firstLine);
            var isAsmHookHeader = codeType is 0xC0 or 0xC2 or 0xD2;
            var blockLines = new List<string> { firstLine };
            var additionalLines = isAsmHookHeader
                ? AsmHookCodePatcher.AdditionalLinesRequired(firstLine)
                : DolCodePatcher.AdditionalLinesRequired(firstLine);
            for (var i = 0; i < additionalLines && index < lineList.Count; i++)
            {
                blockLines.Add(lineList[index].Trim());
                index++;
            }
            var displayLine = string.Join('\n', blockLines);

            if (codeType == 0xC0)
            {
                results.Add(ClassifyBlock(blockLines, displayLine));
                continue;
            }
            if (codeType is 0xC2 or 0xD2)
            {
                results.Add(ClassifyHook(blockLines, displayLine, dol, relFile, relLoadAddress));
                continue;
            }

            IReadOnlyList<DolCodePatcher.Patch> patches;
            try
            {
                patches = DolCodePatcher.ParseLines(blockLines);
            }
            catch (InvalidDataException ex)
            {
                results.Add(new ClassifiedCheat(displayLine, blockLines, CheatKind.Malformed, ex.Message));
                continue;
            }
            if (patches.Count == 0)
            {
                continue;
            }

            var isCode = false;
            var isInvalid = false;
            var detail = "";
            foreach (var patch in patches)
            {
                var dolSection = dol is not null ? DolCodePatcher.FindSection(dol, patch) : null;
                if (dolSection is not null)
                {
                    isCode |= dolSection.IsExecutable;
                    detail = $"DOL:{dolSection.Name}";
                    continue;
                }
                var relSection = relFile is not null ? RelCodePatcher.FindSection(relFile, patch, relLoadAddress) : null;
                if (relSection is not null)
                {
                    isCode |= relSection.Executable;
                    detail = $"REL section #{relSection.Index}";
                    continue;
                }
                isInvalid = true;
            }

            results.Add(isCode
                ? new ClassifiedCheat(displayLine, blockLines, CheatKind.Code, detail)
                : isInvalid
                    ? new ClassifiedCheat(displayLine, blockLines, CheatKind.InvalidAddress,
                        "At least one write is not inside any installed DOL or REL section")
                    : new ClassifiedCheat(displayLine, blockLines, CheatKind.Data, detail));
        }

        return results;
    }

    /// <summary>C0 (Execute ASM) is always Code kind when it parses at all - it's a standalone
    /// routine with no address to validate against DOL/REL sections, unlike every other type.</summary>
    private static ClassifiedCheat ClassifyBlock(List<string> blockLines, string displayLine)
    {
        try
        {
            var localIndex = 0;
            AsmHookCodePatcher.ParseC0(blockLines, ref localIndex);
            return new ClassifiedCheat(displayLine, blockLines, CheatKind.Code, "C0 (standalone ASM block)");
        }
        catch (InvalidDataException ex)
        {
            return new ClassifiedCheat(displayLine, blockLines, CheatKind.Malformed, ex.Message);
        }
    }

    /// <summary>C2 (D2 rejected inside ParseC2 itself) replaces exactly one original word at its
    /// hook address, so it's validated against DOL/REL sections the same way a basic-write patch
    /// is - probed as a synthetic 4-byte Patch at HookAddress, since AsmHookPatch itself carries no
    /// DolCodePatcher.Patch to reuse FindSection with directly.</summary>
    private static ClassifiedCheat ClassifyHook(
        List<string> blockLines, string displayLine, DolFile? dol, RelFile? relFile, uint relLoadAddress)
    {
        AsmHookPatch hook;
        try
        {
            var localIndex = 0;
            hook = AsmHookCodePatcher.ParseC2(blockLines, ref localIndex);
        }
        catch (InvalidDataException ex)
        {
            return new ClassifiedCheat(displayLine, blockLines, CheatKind.Malformed, ex.Message);
        }

        var probe = new DolCodePatcher.Patch(hook.HookAddress, 0, 4, hook.Source);
        var dolSection = dol is not null ? DolCodePatcher.FindSection(dol, probe) : null;
        if (dolSection is not null)
        {
            return dolSection.IsExecutable
                ? new ClassifiedCheat(displayLine, blockLines, CheatKind.Code, $"DOL:{dolSection.Name}")
                : new ClassifiedCheat(displayLine, blockLines, CheatKind.InvalidAddress,
                    "C2 hook address is in a non-executable DOL section");
        }
        var relSection = relFile is not null ? RelCodePatcher.FindSection(relFile, probe, relLoadAddress) : null;
        if (relSection is not null)
        {
            return relSection.Executable
                ? new ClassifiedCheat(displayLine, blockLines, CheatKind.Code, $"REL section #{relSection.Index}")
                : new ClassifiedCheat(displayLine, blockLines, CheatKind.InvalidAddress,
                    "C2 hook address is in a non-executable REL section");
        }
        return new ClassifiedCheat(displayLine, blockLines, CheatKind.InvalidAddress,
            "C2 hook address is not inside any installed DOL or REL section");
    }

    /// <summary>Best-effort code-type byte (the top byte of the address word) for a raw
    /// "AAAAAAAA VVVVVVVV" line, purely so the UI can group a Malformed classification's lines into
    /// an "N unsupported code type(s): 0xC2 (2), 0x20 (1)" summary - returns null for anything that
    /// doesn't even parse as a hex address word.</summary>
    public static byte? TryGetCodeType(string rawLine)
    {
        var parts = rawLine.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && uint.TryParse(parts[0], NumberStyles.HexNumber, null, out var addressWord)
            ? (byte)(addressWord >> 24)
            : null;
    }

    /// <summary>Reads code_patches.local.txt (see LocalCodePatchesPath) - one "AAAAAAAA VVVVVVVV"
    /// per line, nothing more, since this file is never anything but that.</summary>
    public static List<string> ReadCodePatches(string localCodePatchesPath) =>
        File.Exists(localCodePatchesPath)
            ? File.ReadAllLines(localCodePatchesPath).Select(l => l.Trim()).Where(l => l.Length > 0).ToList()
            : [];

    public static void WriteCodePatches(string localCodePatchesPath, IReadOnlyList<string> codeLines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localCodePatchesPath)!);
        File.WriteAllLines(localCodePatchesPath, codeLines);
    }

    public static List<string> ReadDataCodes(string configTomlPath)
    {
        if (!File.Exists(configTomlPath))
        {
            return [];
        }
        var (_, arrayLines) = FindTomlArray(File.ReadAllLines(configTomlPath), "cheats", "codes");
        return arrayLines;
    }

    /// <summary>
    /// Writes [cheats] codes = [...] as one line (simplest valid TOML for a string array), leaving
    /// every other section/key in the file untouched. Mirrors the line-based editing
    /// runtime_config.h's WriteSetting does on the C++ side, just for an array value instead of a
    /// scalar one.
    /// </summary>
    public static void WriteDataCodes(string configTomlPath, IReadOnlyList<string> codeLines)
    {
        var lines = File.Exists(configTomlPath) ? File.ReadAllLines(configTomlPath).ToList() : [];
        var sectionStart = lines.FindIndex(l => l.Trim() == "[cheats]");
        var arrayText = "codes = [" + string.Join(", ", codeLines.Select(c => $"\"{EscapeToml(c)}\"")) + "]";

        if (sectionStart < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length != 0)
            {
                lines.Add("");
            }
            lines.Add("[cheats]");
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
            var codesLine = -1;
            for (var i = sectionStart + 1; i < sectionEnd; i++)
            {
                if (lines[i].TrimStart().StartsWith("codes", StringComparison.Ordinal))
                {
                    codesLine = i;
                    break;
                }
            }
            if (codesLine >= 0)
            {
                lines[codesLine] = arrayText;
            }
            else
            {
                lines.Insert(sectionEnd, arrayText);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(configTomlPath)!);
        File.WriteAllLines(configTomlPath, lines);
    }

    private static (int SectionLine, List<string> Values) FindTomlArray(string[] lines, string section, string key)
    {
        var sectionStart = Array.FindIndex(lines, l => l.Trim() == $"[{section}]");
        if (sectionStart < 0)
        {
            return (-1, []);
        }
        for (var i = sectionStart + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('['))
            {
                break;
            }
            if (!trimmed.StartsWith(key, StringComparison.Ordinal))
            {
                continue;
            }
            var bracketStart = trimmed.IndexOf('[');
            var bracketEnd = trimmed.LastIndexOf(']');
            if (bracketStart < 0 || bracketEnd <= bracketStart)
            {
                continue;
            }
            var inner = trimmed[(bracketStart + 1)..bracketEnd];
            var values = new List<string>();
            foreach (var part in SplitTomlArrayItems(inner))
            {
                values.Add(UnquoteToml(part.Trim()));
            }
            return (i, values);
        }
        return (sectionStart, []);
    }

    private static IEnumerable<string> SplitTomlArrayItems(string inner)
    {
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in inner)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            if (ch == ',' && !inQuotes)
            {
                yield return current.ToString();
                current.Clear();
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static string UnquoteToml(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\")
            : value;

    private static string EscapeToml(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
