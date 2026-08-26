namespace WiiCompiled.SimpleUI;

/// <summary>
/// A minimal standalone frontend for WiiCompiled-Setup.exe: pick a disc image, install, then play.
/// It talks to the setup executable exactly the way any frontend (Wheel Wizard included) does -
/// over its documented CLI and --progress-json protocol - so nothing about the setup project itself
/// changes. See SetupClient.cs for the protocol details.
/// </summary>
public sealed class MainForm : Form
{
    private readonly Settings _settings = Settings.Load();
    private CancellationTokenSource? _running;

    private readonly TextBox _setupExeBox = new() { ReadOnly = true };
    private readonly TextBox _gameBox = new();
    private readonly TextBox _installDirBox = new();
    private readonly CheckBox _retroCheckBox = new() { Text = "Include Retro Rewind" };
    private readonly TextBox _retroDirBox = new() { Enabled = false };
    private readonly Button _retroBrowseButton = new() { Text = "Browse...", Enabled = false };
    private readonly RadioButton _downloadPayloadRadio = new() { Text = "Download Retro-WFC payload (recommended)", Enabled = false };
    private readonly RadioButton _skipPayloadRadio = new() { Text = "Skip Retro-WFC payload", Enabled = false };
    private readonly CheckBox _legacyWfcCheckBox = new()
    {
        Text = "NewWFC (online for the base game, no Retro Rewind)"
    };
    // Master switch for whether Install/Update compiles the cheat library in at all - separate
    // from each entry's own Enabled flag in the Cheats window. Visible here, at the same level as
    // NewWFC, so it's obvious at a glance whether the next compile will include cheats, instead of
    // that only being discoverable inside the Cheats submenu.
    private readonly CheckBox _cheatsCheckBox = new() { Text = "Cheats", Checked = true };
    private readonly Button _installButton = new()
    {
        Text = "Install / Update", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(8, 4, 8, 4), Anchor = AnchorStyles.Left
    };
    private readonly ProgressBar _progressBar = new() { Minimum = 0, Maximum = 100 };
    private readonly Label _statusLabel = new() { AutoSize = true, Text = "Idle." };
    private static Button AutoSizedButton(string text) => new()
    {
        Text = text, Enabled = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(8, 4, 8, 4), Margin = new Padding(3, 3, 8, 3)
    };
    private readonly Button _launchBaseButton = AutoSizedButton("Play Mario Kart Wii");
    private readonly Button _launchRetroButton = AutoSizedButton("Play Retro Rewind");
    private readonly Button _checkButton = AutoSizedButton("Check status");
    private readonly Button _cheatsButton = AutoSizedButton("Cheats...");
    private readonly Button _myStuffButton = AutoSizedButton("My Stuff...");
    // Persistent, not just inside CheatsForm: a pending code-cheat change is otherwise invisible
    // once that window is closed, and someone could hit Play without realizing it never took
    // effect. Stays up until an actual recompile resolves it - see RefreshCheatsPendingBanner.
    private readonly Label _cheatsPendingBanner = new()
    {
        Dock = DockStyle.Top, AutoSize = false, Height = 32, TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(8, 0, 8, 0), Visible = false,
    };
    private readonly TextBox _logBox = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 8.5f)
    };

    public MainForm()
    {
        Text = "WiiCompiled";
        Width = 760;
        Height = 640;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 480);

        BuildLayout();
        LoadSettingsIntoControls();
        RefreshLaunchAvailability();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(_cheatsPendingBanner, 0, 0);
        root.Controls.Add(BuildInstallGroup(), 0, 1);
        root.Controls.Add(BuildPlayGroup(), 0, 2);
        root.Controls.Add(BuildLogGroup(), 0, 3);
    }

    /// <summary>Reflects CheatsService.GetPendingCodePatches - the same read-only diff CheatsForm's
    /// own banner uses, so the two never disagree. Call after anything that could change either
    /// side: opening this form, closing the Cheats window, or a successful install/recompile.</summary>
    private void RefreshCheatsPendingBanner()
    {
        if (string.IsNullOrWhiteSpace(_installDirBox.Text) || !File.Exists(InstalledSetupExePath))
        {
            _cheatsPendingBanner.Visible = false;
            return;
        }

        var (pendingAdd, pendingRemove) = CheatsService.GetPendingCodePatches(_installDirBox.Text);
        if (pendingAdd.Count == 0 && pendingRemove.Count == 0)
        {
            _cheatsPendingBanner.Visible = false;
            return;
        }

        _cheatsPendingBanner.Visible = true;
        _cheatsPendingBanner.BackColor = pendingAdd.Count > 0 ? Color.MistyRose : Color.Cornsilk;
        _cheatsPendingBanner.ForeColor = pendingAdd.Count > 0 ? Color.Firebrick : Color.DarkOrange;
        _cheatsPendingBanner.Text = $"Code cheats need a recompile: {pendingAdd.Count} to enable, " +
            $"{pendingRemove.Count} to disable. Open Cheats, or Install/Update, to apply.";
    }

    private GroupBox BuildInstallGroup()
    {
        var group = new GroupBox { Text = "Install", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        group.Controls.Add(layout);

        int row = 0;
        Label AddRow(string label, Control field, Control? button)
        {
            layout.RowCount = row + 1;
            var labelControl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) };
            layout.Controls.Add(labelControl, 0, row);
            field.Dock = DockStyle.Fill;
            layout.Controls.Add(field, 1, row);
            if (button is not null) layout.Controls.Add(button, 2, row);
            row++;
            return labelControl;
        }

        var setupBrowse = new Button { Text = "Locate..." };
        setupBrowse.Click += (_, _) => BrowseSetupExe();
        AddRow("WiiCompiled-Setup.exe:", _setupExeBox, setupBrowse);

        var gameBrowse = new Button { Text = "Browse..." };
        gameBrowse.Click += (_, _) => BrowseGame();
        AddRow("Wii disc image:", _gameBox, gameBrowse);

        var installBrowse = new Button { Text = "Browse..." };
        installBrowse.Click += (_, _) => BrowseInstallDirectory();
        AddRow("Install folder:", _installDirBox, installBrowse);

        layout.RowCount = row + 1;
        layout.Controls.Add(_retroCheckBox, 0, row);
        layout.SetColumnSpan(_retroCheckBox, 3);
        row++;
        _retroCheckBox.CheckedChanged += (_, _) => UpdateRetroControlsEnabled();

        _retroBrowseButton.Click += (_, _) => BrowseRetroDirectory();
        var retroDirLabel = AddRow("Retro Rewind folder:", _retroDirBox, _retroBrowseButton);

        layout.RowCount = row + 1;
        var wfcAndCheatsRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
        wfcAndCheatsRow.Controls.Add(_legacyWfcCheckBox);
        _cheatsCheckBox.Margin = new Padding(24, 3, 3, 3);
        wfcAndCheatsRow.Controls.Add(_cheatsCheckBox);
        layout.Controls.Add(wfcAndCheatsRow, 0, row);
        layout.SetColumnSpan(wfcAndCheatsRow, 3);
        row++;
        // Mutually exclusive: NewWFC-Legacy is base-only, Retro Rewind already has its own
        // Retro-WFC payload mechanism, and the setup backend rejects combining the two.
        _legacyWfcCheckBox.CheckedChanged += (_, _) =>
        {
            _retroCheckBox.Enabled = !_legacyWfcCheckBox.Checked;
        };
        _retroCheckBox.CheckedChanged += (_, _) =>
        {
            _legacyWfcCheckBox.Enabled = !_retroCheckBox.Checked;
        };

        layout.RowCount = row + 1;
        var payloadPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true };
        _downloadPayloadRadio.Checked = true;
        payloadPanel.Controls.Add(_downloadPayloadRadio);
        payloadPanel.Controls.Add(_skipPayloadRadio);
        layout.Controls.Add(payloadPanel, 1, row);
        layout.SetColumnSpan(payloadPanel, 2);
        row++;

        // Retro Rewind isn't part of this build's supported path right now. Left fully wired up
        // underneath (CLI flags, InstallOptions, ProductRepairService, etc. still work) - just
        // hidden here rather than torn out, to avoid a large, unrelated diff.
        _retroCheckBox.Visible = false;
        retroDirLabel.Visible = false;
        _retroDirBox.Visible = false;
        _retroBrowseButton.Visible = false;
        payloadPanel.Visible = false;
        _launchRetroButton.Visible = false;

        layout.RowCount = row + 1;
        _installButton.Click += async (_, _) => await RunInstallAsync();
        layout.Controls.Add(_installButton, 1, row);
        row++;

        layout.RowCount = row + 1;
        var progressPanel = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        _progressBar.Dock = DockStyle.Fill;
        progressPanel.Controls.Add(_progressBar);
        progressPanel.Controls.Add(_statusLabel);
        layout.Controls.Add(progressPanel, 1, row);
        layout.SetColumnSpan(progressPanel, 2);

        return group;
    }

    private GroupBox BuildPlayGroup()
    {
        var group = new GroupBox { Text = "Play", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        _launchBaseButton.Click += async (_, _) => await RunLaunchAsync(retro: false);
        _launchRetroButton.Click += async (_, _) => await RunLaunchAsync(retro: true);
        _checkButton.Click += async (_, _) => await RunCheckAsync();
        _cheatsButton.Click += async (_, _) => await OpenCheatsAsync();
        _myStuffButton.Click += (_, _) => OpenMyStuff();
        flow.Controls.Add(_launchBaseButton);
        flow.Controls.Add(_launchRetroButton);
        flow.Controls.Add(_checkButton);
        flow.Controls.Add(_cheatsButton);
        flow.Controls.Add(_myStuffButton);
        group.Controls.Add(flow);
        return group;
    }

    private GroupBox BuildLogGroup()
    {
        var group = new GroupBox { Text = "Log", Dock = DockStyle.Fill, Padding = new Padding(8) };
        group.Controls.Add(_logBox);
        return group;
    }

    private void LoadSettingsIntoControls()
    {
        _setupExeBox.Text = _settings.ResolveSetupExe() ?? "";
        _gameBox.Text = _settings.GamePath ?? "";
        _installDirBox.Text = string.IsNullOrWhiteSpace(_settings.InstallDirectory)
            ? Settings.DefaultInstallDirectory : _settings.InstallDirectory;
        _retroCheckBox.Checked = _settings.IncludeRetroRewind;
        _retroDirBox.Text = _settings.RetroDirectory ?? "";
        _downloadPayloadRadio.Checked = _settings.DownloadRetroWfcPayload;
        _skipPayloadRadio.Checked = !_settings.DownloadRetroWfcPayload;
        _legacyWfcCheckBox.Checked = _settings.EnableLegacyWfc;
        _cheatsCheckBox.Checked = _settings.EnableCheats;
        UpdateRetroControlsEnabled();
        _legacyWfcCheckBox.Enabled = !_retroCheckBox.Checked;
        _retroCheckBox.Enabled = !_legacyWfcCheckBox.Checked;

        if (string.IsNullOrWhiteSpace(_setupExeBox.Text))
            Log("WiiCompiled-Setup.exe was not found automatically. Click \"Locate...\" and point it " +
                "at the one produced by Build-Installer.ps1 (Launcher/dist/WiiCompiled-Setup.exe), or " +
                "an existing installation.");
    }

    private void UpdateRetroControlsEnabled()
    {
        var on = _retroCheckBox.Checked;
        _retroDirBox.Enabled = on;
        _retroBrowseButton.Enabled = on;
        _downloadPayloadRadio.Enabled = on;
        _skipPayloadRadio.Enabled = on;
    }

    private void BrowseSetupExe()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "WiiCompiled-Setup.exe|WiiCompiled-Setup.exe|All executables|*.exe",
            Title = "Locate WiiCompiled-Setup.exe"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _setupExeBox.Text = dialog.FileName;
    }

    private void BrowseGame()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Wii disc images|*.iso;*.gcm;*.gcz;*.ciso;*.wbfs;*.wia;*.rvz|All files|*.*",
            Title = "Select your clean PAL Mario Kart Wii (RMCP01) disc image"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _gameBox.Text = dialog.FileName;
    }

    private void BrowseInstallDirectory()
    {
        using var dialog = new FolderBrowserDialog { Description = "Select the install folder" };
        if (!string.IsNullOrWhiteSpace(_installDirBox.Text)) dialog.SelectedPath = _installDirBox.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) _installDirBox.Text = dialog.SelectedPath;
    }

    private void BrowseRetroDirectory()
    {
        using var dialog = new FolderBrowserDialog { Description = "Select Wheel Wizard's Retro Rewind folder (…/PulsarPacks/completed/RetroRewind/RetroRewind6)" };
        if (dialog.ShowDialog(this) == DialogResult.OK) _retroDirBox.Text = dialog.SelectedPath;
    }

    private void Log(string line) => _logBox.AppendText(line + Environment.NewLine);

    private void SaveCurrentSettings()
    {
        _settings.SetupExePath = _setupExeBox.Text;
        _settings.GamePath = _gameBox.Text;
        _settings.InstallDirectory = _installDirBox.Text;
        _settings.IncludeRetroRewind = _retroCheckBox.Checked;
        _settings.RetroDirectory = _retroDirBox.Text;
        _settings.DownloadRetroWfcPayload = _downloadPayloadRadio.Checked;
        _settings.EnableLegacyWfc = _legacyWfcCheckBox.Checked;
        _settings.EnableCheats = _cheatsCheckBox.Checked;
        _settings.Save();
    }

    // Install/recompile busy: blocks everything, including Cheats/My Stuff, since a recompile can
    // replace the very DOL/REL those windows classify cheat addresses against mid-edit.
    private void SetBusy(bool busy)
    {
        _installButton.Enabled = !busy;
        _launchBaseButton.Enabled = !busy && File.Exists(InstalledSetupExePath);
        _launchRetroButton.Enabled = !busy && File.Exists(InstalledSetupExePath);
        _checkButton.Enabled = !busy && File.Exists(InstalledSetupExePath);
        _cheatsButton.Enabled = !busy && File.Exists(InstalledSetupExePath);
        _myStuffButton.Enabled = !busy && File.Exists(InstalledSetupExePath);
    }

    // Launch-only busy: a game process is running. Blocks starting another launch or an install
    // (recompiling into GameAssets/DATA while the running game has it open is asking for trouble),
    // but deliberately leaves Cheats and My Stuff enabled - both only ever touch Config.toml or
    // their own JSON, and editing a live data cheat or a My Stuff slot *while playing* is the
    // whole point of the runtime's hot-reload (cheats) / next-launch (My Stuff) design. Disabling
    // them for the entire play session would defeat that.
    private void SetLaunchBusy(bool busy)
    {
        _installButton.Enabled = !busy;
        _launchBaseButton.Enabled = !busy && File.Exists(InstalledSetupExePath);
        _launchRetroButton.Enabled = !busy && File.Exists(InstalledSetupExePath);
        _checkButton.Enabled = !busy && File.Exists(InstalledSetupExePath);
    }

    private string InstalledSetupExePath => Path.Combine(
        string.IsNullOrWhiteSpace(_installDirBox.Text) ? "" : _installDirBox.Text, "WiiCompiled-Setup.exe");

    private void RefreshLaunchAvailability()
    {
        var installed = File.Exists(InstalledSetupExePath);
        _launchBaseButton.Enabled = installed;
        _launchRetroButton.Enabled = installed;
        _checkButton.Enabled = installed;
        _cheatsButton.Enabled = installed;
        _myStuffButton.Enabled = installed;
        RefreshCheatsPendingBanner();
    }

    private async Task OpenCheatsAsync()
    {
        // CheatsForm never runs the setup process itself - it just closes and asks for a
        // recompile, which happens here, on MainForm, using the same RunInstallAsync() the
        // Install/Update button uses (busy-state, progress bar, log, and the payload-bearing
        // _setupExeBox exe all live here already). A modal dialog is the wrong owner for a
        // multi-minute operation: it used to crash if closed mid-recompile.
        var recompileRequested = false;
        var playRequested = false;
        using var cheats = new CheatsForm(_installDirBox.Text,
            onRecompileRequested: () => recompileRequested = true,
            onPlayRequested: () => playRequested = true);
        cheats.ShowDialog(this);
        RefreshCheatsPendingBanner();
        if (recompileRequested)
        {
            await RunInstallAsync();
        }
        if (playRequested)
        {
            await RunLaunchAsync(retro: false);
        }
    }

    private void OpenMyStuff()
    {
        // Unlike Cheats, My Stuff never needs a recompile or a MainForm handoff - it only ever
        // writes Config.toml/its own JSON, both instant, so the window can just do everything
        // itself and there's nothing to react to once it closes.
        using var myStuff = new MyStuffForm(_installDirBox.Text);
        myStuff.ShowDialog(this);
    }

    private async Task RunInstallAsync()
    {
        if (string.IsNullOrWhiteSpace(_setupExeBox.Text) || !File.Exists(_setupExeBox.Text))
        {
            MessageBox.Show(this, "Locate WiiCompiled-Setup.exe first.", "WiiCompiled",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(_gameBox.Text))
        {
            MessageBox.Show(this, "Select your Mario Kart Wii disc image first.", "WiiCompiled",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(_installDirBox.Text))
        {
            MessageBox.Show(this, "Choose an install folder first.", "WiiCompiled",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_retroCheckBox.Checked && string.IsNullOrWhiteSpace(_retroDirBox.Text))
        {
            MessageBox.Show(this, "Choose a Retro Rewind folder, or untick \"Include Retro Rewind\".",
                "WiiCompiled", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SaveCurrentSettings();
        // Re-derives Config.toml/code_patches.local.txt from cheats-library.json itself before
        // anything else - the one thing every compile can fall back to regardless of whether
        // CheatsForm (or anything else) definitely flushed its own writes first, which is what
        // actually rules out "stale cheat data" rather than just hoping every other code path got
        // it right. Only after that is current does the pending-diff check below mean anything.
        try
        {
            var (syncedDataLines, syncedCodeLines) = CheatsService.SyncDerivedFilesFromLibrary(
                _installDirBox.Text, _cheatsCheckBox.Checked);
            Log(_cheatsCheckBox.Checked
                ? $"Cheats: {syncedDataLines} live data line(s), {syncedCodeLines} code line(s) to compile in."
                : "Cheats: disabled (the \"Cheats\" box on the main window is unchecked) - compiling with none.");
        }
        catch (Exception ex)
        {
            // A silent failure here (e.g. an unhandled exception in classification) would leave
            // whatever code_patches.local.txt already had on disk and compile with that stale
            // content instead - exactly the "cheats not compiling" symptom this whole method exists
            // to rule out. Surface it and stop, rather than proceed on data that may not reflect the
            // library.
            Log("FAILED to sync cheats from the library: " + ex.Message);
            MessageBox.Show(this, "Could not prepare the cheat library for compiling:\n\n" + ex.Message,
                "WiiCompiled", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        // Otherwise an otherwise-current install would silently skip cheats set up in a previous
        // Cheats-window session, since the product fingerprint has no idea code_patches changed -
        // see CheatsService.ForceRebuildIfCodePatchesPending.
        CheatsService.ForceRebuildIfCodePatchesPending(_installDirBox.Text);
        var arguments = new List<string> { "--silent", "--game", _gameBox.Text, "--install-dir", _installDirBox.Text };
        if (_retroCheckBox.Checked)
        {
            arguments.Add("--retro-dir");
            arguments.Add(_retroDirBox.Text);
            arguments.Add(_downloadPayloadRadio.Checked ? "--download-retro-wfc-payload" : "--skip-retro-wfc-payload");
        }
        else if (_legacyWfcCheckBox.Checked)
        {
            arguments.Add("--enable-legacy-wfc");
        }

        SetBusy(true);
        _progressBar.Value = 0;
        _statusLabel.Text = "Starting...";
        Log($"Running: {_setupExeBox.Text} {string.Join(' ', arguments)} --progress-json");

        _running = new CancellationTokenSource();
        var client = new SetupClient(_setupExeBox.Text);
        try
        {
            var result = await client.RunAsync(arguments.ToArray(),
                onProgress: (stage, message, percent) => BeginInvoke(() =>
                {
                    _progressBar.Value = Math.Clamp(percent, 0, 100);
                    _statusLabel.Text = $"[{stage}] {message}";
                }),
                onDiagnostic: line => BeginInvoke(() => Log(line)),
                _running.Token);

            if (result.Success)
            {
                _progressBar.Value = 100;
                _statusLabel.Text = "Installed.";
                Log($"Done. Installed at {result.InstallDirectory}");
                // Keeps the Cheats window's pending/compiled diff accurate even when this button,
                // not its own Recompile button, is what actually applied any code_patches.
                var compiledCodeLines = CheatsService.ReadCodePatches(CheatsService.LocalCodePatchesPath(_installDirBox.Text));
                File.WriteAllLines(CheatsService.CompiledCodePatchesMarkerPath(_installDirBox.Text), compiledCodeLines);
            }
            else
            {
                _statusLabel.Text = "Failed.";
                Log("FAILED: " + result.Error);
                MessageBox.Show(this, result.Error ?? "The install failed.", "WiiCompiled",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Failed.";
            Log("FAILED: " + ex.Message);
            MessageBox.Show(this, ex.Message, "WiiCompiled", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _running.Dispose();
            _running = null;
            SetBusy(false);
            RefreshLaunchAvailability();
        }
    }

    private async Task RunLaunchAsync(bool retro)
    {
        var installedExe = InstalledSetupExePath;
        if (!File.Exists(installedExe))
        {
            MessageBox.Show(this, "Install first.", "WiiCompiled", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetLaunchBusy(true);
        _statusLabel.Text = retro ? "Playing Retro Rewind..." : "Playing Mario Kart Wii...";
        Log($"Launching {(retro ? "Retro Rewind" : "Mario Kart Wii")}...");

        var client = new SetupClient(installedExe);
        try
        {
            var exitCode = await client.RunSimpleAsync(
                [retro ? "--launch-retro" : "--launch-base"],
                onDiagnostic: line => BeginInvoke(() => Log(line)),
                CancellationToken.None);
            _statusLabel.Text = exitCode == 0 ? "Idle." : $"The game exited with code {exitCode}.";
            Log($"Game process exited with code {exitCode}.");
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Failed.";
            Log("FAILED: " + ex.Message);
            MessageBox.Show(this, ex.Message, "WiiCompiled", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetLaunchBusy(false);
        }
    }

    private async Task RunCheckAsync()
    {
        var installedExe = InstalledSetupExePath;
        if (!File.Exists(installedExe)) return;

        SetBusy(true);
        _statusLabel.Text = "Checking...";
        var client = new SetupClient(installedExe);
        try
        {
            var result = await client.RunAsync(["--check-products"],
                onProgress: (stage, message, percent) => BeginInvoke(() =>
                {
                    _statusLabel.Text = $"[{stage}] {message}";
                    _progressBar.Value = Math.Clamp(percent, 0, 100);
                }),
                onDiagnostic: line => BeginInvoke(() => Log(line)),
                CancellationToken.None);
            // check-products reports protocol success even when a rebuild is required; the actual
            // signal is the exit code (2 = rebuild required), not the result's own success flag.
            var current = result.Success && result.ExitCode == 0;
            _statusLabel.Text = current ? "Up to date." : "Needs a rebuild.";
            Log(current ? "check-products: current." : "check-products: rebuild required.");
        }
        finally
        {
            SetBusy(false);
            RefreshLaunchAvailability();
        }
    }
}
