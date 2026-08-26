namespace WiiCompiled.SimpleUI;

/// <summary>Add/Edit dialog for one CheatEntry - see CheatsForm.</summary>
internal sealed class CheatEntryDialog : Form
{
    private readonly TextBox _nameBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _creatorBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _descriptionBox = new()
    {
        Dock = DockStyle.Fill, Multiline = true, Height = 60, ScrollBars = ScrollBars.Vertical,
    };
    private readonly TextBox _codesBox = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical,
        Font = new Font(FontFamily.GenericMonospace, 9f), AcceptsReturn = true,
    };
    private readonly Label _errorLabel = new() { AutoSize = true, ForeColor = Color.Firebrick, Text = "" };

    public CheatEntry Result { get; private set; } = new();

    public CheatEntryDialog(CheatEntry? existing)
    {
        Text = existing is null ? "Add New Code" : "Edit Code";
        Width = 520;
        Height = 480;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;

        if (existing is not null)
        {
            _nameBox.Text = existing.Name;
            _creatorBox.Text = existing.Creator;
            _descriptionBox.Text = existing.Description;
            _codesBox.Text = existing.Codes;
        }

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, Padding = new Padding(10) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(new Label { Text = "Name:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) }, 0, 0);
        root.Controls.Add(_nameBox, 1, 0);
        root.Controls.Add(new Label { Text = "Creator:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) }, 0, 1);
        root.Controls.Add(_creatorBox, 1, 1);
        root.Controls.Add(new Label { Text = "Description:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) }, 0, 2);
        root.Controls.Add(_descriptionBox, 1, 2);

        var codesGroup = new GroupBox { Text = "Code (one \"AAAAAAAA VVVVVVVV\" per line)", Dock = DockStyle.Fill };
        codesGroup.Controls.Add(_codesBox);
        root.Controls.Add(codesGroup, 0, 3);
        root.SetColumnSpan(codesGroup, 2);

        root.Controls.Add(_errorLabel, 0, 4);
        root.SetColumnSpan(_errorLabel, 2);

        var buttonRow = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var okButton = new Button { Text = "OK", AutoSize = true };
        okButton.Click += (_, _) => TryAccept();
        buttonRow.Controls.Add(cancelButton);
        buttonRow.Controls.Add(okButton);
        root.Controls.Add(buttonRow, 0, 5);
        root.SetColumnSpan(buttonRow, 2);

        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    private void TryAccept()
    {
        if (string.IsNullOrWhiteSpace(_nameBox.Text))
        {
            _errorLabel.Text = "Name is required.";
            return;
        }
        if (string.IsNullOrWhiteSpace(_codesBox.Text))
        {
            _errorLabel.Text = "At least one code line is required.";
            return;
        }

        Result = new CheatEntry
        {
            Name = _nameBox.Text.Trim(),
            Creator = _creatorBox.Text.Trim(),
            Description = _descriptionBox.Text.Trim(),
            Codes = _codesBox.Text.Trim(),
        };
        DialogResult = DialogResult.OK;
        Close();
    }
}
