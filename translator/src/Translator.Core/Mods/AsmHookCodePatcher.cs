using Translator.Core.Disassembly;
using Translator.Core.Parsing.Kamek;

namespace Translator.Core.Mods;

/// <summary>
/// One Gecko "C2" (Insert ASM) code: replace the single original instruction at HookAddress with a
/// branch into freshly-placed scratch space containing InstructionWords, then either fall through
/// to whatever the payload's own last instruction does (if it's already a blr or unconditional
/// branch) or auto-return to HookAddress+4 (see AsmHookPlanner.BuildHookAndScratch) - matching real
/// Gecko codehandler behavior, where the codehandler always appends that return branch itself
/// unless the payload already ends in one.
///
/// Unlike the 00/02/04/06/08 basic-write family (DolCodePatcher.cs), this can't be a simple
/// same-size byte patch: the payload is N-1 real instruction words (the terminating word must be
/// exactly 00000000, see ParseC2) that do not fit in the ONE word slot they replace. See
/// AsmHookPlanner for how scratch space is found and wired into the build.
/// </summary>
public sealed record AsmHookPatch(uint HookAddress, IReadOnlyList<uint> InstructionWords, string Source)
{
    /// <summary>True when the payload's own last word already ends its own control flow (blr, or
    /// an unconditional b/bl) - in that case nothing needs to fall through to HookAddress+4, so no
    /// auto-return branch is appended. A conditional branch or anything else as the last word still
    /// gets the auto-return, since the payload might fall through past it.</summary>
    public bool EndsInTerminator =>
        InstructionWords.Count > 0 && IsTerminatingWord(InstructionWords[^1]);

    private static bool IsTerminatingWord(uint word) =>
        PpcControlFlow.IsReturn(word) ||
        PpcControlFlow.IsRelativeUnlinkedBranch(word) ||
        PpcControlFlow.IsRelativeLinkedBranch(word);
}

/// <summary>
/// One Gecko "C0" (Execute ASM) code: a standalone instruction block with no hook address at all -
/// unlike C2, it never replaces anything in an existing function. It runs as its own free-standing
/// routine once per active-code-list pass (here: once per VBlank, alongside the RAM-write cheats -
/// see AsmHookRuntimeDispatch) and must end in blr, since nothing auto-generates a return for it the
/// way C2's missing-terminator case does.
/// </summary>
public sealed record AsmBlockPatch(IReadOnlyList<uint> InstructionWords, string Source);

public static class AsmHookCodePatcher
{
    /// <summary>
    /// Parses one C2 code starting at rawLines[index] (already confirmed by the caller to be a C2
    /// header line): "C2XXXXXX NNNNNNNN" followed by NNNNNNNN lines (2*NNNNNNNN words) of raw PPC
    /// instructions, whose very last word must be exactly 00000000 - a required terminator, not an
    /// instruction (real Gecko codehandlers use it to mark the end of the payload before their own
    /// auto-appended return branch). Advances index past everything it consumed.
    /// </summary>
    public static AsmHookPatch ParseC2(IReadOnlyList<string> rawLines, ref int index)
    {
        var line = rawLines[index].Trim();
        index++;

        var (addressWord, countWord) = ParseWordPair(line, line);
        var codeType = (byte)(addressWord >> 24);
        if (codeType == 0xD2)
        {
            throw new InvalidDataException(
                $"Code patch '{line}' uses a 'po' (pointer-relative) code type (0xD2); only 'ba' " +
                "(fixed-base, C2) is supported - po depends on a prior code having set a pointer, " +
                "and nothing here tracks that state across codes.");
        }
        if (codeType != 0xC2)
        {
            throw new InvalidDataException($"Code patch '{line}' has type 0x{codeType:X2}, not C2/D2.");
        }

        var hookAddress = 0x80000000u + (addressWord & 0x00FFFFFFu);
        var lineCount = countWord;
        if (lineCount == 0)
        {
            throw new InvalidDataException($"Code patch '{line}' (C2) declares zero instruction line(s).");
        }

        var words = new List<uint>(checked((int)lineCount) * 2);
        for (var i = 0u; i < lineCount; i++)
        {
            if (index >= rawLines.Count)
            {
                throw new InvalidDataException(
                    $"Code patch '{line}' (C2) declares {lineCount} instruction line(s), which needs " +
                    $"{lineCount} more line(s); only {i} were found.");
            }
            var dataLine = rawLines[index].Trim();
            index++;
            var (word1, word2) = ParseWordPair(dataLine, line + " / " + dataLine);
            words.Add(word1);
            words.Add(word2);
        }

        if (words[^1] != 0)
        {
            throw new InvalidDataException(
                $"Code patch '{line}' (C2) must end with a 00000000 terminator word (its last word " +
                $"is 0x{words[^1]:X8}); the codehandler uses that word to mark the end of the payload.");
        }
        var instructionWords = words.GetRange(0, words.Count - 1);
        if (instructionWords.Count == 0)
        {
            throw new InvalidDataException(
                $"Code patch '{line}' (C2) has no instruction words before its 00000000 terminator.");
        }

        return new AsmHookPatch(hookAddress, instructionWords, line);
    }

    /// <summary>
    /// Parses one C0 code starting at rawLines[index] (already confirmed by the caller to be a C0
    /// header line): "C0000000 NNNNNNNN" - address is always literally C0000000, there is no hook
    /// address at all - followed by NNNNNNNN lines (2*NNNNNNNN words) of raw PPC instructions that
    /// must end in blr (0x4E800020), optionally with one trailing 00000000 pad word if that's what
    /// it takes to fill out the last line. Advances index past everything it consumed.
    /// </summary>
    public static AsmBlockPatch ParseC0(IReadOnlyList<string> rawLines, ref int index)
    {
        var line = rawLines[index].Trim();
        index++;

        var (addressWord, countWord) = ParseWordPair(line, line);
        if (addressWord != 0xC0000000u)
        {
            throw new InvalidDataException(
                $"Code patch '{line}' has type 0xC0 but a nonzero address (0x{addressWord & 0x00FFFFFFu:X6}); " +
                "C0 (Execute ASM) takes no address at all - it always reads \"C0000000 NNNNNNNN\".");
        }

        var lineCount = countWord;
        if (lineCount == 0)
        {
            throw new InvalidDataException($"Code patch '{line}' (C0) declares zero instruction line(s).");
        }

        var words = new List<uint>(checked((int)lineCount) * 2);
        for (var i = 0u; i < lineCount; i++)
        {
            if (index >= rawLines.Count)
            {
                throw new InvalidDataException(
                    $"Code patch '{line}' (C0) declares {lineCount} instruction line(s), which needs " +
                    $"{lineCount} more line(s); only {i} were found.");
            }
            var dataLine = rawLines[index].Trim();
            index++;
            var (word1, word2) = ParseWordPair(dataLine, line + " / " + dataLine);
            words.Add(word1);
            words.Add(word2);
        }

        var endsInBlr = PpcControlFlow.IsReturn(words[^1]) ||
            (words.Count >= 2 && words[^1] == 0 && PpcControlFlow.IsReturn(words[^2]));
        if (!endsInBlr)
        {
            throw new InvalidDataException(
                $"Code patch '{line}' (C0) must end in blr (0x4E800020) - a standalone block that " +
                "doesn't return would fall through into whatever happens to follow it in memory, " +
                "since nothing auto-generates a return the way a C2 hook's missing terminator does.");
        }

        return new AsmBlockPatch(words, line);
    }

    /// <summary>
    /// How many additional lines a C0/C2/D2 header at rawLines[index] needs (its own NNNNNNNN line
    /// count) - lets a caller group raw lines for display without duplicating ParseC0/ParseC2's own
    /// parse logic. Returns 0 for anything that isn't recognizably one of those headers.
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
            !uint.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out var addressWord) ||
            !uint.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var countWord))
        {
            return 0;
        }
        var codeType = (byte)(addressWord >> 24);
        return codeType is 0xC0 or 0xC2 or 0xD2 ? checked((int)countWord) : 0;
    }

    private static (uint AddressWord, uint ValueWord) ParseWordPair(string line, string sourceForError)
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !uint.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out var addressWord) ||
            !uint.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var valueWord))
        {
            throw new InvalidDataException($"Code patch '{sourceForError}' is not \"AAAAAAAA VVVVVVVV\" (two 8-digit hex words).");
        }
        return (addressWord, valueWord);
    }
}

/// <summary>
/// Places a parsed AsmHookPatch's payload into scratch space and computes the branch that replaces
/// the original hook-address word - pure address/encoding math, independent of where scratch
/// memory actually lives in a given build (see the translator CLI for how a scratch address is
/// chosen). Kept separate from AsmHookCodePatcher so this half can be unit-verified without needing
/// a real DOL/REL/program image.
/// </summary>
public static class AsmHookPlanner
{
    /// <summary>
    /// HookWordValue: the branch instruction that replaces the single original word at
    /// patch.HookAddress. ScratchWords: the bytes to place at scratchAddress - the payload's own
    /// instruction words, plus (only if the payload doesn't already end in blr/an unconditional
    /// branch) one auto-generated "b HookAddress+4" so execution rejoins the original function
    /// exactly where the real Gecko codehandler would leave it.
    /// </summary>
    public static (uint HookWordValue, IReadOnlyList<uint> ScratchWords) BuildHookAndScratch(
        AsmHookPatch patch, uint scratchAddress)
    {
        var hookWord = KamekPpcEncoding.EncodeBranch(patch.HookAddress, scratchAddress, link: false);

        var scratchWords = new List<uint>(patch.InstructionWords);
        if (!patch.EndsInTerminator)
        {
            var returnBranchAddress = checked(scratchAddress + (uint)(scratchWords.Count * 4));
            var returnTarget = checked(patch.HookAddress + 4);
            scratchWords.Add(KamekPpcEncoding.EncodeBranch(returnBranchAddress, returnTarget, link: false));
        }
        return (hookWord, scratchWords);
    }
}
