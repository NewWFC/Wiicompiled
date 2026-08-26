using System.Globalization;

namespace Translator.Core.Parsing.Dol;

/// <summary>
/// Patches raw DOL file bytes *before* translate-recursive decodes instructions from them, so a
/// Gecko/Action Replay-style code targeting a .text address actually changes compiled behavior - a
/// runtime memory write to the same address cannot, because nothing in this recomp re-reads guest
/// instruction bytes at runtime to decide what executes; the translated C++ function bodies are the
/// behavior. See runtime/include/cheat_codes.h for the (deliberately data/RW-only) runtime
/// counterpart and why the two are not interchangeable.
///
/// Supported code types - the "basic write" family, ba-relative only (see ParseLines for why `po`
/// is out of scope): 00/01 (8-bit write &amp; fill), 02/03 (16-bit write &amp; fill), 04/05 (32-bit
/// write), 06/07 (string write - a raw byte sequence from however many following lines its own byte
/// count needs), 08/09 (write &amp; fill with an address AND value increment per step, two lines).
/// Each expands to one or more plain (address, value, width) writes at parse time, so everything
/// downstream - DOL/REL section lookup, byte patching - only ever deals with a single-write
/// primitive it already knows how to apply.
/// </summary>
public static class DolCodePatcher
{
    public readonly record struct Patch(uint Address, uint Value, int WidthBytes, string Source);

    /// <summary>
    /// Parses a whole block of "AAAAAAAA VVVVVVVV" lines (comments/blank lines allowed and
    /// skipped), consuming a second line for any code type that needs one (currently only 08/09).
    /// Throws for a malformed or genuinely unsupported line - a code patch silently doing nothing
    /// at translate time is far more confusing than a build failure that names the bad line.
    /// </summary>
    public static IReadOnlyList<Patch> ParseLines(IReadOnlyList<string> rawLines)
    {
        var result = new List<Patch>();
        var index = 0;
        while (index < rawLines.Count)
        {
            var line = rawLines[index].Trim();
            index++;
            if (line.Length == 0 || line[0] == '#' || line[0] == ';')
            {
                continue;
            }

            var (addressWord, valueWord) = ParseWordPair(line, line);
            var codeType = (byte)(addressWord >> 24);
            if (codeType is 0x10 or 0x11 or 0x12 or 0x13 or 0x14 or 0x15 or 0x16 or 0x17 or 0x18 or 0x19)
            {
                throw new InvalidDataException(
                    $"Code patch '{line}' uses a 'po' (pointer-relative) code type (0x{codeType:X2}); " +
                    "only 'ba' (fixed-base) code types are supported - po depends on a prior code " +
                    "having set a pointer, and nothing here tracks that state across codes.");
            }

            var address = ResolveBaAddress(addressWord, out var baseCodeType);
            switch (baseCodeType)
            {
                case 0x00:
                    AddFill(result, address, valueWord & 0xFFu, 1, (valueWord >> 16) & 0xFFFFu, line);
                    break;
                case 0x02:
                    AddFill(result, address, valueWord & 0xFFFFu, 2, (valueWord >> 16) & 0xFFFFu, line);
                    break;
                case 0x04:
                    result.Add(new Patch(address, valueWord, 4, line));
                    break;
                case 0x06:
                {
                    var byteCount = valueWord;
                    if (byteCount == 0)
                    {
                        break;
                    }
                    var lineCount = (int)((byteCount + 7) / 8);
                    var bytes = new List<byte>((int)byteCount + 8);
                    for (var i = 0; i < lineCount; i++)
                    {
                        if (index >= rawLines.Count)
                        {
                            throw new InvalidDataException(
                                $"Code patch '{line}' is a string write (type 06/07) of {byteCount} byte(s), " +
                                $"which needs {lineCount} more line(s) of raw bytes; only {i} were found.");
                        }
                        var dataLine = rawLines[index].Trim();
                        index++;
                        var (word1, word2) = ParseWordPair(dataLine, line + " / " + dataLine);
                        AppendBytes(bytes, word1);
                        AppendBytes(bytes, word2);
                    }
                    for (var i = 0u; i < byteCount; i++)
                    {
                        result.Add(new Patch(checked(address + i), bytes[(int)i], 1,
                            $"{line} (byte {i + 1}/{byteCount})"));
                    }
                    break;
                }
                case 0x08:
                {
                    if (index >= rawLines.Count)
                    {
                        throw new InvalidDataException(
                            $"Code patch '{line}' is a write-and-fill (type 08/09) code, which needs a " +
                            "second \"TNNNZZZZ VVVVVVVV\" line that is missing.");
                    }
                    var secondLine = rawLines[index].Trim();
                    index++;
                    var combinedSource = line + " / " + secondLine;
                    var (control, valueIncrement) = ParseWordPair(secondLine, combinedSource);
                    var widthBytes = ((control >> 28) & 0xFu) switch
                    {
                        0 => 1,
                        1 => 2,
                        2 => 4,
                        var t => throw new InvalidDataException(
                            $"Code patch '{combinedSource}' has value size T=0x{t:X} in its second line; " +
                            "only 0 (byte), 1 (halfword), or 2 (word) are defined."),
                    };
                    var additionalWrites = (control >> 16) & 0xFFFu;
                    var addressIncrement = control & 0xFFFFu;
                    for (var step = 0u; step <= additionalWrites; step++)
                    {
                        var stepAddress = checked(address + step * addressIncrement);
                        var stepValue = unchecked(valueWord + step * valueIncrement);
                        result.Add(new Patch(stepAddress, stepValue, widthBytes,
                            $"{combinedSource} (write {step + 1}/{additionalWrites + 1})"));
                    }
                    break;
                }
                default:
                    throw new InvalidDataException(
                        $"Code patch '{line}' has type 0x{codeType:X2}; only the basic-write family " +
                        "(00/01, 02/03, 04/05, 06/07, 08/09) is supported. A code hook needs the translator's " +
                        "mod-patch machinery instead, not a raw byte patch.");
            }
        }
        return result;
    }

    /// <summary>
    /// How many additional raw lines follow this line's own "AAAAAAAA VVVVVVVV" pair before the
    /// next code begins - 1 for a write-and-fill (08/09, the "TNNNZZZZ VVVVVVVV" control line),
    /// ceil(byteCount/8) for a string write (06/07, byteCount read straight from the value word),
    /// 0 for everything else. Lets a caller that needs to group raw lines for display (e.g. a UI
    /// showing one status row per code) decide how many lines to consume without duplicating
    /// ParseLines' own logic. Returns 0 for a blank/comment line or anything unparseable;
    /// ParseLines is the source of truth for whether a line is actually valid.
    /// </summary>
    public static int AdditionalLinesRequired(string firstLine)
    {
        var trimmed = firstLine.Trim();
        if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == ';')
        {
            return 0;
        }
        var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 ||
            !uint.TryParse(parts[0], NumberStyles.HexNumber, null, out var addressWord) ||
            !uint.TryParse(parts[1], NumberStyles.HexNumber, null, out var valueWord))
        {
            return 0;
        }
        var baseCodeType = (byte)((addressWord >> 24) & ~0x01);
        if (baseCodeType is 0x08 or 0x18)
        {
            return 1;
        }
        if (baseCodeType is 0x06 or 0x16)
        {
            return (int)((valueWord + 7) / 8);
        }
        return 0;
    }

    /// <summary>Convenience for a single already-known-to-be-one-line code (00/02/04, never 08/09).
    /// Prefer ParseLines for anything pasted by a user, since it correctly handles two-line codes.</summary>
    public static Patch? ParseLine(string rawLine)
    {
        var patches = ParseLines([rawLine]);
        return patches.Count switch
        {
            0 => null,
            1 => patches[0],
            _ => throw new InvalidDataException(
                $"Code patch '{rawLine}' needs more than one line (it is type 08/09 or expands to " +
                "more than one write); call ParseLines with the full block instead."),
        };
    }

    private static (uint AddressWord, uint ValueWord) ParseWordPair(string line, string sourceForError)
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !uint.TryParse(parts[0], NumberStyles.HexNumber, null, out var addressWord) ||
            !uint.TryParse(parts[1], NumberStyles.HexNumber, null, out var valueWord))
        {
            throw new InvalidDataException($"Code patch '{sourceForError}' is not \"AAAAAAAA VVVVVVVV\" (two 8-digit hex words).");
        }
        return (addressWord, valueWord);
    }

    /// <summary>0x80000000 + the 24-bit offset, +0x01000000 more if the code type is the "high
    /// address" sibling of its base type (00-&gt;01, 02-&gt;03, 04-&gt;05, 08-&gt;09) - see the class
    /// doc. Returns the *base* code type (00/02/04/08) via baseCodeType so the caller only needs to
    /// switch on four values instead of eight.</summary>
    private static uint ResolveBaAddress(uint addressWord, out byte baseCodeType)
    {
        var codeType = (byte)(addressWord >> 24);
        var isHigh = (codeType & 0x01) != 0;
        baseCodeType = (byte)(codeType & ~0x01);
        var offset = addressWord & 0x00FFFFFFu;
        if (isHigh)
        {
            offset += 0x01000000u;
        }
        return 0x80000000u + offset;
    }

    private static void AddFill(List<Patch> result, uint address, uint value, int widthBytes,
        uint fillCount, string source)
    {
        for (var i = 0u; i < fillCount; i++)
        {
            var stepAddress = checked(address + i * (uint)widthBytes);
            result.Add(new Patch(stepAddress, value, widthBytes,
                fillCount <= 1 ? source : $"{source} (fill {i + 1}/{fillCount})"));
        }
    }

    /// <summary>Appends a 32-bit word's four bytes MSB-first (d1d2d3d4, matching how the hex word is
    /// written) - the packing a type 06/07 string write's data lines use.</summary>
    private static void AppendBytes(List<byte> bytes, uint word)
    {
        bytes.Add((byte)(word >> 24));
        bytes.Add((byte)(word >> 16));
        bytes.Add((byte)(word >> 8));
        bytes.Add((byte)word);
    }

    public static DolSection? FindSection(DolFile dol, Patch patch) =>
        dol.Sections.FirstOrDefault(s =>
            s.HasData &&
            patch.Address >= s.VirtualAddress &&
            checked(patch.Address + (uint)patch.WidthBytes) <= s.VirtualAddress + s.Size);

    /// <summary>
    /// Applies whichever of <paramref name="patches"/> target this DOL (per <see cref="FindSection"/>)
    /// to a fresh copy of its raw file bytes, and reports which ones matched via
    /// <paramref name="applied"/> - a caller juggling both DOL and REL patches needs to know what is
    /// left over for the other file, and what was never found in either. A patch that *does* match a
    /// DOL section but targets non-executable data still throws immediately: that is not "try the
    /// other file", it is a real usage error (see the message for where it belongs instead).
    /// </summary>
    public static byte[] ApplyPatches(string dolPath, DolFile dol, IReadOnlyList<Patch> patches,
        out IReadOnlyList<Patch> applied)
    {
        var rawBytes = File.ReadAllBytes(dolPath);
        var appliedList = new List<Patch>();

        foreach (var patch in patches)
        {
            var section = FindSection(dol, patch);
            if (section is null)
            {
                continue;
            }
            if (!section.IsExecutable)
            {
                throw new InvalidDataException(
                    $"Code patch '{patch.Source}' targets 0x{patch.Address:X8} in DOL section '{section.Name}', " +
                    "which is not executable. A data/RAM write belongs in Config.toml's runtime " +
                    "[cheats] list, not recomp.yml's code_patches - it needs to be reapplied every " +
                    "frame there, which a one-time compiled-in byte patch cannot do.");
            }

            var fileOffset = checked((int)(section.FileOffset + (patch.Address - section.VirtualAddress)));
            for (var i = 0; i < patch.WidthBytes; i++)
            {
                var shift = 8 * (patch.WidthBytes - 1 - i);
                rawBytes[fileOffset + i] = (byte)((patch.Value >> shift) & 0xFFu);
            }
            appliedList.Add(patch);
        }

        applied = appliedList;
        return rawBytes;
    }
}
