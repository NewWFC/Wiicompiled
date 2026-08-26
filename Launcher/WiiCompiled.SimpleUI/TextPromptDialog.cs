namespace WiiCompiled.SimpleUI;

/// <summary>Minimal "one text field, OK/Cancel" prompt - used by MyStuffForm to rename a slot.</summary>
internal sealed class TextPromptDialog : Form
{
    private readonly TextBox _textBox = new() { Dock = DockStyle.Top };

    public string Value => _textBox.Text;

    public TextPromptDialog(string title, string label, string initialValue)
    {
        Text = title;
        Width = 360;
        Height = 150;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(new Label { Text = label, AutoSize = true }, 0, 0);
        _textBox.Text = initialValue;
        root.Controls.Add(_textBox, 0, 1);

        var buttonRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var okButton = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        buttonRow.Controls.Add(cancelButton);
        buttonRow.Controls.Add(okButton);
        root.Controls.Add(buttonRow, 0, 2);

        AcceptButton = okButton;
        CancelButton = cancelButton;
    }
}
