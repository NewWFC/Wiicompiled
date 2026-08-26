using Translator.Core.Parsing.Dol;

namespace Translator.Core.Parsing.Rel;

/// <summary>
/// REL counterpart to DolCodePatcher - StaticR.rel is loaded game code too, not just data, so a
/// code_patches address can land in either file. RelFile.BuildImage copies the raw REL file bytes
/// verbatim starting at image offset 0 (see RelFile.cs), which lands at guest address BaseAddress,
/// so "guest address" and "REL file offset" differ only by that constant - the same file-offset
/// math DolCodePatcher uses, just against an already-built, directly mutable RelImage.Data instead
/// of a byte[] that has to be re-parsed into a new DolFile.
/// </summary>
public static class RelCodePatcher
{
    public static RelSection? FindSection(RelFile relFile, DolCodePatcher.Patch patch, uint baseAddress)
    {
        if (patch.Address < baseAddress)
        {
            return null;
        }
        var fileOffset = patch.Address - baseAddress;
        return relFile.Sections.FirstOrDefault(s =>
            s.Size > 0 &&
            fileOffset >= s.FileOffset &&
            checked(fileOffset + (uint)patch.WidthBytes) <= s.FileOffset + s.Size);
    }

    /// <summary>
    /// Mutates <paramref name="image"/>.Data in place for whichever of <paramref name="patches"/>
    /// target this REL, reporting matches via <paramref name="applied"/> the same way
    /// DolCodePatcher.ApplyPatches does. A match on a non-executable section still throws
    /// immediately, for the same reason as the DOL case.
    /// </summary>
    public static void ApplyPatches(RelFile relFile, RelImage image, IReadOnlyList<DolCodePatcher.Patch> patches,
        out IReadOnlyList<DolCodePatcher.Patch> applied)
    {
        var appliedList = new List<DolCodePatcher.Patch>();

        foreach (var patch in patches)
        {
            var section = FindSection(relFile, patch, image.BaseAddress);
            if (section is null)
            {
                continue;
            }
            if (!section.Executable)
            {
                throw new InvalidDataException(
                    $"Code patch '{patch.Source}' targets 0x{patch.Address:X8} in REL section #{section.Index}, " +
                    "which is not executable. A data/RAM write belongs in Config.toml's runtime " +
                    "[cheats] list, not recomp.yml's code_patches - it needs to be reapplied every " +
                    "frame there, which a one-time compiled-in byte patch cannot do.");
            }

            var imageOffset = checked((int)(patch.Address - image.BaseAddress));
            for (var i = 0; i < patch.WidthBytes; i++)
            {
                var shift = 8 * (patch.WidthBytes - 1 - i);
                image.Data[imageOffset + i] = (byte)((patch.Value >> shift) & 0xFFu);
            }
            appliedList.Add(patch);
        }

        applied = appliedList;
    }
}
