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
    private readonly ToolTip _tips = new() { AutoPopDelay = 15000 };

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
        Text = "CATIA CFD Clean";
        Width = 480;
        StartPosition = FormStartPosition.CenterScreen;

        // Sections run in workflow order. Labels stay short; tooltips carry the detail.
        var (viewGroup, view) = MakeGroup("View", 3);
        view.Controls.Add(MakeButton("Show/hide selected", ToggleSelectedFaces,
            "Hides the bodies of the selected faces or bodies, or shows them again if all are hidden."), 0, 0);
        view.Controls.Add(MakeButton("Show/hide all", ToggleAll,
            "Hides every body, or shows every body if any is hidden."), 1, 0);
        view.Controls.Add(MakeButton("Show body's solids", ShowSolidsInSelectedBody,
            "Shows all solids and geosets inside the selected body."), 2, 0);

        var (lockGroup, lockGrid) = MakeGroup("Lock", 3);
        lockGrid.Controls.Add(MakeButton("Lock visible", ResetLock,
            "Replaces the lock with the bodies visible now."), 0, 0);
        lockGrid.Controls.Add(MakeButton("Add visible", AppendToLock,
            "Adds the bodies visible now to the lock, keeping those already in it."), 1, 0);
        lockGrid.Controls.Add(MakeButton("Show/hide locked", ToggleLocked,
            "Shows or hides the locked bodies; nothing else changes."), 2, 0);

        // Every button acts on the current CATIA selection or stored state in one step; no wizards.
        var (sealGroup, seal) = MakeGroup("Seals", 2);
        AddSpanning(seal, MakeButton("Set O-I bodies from selection", SetOiBodies,
            "Select two bodies (or faces of two bodies). They become Body 1 and Body 2. " +
            "Starts a new O-I seal: the previous red and green seals are forgotten (nothing is deleted)."), 0);
        AddSpanning(seal, MakeButton("Cycle O-I bodies: 1, both, 2, none", CycleBodies,
            "Each click moves to the next: only Body 1, both, only Body 2, neither."), 1);
        seal.Controls.Add(MakeButton("Seal faces from selection", SelectSealFaces,
            "Stores the selected faces as the red seal of Body 1 or Body 2 (whichever owns them)."), 0, 2);
        seal.Controls.Add(MakeButton("Seal body from selection", SelectSealBody,
            "Copies the whole of Body 1 or Body 2 (select it or one of its faces) as its red seal."), 1, 2);
        AddSpanning(seal, MakeButton("Make green seal", MakeSeal,
            "Thickens both red seals, then intersects them into the green seal."), 3);
        seal.Controls.Add(MakeButton("Plug from selection", MakePlug,
            $"Makes a {PlugMm} mm green plug from the selected closed chain of curves."), 0, 4);
        seal.Controls.Add(MakeButton("Sphere from point", MakeSphere,
            $"Makes a green sphere plug of {SphereRadiusMm} mm radius centred on the selected point."), 1, 4);
        AddSpanning(seal, MakeButton("Flip selected plug", FlipPlug,
            "Moves the selected plug or test seal to the other side of its curves."), 5);
        var (wtGroup, wt) = MakeGroup("Watertight test", 2);
        wt.Controls.Add(MakeButton("Flow point from selection", SetFlowPoint,
            "Stores the selected point (on the inside surface of the flow volume). Kept between tests."), 0, 0);
        wt.Controls.Add(MakeButton("Plug ↔ test seal", ToggleTestSeal,
            "Turns the selected green plug into an orange test seal, or a test seal back into a plug."), 1, 0);
        wt.Controls.Add(MakeButton("Run test", TestWatertight,
            "Tests whether the visible bodies seal the flow volume. Cleans up any previous test first."), 0, 1);
        wt.Controls.Add(MakeButton("Clean up test", CleanUpTest,
            "Deletes the test's temporary bodies and shows the tested bodies again."), 1, 1);

        var (partGroup, part) = MakeGroup("Part", 1);
        part.Controls.Add(MakeButton("Re-catalogue part", Recatalogue,
            "Re-reads the part's bodies and forgets stored ones that are gone. Use after deleting or undoing in CATIA."), 0, 0);

        _root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(8, 6, 8, 6),
            AutoScroll = true,
        };
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        GroupBox[] groups = { viewGroup, lockGroup, sealGroup, wtGroup, partGroup };
        for (int i = 0; i < groups.Length; i++)
        {
            _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _root.Controls.Add(groups[i], 0, i);
        }
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // absorbs extra height

        Controls.Add(_root);
        Controls.Add(_status);

        SetStatus("Ready. Hover over a button to see what it does.");
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

    private Button MakeButton(string text, Action onClick, string? tip = null)
    {
        var button = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            Margin = new Padding(4),
            MinimumSize = new Size(0, 34),
        };
        button.Click += (_, _) => onClick();
        if (tip is not null)
            _tips.SetToolTip(button, tip);
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
        var added = CatiaBodies.GetVisibleBodies(doc).Select(CatiaBodies.NameOf)
            .Distinct().Except(state.Locked).ToList();
        state.Locked.AddRange(added);
        SetStatus($"Added {Bodies(added.Count)} to the lock ({state.Locked.Count} total).");
    });

    private void ToggleLocked() => RunWithState((doc, state) =>
    {
        if (state.Locked.Count == 0)
        {
            SetStatus("The lock is empty. Use Lock visible or Add visible first.");
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

    // Sets Body 1 and Body 2 from a selection that resolves to exactly two bodies, starting a new
    // O-I seal: every previous O-I reference is dropped (the bodies stay in CATIA).
    private void SetOiBodies() => RunWithState((doc, state) =>
    {
        var names = CatiaBodies.GetBodiesOfSelection(doc).Select(CatiaBodies.NameOf).ToList();
        if (names.Count != 2)
        {
            SetStatus($"Select exactly two bodies (or faces of two bodies); the selection resolves to {Bodies(names.Count)}.");
            return;
        }

        Array.Clear(state.Seals);
        state.Result = null;
        state.Bodies[1] = names[0];
        state.Bodies[2] = names[1];
        SetStatus($"New O-I seal: Body 1 {names[0]}, Body 2 {names[1]}. Now select the seal faces.");
    });

    private void CycleBodies() => RunWithState((doc, state) =>
    {
        if (state.Bodies[1] is null || state.Bodies[2] is null)
        {
            SetStatus("Set the O-I bodies first.");
            return;
        }

        var (found, missing) = Resolve(doc, state.Bodies);
        if (missing > 0)
        {
            SetStatus("An O-I body is missing from the part." + MissingNote(missing));
            return;
        }

        var (one, two) = CatiaBodies.CycleTwo(doc, found[0], found[1]);
        SetStatus((one, two) switch
        {
            (true, true) => "Showing both O-I bodies.",
            (true, false) => "Showing Body 1 only.",
            (false, true) => "Showing Body 2 only.",
            _ => "Both O-I bodies hidden.",
        });
    });


    // Takes the faces already selected in CATIA, works out whether they are Body 1's or Body 2's,
    // and stores them (joined in a hidden geoset) as that body's seal faces. Thickening happens
    // in Make green seal.
    private void SelectSealFaces() => RunWithState((doc, state) =>
    {
        if (state.Bodies[1] is null && state.Bodies[2] is null)
        {
            SetStatus("Set the O-I bodies first.");
            return;
        }

        var (owner, faces) = CatiaSeals.GetSelectedFaces(doc);
        string ownerName = CatiaBodies.NameOf(owner);
        int slot = SlotOf(state, ownerName);
        if (slot == 0)
        {
            SetStatus($"The selected faces belong to {ownerName}, which is not set as Body 1 or Body 2.");
            return;
        }

        StoreSeal(doc, state, slot, CatiaSeals.CreateSealFacesBody(doc, CatiaSeals.NextKey(doc), faces),
            $"Stored {faces.Count} seal face(s) for Body {slot}.");
    });

    // Takes Body 1 or Body 2 (selected, or one of its faces) and copies the whole of it as that
    // body's red seal. Make green seal uses the copy as it is, without thickening.
    private void SelectSealBody() => RunWithState((doc, state) =>
    {
        if (state.Bodies[1] is null && state.Bodies[2] is null)
        {
            SetStatus("Set the O-I bodies first.");
            return;
        }

        var names = CatiaBodies.GetBodiesOfSelection(doc).Select(CatiaBodies.NameOf).Distinct().ToList();
        var slots = names.Select(n => SlotOf(state, n)).Where(s => s != 0).Distinct().ToList();
        if (slots.Count != 1)
        {
            SetStatus(slots.Count == 0
                ? "Select Body 1 or Body 2 (or one of its faces) in CATIA first."
                : "The selection covers both Body 1 and Body 2. Select only one of them.");
            return;
        }

        int slot = slots[0];
        object source = CatiaBodies.FindBody(doc, state.Bodies[slot])!;
        StoreSeal(doc, state, slot, CatiaSeals.CreateSealCopyBody(doc, CatiaSeals.NextKey(doc), source),
            $"Copied the whole of Body {slot} as its red seal.");
    });

    private static int SlotOf(PartState state, string bodyName) =>
        state.Bodies[2] == bodyName ? 2 : state.Bodies[1] == bodyName ? 1 : 0;

    // Stores a new seal body for the slot, deleting the one it replaces.
    private void StoreSeal(object doc, PartState state, int slot, object sealBody, string done)
    {
        string? previous = state.Seals[slot];
        state.Seals[slot] = CatiaBodies.NameOf(sealBody);
        if (previous is not null)
        {
            if (CatiaBodies.FindBody(doc, previous) is { } old)
                CatiaSeals.TryDelete(doc, old);
            state.Result = null;
        }

        int other = 3 - slot;
        string next = state.Seals[other] is null
            ? $"Now select the seal faces (or seal body) of Body {other}."
            : "Click Make green seal.";
        SetStatus($"{done} {next}");
    }

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

    // ---- Plugs ----

    private const double PlugMm = 2.0;

    // Makes a green plug from the closed chain of curves selected in CATIA.
    private void MakePlug() => RunWithState((doc, state) =>
    {
        List<object> curves = CatiaSeals.GetSelectedCurveReferences(doc);
        object plug = CatiaSeals.CreatePlugBody(doc, CatiaSeals.NextKey(doc), curves, PlugMm, false);
        CatiaSeals.SetBodyColor(doc, plug, CatiaSeals.ResultColor);
        state.Plugs.Add(CatiaBodies.NameOf(plug));
        SetStatus($"Plug made from {curves.Count} curve(s), {PlugMm} mm. Flip selected plug if it is on the wrong side.");
    });

    private const double SphereRadiusMm = 1.0;

    // Makes a green sphere plug centred on the point selected in CATIA.
    private void MakeSphere() => RunWithState((doc, state) =>
    {
        double[] centre = CatiaWatertight.GetSelectedPoint(doc,
            "Select exactly one point (a vertex or a point feature) for the sphere's centre.");
        object sphere = CatiaSeals.CreateSphereBody(doc, CatiaSeals.NextKey(doc), centre, SphereRadiusMm);
        CatiaSeals.SetBodyColor(doc, sphere, CatiaSeals.ResultColor);
        state.Plugs.Add(CatiaBodies.NameOf(sphere));
        SetStatus($"Sphere plug {CatiaBodies.NameOf(sphere)} made, {SphereRadiusMm} mm radius.");
    });

    private void FlipPlug() => RunWithState((doc, state) =>
    {
        object? body = CatiaBodies.GetSelectedBody(doc);
        string? name = body is null ? null : CatiaBodies.NameOf(body);
        if (name is null || !(state.Plugs.Contains(name) || state.TestSeals.Contains(name)))
        {
            SetStatus("Select a plug or test seal (or one of its faces) in CATIA first.");
            return;
        }

        CatiaSeals.FlipSeal(doc, body!);
        SetStatus($"Flipped {name}.");
    });

    // Moves the selected body between plug (green) and test seal (orange). Geometry is unchanged.
    private void ToggleTestSeal() => RunWithState((doc, state) =>
    {
        object? body = CatiaBodies.GetSelectedBody(doc);
        string? name = body is null ? null : CatiaBodies.NameOf(body);
        if (name is not null && state.Plugs.Remove(name))
        {
            state.TestSeals.Add(name);
            CatiaSeals.SetBodyColor(doc, body!, CatiaSeals.TestSealColor);
            SetStatus($"{name} is now an orange test seal.");
        }
        else if (name is not null && state.TestSeals.Remove(name))
        {
            state.Plugs.Add(name);
            CatiaSeals.SetBodyColor(doc, body!, CatiaSeals.ResultColor);
            SetStatus($"{name} is now a green plug.");
        }
        else
        {
            SetStatus("Select a plug or test seal (or one of its faces) in CATIA first.");
        }
    });

    // ---- Watertight test ----

    /// <summary>
    /// Unions isolated copies of the visible bodies and splits the union's boundary into shells.
    /// A watertight flow volume is a shell of its own; a leak merges it with the outer skin. Only
    /// the flow volume shell is left shown for inspection. Any previous test is cleaned up first.
    /// State is saved after each step, so Clean up works even after an error or a restart.
    /// </summary>
    private void TestWatertight() => RunWithState((doc, state) =>
    {
        if (state.WtFlowPoint is null)
        {
            SetStatus("Select a point on the inside surface of the flow volume and click Flow point from selection first.");
            return;
        }

        if (state.WtActive)
            CleanUpWatertight(doc, state);

        state.WtBodies.AddRange(CatiaBodies.GetVisibleBodies(doc).Select(CatiaBodies.NameOf).Distinct());
        var (toCopy, _) = Resolve(doc, state.WtBodies);
        int empty = toCopy.RemoveAll(CatiaWatertight.IsEmpty);
        string skipped = empty == 0 ? "" : $" Skipped {Bodies(empty)} with no features.";
        if (toCopy.Count == 0)
        {
            state.ClearWatertight();
            SetStatus("Nothing to test: show the bodies to test (with any test seals) first." + skipped);
            return;
        }
        state.Save();

        Progress($"Copying {Bodies(toCopy.Count)}...");
        var (copies, failed) = CatiaWatertight.CopyAsResults(doc, toCopy);
        state.WtCopies.AddRange(copies.Select(CatiaBodies.NameOf));
        state.Save();
        if (failed.Count > 0)
        {
            SetStatus($"Test stopped: CATIA could not copy {string.Join(", ", failed)}. " +
                      "Hide or fix those bodies, then run the test again.");
            return;
        }

        Progress($"Uniting {Bodies(copies.Count)}...");
        object union = CatiaWatertight.Union(doc, copies);
        state.WtUnion = CatiaBodies.NameOf(union);
        state.Save();

        Progress("Extracting the flow volume shell...");
        var r = CatiaWatertight.ExtractFlowShell(doc, union, state.WtFlowPoint);
        CatiaWatertight.ShowOnlyShells(doc);

        string verdict =
            r.FlowDistanceMm > 0.01 ? $"The flow point is {r.FlowDistanceMm:0.##} mm from the tested bodies; pick a point on the inside surface." :
            r.FlowMm2 >= r.SkinMm2 * 0.999 ? "LEAK: the flow volume is connected to the outer skin." :
            r.FlowMm2 > r.SkinMm2 - r.FlowMm2 ? "Probably a LEAK: the flow volume shell is larger than all other shells together." :
            "WATERTIGHT: the flow volume is a separate shell.";
        SetStatus($"{verdict} Flow shell {r.FlowMm2:0} of {r.SkinMm2:0} mm2, {r.Ms / 1000.0:0.0} s.{skipped}");
    });

    private void SetFlowPoint() => RunWithState((doc, state) =>
    {
        state.WtFlowPoint = CatiaWatertight.GetSelectedPoint(doc,
            "Select exactly one point (a vertex or a point feature) on the inside surface of the flow volume.");
        SetStatus("Flow point stored.");
    });

    private void CleanUpTest() => RunWithState((doc, state) =>
    {
        bool hadTest = state.WtActive;
        int shown = CleanUpWatertight(doc, state);
        SetStatus(hadTest ? $"Watertight test cleaned up; {Bodies(shown)} shown again." : "No watertight test to clean up.");
    });

    // Deletes the shells geoset, the union and the copies, and shows the tested bodies again.
    // Returns how many bodies were shown.
    private int CleanUpWatertight(object doc, PartState state)
    {
        Progress("Cleaning up the watertight test...");
        CatiaWatertight.CleanUp(doc, new[] { state.WtUnion }.Concat(state.WtCopies).OfType<string>());

        var (shown, _) = Resolve(doc, state.WtBodies);
        if (shown.Count > 0)
            CatiaBodies.ShowExactlyBodies(doc, shown);

        state.ClearWatertight();
        state.Save();
        return shown.Count;
    }

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
            state.Save();
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

    // Shows a message straight away, during a long operation that blocks the UI.
    private void Progress(string message)
    {
        SetStatus(message);
        _status.Refresh();
    }
}
