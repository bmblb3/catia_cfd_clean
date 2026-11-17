using System.Windows.Forms;

namespace CATIA_CFD_Clean;

// Asks the user to select seal faces in CATIA.
internal sealed class SelectFacesDialog : Form
{
    public SelectFacesDialog(string bodyLabel, string? customInstructions = null, string? title = null)
    {
        Text = title ?? $"Select seal faces - {bodyLabel}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        TopMost = true;
        ClientSize = new Size(360, 140);

        var instructions = new Label
        {
            Text = customInstructions ??
                   ($"In CATIA, select one or more faces of {bodyLabel}\n" +
                    "(Ctrl+click to select several).\n\n" +
                    "Then click OK."),
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

// Live seal adjustment: every change is applied to the seal in CATIA straight away,
// so the user can rotate the view, check the result, and keep tweaking.
internal sealed class AdjustSealDialog : Form
{
    private readonly NumericUpDown _distance = new()
    {
        DecimalPlaces = 2,
        Minimum = 0.01m,
        Maximum = 1000m,
        Increment = 0.5m,
        Width = 90,
    };

    private readonly Label _state = new() { AutoSize = false };
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 350 };

    private readonly Action<double> _applyThickness;
    private readonly Action _flip;
    private decimal _appliedDistance;
    private bool _hasError;

    public double DistanceMm => (double)_appliedDistance;
    public bool Flipped { get; private set; }

    public AdjustSealDialog(string bodyLabel, double initialDistance,
        Action<double> applyThickness, Action flip)
    {
        _applyThickness = applyThickness;
        _flip = flip;
        _distance.Value = (decimal)initialDistance;
        _appliedDistance = _distance.Value;

        Text = $"Adjust seal - {bodyLabel}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        TopMost = true;
        ClientSize = new Size(380, 175);

        var instructions = new Label
        {
            Text = "Changes are applied in CATIA as you edit.\n" +
                   "Rotate the view in CATIA to check the seal, then click Done.",
            Location = new Point(12, 12),
            Size = new Size(356, 36),
        };

        var distanceLabel = new Label { Text = "Thickness (mm):", AutoSize = true, Location = new Point(12, 64) };
        _distance.Location = new Point(120, 60);

        var flipButton = new Button
        {
            Text = "Flip side",
            Location = new Point(230, 58),
            Size = new Size(138, 28),
        };
        flipButton.Click += (_, _) => FlipNow();

        _state.Location = new Point(12, 100);
        _state.Size = new Size(356, 20);
        SetState($"Seal shown at {_appliedDistance} mm.", isError: false);

        var done = new Button
        {
            Text = "Done",
            Location = new Point(200, 133),
            Size = new Size(80, 30),
        };
        done.Click += (_, _) => Finish();

        var discard = new Button
        {
            Text = "Discard",
            DialogResult = DialogResult.Cancel,
            Location = new Point(288, 133),
            Size = new Size(80, 30),
        };

        _distance.ValueChanged += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Start();
        };
        _debounce.Tick += (_, _) => ApplyPendingThickness();

        Controls.AddRange(new Control[] { instructions, distanceLabel, _distance, flipButton, _state, done, discard });
        AcceptButton = done;
        CancelButton = discard;
    }

    private void ApplyPendingThickness()
    {
        _debounce.Stop();
        decimal value = _distance.Value;
        if (value == _appliedDistance && !_hasError)
            return;

        Run(() => _applyThickness((double)value), $"Thickness set to {value} mm.");
        if (!_hasError)
            _appliedDistance = value;
    }

    private void FlipNow()
    {
        ApplyPendingThickness();
        Run(_flip, "Flipped to the other side.");
        if (!_hasError)
            Flipped = !Flipped;
    }

    private void Finish()
    {
        _ = _distance.Value; // commits any typed-but-unconfirmed text
        ApplyPendingThickness();
        if (_hasError)
            return; // keep the dialog open so the user can fix the value

        DialogResult = DialogResult.OK;
    }

    private void Run(Action action, string successMessage)
    {
        UseWaitCursor = true;
        try
        {
            action();
            SetState(successMessage, isError: false);
        }
        catch (Exception ex)
        {
            SetState($"CATIA could not update: {ex.Message}", isError: true);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void SetState(string message, bool isError)
    {
        _hasError = isError;
        _state.Text = message;
        _state.ForeColor = isError ? Color.Firebrick : SystemColors.GrayText;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _debounce.Dispose();
        base.Dispose(disposing);
    }
}
