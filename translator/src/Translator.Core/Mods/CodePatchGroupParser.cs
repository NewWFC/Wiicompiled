using System.Globalization;
using Translator.Core.Parsing.Dol;

namespace Translator.Core.Mods;

/// <summary>One code_patches.local.txt / recomp.yml code_patches block, split by what kind of
/// patch each logical code turned out to be. BasicWrites are simple same-size byte overwrites
/// (DolCodePatcher/RelCodePatcher); Hooks (C2/D2) replace one original instruction with a branch
/// into scratch space; Blocks (C0) are free-standing routines with no hook address at all - see
/// AsmHookCodePatcher for why C2/C0 can't be represented as a Patch the way the basic-write family
/// is.</summary>
public sealed record CodePatchGroup(
    IReadOnlyList<DolCodePatcher.Patch> BasicWrites,
    IReadOnlyList<AsmHookPatch> Hooks,
    IReadOnlyList<AsmBlockPatch> Blocks);

/// <summary>
/// Splits a raw code_patches line block by leading code type and hands each logical code to
/// whichever parser owns it - DolCodePatcher for the basic-write family (00-09), AsmHookCodePatcher
/// for C0/C2/D2. Neither of those parsers needs to know the other exists: a basic-write code's own
/// lines are sliced out (using DolCodePatcher.AdditionalLinesRequired to find its end) and handed to
/// DolCodePatcher.ParseLines exactly as before, so this adds C0/C2/D2 support without changing
/// DolCodePatcher's own tested parsing at all.
/// </summary>
public static class CodePatchGroupParser
{
    public static CodePatchGroup ParseLines(IReadOnlyList<string> rawLines)
    {
        var basicWrites = new List<DolCodePatcher.Patch>();
        var hooks = new List<AsmHookPatch>();
        var blocks = new List<AsmBlockPatch>();
        var index = 0;

        while (index < rawLines.Count)
        {
            var line = rawLines[index].Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';')
            {
                index++;
                continue;
            }

            switch (PeekCodeType(line))
            {
                case 0xC0:
                    blocks.Add(AsmHookCodePatcher.ParseC0(rawLines, ref index));
                    break;
                case 0xC2:
                case 0xD2:
                    hooks.Add(AsmHookCodePatcher.ParseC2(rawLines, ref index));
                    break;
                default:
                {
                    // Not a C0/C2/D2 header - let DolCodePatcher own exactly this one code's own
                    // lines (its own AdditionalLinesRequired already knows the basic-write family's
                    // line-grouping rules), so its existing error messages and parsing stay intact.
                    var additionalLines = DolCodePatcher.AdditionalLinesRequired(line);
                    var codeLineCount = Math.Min(1 + additionalLines, rawLines.Count - index);
                    var slice = new string[codeLineCount];
                    for (var i = 0; i < codeLineCount; i++)
                    {
                        slice[i] = rawLines[index + i];
                    }
                    basicWrites.AddRange(DolCodePatcher.ParseLines(slice));
                    index += codeLineCount;
                    break;
                }
            }
        }

        return new CodePatchGroup(basicWrites, hooks, blocks);
    }

    private static byte PeekCodeType(string line)
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1 || !uint.TryParse(parts[0], NumberStyles.HexNumber, null, out var addressWord))
        {
            return 0;
        }
        return (byte)(addressWord >> 24);
    }
}
