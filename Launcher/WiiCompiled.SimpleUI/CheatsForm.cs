using CheckBoxState = System.Windows.Forms.VisualStyles.CheckBoxState;

namespace WiiCompiled.SimpleUI;

/// <summary>
/// Dolphin-Gecko-manager-style cheat list: named entries, each with a checkbox, an
/// Add/Edit/Remove workflow, and no download button. Checking an entry on/off (or adding, editing,
/// removing one) immediately re-derives what's active: each enabled entry's lines are classified
/// against the installed game's own main.dol/StaticR.rel exactly like the translator would (see
/// CheatsService) - a data-target line goes in Config.toml (live, no rebuild), a code-target line
/// goes in recomp.yml's code_patches (needs a recompile, tracked by the banner/Recompile button).
///
/// This window never runs the setup process itself - "Recompile now" and "Play Mario Kart Wii"
/// both just close the dialog and ask MainForm to do the actual work (onRecompileRequested,
/// onPlayRequested), the same way its own Install/Update and Play buttons do. A modal dialog is
/// the wrong owner for a multi-minute operation: closing it mid-recompile used to crash the app
/// (BeginInvoke/control access after the dialog's handle was gone). Routing the actual work
/// through MainForm's own long-lived busy-state/progress/log means this window can be closed
/// anytime without disturbing anything.
///
/// There is deliberately no "Apply" button: every check/add/edit/remove already writes
/// Config.toml and code_patches.local.txt immediately (see ApplyAndRefreshBanner), so closing this
/// window - by any means, not just Recompile/Play - never loses anything. A data-target cheat is
/// live as soon as the running game's own periodic poll picks up Config.toml (cheat_codes.h); a
/// code-target cheat is already staged the moment you check it and just needs Recompile (or
/// MainForm's own Install/Update, or Play here) to actually be compiled in - an earlier version of
/// this window had a separate "Apply now" button that did nothing but confirm this, and its mere
/// existence was confusing enough (implying a required extra step) that it was removed.
/// </summary>
public sealed class CheatsForm : Form
{
    private readonly string _installDirectory;
    private readonly Action _onRecompileRequested;
    private readonly Action _onPlayRequested;
    private readonly List<CheatEntry> _entries;
    // Parallel to _entries, rebuilt by ApplyAndRefreshBanner - the row's status swatch color. See
    // RefreshItemColors for what each color means; always populated, never "no color."
    private readonly List<Color> _itemColors = new();
    // Cached on first paint (see CheckBoxGlyphSize) - CreateGraphics needs a window handle, which
    // doesn't exist yet during the constructor's own BuildLayout/RefreshList.
    private Size? _checkBoxGlyphSize;

    // Plain ListBox, not CheckedListBox: CheckedListBox owns its checkbox column internally (its
    // check state is unrelated to CheatEntry.Enabled, which is the actual source of truth here),
    // which made the right-side status swatch unreliable to paint and made "click anywhere in the
    // row toggles it" hard to fully suppress. Drawing everything ourselves - checkbox, swatch, and
    // gating the toggle to the glyph's own bounds in List_MouseUp - sidesteps both: a plain
    // ListBox's click only ever selects, never toggles anything on its own.
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed };
    private readonly Label _nameValue = new() { AutoSize = true, Text = "" };
    private readonly Label _creatorValue = new() { AutoSize = true, Text = "" };
    private readonly TextBox _descriptionValue = new()
    {
        Multiline = true, ReadOnly = true, Height = 50, Dock = DockStyle.Top, BorderStyle = BorderStyle.None,
    };
    private readonly TextBox _codesPreview = new()
    {
        Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical,
        Font = new Font(FontFamily.GenericMonospace, 9f),
    };
    private readonly Button _addButton = new() { Text = "Add New Code...", AutoSize = true };
    private readonly Button _editButton = new() { Text = "Edit Code...", AutoSize = true, Enabled = false };
    private readonly Button _removeButton = new() { Text = "Remove Code", AutoSize = true, Enabled = false };
    private readonly Label _bannerLabel = new()
    {
        Dock = DockStyle.Top, AutoSize = false, Height = 36, TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(8, 0, 8, 0), Visible = false,
    };
    // Separate from _bannerLabel (which is red/orange, for "pending recompile") - a purple/indigo
    // tone reads as its own distinct warning category: lines that will never do anything, not lines
    // waiting on a recompile. See ApplyAndRefreshBanner.
    private readonly Label _unsupportedBannerLabel = new()
    {
        Dock = DockStyle.Top, AutoSize = false, Height = 36, TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(8, 0, 8, 0), Visible = false, BackColor = Color.Thistle, ForeColor = Color.Indigo,
    };
    private readonly Button _recompileButton = new() { Text = "Recompile now", Enabled = false, AutoSize = true };
    private readonly Button _playButton = new() { Text = "Play Mario Kart Wii", AutoSize = true };
    private readonly Label _statusFooter = new() { AutoSize = true, Text = "" };
    // Concrete, timestamped feedback for the data-target (RAM-write) cheats specifically, in place
    // of the old "Apply now" button - those are already live as soon as the running game's own
    // periodic poll next reads Config.toml (cheat_codes.h), so there's nothing to press; this just
    // shows exactly when this window last wrote that file, for anyone who wants to actually see it
    // happened rather than take it on faith.
    private readonly Label _dataStatusLabel = new() { Dock = DockStyle.Top, AutoSize = true, ForeColor = Color.DimGray };
    private DateTime? _dataCheatsAppliedAt;

    public CheatsForm(string installDirectory, Action onRecompileRequested, Action onPlayRequested)
    {
        _installDirectory = installDirectory;
        _onRecompileRequested = onRecompileRequested;
        _onPlayRequested = onPlayRequested;
        _entries = CheatsService.LoadLibrary(installDirectory);

        Text = "Cheats";
        Width = 560;
        Height = 700;
        StartPosition = FormStartPosition.CenterParent;

        BuildLayout();
        RefreshList();
        ApplyAndRefreshBanner();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(_bannerLabel, 0, 0);
        root.Controls.Add(_unsupportedBannerLabel, 0, 1);

        var listGroup = new GroupBox { Text = "Codes", Dock = DockStyle.Fill };
        listGroup.Controls.Add(_list);
        root.Controls.Add(listGroup, 0, 2);
        _list.SelectedIndexChanged += (_, _) => RefreshDetails();
        _list.DrawItem += List_DrawItem;
        _list.MouseUp += List_MouseUp;

        var detailPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        detailPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detailPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detailPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detailPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detailPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        detailPanel.Controls.Add(LabelRow("Name:", _nameValue), 0, 0);
        detailPanel.Controls.Add(LabelRow("Creator:", _creatorValue), 0, 1);
        detailPanel.Controls.Add(new Label { Text = "Description:", AutoSize = true }, 0, 2);
        detailPanel.Controls.Add(_descriptionValue, 0, 3);
        var codesGroup = new GroupBox { Text = "Code", Dock = DockStyle.Fill };
        codesGroup.Controls.Add(_codesPreview);
        detailPanel.Controls.Add(codesGroup, 0, 4);
        root.Controls.Add(detailPanel, 0, 3);

        root.Controls.Add(_dataStatusLabel, 0, 4);

        var buttonRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        _addButton.Click += (_, _) => AddEntry();
        _editButton.Click += (_, _) => EditSelectedEntry();
        _removeButton.Click += (_, _) => RemoveSelectedEntry();
        buttonRow.Controls.Add(_addButton);
        buttonRow.Controls.Add(_editButton);
        buttonRow.Controls.Add(_removeButton);
        root.Controls.Add(buttonRow, 0, 5);

        var footerRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        _recompileButton.Click += (_, _) =>
        {
            // Close first: the recompile itself runs on MainForm after this dialog returns, so
            // there's nothing left here that could touch a disposed control.
            Close();
            _onRecompileRequested();
        };
        _playButton.Click += (_, _) =>
        {
            // Same reasoning as Recompile: launching (and waiting for) the game is MainForm's job,
            // not a modal dialog's - this window just asks for it and gets out of the way.
            Close();
            _onPlayRequested();
        };
        footerRow.Controls.Add(_recompileButton);
        footerRow.Controls.Add(_playButton);
        footerRow.Controls.Add(_statusFooter);
        root.Controls.Add(footerRow, 0, 6);
    }

    private static Control LabelRow(string label, Label value)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 0, 4, 0) });
        panel.Controls.Add(value);
        return panel;
    }

    private void RefreshList()
    {
        var index = _list.SelectedIndex;
        _list.Items.Clear();
        foreach (var entry in _entries)
        {
            _list.Items.Add(entry.Name);
        }
        _list.SelectedIndex = index >= 0 && index < _entries.Count ? index : -1;
        RefreshDetails();
    }

    private CheatEntry? SelectedEntry =>
        _list.SelectedIndex >= 0 && _list.SelectedIndex < _entries.Count ? _entries[_list.SelectedIndex] : null;

    private void RefreshDetails()
    {
        var entry = SelectedEntry;
        _editButton.Enabled = entry is not null;
        _removeButton.Enabled = entry is not null;
        _nameValue.Text = entry?.Name ?? "";
        _creatorValue.Text = entry?.Creator ?? "";
        _descriptionValue.Text = entry?.Description ?? "";
        _codesPreview.Text = entry?.Codes ?? "";
    }

    private const int SwatchSize = 12;
    private const int SwatchMargin = 6;

    /// <summary>Checkbox glyph size, same for checked/unchecked - cached after the first paint so
    /// List_DrawItem and List_MouseUp (which needs it to hit-test a click before any paint has
    /// necessarily happened) always agree on exactly the same bounds.</summary>
    private Size CheckBoxGlyphSize(Graphics graphics)
    {
        _checkBoxGlyphSize ??= CheckBoxRenderer.GetGlyphSize(graphics, CheckBoxState.UncheckedNormal);
        return _checkBoxGlyphSize.Value;
    }

    private Rectangle CheckBoxBounds(Rectangle itemBounds, Graphics graphics)
    {
        var size = CheckBoxGlyphSize(graphics);
        return new Rectangle(
            itemBounds.Left + 2, itemBounds.Top + (itemBounds.Height - size.Height) / 2,
            size.Width, size.Height);
    }

    /// <summary>Draws the checkbox glyph (matching what CheckedListBox draws natively), the
    /// entry's name, and a small color swatch on the right - green (live either way: no code-target
    /// lines at all, or its code-target lines already match what's compiled), orange (enabled, has
    /// a code-target line not yet compiled in - needs Recompile to take effect), red (disabled, but
    /// a code-target line is still baked into the currently compiled game until the next
    /// recompile), or gray (disabled, has code-target lines, nothing pending - enabling it will
    /// need a recompile, same as orange would once checked). The swatch is shown regardless of
    /// whether a game happens to be running right now - it reflects the cheat's own classification
    /// and compiled/pending state, nothing about a live process.</summary>
    private void List_DrawItem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= _entries.Count)
        {
            return;
        }

        var checkBoxState = _entries[e.Index].Enabled ? CheckBoxState.CheckedNormal : CheckBoxState.UncheckedNormal;
        var checkBoxBounds = CheckBoxBounds(e.Bounds, e.Graphics);
        CheckBoxRenderer.DrawCheckBox(e.Graphics, checkBoxBounds.Location, checkBoxState);

        var swatchBounds = new Rectangle(
            e.Bounds.Right - SwatchMargin - SwatchSize,
            e.Bounds.Top + (e.Bounds.Height - SwatchSize) / 2,
            SwatchSize, SwatchSize);
        if (e.Index < _itemColors.Count)
        {
            using var brush = new SolidBrush(_itemColors[e.Index]);
            e.Graphics.FillRectangle(brush, swatchBounds);
            e.Graphics.DrawRectangle(SystemPens.ControlDark, swatchBounds);
        }

        var textLeft = checkBoxBounds.Right + 4;
        var textBounds = new Rectangle(
            textLeft, e.Bounds.Top,
            Math.Max(0, swatchBounds.Left - SwatchMargin - textLeft), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, _entries[e.Index].Name, e.Font, textBounds, e.ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        e.DrawFocusRectangle();
    }

    /// <summary>A plain ListBox click only ever selects (see the _list field) - this is what makes
    /// a click actually toggle something, but only when it lands on the checkbox glyph itself;
    /// clicking the rest of the row (or blank space below the last item, or the click that merely
    /// brings this window to the foreground) does nothing beyond the selection ListBox already
    /// gives for free, matching Dolphin's own cheat list.</summary>
    private void List_MouseUp(object? sender, MouseEventArgs e)
    {
        var index = _list.IndexFromPoint(e.Location);
        if (index < 0 || index >= _entries.Count)
        {
            return;
        }
        using var graphics = _list.CreateGraphics();
        var checkBoxBounds = CheckBoxBounds(_list.GetItemRectangle(index), graphics);
        if (!checkBoxBounds.Contains(e.Location))
        {
            return;
        }
        _entries[index].Enabled = !_entries[index].Enabled;
        _list.Invalidate();
        ApplyAndRefreshBanner();
    }

    private void AddEntry()
    {
        using var dialog = new CheatEntryDialog(null);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        // A freshly-added code is checked by default: CheatEntry.Enabled defaults to false, and
        // without this a code you just added does nothing until you separately go check its box -
        // easy to miss, and easy to mistake for "the recompile didn't pick up my new code."
        dialog.Result.Enabled = true;
        _entries.Add(dialog.Result);
        RefreshList();
        _list.SelectedIndex = _entries.Count - 1;
        ApplyAndRefreshBanner();
    }

    private void EditSelectedEntry()
    {
        if (SelectedEntry is not { } entry)
        {
            return;
        }
        using var dialog = new CheatEntryDialog(entry);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        var index = _list.SelectedIndex;
        _entries[index] = dialog.Result;
        _entries[index].Enabled = entry.Enabled;
        RefreshList();
        _list.SelectedIndex = index;
        ApplyAndRefreshBanner();
    }

    private void RemoveSelectedEntry()
    {
        var index = _list.SelectedIndex;
        if (index < 0)
        {
            return;
        }
        _entries.RemoveAt(index);
        RefreshList();
        ApplyAndRefreshBanner();
    }

    /// <summary>Persists the library, re-derives Config.toml/recomp.yml from every enabled entry's
    /// lines, refreshes the pending-recompile banner, and recolors the list (see
    /// RefreshItemColors) - the single place any list change (check, add, edit, remove) routes
    /// through.</summary>
    private void ApplyAndRefreshBanner()
    {
        CheatsService.SaveLibrary(_installDirectory, _entries);

        // Classified per entry, not as one flattened/deduplicated blob: DolCodePatcher.ParseLines
        // groups a two-line (08/09) code by walking lines sequentially, and flattening every
        // entry's lines together first could let one entry's line get misread as another entry's
        // second line if an entry's own body ends with an unpaired first line. Every entry is
        // classified here (not just enabled ones) since the list coloring below needs a disabled
        // entry's own code lines too (to know whether they're still baked into the compiled game).
        var perEntryClassified = _entries
            .Select(e => CheatsService.Classify(_installDirectory, e.Lines))
            .ToList();
        var classified = _entries.Zip(perEntryClassified)
            .Where(pair => pair.First.Enabled)
            .SelectMany(pair => pair.Second)
            .ToList();
        var dataLines = classified.Where(c => c.Kind == CheatKind.Data).SelectMany(c => c.RawLines).ToList();

        string? writeError = null;
        try
        {
            // SaveLibrary above already wrote cheats-library.json, so this re-reads exactly what
            // was just saved - same result as writing dataLines/codeLines directly, but routed
            // through the one shared path MainForm.RunInstallAsync also calls before every
            // compile, instead of two independent copies of this logic that could drift apart.
            CheatsService.SyncDerivedFilesFromLibrary(_installDirectory);
            _dataCheatsAppliedAt = DateTime.Now;
        }
        catch (Exception ex)
        {
            writeError = "Could not write cheat files: " + ex.Message;
        }

        var (pendingAdd, pendingRemove) = CheatsService.GetPendingCodePatches(_installDirectory);

        // Grouped by code type (0xC2, 0x20, ...) so "3 lines silently do nothing" becomes "which
        // types, how many of each" - the same summary regardless of whether the cause was a genuinely
        // unsupported type, a 'po' code, or an unparseable line (grouped under "invalid" instead).
        var unsupportedGroups = classified
            .Where(c => c.Kind == CheatKind.Malformed)
            .GroupBy(c => CheatsService.TryGetCodeType(c.RawLines[0]) is { } t ? $"0x{t:X2}" : "invalid")
            .OrderByDescending(g => g.Count())
            .ToList();
        if (unsupportedGroups.Count > 0)
        {
            _unsupportedBannerLabel.Visible = true;
            var total = unsupportedGroups.Sum(g => g.Count());
            var summary = string.Join(", ", unsupportedGroups.Select(g => $"{g.Key} ({g.Count()})"));
            _unsupportedBannerLabel.Text =
                $"{total} unsupported code type line(s) ignored: {summary}";
        }
        else
        {
            _unsupportedBannerLabel.Visible = false;
        }

        if (pendingAdd.Count > 0 || pendingRemove.Count > 0)
        {
            _bannerLabel.Visible = true;
            _bannerLabel.BackColor = pendingAdd.Count > 0 ? Color.MistyRose : Color.Cornsilk;
            _bannerLabel.ForeColor = pendingAdd.Count > 0 ? Color.Firebrick : Color.DarkOrange;
            _bannerLabel.Text = $"Code cheats out of sync with the compiled game: " +
                $"{pendingAdd.Count} to enable, {pendingRemove.Count} to disable. Click Recompile.";
            _recompileButton.Enabled = true;
        }
        else
        {
            _bannerLabel.Visible = false;
            _recompileButton.Enabled = false;
        }

        // Malformed lines already have their own banner above; only InvalidAddress needs a mention
        // here, so a code-type warning and an address warning don't say overlapping things at once.
        // writeError takes priority over both - a failed write means nothing below can be trusted.
        var invalidAddressCount = classified.Count(c => c.Kind == CheatKind.InvalidAddress);
        _statusFooter.Text = writeError
            ?? (invalidAddressCount > 0
                ? $"{invalidAddressCount} line(s) across enabled codes target an address outside the installed DOL/REL."
                : "");

        if (writeError is null)
        {
            _dataStatusLabel.Text = dataLines.Count > 0
                ? $"{dataLines.Count} data cheat line(s) written to Config.toml at " +
                  $"{_dataCheatsAppliedAt:HH:mm:ss} - live in a running game within about a second."
                : "No data-target cheats currently enabled.";
        }

        RefreshItemColors(perEntryClassified);
    }

    /// <summary>Recomputes _itemColors from each entry's own classified code lines (already known,
    /// not reclassified here) against what's actually baked into the currently compiled game. Every
    /// entry gets a color, checked or not, running game or not - it's a property of the cheat
    /// itself (data vs code, and pending vs settled), never of a live process:
    /// - green: no code-target lines at all (data-only - always applies live, in either direction,
    ///   checked or not - a recompile is never involved).
    /// - gold: enabled, and every code-target line is already compiled in (settled *right now*),
    ///   but it still modifies code - unlike green, unchecking this later will need a Recompile
    ///   before that removal actually takes effect (it'll go red the moment you uncheck it).
    /// - orange: enabled with a code-target line not yet compiled in - needs Recompile to actually
    ///   take effect.
    /// - red: disabled, but a code-target line is still compiled in - the compiled game still has
    ///   it until the next recompile.
    /// - gray: disabled, has code-target lines, and none are compiled in - nothing pending, but
    ///   enabling this will need a recompile (same as orange would, once checked).
    /// </summary>
    private void RefreshItemColors(IReadOnlyList<List<ClassifiedCheat>> perEntryClassified)
    {
        var compiled = new HashSet<string>(CheatsService.ReadCompiledCodePatches(_installDirectory));
        _itemColors.Clear();
        for (var i = 0; i < _entries.Count; i++)
        {
            var entryCodeLines = perEntryClassified[i]
                .Where(c => c.Kind == CheatKind.Code)
                .SelectMany(c => c.RawLines)
                .ToList();

            Color color;
            if (entryCodeLines.Count == 0)
            {
                color = Color.Green;
            }
            else if (_entries[i].Enabled)
            {
                color = entryCodeLines.All(compiled.Contains) ? Color.Gold : Color.DarkOrange;
            }
            else
            {
                color = entryCodeLines.Any(compiled.Contains) ? Color.Red : Color.Gray;
            }
            _itemColors.Add(color);
        }
        _list.Invalidate();
    }
}
