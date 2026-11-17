using System.Windows.Forms;

namespace CATIA_CFD_Clean;

internal sealed class MainForm : Form
{
    private readonly object?[] _bodies = new object?[3]; // index 1 and 2 used
    private readonly object?[] _seals = new object?[3];  // index 1 and 2 used
    private object? _sealResult;                         // the green intersected "Seal" body

    private readonly Button _makeSeal;

    private readonly Label _status = new()
    {
        Dock = DockStyle.Bottom,
        Height = 48,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(8, 0, 8, 0),
    };

    public MainForm()
    {
        Text = "CATIA Body Viewer";
        Width = 480;
        Height = 480;
        StartPosition = FormStartPosition.CenterScreen;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            Padding = new Padding(12),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (int i = 0; i < 6; i++)
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 6));

        table.Controls.Add(MakeButton("Store as Body 1", () => Store(1)), 0, 0);
        table.Controls.Add(MakeButton("Store as Body 2", () => Store(2)), 1, 0);
        table.Controls.Add(MakeButton("Show Body 1 only", () => ShowBody(1)), 0, 1);
        table.Controls.Add(MakeButton("Show Body 2 only", () => ShowBody(2)), 1, 1);
        table.Controls.Add(MakeButton("Select seal faces (Body 1)", () => SelectSealFaces(1)), 0, 2);
        table.Controls.Add(MakeButton("Select seal faces (Body 2)", () => SelectSealFaces(2)), 1, 2);
        table.Controls.Add(MakeButton("Show seal 1 only", () => ShowSeal(1)), 0, 3);
        table.Controls.Add(MakeButton("Show seal 2 only", () => ShowSeal(2)), 1, 3);

        Button showAssembly = MakeButton("Show Body 1, Body 2 and seals together", ShowAssembly);
        table.Controls.Add(showAssembly, 0, 4);
        table.SetColumnSpan(showAssembly, 2);

        _makeSeal = MakeButton("Make seal", MakeSeal);
        _makeSeal.Enabled = false;
        table.Controls.Add(_makeSeal, 0, 5);
        table.SetColumnSpan(_makeSeal, 2);

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
        Run(() =>
        {
            dynamic doc = CatiaConnection.GetActiveDocument();
            object? body = CatiaBodies.GetSelectedBody(doc);
            if (body is null)
            {
                SetStatus("Select a body in CATIA first.");
                return;
            }

            _bodies[slot] = body;
            SetStatus($"Stored the selected body as Body {slot}.");
        });
    }

    private void ShowBody(int slot)
    {
        object? body = _bodies[slot];
        if (body is null)
        {
            SetStatus($"Body {slot} is not stored yet.");
            return;
        }

        Run(() =>
        {
            dynamic doc = CatiaConnection.GetActiveDocument();
            CatiaBodies.ShowOnly(doc, body);
            SetStatus($"Showing Body {slot} only.");
        });
    }

    private void SelectSealFaces(int slot)
    {
        object? body = _bodies[slot];
        if (body is null)
        {
            SetStatus($"Store Body {slot} first.");
            return;
        }

        Run(() =>
        {
            dynamic doc = CatiaConnection.GetActiveDocument();
            CatiaBodies.ShowOnly(doc, body);

            using var selectDialog = new SelectFacesDialog($"Body {slot}");
            if (selectDialog.ShowDialog(this) != DialogResult.OK)
            {
                SetStatus("Seal creation cancelled.");
                return;
            }

            List<object> faces = CatiaSeals.GetSelectedFaceReferences(doc, body);

            // Replace any seal previously created for this body.
            if (_seals[slot] is { } previous)
            {
                CatiaSeals.TryDelete(doc, previous);
                _seals[slot] = null;
                _sealResult = null;
            }

            // Create with defaults: 3mm, no flip.
            const double defaultThicknessMm = 3.0;
            const bool defaultReverse = false;
            _seals[slot] = CatiaSeals.CreateSealBody(
                doc, $"Seal body {slot}", faces, defaultThicknessMm, defaultReverse);

            // Show context: both original bodies and both seals (if they exist).
            ShowSealWithContext(doc, slot);
            SetStatus($"Seal created with 3mm thickness (0 flip). Adjust if needed.");

            // Let user adjust if desired.
            using var adjustDialog = new AdjustSealDialog(defaultThicknessMm, defaultReverse);
            if (adjustDialog.ShowDialog(this) == DialogResult.OK &&
                (adjustDialog.DistanceMm != defaultThicknessMm || adjustDialog.Reverse != defaultReverse))
            {
                CatiaSeals.TryDelete(doc, _seals[slot]);
                _seals[slot] = CatiaSeals.CreateSealBody(
                    doc, $"Seal body {slot}", faces, adjustDialog.DistanceMm, adjustDialog.Reverse);
                ShowSealWithContext(doc, slot);
            }

            UpdateMakeSealEnabled();
            SetStatus($"Seal volume for Body {slot} created from {faces.Count} face(s).");
        });
    }

    private void ShowSeal(int slot)
    {
        object? seal = _seals[slot];
        if (seal is null)
        {
            SetStatus($"No seal created for Body {slot} yet.");
            return;
        }

        Run(() =>
        {
            dynamic doc = CatiaConnection.GetActiveDocument();
            CatiaBodies.ShowOnly(doc, seal);
            SetStatus($"Showing seal {slot} only.");
        });
    }

    private void ShowSealWithContext(dynamic doc, int slot)
    {
        // Show both original bodies and both seals so user can see the spatial relationship.
        var toShow = new List<object>();
        if (_bodies[1] is not null) toShow.Add(_bodies[1]);
        if (_bodies[2] is not null) toShow.Add(_bodies[2]);
        if (_seals[1] is not null) toShow.Add(_seals[1]);
        if (_seals[2] is not null) toShow.Add(_seals[2]);

        if (toShow.Count > 0)
            CatiaBodies.ShowOnlyBodies(doc, toShow);
    }

    private void MakeSeal()
    {
        if (_seals[1] is not { } seal1 || _seals[2] is not { } seal2)
        {
            SetStatus("Both seals are needed first.");
            return;
        }

        Run(() =>
        {
            dynamic doc = CatiaConnection.GetActiveDocument();
            _sealResult = CatiaSeals.MakeSeal(doc, seal1, seal2);
            SetStatus("Seal made: the intersection is a new green body.");
        });
    }

    // Shows Body 1, Body 2, both seal bodies and the final Seal (whichever exist),
    // and hides everything else, including PartBody.
    private void ShowAssembly()
    {
        var toShow = new List<object>();
        foreach (object? body in new[] { _bodies[1], _bodies[2], _seals[1], _seals[2], _sealResult })
        {
            if (body is not null)
                toShow.Add(body);
        }

        if (toShow.Count == 0)
        {
            SetStatus("Nothing stored yet.");
            return;
        }

        Run(() =>
        {
            dynamic doc = CatiaConnection.GetActiveDocument();
            CatiaBodies.ShowOnlyBodies(doc, toShow);
            SetStatus($"Showing {toShow.Count} bodies together; everything else hidden.");
        });
    }

    private void UpdateMakeSealEnabled() =>
        _makeSeal.Enabled = _seals[1] is not null && _seals[2] is not null;

    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
    }

    private void SetStatus(string message) => _status.Text = message;
}
