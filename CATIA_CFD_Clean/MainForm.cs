using System.Windows.Forms;

namespace CATIA_CFD_Clean;

internal sealed class MainForm : Form
{
    private object? _body1;
    private object? _body2;

    private readonly Label _status = new()
    {
        Dock = DockStyle.Bottom,
        Height = 40,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(8, 0, 8, 0),
    };

    public MainForm()
    {
        Text = "CATIA Body Viewer";
        Width = 460;
        Height = 240;
        StartPosition = FormStartPosition.CenterScreen;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(12),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        table.Controls.Add(MakeButton("Store as Body 1", () => Store(1)), 0, 0);
        table.Controls.Add(MakeButton("Store as Body 2", () => Store(2)), 1, 0);
        table.Controls.Add(MakeButton("Show Body 1 only", () => ShowOnly(1)), 0, 1);
        table.Controls.Add(MakeButton("Show Body 2 only", () => ShowOnly(2)), 1, 1);

        Controls.Add(table);
        Controls.Add(_status);
        SetStatus("Select a body in CATIA, then click a Store button.");
    }

    private static Button MakeButton(string text, Action onClick)
    {
        var button = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            Margin = new Padding(6),
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void Store(int slot)
    {
        try
        {
            dynamic doc = CatiaConnection.GetActiveDocument();
            object? body = CatiaBodies.GetSelectedBody(doc);
            if (body is null)
            {
                SetStatus("Select a body in CATIA first.");
                return;
            }

            if (slot == 1) _body1 = body;
            else _body2 = body;

            SetStatus($"Stored the selected body as Body {slot}.");
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
    }

    private void ShowOnly(int slot)
    {
        object? body = slot == 1 ? _body1 : _body2;
        if (body is null)
        {
            SetStatus($"Body {slot} is not stored yet.");
            return;
        }

        try
        {
            dynamic doc = CatiaConnection.GetActiveDocument();
            CatiaBodies.ShowOnly(doc, body);
            SetStatus($"Showing Body {slot} only.");
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
    }

    private void SetStatus(string message) => _status.Text = message;
}
