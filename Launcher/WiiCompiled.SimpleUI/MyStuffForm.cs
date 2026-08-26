using System.Diagnostics;

namespace WiiCompiled.SimpleUI;

/// <summary>
/// "My Stuff" slot manager: each slot is a folder under MyStuffService.RootDirectory layered over
/// the game's own files by filename (see MyStuffService / riivolution.cpp's RiivoDiscoverRoots) -
/// always highest priority, above Retro Rewind's own pack and the base disc. Every list row has its
/// own on/off toggle on the right (see List_DrawItem/List_MouseUp), plus a master "Enable My Stuff"
/// switch at the top that overrides all of them at once. Purely a boot-time Config.toml read -
/// unlike a code-target cheat, nothing here ever needs a recompile, only relaunching the game, so
/// (unlike CheatsForm) this window does all its own work instead of handing off to MainForm.
/// </summary>
public sealed class MyStuffForm : Form
{
    private readonly string _installDirectory;
    private readonly MyStuffLibrary _library;

    private readonly CheckBox _masterEnabledCheckBox = new() { Text = "Enable My Stuff", AutoSize = true, Dock = DockStyle.Top };
    private readonly Label _explanationLabel = new()
    {
        AutoSize = false, Dock = DockStyle.Top, Height = 44,
        Text = "Drop a file anywhere in a slot's folder and it replaces the game's own file with " +
            "the same name. no need to match the disc's own folder layout. Takes effect the next " +
            "time you launch the game.",
    };
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed };
    private readonly Button _addButton = new() { Text = "Add Slot...", AutoSize = true };
    private readonly Button _renameButton = new() { Text = "Rename...", AutoSize = true, Enabled = false };
    private readonly Button _removeButton = new() { Text = "Remove Slot", AutoSize = true, Enabled = false };
    private readonly Button _openFolderButton = new() { Text = "Open Folder", AutoSize = true, Enabled = false };
    private readonly Label _statusFooter = new() { AutoSize = true, Text = "" };

    private const int ToggleWidth = 32;
    private const int ToggleHeight = 16;
    private const int ToggleMargin = 8;

    public MyStuffForm(string installDirectory)
    {
        _installDirectory = installDirectory;
        _library = MyStuffService.Load(installDirectory);

        Text = "My Stuff";
        Width = 480;
        Height = 520;
        StartPosition = FormStartPosition.CenterParent;

        BuildLayout();
        RefreshList();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        _masterEnabledCheckBox.Checked = _library.Enabled;
        _masterEnabledCheckBox.CheckedChanged += (_, _) =>
        {
            _library.Enabled = _masterEnabledCheckBox.Checked;
            Persist();
        };
        root.Controls.Add(_masterEnabledCheckBox, 0, 0);
        root.Controls.Add(_explanationLabel, 0, 1);

        var listGroup = new GroupBox { Text = "Slots", Dock = DockStyle.Fill };
        listGroup.Controls.Add(_list);
        root.Controls.Add(listGroup, 0, 2);
        _list.SelectedIndexChanged += (_, _) => RefreshButtons();
        _list.DrawItem += List_DrawItem;
        _list.MouseUp += List_MouseUp;

        var buttonRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        _addButton.Click += (_, _) => AddSlot();
        _renameButton.Click += (_, _) => RenameSelectedSlot();
        _removeButton.Click += (_, _) => RemoveSelectedSlot();
        _openFolderButton.Click += (_, _) => OpenSelectedSlotFolder();
        buttonRow.Controls.Add(_addButton);
        buttonRow.Controls.Add(_renameButton);
        buttonRow.Controls.Add(_removeButton);
        buttonRow.Controls.Add(_openFolderButton);
        root.Controls.Add(buttonRow, 0, 3);

        root.Controls.Add(_statusFooter, 0, 4);
    }

    private MyStuffSlot? SelectedSlot =>
        _list.SelectedIndex >= 0 && _list.SelectedIndex < _library.Slots.Count
            ? _library.Slots[_list.SelectedIndex]
            : null;

    private void RefreshButtons()
    {
        var hasSelection = SelectedSlot is not null;
        _renameButton.Enabled = hasSelection;
        _removeButton.Enabled = hasSelection;
        _openFolderButton.Enabled = hasSelection;
    }

    private void RefreshList()
    {
        var index = _list.SelectedIndex;
        _list.Items.Clear();
        foreach (var slot in _library.Slots)
        {
            _list.Items.Add(slot.Name);
        }
        _list.SelectedIndex = index >= 0 && index < _library.Slots.Count ? index : -1;
        RefreshButtons();
        _list.Invalidate();
    }

    private void AddSlot()
    {
        var slot = new MyStuffSlot
        {
            Name = MyStuffService.NextDefaultName(_library.Slots),
            FolderId = MyStuffService.NextFolderId(_library.Slots),
            Enabled = true,
        };
        var folder = MyStuffService.SlotDirectory(_installDirectory, slot);
        Directory.CreateDirectory(folder);
        _library.Slots.Add(slot);
        RefreshList();
        _list.SelectedIndex = _library.Slots.Count - 1;
        Persist();
        _statusFooter.Text = $"Created {folder}";
    }

    private void RenameSelectedSlot()
    {
        if (SelectedSlot is not { } slot)
        {
            return;
        }
        using var dialog = new TextPromptDialog("Rename Slot", "Name:", slot.Name);
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.Value))
        {
            return;
        }
        // Only the display label changes - the folder on disk (FolderId) never gets renamed, so
        // this can never fail because Explorer or the game has the folder open.
        slot.Name = dialog.Value.Trim();
        RefreshList();
        Persist();
    }

    private void RemoveSelectedSlot()
    {
        var index = _list.SelectedIndex;
        if (index < 0)
        {
            return;
        }
        var slot = _library.Slots[index];
        var folder = MyStuffService.SlotDirectory(_installDirectory, slot);
        _library.Slots.RemoveAt(index);
        RefreshList();
        Persist();
        // Deliberately not deleted - it may hold files the user cares about that just happen to
        // also live under MyStuff. Removing the slot only stops it being layered in.
        _statusFooter.Text = $"Removed from the list - its folder is untouched: {folder}";
    }

    private void OpenSelectedSlotFolder()
    {
        if (SelectedSlot is not { } slot)
        {
            return;
        }
        var folder = MyStuffService.SlotDirectory(_installDirectory, slot);
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private void Persist()
    {
        MyStuffService.Save(_installDirectory, _library);
        try
        {
            MyStuffService.WriteConfig(CheatsService.DefaultConfigTomlPath(), _installDirectory, _library);
        }
        catch (Exception ex)
        {
            _statusFooter.Text = "Could not write Config.toml: " + ex.Message;
        }
    }

    private static Rectangle ToggleBounds(Rectangle itemBounds) => new(
        itemBounds.Right - ToggleMargin - ToggleWidth,
        itemBounds.Top + (itemBounds.Height - ToggleHeight) / 2,
        ToggleWidth, ToggleHeight);

    /// <summary>Draws the slot name on the left and a small on/off switch on the right (green
    /// track + white knob when enabled, gray track + knob-left when not) - a plain ListBox, not a
    /// CheckedListBox, so a click anywhere in the row only ever selects it; only List_MouseUp,
    /// gated to the switch's own bounds, ever toggles anything.</summary>
    private void List_DrawItem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= _library.Slots.Count)
        {
            return;
        }
        var slot = _library.Slots[e.Index];

        var toggleBounds = ToggleBounds(e.Bounds);
        using (var trackBrush = new SolidBrush(slot.Enabled ? Color.MediumSeaGreen : Color.Gainsboro))
        {
            e.Graphics.FillRectangle(trackBrush, toggleBounds);
        }
        e.Graphics.DrawRectangle(SystemPens.ControlDark, toggleBounds);
        var knobSize = toggleBounds.Height - 4;
        var knobX = slot.Enabled ? toggleBounds.Right - 2 - knobSize : toggleBounds.Left + 2;
        e.Graphics.FillEllipse(Brushes.White, knobX, toggleBounds.Top + 2, knobSize, knobSize);

        var textLeft = e.Bounds.Left + 4;
        var textBounds = new Rectangle(
            textLeft, e.Bounds.Top, Math.Max(0, toggleBounds.Left - ToggleMargin - textLeft), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, slot.Name, e.Font, textBounds, e.ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        e.DrawFocusRectangle();
    }

    private void List_MouseUp(object? sender, MouseEventArgs e)
    {
        var index = _list.IndexFromPoint(e.Location);
        if (index < 0 || index >= _library.Slots.Count)
        {
            return;
        }
        if (!ToggleBounds(_list.GetItemRectangle(index)).Contains(e.Location))
        {
            return;
        }
        _library.Slots[index].Enabled = !_library.Slots[index].Enabled;
        _list.Invalidate();
        Persist();
    }
}
