using System.Windows.Forms;

namespace CATIA_CFD_Clean;

// The app keeps no CATIA state of its own: what it holds for a part (O-I bodies and seals, the
// lock, plugs) lives in the .sqlite3 file next to the CATPart. Each operation reads it, works in
// CATIA, then writes it back (see RunWithState), so it survives restarts and follows whichever
// part is active.
internal sealed class MainForm : Form
{
    private bool _busy;                                  // a CATIA operation is running

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
        seal.Controls.Add(MakeButton("Toggle show red seal", () => ToggleSeal(1)), 0, 3);
        seal.Controls.Add(MakeButton("Toggle show red seal", () => ToggleSeal(2)), 1, 3);
        AddSpanning(seal, MakeButton("Use selected faces as seal faces (Body 1 or 2)", SelectSealFaces), 4);
        AddSpanning(seal, MakeButton("Show bodies, seals and plugs together", ShowAssembly), 5);
        AddSpanning(seal, MakeButton("Make seal (thicken both, then intersect)", MakeSeal), 6);
        AddSpanning(seal, MakeButton("Toggle show green seal", ToggleGreenSeal), 7);
        AddSpanning(seal, MakeButton("Reset (forget bodies and seals; nothing is deleted)", ResetSealing), 8);

        var (plugGroup, plug) = MakeGroup("Plug sealing", 1);
        plug.Controls.Add(MakeButton("Select plug curve", SelectPlugCurve), 0, 0);

        var (visGroup, vis) = MakeGroup("Visibility", 2);
        vis.Controls.Add(MakeButton("Toggle bodies of selected faces or bodies", ToggleSelectedFaces), 0, 0);
        vis.Controls.Add(MakeButton("Show all solids in selected body", ShowSolidsInSelectedBody), 1, 0);

        var (catGroup, cat) = MakeGroup("Catalogue", 1);
        cat.Controls.Add(MakeButton("Re-catalogue part (after deleting or undoing in CATIA)", Recatalogue), 0, 0);

        _root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(8, 6, 8, 6),
            AutoScroll = true,
        };
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        GroupBox[] groups = { lockGroup, sealGroup, plugGroup, visGroup, catGroup };
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

    // ---- Visibility (no stored state) ----

    /// <summary>
    /// Toggles the bodies owning the selected faces: hides them or, if all are already hidden,
    /// shows them again. Only the body nodes and their geosets change; features are not touched.
    /// </summary>
    private void ToggleSelectedFaces()
    {
        Run(() =>
        {
            object doc = CatiaConnection.GetActiveDocument();
            List<object> bodies = CatiaBodies.GetBodiesOfSelection(doc);
            if (bodies.Count == 0)
            {
                SetStatus("Select one or more faces or bodies in CATIA first.");
                return;
            }

            bool shown = CatiaBodies.ToggleBodies(doc, bodies, hideIfAnyShown: true);
            SetStatus(shown ? $"Showed {Bodies(bodies.Count)}." : $"Hid {Bodies(bodies.Count)}.");
        });
    }

    private void ShowSolidsInSelectedBody()
    {
        Run(() =>
        {
            object doc = CatiaConnection.GetActiveDocument();
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

    private void ToggleAll()
    {
        Run(() =>
        {
            object doc = CatiaConnection.GetActiveDocument();
            bool shown = CatiaBodies.ToggleAllBodies(doc);
            SetStatus(shown ? "Showed all bodies." : "Hid all bodies.");
        });
    }

    // ---- Lock view ----

    private void ResetLock() => RunWithState((doc, state) =>
    {
        state.Locked.Clear();
        state.Locked.AddRange(CatiaBodies.GetVisibleBodies(doc).Select(CatiaBodies.NameOf).Distinct());
        SetStatus($"Lock reset to the {Bodies(state.Locked.Count)} currently visible.");
    });

    private void AppendToLock() => RunWithState((doc, state) =>
    {
        List<object> selected = CatiaBodies.GetSelectedBodies(doc);
        if (selected.Count == 0)
        {
            SetStatus("Select one or more bodies in CATIA first.");
            return;
        }

        int added = 0;
        foreach (string name in selected.Select(CatiaBodies.NameOf))
        {
            if (!state.Locked.Contains(name))
            {
                state.Locked.Add(name);
                added++;
            }
        }
        SetStatus($"Added {Bodies(added)} to the lock ({state.Locked.Count} total).");
    });

    private void ToggleLocked() => RunWithState((doc, state) =>
    {
        if (state.Locked.Count == 0)
        {
            SetStatus("The lock is empty. Use Reset to visible or Append selected first.");
            return;
        }

        var (found, missing) = Resolve(doc, state.Locked);
        if (found.Count == 0)
        {
            SetStatus("None of the locked bodies exist in the part." + MissingNote(missing));
            return;
        }

        bool shown = CatiaBodies.ToggleBodies(doc, found);
        SetStatus($"{(shown ? "Showed" : "Hid")} the {found.Count} locked bod{(found.Count == 1 ? "y" : "ies")}." +
                  MissingNote(missing));
    });

    // ---- O-I sealing ----

    private void Store(int slot) => RunWithState((doc, state) =>
    {
        object? body = CatiaBodies.GetSelectedBody(doc);
        if (body is null)
        {
            SetStatus("Select a body in CATIA first.");
            return;
        }

        state.Bodies[slot] = CatiaBodies.NameOf(body);
        SetStatus($"Stored {state.Bodies[slot]} as Body {slot}.");
    });

    private void ToggleBody(int slot) =>
        ToggleStored(s => s.Bodies[slot], $"Body {slot}", $"Body {slot} is not stored yet.");

    private void ToggleSeal(int slot) =>
        ToggleStored(s => s.Seals[slot], $"red seal {slot}", $"No seal created for Body {slot} yet.");

    private void ToggleGreenSeal() =>
        ToggleStored(s => s.Result, "the green seal", "The green seal has not been made yet.");

    // Flips one stored body's visibility without touching anything else in the part.
    private void ToggleStored(Func<PartState, string?> pick, string label, string notStoredMessage) =>
        RunWithState((doc, state) =>
        {
            string? name = pick(state);
            if (name is null)
            {
                SetStatus(notStoredMessage);
                return;
            }

            object? body = CatiaBodies.FindBody(doc, name);
            if (body is null)
            {
                SetStatus($"{name} ({label}) is missing from the part. Re-catalogue to forget it.");
                return;
            }

            bool shown = CatiaBodies.ToggleBodies(doc, new[] { body });
            SetStatus($"{(shown ? "Showed" : "Hid")} {label}.");
        });

    // Takes the faces already selected in CATIA, works out whether they are Body 1's or Body 2's,
    // and stores them (joined in a hidden geoset) as that body's seal faces. Thickening happens
    // in Make seal.
    private void SelectSealFaces() => RunWithState((doc, state) =>
    {
        if (state.Bodies[1] is null && state.Bodies[2] is null)
        {
            SetStatus("Store Body 1 and Body 2 first.");
            return;
        }

        var (owner, faces) = CatiaSeals.GetSelectedFaces(doc);
        string ownerName = CatiaBodies.NameOf(owner);
        int slot = state.Bodies[2] == ownerName ? 2 : state.Bodies[1] == ownerName ? 1 : 0;
        if (slot == 0)
        {
            SetStatus($"The selected faces belong to {ownerName}, which is not stored as Body 1 or Body 2.");
            return;
        }

        string? previous = state.Seals[slot];
        object sealBody = CatiaSeals.CreateSealFacesBody(doc, CatiaSeals.NextKey(doc), faces);
        state.Seals[slot] = CatiaBodies.NameOf(sealBody);
        if (previous is not null)
        {
            if (CatiaBodies.FindBody(doc, previous) is { } old)
                CatiaSeals.TryDelete(doc, old);
            state.Result = null;
        }

        int other = 3 - slot;
        string next = state.Seals[other] is null
            ? $"Now select the seal faces of Body {other}."
            : "Click Make seal.";
        SetStatus($"Stored {faces.Count} seal face(s) for Body {slot}. {next}");
    });

    // Thickens each seal body that is not thickened yet (red), then intersects the two (green).
    private void MakeSeal() => RunWithState((doc, state) =>
    {
        if (state.Seals[1] is null || state.Seals[2] is null)
        {
            SetStatus("Select the seal faces of both bodies first.");
            return;
        }

        object? seal1 = CatiaBodies.FindBody(doc, state.Seals[1]);
        object? seal2 = CatiaBodies.FindBody(doc, state.Seals[2]);
        if (seal1 is null || seal2 is null)
        {
            SetStatus("A stored seal body is missing from the part. Re-catalogue, then select its seal faces again.");
            return;
        }

        foreach (var (slot, sealBody) in new[] { (1, seal1), (2, seal2) })
        {
            if (CatiaSeals.IsThickened(sealBody))
                continue;
            try
            {
                CatiaSeals.ThickenSeal(doc, sealBody);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not thicken the seal faces of Body {slot}. {ex.Message}", ex);
            }
        }

        state.Result = CatiaBodies.NameOf(CatiaSeals.MakeSeal(doc, seal1, seal2));
        SetStatus($"Seal made: both seals thickened ({CatiaSeals.SealMainSideMm} mm / " +
            $"{CatiaSeals.SealReverseSideMm} mm reversed) and intersected into a green body.");
    });

    // Shows Body 1, Body 2, both seal bodies, the final Seal and the plugs (whichever exist),
    // and hides everything else, including PartBody.
    private void ShowAssembly() => RunWithState((doc, state) =>
    {
        var names = new[] { state.Bodies[1], state.Bodies[2], state.Seals[1], state.Seals[2], state.Result }
            .Concat(state.Plugs.Select(p => p.Name));
        var (toShow, missing) = Resolve(doc, names);
        if (toShow.Count == 0)
        {
            SetStatus("Nothing stored yet." + MissingNote(missing));
            return;
        }

        CatiaBodies.ShowOnlyBodies(doc, toShow);
        SetStatus($"Showing {toShow.Count} bodies together; everything else hidden." + MissingNote(missing));
    });

    // Forgets Body 1/2, both seal bodies and the green seal so a new O-I seal can be started.
    // Only the app's references are cleared; the bodies stay in the CATIA part.
    private void ResetSealing() => RunWithState((_, state) =>
    {
        Array.Clear(state.Bodies);
        Array.Clear(state.Seals);
        state.Result = null;
        SetStatus("O-I sealing reset. Existing bodies stay in CATIA. Store a new Body 1 and Body 2.");
    });

    // ---- Plugs ----

    private void SelectPlugCurve() => RunWithState((doc, state) =>
    {
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
        object plug = CatiaSeals.CreatePlugBody(doc, CatiaSeals.NextKey(doc), curves, defaultThicknessMm, false);
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
        state.Plugs.Add(new PartState.Plug(CatiaBodies.NameOf(plug), adjustDialog.DistanceMm, adjustDialog.Flipped));

        string side = adjustDialog.Flipped ? ", flipped" : "";
        SetStatus($"Plug created: {curves.Count} curve(s), {adjustDialog.DistanceMm} mm{side}.");
    });

    // ---- Catalogue ----

    // Snapshots the part's bodies into the .sqlite3 file (replacing the previous catalogue) and
    // forgets stored bodies that no longer exist. Use after deleting or undoing in CATIA.
    private void Recatalogue() => RunWithState((doc, state) =>
    {
        CatiaCatalogue.Result result = CatiaCatalogue.Run(doc);
        int dropped = state.Prune(result.Names);
        string dup = result.DuplicateNames == 0 ? "" : $", {result.DuplicateNames} shared name(s)";
        string forgot = dropped == 0 ? "" : $" Forgot {dropped} stored reference(s) to missing bodies.";
        SetStatus($"Catalogued {Bodies(result.Bodies)}{dup}.{forgot}");
    });

    // ---- Helpers ----

    // Looks up stored bodies by name. Returns those still in the part and how many are missing.
    private static (List<object> Found, int Missing) Resolve(object doc, IEnumerable<string?> names)
    {
        var found = new List<object>();
        int missing = 0;
        foreach (string? name in names)
        {
            if (name is null)
                continue;
            if (CatiaBodies.FindBody(doc, name) is { } body)
                found.Add(body);
            else
                missing++;
        }
        return (found, missing);
    }

    private static string MissingNote(int missing) => missing == 0
        ? ""
        : $" {Bodies(missing)} stored but missing from the part; Re-catalogue to forget {(missing == 1 ? "it" : "them")}.";

    private static string Bodies(int n) => $"{n} bod{(n == 1 ? "y" : "ies")}";

    // Runs an operation on the active part's stored state: reads it from the part's .sqlite3
    // file, runs the operation (CATIA first), then writes it back. If the operation throws,
    // nothing is written.
    private void RunWithState(Action<object, PartState> action)
    {
        Run(() =>
        {
            object doc = CatiaConnection.GetActiveDocument();
            string dbPath = PartDb.PathFor(doc);
            var state = PartState.Load(dbPath);
            action(doc, state);
            state.Save(dbPath);
        });
    }

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
