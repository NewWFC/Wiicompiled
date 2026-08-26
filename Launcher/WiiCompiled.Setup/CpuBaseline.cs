using System.Runtime.Intrinsics.X86;

namespace WiiCompiled.Setup;

/// <summary>
/// The x86-64 ISA baseline a product is compiled for. Mirrors MKW_CPU_BASELINE in
/// runtime/CMakeLists.txt / runtime/cmake/PublicProducts.cmake and the feature table in
/// runtime/src/host_cpu_baseline.cpp - the two must always agree, since this side only decides
/// which variant to build, while that file is the sole authoritative runtime gate.
/// </summary>
internal enum CpuBaseline { V3, V2 }

internal static class CpuBaselineOps
{
    public static string ToFlag(this CpuBaseline baseline) => baseline switch
    {
        CpuBaseline.V3 => "v3",
        CpuBaseline.V2 => "v2",
        _ => throw new ArgumentOutOfRangeException(nameof(baseline))
    };

    public static CpuBaseline Parse(string value) => value.Trim().ToLowerInvariant() switch
    {
        "v3" => CpuBaseline.V3,
        "v2" => CpuBaseline.V2,
        _ => throw new ArgumentException($"Unknown CPU baseline '{value}' (expected v3 or v2).")
    };

    /// <summary>Parses --cpu-baseline's value, where "auto" (the default) means no override.</summary>
    public static CpuBaseline? ParseOverride(string value) =>
        value.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : Parse(value);
}

/// <summary>
/// Detects whether the host CPU supports the x86-64-v3 feature set the normal build requires, so
/// the installer can silently fall back to the x86-64-v2 compatibility build on older machines
/// instead of producing a binary that immediately hits host_cpu_baseline.cpp's guard. This is a
/// build-time decision only, not a safety guarantee - the native guard remains authoritative at
/// runtime regardless of what this returns, so a wrong or overly generous answer here degrades to
/// "the produced binary still refuses to run with a clear message," never a silent misbehavior.
/// </summary>
internal static class CpuBaselineDetector
{
    // (Leaf, SubLeaf, Reg, Bit): Reg is 0=Eax, 1=Ebx, 2=Ecx, 3=Edx, matching X86Base.CpuId's tuple
    // order. Deliberately the same leaf/subleaf/reg/bit values as host_cpu_baseline.cpp's v3 table
    // (that file is the source of truth; keep the two in sync by hand since one is C++/CPUID and
    // the other is C#/X86Base). Unlike the native guard, this does not check XGETBV/XCR0 OS-level
    // AVX register-state support - not needed for a build-time decision, see the type doc above.
    private static readonly (int Leaf, int SubLeaf, int Reg, int Bit)[] V3RequiredFeatures =
    [
        (1, 0, 2, 0),   // SSE3
        (1, 0, 2, 9),   // SSSE3
        (1, 0, 2, 12),  // FMA
        (1, 0, 2, 13),  // CMPXCHG16B
        (1, 0, 2, 19),  // SSE4.1
        (1, 0, 2, 20),  // SSE4.2
        (1, 0, 2, 22),  // MOVBE
        (1, 0, 2, 23),  // POPCNT
        (1, 0, 2, 27),  // OSXSAVE
        (1, 0, 2, 28),  // AVX
        (1, 0, 2, 29),  // F16C
        (7, 0, 1, 3),   // BMI1
        (7, 0, 1, 5),   // AVX2
        (7, 0, 1, 8),   // BMI2
        (unchecked((int)0x80000001u), 0, 2, 0),  // LAHF-SAHF
        (unchecked((int)0x80000001u), 0, 2, 5),  // LZCNT
    ];

    public static CpuBaseline DetectHost()
    {
        if (!X86Base.IsSupported) return CpuBaseline.V2; // conservative fallback; not expected on win-x64
        return HostSupportsV3() ? CpuBaseline.V3 : CpuBaseline.V2;
    }

    private static bool HostSupportsV3()
    {
        var maxBasic = unchecked((uint)X86Base.CpuId(0, 0).Eax);
        var maxExtended = unchecked((uint)X86Base.CpuId(unchecked((int)0x80000000u), 0).Eax);

        foreach (var (leaf, subLeaf, reg, bit) in V3RequiredFeatures)
        {
            var leafU = unchecked((uint)leaf);
            var leafAvailable = (leafU & 0x80000000u) != 0 ? leafU <= maxExtended : leafU <= maxBasic;
            if (!leafAvailable) return false;

            var (eax, ebx, ecx, edx) = X86Base.CpuId(leaf, subLeaf);
            var value = unchecked((uint)(reg switch { 0 => eax, 1 => ebx, 2 => ecx, _ => edx }));
            if ((value & (1u << bit)) == 0) return false;
        }
        return true;
    }
}
