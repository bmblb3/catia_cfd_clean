using System.Windows.Forms;

namespace CATIA_CFD_Clean;

internal sealed class MainForm : Form
{
    private readonly object?[] _bodies = new object?[3]; // index 1 and 2 used
    private readonly object?[] _seals = new object?[3];  // index 1 and 2 used
    private object? _sealResult;                         // the green intersected "Seal" body
    private readonly List<object> _locked = new();      // bodies in the locked view
    private readonly List<object> _plugs = new();       // confirmed plug bodies

    private bool _busy;                                  // a CATIA operation is running

    private readonly Button _makeSeal;
    private readonly TableLayoutPanel _root;

    private readonly Label _status = new()
    {
        Dock = DockStyle.Bottom,
        Height = 44,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(10, 0, 10, 0),
        BorderStyle = BorderStyle.Fixed3D,
    };

    public MainForm()
    {
        Text = "CATIA Body Viewer";
        Width = 480;
        StartPosition = FormStartPosition.CenterScreen;

        var (lockGroup, lockGrid) = MakeGroup("Lock view", 2);
        lockGrid.Controls.Add(MakeButton("Reset to visible", ResetLock), 0, 0);
        lockGrid.Controls.Add(MakeButton("Append selected", AppendToLock), 1, 0);
        lockGrid.Controls.Add(MakeButton("Toggle show locked", ToggleLocked), 0, 1);
        lockGrid.Controls.Add(MakeButton("Toggle show all", ToggleAll), 1, 1);

        // O-I sealing: one column per body, actions in workflow order.
        var (sealGroup, seal) = MakeGroup("O-I sealing", 2);
        seal.Controls.Add(MakeHeader("Body 1"), 0, 0);
        seal.Controls.Add(MakeHeader("Body 2"), 1, 0);
        seal.Controls.Add(MakeButton("Store selected body", () => Store(1)), 0, 1);
        seal.Controls.Add(MakeButton("Store selected body", () => Store(2)), 1, 1);
        seal.Controls.Add(MakeButton("Toggle show body", () => ToggleBody(1)), 0, 2);
        seal.Controls.Add(MakeButton("Toggle show body", () => ToggleBody(2)), 1, 2);
        seal.Controls.Add(MakeButton("Select seal faces", () => SelectSealFaces(1)), 0, 3);
        seal.Controls.Add(MakeButton("Select seal faces", () => SelectSealFaces(2)), 1, 3);
        seal.Controls.Add(MakeButton("Toggle show red seal", () => ToggleSeal(1)), 0, 4);
        seal.Controls.Add(MakeButton("Toggle show red seal", () => ToggleSeal(2)), 1, 4);
        AddSpanning(seal, MakeButton("Show bodies, seals and plugs together", ShowAssembly), 5);
        _makeSeal = MakeButton("Make seal (intersect seal 1 and seal 2)", MakeSeal);
        _makeSeal.Enabled = false;
        AddSpanning(seal, _makeSeal, 6);
        AddSpanning(seal, MakeButton("Toggle show green seal", ToggleGreenSeal), 7);

        var (plugGroup, plug) = MakeGroup("Plug sealing", 1);
        plug.Controls.Add(MakeButton("Select plug curve", SelectPlugCurve), 0, 0);

        var (visGroup, vis) = MakeGroup("Visibility", 2);
        vis.Controls.Add(MakeButton("Toggle bodies of selected faces or bodies", ToggleSelectedFaces), 0, 0);
        vis.Controls.Add(MakeButton("Show all solids in selected body", ShowSolidsInSelectedBody), 1, 0);

        _root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(8, 6, 8, 6),
            AutoScroll = true,
        };
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        GroupBox[] groups = { lockGroup, sealGroup, plugGroup, visGroup };
        for (int i = 0; i < groups.Length; i++)
        {
            _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _root.Controls.Add(groups[i], 0, i);
        }
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // absorbs extra height

        Controls.Add(_root);
        Controls.Add(_status);

        SetStatus("Select a body in CATIA, then click a Store button.");
    }

    // ---- Layout helpers ----

    private static (GroupBox Group, TableLayoutPanel Grid) MakeGroup(string title, int columns)
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = columns,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        for (int i = 0; i < columns; i++)
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));

        var group = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(6, 4, 6, 6),
            Margin = new Padding(0, 0, 0, 6),
        };
        group.Controls.Add(grid);
        return (group, grid);
    }

    private static void AddSpanning(TableLayoutPanel grid, Control control, int row)
    {
        grid.Controls.Add(control, 0, row);
        grid.SetColumnSpan(control, grid.ColumnCount);
    }

    private static Label MakeHeader(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        AutoSize = true,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font(SystemFonts.MessageBoxFont ?? DefaultFont, FontStyle.Bold),
        Margin = new Padding(4, 2, 4, 0),
    };

    private static Button MakeButton(string text, Action onClick)
    {
        var button = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            Margin = new Padding(4),
            MinimumSize = new Size(0, 34),
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    // ---- Form lifetime ----

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // Size the window to its content so nothing is stretched or clipped at any DPI.
        Size content = _root.GetPreferredSize(new Size(_root.ClientSize.Width, 0));
        ClientSize = new Size(ClientSize.Width, content.Height + _status.Height);
        MinimumSize = new Size(420, Height);
    }

    // ---- Visibility ----

    /// <summary>
    /// Toggles the bodies owning the selected faces: hides them or, if all are already hidden,
    /// shows them again. Only the body nodes and their geosets change; features are not touched.
    /// </summary>
    private void ToggleSelectedFaces()
    {
        Run(() =>
        {
            dynamic doc = CatiaConnection.GetActiveDocument();
            List<object> bodies = CatiaBodies.GetBodiesOfSelection(doc);
            if (bodies.Count == 0)
            {
                SetStatus("Select one or more faces or bodies in CATIA first.");
                return;
            }

            bool shown = CatiaBodies.ToggleBodies(doc, bodies, hideIfAnyShown: true);
            string noun = $"{bodies.Count} bod{(bodies.Count == 1 ? "y" : "ies")}";
            SetStatus(shown ? $"Showed {noun}." : $"Hid {noun}.");
        });
    }

    private void ShowSolidsInSelectedBody()
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

            CatiaBodies.ShowAllSolidsInBody(doc, body);
            SetStatus($"Showing all solids in {CatiaBodies.NameOf(body)}.");
        });
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

    private void ToggleBody(int slot) =>
        ToggleStored(_bodies[slot], $"Body {slot}", $"Body {slot} is not stored yet.");

    private void ToggleSeal(int slot) =>
        ToggleStored(_seals[slot], $"red seal {slot}", $"No seal created for Body {slot} yet.");

    private void ToggleGreenSeal() =>
        ToggleStored(_sealResult, "the green seal", "The green seal has not been made yet.");

    // Flips one stored body's visibility without touching anything else in the part.
    private void ToggleStored(object? body, string label, string missingMessage)
    {
        if (body is null)
        {
            SetStatus(missingMessage);
            return;
        }

        Run(() =>
        {
            dynamic doc = CatiaConnection.GetActiveDocument();
            bool shown = CatiaBodies.ToggleBodies(doc, new[] { body });
            SetStatus($"{(shown ? "Showed" : "Hid")} {label}.");
        });
    }

    private void ToggleAll()
    {
        Run(() =>
        {
            dynamic doc = CatiaConnection.GetActiveDocument();
            bool shown = CatiaBodies.ToggleAllBodies(doc);
            SetStatus(shown ? "Showed all bodies." : "Hid all bodies.");
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
            const bool defaultReversed = true;
            object? previous = _seals[slot];
            object seal = CatiaSeals.CreateSealBody(
                doc, $"Seal body {slot}", faces, defaultThicknessMm, defaultReversed);
            _seals[slot] = seal;
            // Leave the current view as it is; don't re-show the bodies and seals.
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

    private void ToggleLocked()
    {
        if (_locked.Count == 0)
        {
            SetStatus("The lock is empty. Use Reset to visible or Append selected first.");
            return;
        }

        Run(() =>
        {
            dynamic doc = CatiaConnection.GetActiveDocument();
            bool shown = CatiaBodies.ToggleBodies(doc, _locked);
            string noun = $"{_locked.Count} locked bod{(_locked.Count == 1 ? "y" : "ies")}";
            SetStatus($"{(shown ? "Showed" : "Hid")} the {noun}.");
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

    // Runs a CATIA operation, reporting errors in the status bar. Operations do not nest:
    // a second one (e.g. a click while CATIA calls pump messages) is refused.
    private void Run(Action action)
    {
        if (_busy)
        {
            SetStatus("Finish the current operation first.");
            return;
        }

        _busy = true;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private void SetStatus(string message) => _status.Text = message;
}
