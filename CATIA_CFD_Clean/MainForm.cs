using System.Windows.Forms;

namespace CATIA_CFD_Clean;

internal sealed class MainForm : Form
{
    private readonly object?[] _bodies = new object?[3]; // index 1 and 2 used
    private readonly object?[] _seals = new object?[3];  // index 1 and 2 used
    private object? _sealResult;                         // the green intersected "Seal" body
    private readonly List<object> _locked = new();      // bodies in the locked view
    private readonly List<object> _plugs = new();       // confirmed plug bodies

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
        Height = 660;
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

        var sealGroup = new GroupBox { Text = "O-I sealing", Dock = DockStyle.Fill };
        sealGroup.Controls.Add(table);

        var plugGroup = new GroupBox { Text = "Plug sealing", Dock = DockStyle.Fill };
        var plugPanel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 4) };
        plugPanel.Controls.Add(MakeButton("Select plug curve", SelectPlugCurve), 0, 0);
        plugGroup.Controls.Add(plugPanel);

        var lockGroup = new GroupBox { Text = "Lock view", Dock = DockStyle.Fill };
        var lockPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            Padding = new Padding(6, 4, 6, 4),
        };
        for (int i = 0; i < 3; i++)
            lockPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        lockPanel.Controls.Add(MakeButton("Reset to visible", ResetLock), 0, 0);
        lockPanel.Controls.Add(MakeButton("Append to lock", AppendToLock), 1, 0);
        lockPanel.Controls.Add(MakeButton("Show only", ShowLocked), 2, 0);
        lockGroup.Controls.Add(lockPanel);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        root.Controls.Add(sealGroup, 0, 0);
        root.Controls.Add(plugGroup, 0, 1);
        root.Controls.Add(lockGroup, 0, 2);

        Controls.Add(root);
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

            // Create with the default thickness. Any previous seal is kept (hidden) until
            // the user confirms, so discarding restores it.
            const double defaultThicknessMm = 3.0;
            object? previous = _seals[slot];
            object seal = CatiaSeals.CreateSealBody(
                doc, $"Seal body {slot}", faces, defaultThicknessMm, false);
            _seals[slot] = seal;
            ShowSealWithContext(doc, slot);
            SetStatus($"Adjusting seal for Body {slot}. Changes update live in CATIA.");

            using var adjustDialog = new AdjustSealDialog(
                $"Body {slot}",
                defaultThicknessMm,
                distance => CatiaSeals.SetSealThickness(doc, seal, distance),
                () => CatiaSeals.FlipSeal(doc, seal));

            if (adjustDialog.ShowDialog(this) != DialogResult.OK)
            {
                CatiaSeals.TryDelete(doc, seal);
                _seals[slot] = previous;
                ShowSealWithContext(doc, slot);
                SetStatus(previous is null
                    ? "Seal discarded."
                    : $"New seal discarded; kept the previous seal for Body {slot}.");
                return;
            }

            CatiaSeals.SetBodyColor(doc, seal, CatiaSeals.ConfirmedColor);

            if (previous is not null)
            {
                CatiaSeals.TryDelete(doc, previous);
                _sealResult = null;
            }

            UpdateMakeSealEnabled();
            string side = adjustDialog.Flipped ? ", flipped" : "";
            SetStatus($"Seal for Body {slot}: {faces.Count} face(s), {adjustDialog.DistanceMm} mm{side}.");
        });
    }

    private void SelectPlugCurve()
    {
        Run(() =>
        {
            dynamic doc = CatiaConnection.GetActiveDocument();

            using var selectDialog = new SelectFacesDialog(
                "plug",
                "In CATIA, select a closed chain of curves\n" +
                "(Ctrl+click to select several).\n\n" +
                "Then click OK.",
                "Select plug curve");
            if (selectDialog.ShowDialog(this) != DialogResult.OK)
            {
                SetStatus("Plug creation cancelled.");
                return;
            }

            List<object> curves = CatiaSeals.GetSelectedCurveReferences(doc);

            const double defaultThicknessMm = 2.0;
            object plug = CatiaSeals.CreatePlugBody(doc, "Plug body", curves, defaultThicknessMm, false);
            SetStatus("Adjusting plug. Changes update live in CATIA.");

            using var adjustDialog = new AdjustSealDialog(
                "Plug",
                defaultThicknessMm,
                distance => CatiaSeals.SetSealThickness(doc, plug, distance),
                () => CatiaSeals.FlipSeal(doc, plug));

            if (adjustDialog.ShowDialog(this) != DialogResult.OK)
            {
                CatiaSeals.TryDelete(doc, plug);
                SetStatus("Plug discarded.");
                return;
            }

            CatiaSeals.SetBodyColor(doc, plug, CatiaSeals.ResultColor);
            _plugs.Add(plug);

            string side = adjustDialog.Flipped ? ", flipped" : "";
            SetStatus($"Plug created: {curves.Count} curve(s), {adjustDialog.DistanceMm} mm{side}.");
        });
    }

    private void ResetLock()
    {
        Run(() =>
        {
            dynamic doc = CatiaConnection.GetActiveDocument();
            _locked.Clear();
            _locked.AddRange(CatiaBodies.GetVisibleBodies(doc));
            SetStatus($"Lock reset to the {_locked.Count} currently visible bod{(_locked.Count == 1 ? "y" : "ies")}.");
        });
    }

    private void AppendToLock()
    {
        Run(() =>
        {
            dynamic doc = CatiaConnection.GetActiveDocument();
            List<object> selected = CatiaBodies.GetSelectedBodies(doc);
            if (selected.Count == 0)
            {
                SetStatus("Select one or more bodies in CATIA first.");
                return;
            }

            var names = new HashSet<string>(_locked.Select(CatiaBodies.NameOf));
            int added = 0;
            foreach (object body in selected)
            {
                if (names.Add(CatiaBodies.NameOf(body)))
                {
                    _locked.Add(body);
                    added++;
                }
            }
            SetStatus($"Added {added} bod{(added == 1 ? "y" : "ies")} to the lock ({_locked.Count} total).");
        });
    }

    private void ShowLocked()
    {
        if (_locked.Count == 0)
        {
            SetStatus("The lock is empty. Use Reset to visible or Append to lock first.");
            return;
        }

        Run(() =>
        {
            dynamic doc = CatiaConnection.GetActiveDocument();
            CatiaBodies.ShowOnlyBodies(doc, _locked);
            SetStatus($"Showing the {_locked.Count} locked bod{(_locked.Count == 1 ? "y" : "ies")} only.");
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
        toShow.AddRange(_plugs);

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
