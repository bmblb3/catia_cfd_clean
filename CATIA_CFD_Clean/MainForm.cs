using System.Windows.Forms;

namespace CATIA_CFD_Clean;

internal sealed class MainForm : Form
{
    private readonly object?[] _bodies = new object?[3]; // index 1 and 2 used
    private readonly object?[] _seals = new object?[3];  // index 1 and 2 used
    private object? _sealResult;                         // the green intersected "Seal" body
    private readonly List<object> _locked = new();      // bodies in the locked view
    private readonly List<object> _plugs = new();       // confirmed plug bodies

    // ---- Smart F2 ----
    private readonly CatiaF2Hook _f2Hook;
    private int _f2HookError = -1;        // -1 = not started, 0 = running, else Win32 error
    private int _f2Pending;               // 1 while a toggle is queued on the UI thread (Interlocked)
    private bool _polling;                // a poll tick is in progress (COM calls can pump messages)
    private bool _busy;                   // a CATIA operation started from this form is running
    private (int Pid, bool IsCatia) _foregroundCache;
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 100 };

    private readonly Button _makeSeal;
    private readonly Label _f2State = new()
    {
        Dock = DockStyle.Fill,
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(6, 2, 6, 2),
    };
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
        _f2Hook = new CatiaF2Hook(OnF2FromHook);
        Text = "CATIA Body Viewer";
        Width = 480;
        StartPosition = FormStartPosition.CenterScreen;

        // O-I sealing: one column per body, actions in workflow order.
        var (sealGroup, seal) = MakeGroup("O-I sealing", 2);
        seal.Controls.Add(MakeHeader("Body 1"), 0, 0);
        seal.Controls.Add(MakeHeader("Body 2"), 1, 0);
        seal.Controls.Add(MakeButton("Store selected body", () => Store(1)), 0, 1);
        seal.Controls.Add(MakeButton("Store selected body", () => Store(2)), 1, 1);
        seal.Controls.Add(MakeButton("Show body only", () => ShowBody(1)), 0, 2);
        seal.Controls.Add(MakeButton("Show body only", () => ShowBody(2)), 1, 2);
        seal.Controls.Add(MakeButton("Select seal faces", () => SelectSealFaces(1)), 0, 3);
        seal.Controls.Add(MakeButton("Select seal faces", () => SelectSealFaces(2)), 1, 3);
        seal.Controls.Add(MakeButton("Show seal only", () => ShowSeal(1)), 0, 4);
        seal.Controls.Add(MakeButton("Show seal only", () => ShowSeal(2)), 1, 4);
        AddSpanning(seal, MakeButton("Show bodies, seals and plugs together", ShowAssembly), 5);
        _makeSeal = MakeButton("Make seal (intersect seal 1 and seal 2)", MakeSeal);
        _makeSeal.Enabled = false;
        AddSpanning(seal, _makeSeal, 6);

        var (plugGroup, plug) = MakeGroup("Plug sealing", 1);
        plug.Controls.Add(MakeButton("Select plug curve", SelectPlugCurve), 0, 0);

        var (visGroup, vis) = MakeGroup("Visibility", 2);
        vis.Controls.Add(MakeButton("Toggle bodies of selected faces or bodies (F2)", () => ToggleSelectedFaces(false)), 0, 0);
        vis.Controls.Add(MakeButton("Show all solids in selected body", ShowSolidsInSelectedBody), 1, 0);
        AddSpanning(vis, _f2State, 1);

        var (lockGroup, lockGrid) = MakeGroup("Lock view", 3);
        lockGrid.Controls.Add(MakeButton("Reset to visible", ResetLock), 0, 0);
        lockGrid.Controls.Add(MakeButton("Append selected", AppendToLock), 1, 0);
        lockGrid.Controls.Add(MakeButton("Show only locked", ShowLocked), 2, 0);

        _root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(8, 6, 8, 6),
            AutoScroll = true,
        };
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        GroupBox[] groups = { sealGroup, plugGroup, visGroup, lockGroup };
        for (int i = 0; i < groups.Length; i++)
        {
            _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _root.Controls.Add(groups[i], 0, i);
        }
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // absorbs extra height

        Controls.Add(_root);
        Controls.Add(_status);

        _pollTimer.Tick += PollSelection;
        SetStatus("Select a body in CATIA, then click a Store button.");
        UpdateF2State();
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

        _f2HookError = _f2Hook.Start();
        UpdateF2State();
        _pollTimer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _pollTimer.Stop();
        _f2Hook.Dispose();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pollTimer.Dispose();
            _f2Hook.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- Smart F2 ----

    /// <summary>
    /// Toggles the bodies owning the selected faces: hides them (keeping their solid features
    /// visible) or, if all are already hidden, shows them again.
    /// </summary>
    /// <param name="fromHotkey">True when triggered by F2 (status wording only).</param>
    private void ToggleSelectedFaces(bool fromHotkey)
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

            bool hidden = CatiaBodies.ToggleBodiesKeepSolids(doc, bodies);
            string noun = $"{bodies.Count} bod{(bodies.Count == 1 ? "y" : "ies")}";
            SetStatus($"{(fromHotkey ? "F2: " : "")}{(hidden ? $"Hid {noun}; solids kept visible." : $"Showed {noun}.")}");
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

    // Called on the hook thread. Only queues work; repeated presses while one is queued are dropped.
    private void OnF2FromHook()
    {
        if (Interlocked.Exchange(ref _f2Pending, 1) != 0)
            return;

        try
        {
            BeginInvoke(() =>
            {
                try { ToggleSelectedFaces(true); }
                finally { Volatile.Write(ref _f2Pending, 0); }
            });
        }
        catch
        {
            // Form is closing or its handle is gone.
            Volatile.Write(ref _f2Pending, 0);
        }
    }

    // Keeps the hook's view of CATIA up to date. The hook itself cannot make COM calls, so it
    // relies on this poll to know whether CATIA is in front and whether faces are selected.
    private void PollSelection(object? sender, EventArgs e)
    {
        if (_polling)
            return;

        _polling = true;
        try
        {
            int catiaPid = CatiaF2Hook.GetForegroundCatiaProcessId(ref _foregroundCache);
            bool armed = false;
            if (catiaPid != 0 && !_busy && Volatile.Read(ref _f2Pending) == 0)
            {
                object? doc = CatiaConnection.TryGetActiveDocument();
                if (doc is not null)
                {
                    try
                    {
                        armed = CatiaBodies.HasToggleableSelection(doc);
                    }
                    catch
                    {
                        // CATIA busy or the selection changed mid-read: keep the last known state.
                        armed = _f2Hook.Armed;
                    }
                }
            }

            _f2Hook.CatiaProcessId = catiaPid;
            _f2Hook.Armed = armed;
            UpdateF2State();
        }
        finally
        {
            _polling = false;
        }
    }

    private void UpdateF2State()
    {
        string text = _f2HookError switch
        {
            -1 => "F2 shortcut starting...",
            0 when _f2Hook.Armed => "F2 ready: faces or bodies are selected in CATIA.",
            0 => "F2 shortcut: select faces or bodies in CATIA, then press F2.",
            _ => $"F2 shortcut unavailable (Windows error {_f2HookError}); use the button.",
        };
        if (_f2State.Text != text)
            _f2State.Text = text;
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

    // Runs a CATIA operation, reporting errors in the status bar. Operations do not nest:
    // a second one (e.g. an F2 press while a seal dialog is open) is refused.
    private void Run(Action action)
    {
        if (_busy)
        {
            SetStatus("Finish the current operation first.");
            return;
        }

        _busy = true;
        _f2Hook.Armed = false; // operations change the CATIA selection; re-armed by the next poll
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
