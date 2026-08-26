using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Translator.Core.Loading;

namespace Translator.Core.Disassembly;

/// <summary>
/// Simple disassembler that walks reachable instructions using the in-process PPC decoder.
/// Optimized for demand-driven decoding to avoid decoding unreachable instructions.
/// </summary>
public sealed class PpcDisassemblyLimitExceededException : InvalidOperationException
{
    public PpcDisassemblyLimitExceededException(
        uint entryPoint,
        int maxInstructions,
        int maxBytes,
        int decodedInstructions,
        uint blockedAddress)
        : base(
            $"Disassembly budget exhausted for 0x{entryPoint:X8} after {decodedInstructions} reachable instructions; " +
            $"next reachable address was 0x{blockedAddress:X8} (maxInstructions={maxInstructions}, maxBytes=0x{maxBytes:X}). " +
            "Increase the disassembly budget before trusting the generated control flow.")
    {
        EntryPoint = entryPoint;
        MaxInstructions = maxInstructions;
        MaxBytes = maxBytes;
        DecodedInstructions = decodedInstructions;
        BlockedAddress = blockedAddress;
    }

    public uint EntryPoint { get; }
    public int MaxInstructions { get; }
    public int MaxBytes { get; }
    public int DecodedInstructions { get; }
    public uint BlockedAddress { get; }
}

/// <summary>
/// Records every membership question asked of the known-function entry point set.
/// A cached decode is replayable only while all of these still answer the same way.
/// </summary>
public sealed class BoundaryProbeLog
{
    private List<uint>? _absent;

    /// <summary>Addresses that were asked about and were not boundaries.</summary>
    public IReadOnlyList<uint> AbsentBoundaries => (IReadOnlyList<uint>?)_absent ?? Array.Empty<uint>();

    public void RecordAbsent(uint address)
    {
        _absent ??= new List<uint>();
        if (!_absent.Contains(address))
        {
            _absent.Add(address);
        }
    }

    /// <summary>
    /// True while every recorded question still has the same answer. Boundary
    /// sets only ever grow during a run, so only the negative answers can flip.
    /// </summary>
    public bool IsStillValid(IReadOnlySet<uint>? knownFunctionEntryPoints)
    {
        if (_absent is null || knownFunctionEntryPoints is null)
        {
            return true;
        }

        for (var i = 0; i < _absent.Count; i++)
        {
            if (knownFunctionEntryPoints.Contains(_absent[i]))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class PpcDisassembler : IDisposable
{
    public IReadOnlyList<PpcInstruction> DisassembleFunction(
        ProgramImage image,
        uint entryPoint,
        int maxInstructions = 256,
        int maxBytes = 0x800,
        IReadOnlySet<uint>? knownFunctionEntryPoints = null,
        BoundaryProbeLog? boundaryProbes = null,
        uint? tolerateDecodeFailureFrom = null,
        uint? tolerateDecodeFailureTo = null)
    {
        var instructions = DisassembleFunctionCore(
            image, entryPoint, maxInstructions, maxBytes, knownFunctionEntryPoints, boundaryProbes,
            throwOnBudget: true, tolerateDecodeFailureFrom, tolerateDecodeFailureTo);
        return instructions!;
    }

    /// <summary>
    /// Budget-tolerant variant for speculative decoding (leaf inlining), where an
    /// oversized body is a normal "not a candidate" answer, not an error to throw and swallow.
    /// </summary>
    public IReadOnlyList<PpcInstruction>? TryDisassembleFunction(
        ProgramImage image,
        uint entryPoint,
        int maxInstructions,
        int maxBytes,
        IReadOnlySet<uint>? knownFunctionEntryPoints = null,
        BoundaryProbeLog? boundaryProbes = null,
        uint? tolerateDecodeFailureFrom = null,
        uint? tolerateDecodeFailureTo = null)
        => DisassembleFunctionCore(
            image, entryPoint, maxInstructions, maxBytes, knownFunctionEntryPoints, boundaryProbes,
            throwOnBudget: false, tolerateDecodeFailureFrom, tolerateDecodeFailureTo);

    /// <summary>
    /// True for a decoded instruction's placeholder mnemonic pattern (the decoder never throws for
    /// an unrecognized opcode - it tags it "opc_N"/"unkN"/"xo_N"/"fp_N"/"invalid_..." instead, and
    /// leaves whether that's fatal to the lifter). Used only when tolerateDecodeFailureFrom is set
    /// (see its doc) - a false positive there just means one dead-end path inside a hand-written
    /// Gecko ASM hook's own scratch code isn't explored, never anything about real game code, since
    /// nothing here ever runs for an address below that threshold.
    /// </summary>
    private static bool LooksUnrecognized(PpcInstruction ins) =>
        ins.Mnemonic is not ("nop" or "opc_0") &&
        (ins.Mnemonic.StartsWith("opc_", StringComparison.Ordinal) ||
         ins.Mnemonic.StartsWith("unk", StringComparison.Ordinal) ||
         ins.Mnemonic.StartsWith("xo_", StringComparison.Ordinal) ||
         ins.Mnemonic.StartsWith("fp_", StringComparison.Ordinal) ||
         ins.Mnemonic.StartsWith("invalid_", StringComparison.Ordinal) ||
         ins.Mnemonic == "unknown");

    private static IReadOnlyList<PpcInstruction>? DisassembleFunctionCore(
        ProgramImage image,
        uint entryPoint,
        int maxInstructions,
        int maxBytes,
        IReadOnlySet<uint>? knownFunctionEntryPoints,
        BoundaryProbeLog? boundaryProbes,
        bool throwOnBudget,
        uint? tolerateDecodeFailureFrom = null,
        uint? tolerateDecodeFailureTo = null)
    {
        if (!image.Contains(entryPoint, sizeof(uint)))
        {
            throw new ArgumentOutOfRangeException(nameof(entryPoint), $"Address 0x{entryPoint:X8} is outside the loaded RAM image.");
        }
        var offset = image.GetOffset(entryPoint, sizeof(uint));

        var available = Math.Min(maxBytes, image.Memory.Length - offset);

        // Demand-driven decoding: decode only the instructions we actually visit
        var instructionMap = new Dictionary<uint, PpcInstruction>();
        var reachable = new HashSet<uint>();
        var localControlFlowTargets = new HashSet<uint>();
        var worklist = new Queue<uint>();
        worklist.Enqueue(entryPoint);

        var endAddress = entryPoint + (uint)available;

        // Ordered view used by jump-table recognition, rebuilt lazily when the decoded set grows.
        List<PpcInstruction>? orderedCache = null;
        Dictionary<uint, int>? indexByAddressCache = null;
        var orderedCacheCount = -1;

        while (worklist.Count > 0)
        {
            if (reachable.Count >= maxInstructions)
            {
                if (!throwOnBudget)
                {
                    return null;
                }

                throw new PpcDisassemblyLimitExceededException(
                    entryPoint,
                    maxInstructions,
                    maxBytes,
                    reachable.Count,
                    worklist.Peek());
            }

            var cursor = worklist.Dequeue();
            while (cursor >= entryPoint && cursor < endAddress)
            {
                if (reachable.Count >= maxInstructions)
                {
                    if (!throwOnBudget)
                    {
                        return null;
                    }

                    throw new PpcDisassemblyLimitExceededException(
                        entryPoint,
                        maxInstructions,
                        maxBytes,
                        reachable.Count,
                        cursor);
                }

                // A hand-written Gecko ASM hook's own scratch region (C2/C0 - see the translator
                // CLI's AsmHookScratchBase/AsmHookScratchCapacity) is reserved exclusively for
                // scratch code; no other guest function's disassembly walk may wander into it, no
                // matter how plausible the bytes there happen to decode - they belong to whatever
                // cheat is installed, not to this function. This is a hard, unconditional dead end
                // (checked before even decoding) for any walk whose *own* entry point lies outside
                // the range - a scratch function's own walk, entryPoint inside the range, is exempt
                // so scratch code itself still disassembles normally.
                if (tolerateDecodeFailureFrom is { } scratchStart &&
                    tolerateDecodeFailureTo is { } scratchEnd &&
                    cursor >= scratchStart && cursor < scratchEnd &&
                    (entryPoint < scratchStart || entryPoint >= scratchEnd))
                {
                    break;
                }

                // Decode on demand
                if (!instructionMap.TryGetValue(cursor, out var ins))
                {
                    if (!image.Contains(cursor, sizeof(uint)))
                    {
                        break;
                    }
                    var cursorOffset = image.GetOffset(cursor, sizeof(uint));
                    var word = BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(cursorOffset, 4));
                    ins = PpcDecoder.Decode(cursor, word);

                    // A hand-written Gecko ASM hook's own scratch code (C2/C0 - see the translator
                    // CLI's AsmHookScratchBase) sometimes deliberately places raw data right after a
                    // bl used only to capture a PC-relative pointer via mflr, never intending the
                    // call to actually return there (see runtime/include/cheat_codes.h's own C2/C0
                    // doc for the pattern). This recomp, like any static compiler, can't prove a bl
                    // never returns, so it otherwise has to assume that data is reachable code and
                    // fails to decode it. Only within the scratch address range - never for any real
                    // game address - treat that failure as "this path is a dead end" instead of
                    // poisoning the whole containing function's translation over unreachable bytes.
                    // Both bounds matter: scratch is not guaranteed to sit above every real game
                    // address (it can be relocated into a low-memory "Gecko hole"), so an open-ended
                    // lower bound alone would also tolerate decode failures throughout ordinary game
                    // code sitting above it.
                    if (tolerateDecodeFailureFrom is { } threshold && cursor >= threshold &&
                        (tolerateDecodeFailureTo is not { } end || cursor < end) &&
                        LooksUnrecognized(ins))
                    {
                        break;
                    }

                    instructionMap[cursor] = ins;
                }

                if (!reachable.Add(ins.Address))
                {
                    break;
                }

                var recognizedJumpTable = false;
                if (ins.BranchTargets.Count == 0 && string.Equals(ins.Mnemonic, "bctr", StringComparison.OrdinalIgnoreCase))
                {
                    // For jump tables, we need the ordered list - build it lazily
                    if (orderedCache is null || indexByAddressCache is null || orderedCacheCount != instructionMap.Count)
                    {
                        orderedCache = instructionMap.Values.OrderBy(i => i.Address).ToList();
                        indexByAddressCache = new Dictionary<uint, int>(orderedCache.Count);
                        for (var idx = 0; idx < orderedCache.Count; idx++)
                        {
                            indexByAddressCache[orderedCache[idx].Address] = idx;
                        }

                        orderedCacheCount = instructionMap.Count;
                    }

                    if (JumpTableDetector.TryRecognize(orderedCache, indexByAddressCache, ins.Address, entryPoint, endAddress, image, out var jtTargets))
                    {
                        ins = new PpcInstruction(ins.Address, ins.Word, ins.Mnemonic, ins.Operands, jtTargets, isReturn: false, ins.IsCall, ins.IsConditionalBranch);
                        instructionMap[ins.Address] = ins;
                        // Same address, so the ordered view's count is unchanged but its content is stale; drop it.
                        orderedCache = null;
                        indexByAddressCache = null;
                        orderedCacheCount = -1;
                        foreach (var target in jtTargets)
                        {
                            localControlFlowTargets.Add(target);
                        }
                        recognizedJumpTable = true;
                    }
                }

                if (ins.IsConditionalBranch)
                {
                    foreach (var target in ins.BranchTargets)
                    {
                        localControlFlowTargets.Add(target);
                        if (!reachable.Contains(target))
                        {
                            worklist.Enqueue(target);
                        }
                    }
                    cursor = ins.EndAddress;
                    continue;
                }

                if (ins.IsUnconditionalBranch)
                {
                    foreach (var target in ins.BranchTargets)
                    {
                        if ((recognizedJumpTable || !IsKnownExternalFunctionEntry(target)) && !reachable.Contains(target))
                        {
                            worklist.Enqueue(target);
                        }
                    }
                    break; // no fallthrough
                }

                if (ins.IsReturn)
                {
                    break;
                }

                cursor = ins.EndAddress;
            }
        }

        var addresses = new uint[reachable.Count];
        reachable.CopyTo(addresses);
        Array.Sort(addresses);
        var ordered = new List<PpcInstruction>(addresses.Length);
        foreach (var address in addresses)
        {
            ordered.Add(instructionMap[address]);
        }

        return ordered;

        bool IsKnownExternalFunctionEntry(uint target)
        {
            if (target == entryPoint || localControlFlowTargets.Contains(target))
            {
                return false;
            }

            if (knownFunctionEntryPoints is null)
            {
                return false;
            }

            var known = knownFunctionEntryPoints.Contains(target);
            if (!known)
            {
                boundaryProbes?.RecordAbsent(target);
            }

            return known;
        }
    }

    public void Dispose()
    {
        // nothing to dispose currently
    }
}
