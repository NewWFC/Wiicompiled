namespace WiiCompiled.Setup;

internal enum AppMode
{
    Help,
    SilentInstall,
    VerifyInputs,
    Uninstall,
    SilentUninstall,
    UninstallWorker,
    SelfTest,
    LaunchBase,
    LaunchRetro,
    CheckProducts,
    RepairProducts,
    Version,
    EmitPayloadIdentities
}

/// <summary>
/// What each mode accepts. Every cross-flag rule below is expressed here once instead of as a
/// per-mode exception list, so adding a mode cannot silently inherit another mode's inputs.
/// </summary>
internal sealed record ModeRules
{
    public string Flag { get; init; } = "this command";
    public bool AcceptsProgressJson { get; init; }
    public bool AcceptsGame { get; init; }
    public bool RequiresGame { get; init; }
    public bool AcceptsRetroDirectory { get; init; }
    public bool RequiresRetroDirectory { get; init; }
    public bool AcceptsPayloadMode { get; init; }
    public bool RequiresInstallDirectory { get; init; }
    public bool RequiresPayloadRoot { get; init; }
    public bool AcceptsPortable { get; init; }
    public bool AcceptsLegacyWfc { get; init; }
    public bool AcceptsCpuBaseline { get; init; }
}

internal sealed class CommandLine
{
    public AppMode Mode { get; private set; } = AppMode.Help;
    public string? GamePath { get; private set; }
    public string? RetroDirectoryPath { get; private set; }
    public RetroWfcPayloadMode RetroWfcPayloadMode { get; private set; }
    public string? InstallDirectory { get; private set; }
    public bool Quiet { get; private set; }
    public bool ProgressJson { get; private set; }
    public string? PayloadRootPath { get; private set; }
    public bool Portable { get; private set; }
    public bool EnableLegacyWfc { get; private set; }

    /// <summary>
    /// Null (the default, "auto" on the CLI) means auto-detect the host CPU
    /// (<see cref="CpuBaselineDetector.DetectHost"/>). An explicit v3/v2 forces that baseline
    /// regardless of the detected CPU - useful for testing the compatibility build on a v3-capable
    /// machine, or forcing v3 back on if auto-detection is ever wrong for a given host.
    /// </summary>
    public CpuBaseline? CpuBaselineOverride { get; private set; }

    public static bool WantsProgressJson(string[] args) =>
        args.Any(argument => argument.Equals("--progress-json", StringComparison.OrdinalIgnoreCase));

    public static CommandLine Parse(string[] args)
    {
        var result = new CommandLine();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--version": result.Mode = AppMode.Version; break;
                case "--silent": result.Mode = AppMode.SilentInstall; break;
                case "--verify-inputs": result.Mode = AppMode.VerifyInputs; break;
                case "--uninstall": result.Mode = AppMode.Uninstall; break;
                case "--silent-uninstall": result.Mode = AppMode.SilentUninstall; result.Quiet = true; break;
                case "--uninstall-worker": result.Mode = AppMode.UninstallWorker; break;
                case "--self-test": result.Mode = AppMode.SelfTest; break;
                case "--launch-base": result.Mode = AppMode.LaunchBase; break;
                case "--launch-retro": result.Mode = AppMode.LaunchRetro; break;
                case "--check-products": result.Mode = AppMode.CheckProducts; break;
                case "--repair-products": result.Mode = AppMode.RepairProducts; break;
                case "--portable": result.Portable = true; break;
                case "--enable-legacy-wfc": result.EnableLegacyWfc = true; break;
                case "--emit-payload-identities": result.Mode = AppMode.EmitPayloadIdentities; break;
                case "--payload-root": result.PayloadRootPath = RequireValue(args, ref i); break;
                case "--quiet": result.Quiet = true; break;
                case "--progress-json": result.ProgressJson = true; break;
                case "--game": result.GamePath = RequireValue(args, ref i); break;
                case "--retro-dir": result.RetroDirectoryPath = RequireValue(args, ref i); break;
                case "--download-retro-wfc-payload":
                    if (result.RetroWfcPayloadMode != RetroWfcPayloadMode.NotApplicable)
                        throw new ArgumentException("Choose only one Retro-WFC payload mode.");
                    result.RetroWfcPayloadMode = RetroWfcPayloadMode.Online;
                    break;
                case "--skip-retro-wfc-payload":
                    if (result.RetroWfcPayloadMode != RetroWfcPayloadMode.NotApplicable)
                        throw new ArgumentException("Choose only one Retro-WFC payload mode.");
                    result.RetroWfcPayloadMode = RetroWfcPayloadMode.Skipped;
                    break;
                case "--install-dir": result.InstallDirectory = RequireValue(args, ref i); break;
                case "--cpu-baseline":
                    result.CpuBaselineOverride = CpuBaselineOps.ParseOverride(RequireValue(args, ref i));
                    break;
                default:
                    throw new ArgumentException($"Unknown CLI option: {args[i]}");
            }
        }

        result.Validate();
        return result;
    }

    private static readonly Dictionary<AppMode, ModeRules> Rules = new()
    {
        [AppMode.SilentInstall] = new ModeRules
        {
            Flag = "--silent",
            AcceptsProgressJson = true, AcceptsGame = true, RequiresGame = true,
            AcceptsRetroDirectory = true, AcceptsPayloadMode = true,
            AcceptsPortable = true, AcceptsLegacyWfc = true, AcceptsCpuBaseline = true
        },
        [AppMode.VerifyInputs] = new ModeRules
        {
            Flag = "--verify-inputs",
            AcceptsProgressJson = true, AcceptsGame = true, RequiresGame = true,
            AcceptsRetroDirectory = true
        },
        [AppMode.CheckProducts] = new ModeRules
        {
            Flag = "--check-products", AcceptsProgressJson = true, AcceptsRetroDirectory = true
        },
        [AppMode.RepairProducts] = new ModeRules
        {
            Flag = "--repair-products",
            AcceptsProgressJson = true, AcceptsRetroDirectory = true, RequiresRetroDirectory = true,
            AcceptsPayloadMode = true, RequiresInstallDirectory = true, AcceptsCpuBaseline = true
        },
        [AppMode.LaunchBase] = new ModeRules { Flag = "--launch-base" },
        [AppMode.LaunchRetro] = new ModeRules { Flag = "--launch-retro" },
        [AppMode.Uninstall] = new ModeRules { Flag = "--uninstall", RequiresInstallDirectory = true },
        [AppMode.SilentUninstall] = new ModeRules
        {
            Flag = "--silent-uninstall", RequiresInstallDirectory = true
        },
        [AppMode.UninstallWorker] = new ModeRules
        {
            Flag = "--uninstall-worker", RequiresInstallDirectory = true
        },
        [AppMode.EmitPayloadIdentities] = new ModeRules
        {
            Flag = "--emit-payload-identities", RequiresPayloadRoot = true
        }
    };

    private void Validate()
    {
        var rules = Rules.GetValueOrDefault(Mode) ?? new ModeRules();

        void Reject(bool present, string option)
        {
            if (present) throw new ArgumentException($"{option} is not valid with {rules.Flag}.");
        }

        Reject(GamePath is not null && !rules.AcceptsGame, "--game");
        Reject(RetroDirectoryPath is not null && !rules.AcceptsRetroDirectory, "--retro-dir");
        Reject(ProgressJson && !rules.AcceptsProgressJson, "--progress-json");
        Reject(PayloadRootPath is not null && !rules.RequiresPayloadRoot, "--payload-root");
        Reject(Portable && !rules.AcceptsPortable, "--portable");
        Reject(RetroWfcPayloadMode != RetroWfcPayloadMode.NotApplicable && !rules.AcceptsPayloadMode,
            "A Retro-WFC payload option");
        Reject(EnableLegacyWfc && !rules.AcceptsLegacyWfc, "--enable-legacy-wfc");
        Reject(CpuBaselineOverride is not null && !rules.AcceptsCpuBaseline, "--cpu-baseline");

        if (rules.RequiresGame && string.IsNullOrWhiteSpace(GamePath))
            throw new ArgumentException("--game is required.");
        if (rules.RequiresRetroDirectory && string.IsNullOrWhiteSpace(RetroDirectoryPath))
            throw new ArgumentException(
                $"--retro-dir is required with {rules.Flag}. Supply Wheel Wizard's canonical Retro Rewind folder.");
        if (rules.RequiresPayloadRoot && string.IsNullOrWhiteSpace(PayloadRootPath))
            throw new ArgumentException($"--payload-root is required with {rules.Flag}.");

        // A portable installation has no default location so the caller must state where that root is.
        if (Portable && string.IsNullOrWhiteSpace(InstallDirectory))
            throw new ArgumentException(
                "--install-dir is required with --portable. Its parent folder becomes the portable root.");
        if (Mode == AppMode.SilentInstall && string.IsNullOrWhiteSpace(InstallDirectory))
            InstallDirectory = ProductInfo.DefaultInstallDirectory;
        if (rules.RequiresInstallDirectory && string.IsNullOrWhiteSpace(InstallDirectory))
            throw new ArgumentException($"--install-dir is required with {rules.Flag}.");

        // The Retro Rewind source and a payload decision are a pair. a base-only operation
        // has no payload to decide.
        if (rules.AcceptsPayloadMode && RetroDirectoryPath is not null &&
            RetroWfcPayloadMode == RetroWfcPayloadMode.NotApplicable)
            throw new ArgumentException(
                "Retro Rewind requires --download-retro-wfc-payload or --skip-retro-wfc-payload.");
        if (RetroDirectoryPath is null && RetroWfcPayloadMode != RetroWfcPayloadMode.NotApplicable)
            throw new ArgumentException(
                "--retro-dir is required when selecting a Retro-WFC payload option.");
        if (EnableLegacyWfc && RetroDirectoryPath is not null)
            throw new ArgumentException(
                "--enable-legacy-wfc cannot be combined with --retro-dir in this release.");
    }

    private static string RequireValue(string[] args, ref int index)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException($"A value is required after {args[index - 1]}.");
        return args[index];
    }
}
