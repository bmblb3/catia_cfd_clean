using System.Windows.Forms;

namespace CATIA_CFD_Clean;

// Asks the user to select seal faces in CATIA.
internal sealed class SelectFacesDialog : Form
{
    public SelectFacesDialog(string bodyLabel)
    {
        Text = $"Select seal faces - {bodyLabel}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        TopMost = true;
        ClientSize = new Size(360, 140);

        var instructions = new Label
        {
            Text = $"In CATIA, select one or more faces of {bodyLabel}\n" +
                   "(Ctrl+click to select several).\n\n" +
                   "Then click OK.",
            Location = new Point(12, 12),
            Size = new Size(336, 70),
        };

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(200, 95),
            Size = new Size(80, 30),
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(288, 95),
            Size = new Size(80, 30),
        };

        Controls.AddRange(new Control[] { instructions, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }
}

// Allows user to adjust seal thickness and direction after seeing the initial result.
internal sealed class AdjustSealDialog : Form
{
    private readonly NumericUpDown _distance = new()
    {
        DecimalPlaces = 2,
        Minimum = 0.01m,
        Maximum = 1000m,
        Increment = 0.5m,
        Value = 3m,
        Width = 90,
    };

    private readonly CheckBox _reverse = new() { Text = "Reverse direction", AutoSize = true };

    public double DistanceMm => (double)_distance.Value;
    public bool Reverse => _reverse.Checked;

    public AdjustSealDialog(double currentDistance, bool currentReverse)
    {
        _distance.Value = (decimal)currentDistance;
        _reverse.Checked = currentReverse;

        Text = "Adjust seal";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        TopMost = true;
        ClientSize = new Size(380, 150);

        var instructions = new Label
        {
            Text = "Adjust the seal thickness and direction if needed.",
            Location = new Point(12, 12),
            Size = new Size(356, 25),
        };

        var distanceLabel = new Label { Text = "Thickness (mm):", AutoSize = true, Location = new Point(12, 50) };
        _distance.Location = new Point(120, 46);
        _reverse.Location = new Point(230, 48);

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(200, 100),
            Size = new Size(80, 30),
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(288, 100),
            Size = new Size(80, 30),
        };

        Controls.AddRange(new Control[] { instructions, distanceLabel, _distance, _reverse, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
