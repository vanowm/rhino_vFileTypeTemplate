using Rhino.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VFileTypeTemplate;

// ---------------------------------------------------------------------------
// Rhino Options page — shown under Tools > Options > File Type Template
// ---------------------------------------------------------------------------

public sealed class VFileTypeTemplateOptionsPage : OptionsDialogPage
{
  private VFileTypeTemplateOptionsControl? _control;

  public VFileTypeTemplateOptionsPage() : base("File Type Template") { }

  public override string LocalPageTitle => "File Type Template";

  public override object PageControl => _control ??= new VFileTypeTemplateOptionsControl();

  public override bool OnApply()
  {
    _control?.SaveConfig();
    return true;
  }

  public override bool OnActivate(bool active)
  {
    if (active)
    {
      _control?.ReloadConfig();
      _control?.InstallHook();
    }
    else
    {
      _control?.RemoveHook();
    }
    return base.OnActivate(active);
  }
}

// ---------------------------------------------------------------------------
// WinForms panel used as the page control
// ---------------------------------------------------------------------------

internal sealed class VFileTypeTemplateOptionsControl : Panel
{
  private readonly CheckBox _enabledCheck;
  private readonly GridView _grid;
  private readonly Button _addBtn;
  private readonly Button _removeBtn;
  private readonly Button _browseBtn;
  private readonly Button _editBtn;
  private KeyboardHook? _keyHook;

  internal void InstallHook() { _keyHook ??= new KeyboardHook(_grid); }
  internal void RemoveHook()  { _keyHook?.Dispose(); _keyHook = null; }

  protected override void Dispose(bool disposing)
  {
    if (disposing) RemoveHook();
    base.Dispose(disposing);
  }

  public VFileTypeTemplateOptionsControl()
  {
    Dock = DockStyle.Fill;

    // ---- Layout ----
    var layout = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      ColumnCount = 1,
      RowCount = 4,
      Padding = new Padding(8),
    };
    layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 0: checkbox
    layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 1: grid
    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 2: buttons
    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 3: info label

    // ---- Enabled checkbox ----
    _enabledCheck = new CheckBox
    {
      Text = "Enabled (automatically apply template when a mapped file type is opened)",
      AutoSize = true,
      Dock = DockStyle.Fill,
      Margin = new Padding(0, 0, 0, 6),
    };

    // ---- Grid ----
    _grid = new GridView
    {
      Dock = DockStyle.Fill,
      Margin = new Padding(0, 0, 0, 4),
      AllowUserToAddRows = false,
      AllowUserToDeleteRows = false,
      RowHeadersVisible = false,
      SelectionMode = DataGridViewSelectionMode.FullRowSelect,
      MultiSelect = false,
      AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
      BackgroundColor = SystemColors.Window,
      GridColor = SystemColors.ControlLight,
      BorderStyle = BorderStyle.FixedSingle,
      EnableHeadersVisualStyles = true,
      ShowCellToolTips = false,
      EditMode = DataGridViewEditMode.EditOnF2,
      AllowUserToResizeRows = false,   // prevents SizeNS cursor in edit mode
    };
    _grid.DefaultCellStyle.BackColor = SystemColors.Window;
    _grid.DefaultCellStyle.ForeColor = SystemColors.ControlText;
    _grid.DefaultCellStyle.SelectionBackColor = SystemColors.Highlight;
    _grid.DefaultCellStyle.SelectionForeColor = SystemColors.HighlightText;
    _grid.AlternatingRowsDefaultCellStyle.BackColor = SystemColors.Window;

    _grid.Columns.Add(new GridTextBoxColumn
    {
      HeaderText = "Extensions (e.g. .dxf, .dwg)",
      Name = "Extension",
      FillWeight = 28,
      MinimumWidth = 100,
    });
    _grid.Columns.Add(new GridTextBoxColumn
    {
      HeaderText = "Template File (.3dm) — blank = Rhino default",
      Name = "TemplatePath",
      FillWeight = 72,
      MinimumWidth = 180,
    });

    // CellFormatting: highlight missing templates; show <Default>/<ext> placeholder; shorten paths.
    _grid.CellFormatting += (s, e) =>
    {
      if (e.RowIndex < 0 || e.CellStyle == null) return;
      var row = _grid.Rows[e.RowIndex];

      var tplRaw = row.Cells["TemplatePath"].Value?.ToString()?.Trim() ?? string.Empty;
      bool missing = !string.IsNullOrEmpty(tplRaw) && !File.Exists(ResolveFullTemplatePath(tplRaw));
      if (missing)
      {
        e.CellStyle.BackColor = Color.MistyRose;
        e.CellStyle.SelectionBackColor = Color.LightCoral;
        e.CellStyle.SelectionForeColor = SystemColors.ControlText;
      }

      bool isCellEditing = _grid.IsCurrentCellInEditMode &&
                           _grid.CurrentCell?.RowIndex == e.RowIndex &&
                           _grid.CurrentCell?.ColumnIndex == e.ColumnIndex;

      if (e.ColumnIndex == _grid.Columns["TemplatePath"].Index)
      {
        // Only show <Default> / shorten when NOT in edit mode for this cell.
        var val = e.Value?.ToString() ?? string.Empty;
        if (!isCellEditing)
        {
          if (string.IsNullOrEmpty(val) && !missing)
          {
            e.Value = "<Default>";
            e.CellStyle.ForeColor = SystemColors.GrayText;
          }
          else
            e.Value = ShortenTemplatePath(val);
          e.FormattingApplied = true;
        }
      }
    };

    // Suppress DataError — also prevent the beep that fires when Cancel=true.
    _grid.DataError += (s, e) => { e.ThrowException = false; e.Cancel = false; };

    // Commit edit when focus leaves the grid (e.g. clicking checkbox or a button).
    // Also remove any rows where Extension is blank.
    _grid.Leave += (s, e) =>
    {
      _grid.EndEdit();
      RemoveEmptyRows();
    };

    // Mark row as "was edited" so RemoveEmptyRows will clean it up if still blank.
    _grid.CellBeginEdit += (s, e) =>
    {
      _grid.Rows[e.RowIndex].Tag = true;
    };

    // Remove empty rows when a cell edit ends.
    _grid.CellEndEdit += (s, e) =>
    {
      BeginInvoke((Action)RemoveEmptyRows);
    };

    // Single click on a data cell → immediately enter edit mode.
    _grid.CellClick += (s, e) =>
    {
      if (e.RowIndex >= 0 && e.ColumnIndex >= 0)
        _grid.BeginEdit(true);
    };

    // When the editing control loses focus (e.g. clicking a button), commit the
    // current value so it isn’t lost. Unsubscribe-then-subscribe to avoid
    // duplicates (the same editing control instance is reused across cells).
    _grid.EditingControlShowing += (s, e) =>
    {
      if (e.Control is not GridTextBoxEditingControl tb) return;
      tb.Leave -= CommitOnEditingControlLeave;
      tb.Leave += CommitOnEditingControlLeave;
      if (_grid.CurrentCell?.ColumnIndex == _grid.Columns["TemplatePath"].Index)
      {
        tb.SetCueBanner("<Default>");
        // Show shortened value so the user edits the bare name, not the full path.
        var raw = _grid.CurrentCell?.Value?.ToString() ?? string.Empty;
        var shortened = ShortenTemplatePath(raw);
        if (tb.Text != shortened)
        {
          tb.Text = shortened;
          tb.SelectAll();
        }
      }
      else
      {
        tb.SetCueBanner(string.Empty); // clear any cue banner from a previous cell
      }
    };

    // Double-click on empty grid area → add new row (skip if empty-extension row exists).
    _grid.MouseDoubleClick += (s, e) =>
    {
      var hit = _grid.HitTest(e.X, e.Y);
      if (hit.RowIndex < 0 && hit.Type != DataGridViewHitTestType.ColumnHeader)
        OnAdd(null, EventArgs.Empty);
    };

    // ---- Buttons ----
    _addBtn    = MakeButton("&Add",            OnAdd);
    _removeBtn = MakeButton("&Remove",          OnRemove);
    _browseBtn = MakeButton("&Browse\u2026",    OnBrowse);
    _editBtn   = MakeButton("&Edit template",   OnEditTemplate);

    var buttonPanel = new FlowLayoutPanel
    {
      AutoSize = true,
      Dock = DockStyle.Fill,
      FlowDirection = FlowDirection.LeftToRight,
      Margin = new Padding(0, 0, 0, 4),
    };
    buttonPanel.Controls.AddRange(new Control[] { _addBtn, _removeBtn, _browseBtn, _editBtn });

    // ---- Info label ----
    var infoLabel = new Label
    {
      Text = "Separate multiple extensions with commas (e.g. .dxf, .dwg). " +
             "The leading dot is optional.\n" +
             "Leave template path empty to apply Rhino's current default template.",
      AutoSize = true,
      Dock = DockStyle.Fill,
      ForeColor = SystemColors.GrayText,
    };

    // ---- Assemble ----
    layout.Controls.Add(_enabledCheck, 0, 0);
    layout.Controls.Add(_grid,         0, 1);
    layout.Controls.Add(buttonPanel,   0, 2);
    layout.Controls.Add(infoLabel,     0, 3);

    Controls.Add(layout);
    ReloadConfig();
  }

  // ---- Button helper ----

  private static Button MakeButton(string text, EventHandler handler)
  {
    var btn = new Button
    {
      Text = text,
      AutoSize = true,
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      Padding = new Padding(6, 0, 6, 0),
      MinimumSize = new Size(80, 26),
      Margin = new Padding(0, 0, 6, 0),
    };
    btn.Click += handler;
    return btn;
  }

  // ---- Template path helpers ----

  private static string GetTemplateDir()
  {
    try
    {
      var tf = Rhino.ApplicationSettings.FileSettings.TemplateFile;
      if (!string.IsNullOrEmpty(tf))
        return Path.GetDirectoryName(tf) ?? string.Empty;
    }
    catch { }
    return string.Empty;
  }

  /// <summary>Shows just the filename when the path is inside the default template dir.</summary>
  private static string ShortenTemplatePath(string fullPath)
  {
    if (string.IsNullOrEmpty(fullPath)) return fullPath;
    var dir = GetTemplateDir();
    if (!string.IsNullOrEmpty(dir) &&
        string.Equals(Path.GetDirectoryName(fullPath), dir, StringComparison.OrdinalIgnoreCase))
      return Path.GetFileNameWithoutExtension(fullPath);
    return fullPath;
  }

  /// <summary>Resolves a bare filename back to a full path via the default template dir.</summary>
  private static string ResolveFullTemplatePath(string path)
  {
    if (string.IsNullOrEmpty(path)) return path;
    if (Path.IsPathRooted(path)) return path;
    var dir = GetTemplateDir();
    if (!string.IsNullOrEmpty(dir))
    {
      var candidate = Path.Combine(dir, path);
      if (File.Exists(candidate)) return candidate;
      // Try appending .3dm when no extension was given
      if (string.IsNullOrEmpty(Path.GetExtension(path)))
      {
        candidate = Path.Combine(dir, path + ".3dm");
        if (File.Exists(candidate)) return candidate;
      }
    }
    return path;
  }

  // ---- Load / Save ----

  public void ReloadConfig()
  {
    var config = VFileTypeTemplateConfig.Load();
    _enabledCheck.Checked = config.Enabled;
    _grid.Rows.Clear();

    // Group mappings that share the same template onto a single row.
    var groups = config.Mappings
      .GroupBy(m => m.TemplatePath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
      .Select(g => new
      {
        Extensions = string.Join(", ",
          g.SelectMany(m => VFileTypeTemplateConfig.SplitExtensions(m.Extension))
           .Distinct(StringComparer.OrdinalIgnoreCase)
           .OrderBy(x => x)),
        Template = g.Key,
      });

    foreach (var group in groups)
      _grid.Rows.Add(group.Extensions, group.Template);
  }

  public void SaveConfig()
  {
    // Commit any cell still being edited.
    _grid.EndEdit();

    // Build ext → template map; if the same extension appears in multiple rows,
    // the last row wins.
    var extToTemplate = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (DataGridViewRow row in _grid.Rows)
    {
      if (row.IsNewRow) continue;
      var rawExt = row.Cells["Extension"].Value?.ToString()?.Trim() ?? string.Empty;
      if (string.IsNullOrEmpty(rawExt)) continue;

      // Resolve bare filename to full path so File.Exists works at runtime.
      var path = ResolveFullTemplatePath(
        row.Cells["TemplatePath"].Value?.ToString()?.Trim() ?? string.Empty);

      foreach (var ext in VFileTypeTemplateConfig.SplitExtensions(rawExt))
        extToTemplate[ext] = path;
    }

    // Re-group by template → one FileTypeMapping per template.
    var config = new VFileTypeTemplateConfig { Enabled = _enabledCheck.Checked };
    foreach (var group in extToTemplate
               .GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
               .OrderBy(g => g.Key))
    {
      config.Mappings.Add(new FileTypeMapping
      {
        Extension    = string.Join(", ", group.Select(kv => kv.Key).OrderBy(e => e)),
        TemplatePath = group.Key,
      });
    }

    config.Save();
    VFileTypeTemplatePlugIn.TryLog($"Options saved: Enabled={config.Enabled} Mappings={config.Mappings.Count}");
  }

  // ---- Button handlers ----

  private void CommitOnEditingControlLeave(object? sender, EventArgs e)
  {
    if (!_grid.IsCurrentCellInEditMode) return;
    var rowIndex = _grid.CurrentCell?.RowIndex ?? -1;
    _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
    // Defer EndEdit and row re-selection to after the focus change completes.
    BeginInvoke((Action)(() =>
    {
      if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();
      // Ensure the full row stays selected (FullRowSelect mode).
      if (rowIndex >= 0 && rowIndex < _grid.RowCount)
        _grid.Rows[rowIndex].Selected = true;
    }));
  }

  private void RemoveEmptyRows()
  {
    // Only remove rows where: both cells are blank AND at least one cell was
    // entered into edit mode (Tag set by CellBeginEdit) AND the user has moved
    // away from the row.
    var currentRowIndex = _grid.CurrentCell?.RowIndex ?? -1;
    var toRemove = _grid.Rows.Cast<DataGridViewRow>()
      .Where(r => !r.IsNewRow &&
                  r.Tag != null &&
                  string.IsNullOrWhiteSpace(r.Cells["Extension"].Value?.ToString()) &&
                  string.IsNullOrWhiteSpace(r.Cells["TemplatePath"].Value?.ToString()) &&
                  r.Index != currentRowIndex)
      .ToList();
    foreach (var r in toRemove)
      _grid.Rows.Remove(r);
  }

  private void OnAdd(object? sender, EventArgs e)
  {
    // Commit any in-progress edit first so the cell value is visible to the check below.
    _grid.EndEdit();

    // Navigate to existing empty-extension row instead of adding a duplicate.
    var existing = _grid.Rows.Cast<DataGridViewRow>()
      .FirstOrDefault(r => !r.IsNewRow &&
        string.IsNullOrEmpty(r.Cells["Extension"].Value?.ToString()));
    if (existing != null)
    {
      _grid.CurrentCell = existing.Cells["Extension"];
      BeginInvoke((Action)(() => _grid.BeginEdit(true)));
      return;
    }
    var idx = _grid.Rows.Add(string.Empty, string.Empty);
    _grid.CurrentCell = _grid.Rows[idx].Cells["Extension"];
    _grid.BeginEdit(true);
  }

  private void OnRemove(object? sender, EventArgs e)
  {
    foreach (DataGridViewRow row in _grid.SelectedRows.Cast<DataGridViewRow>().ToList())
      if (!row.IsNewRow)
        _grid.Rows.Remove(row);
  }

  private void OnBrowse(object? sender, EventArgs e)
  {
    DataGridViewRow? row = null;
    if (_grid.SelectedRows.Count > 0)
      row = _grid.SelectedRows[0];
    else if (_grid.CurrentCell != null)
      row = _grid.Rows[_grid.CurrentCell.RowIndex];
    else if (_grid.Rows.Count > 0)
      row = _grid.Rows[0];
    if (row == null) return;

    using var dlg = new System.Windows.Forms.OpenFileDialog
    {
      Title = "Select template file",
      Filter = "Rhino 3DM files (*.3dm)|*.3dm",
      CheckFileExists = true,
    };

    var existingRaw = row.Cells["TemplatePath"].Value?.ToString() ?? string.Empty;
    var existing = ResolveFullTemplatePath(existingRaw);
    if (!string.IsNullOrEmpty(existing) && File.Exists(existing))
    {
      dlg.InitialDirectory = Path.GetDirectoryName(existing);
      dlg.FileName = Path.GetFileName(existing);  // pre-selects the file
    }
    else
    {
      var tplDir = GetTemplateDir();
      if (!string.IsNullOrEmpty(tplDir))
        dlg.InitialDirectory = tplDir;
    }

    if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
    var shortPath = ShortenTemplatePath(dlg.FileName);

    // If the TemplatePath cell of this row is currently open in the editing control,
    // update the text there directly so the change is visible without flickering.
    bool tplCellEditing = _grid.IsCurrentCellInEditMode &&
                          _grid.CurrentCell?.RowIndex == row.Index &&
                          _grid.CurrentCell?.ColumnIndex == _grid.Columns["TemplatePath"].Index;
    if (tplCellEditing && _grid.EditingControl is TextBox editTb)
    {
      editTb.Text = shortPath;
      editTb.SelectAll();
    }
    else
    {
      _grid.EndEdit();
      row.Cells["TemplatePath"].Value = shortPath;
    }

    // Move focus to the Extension cell of this row so the user can verify/edit it.
    _grid.CurrentCell = row.Cells["Extension"];
    _grid.Focus();
  }

  private void OnEditTemplate(object? sender, EventArgs e)
  {
    if (_grid.SelectedRows.Count == 0) return;

    var row = _grid.SelectedRows[0];
    var raw = row.Cells["TemplatePath"].Value?.ToString()?.Trim() ?? string.Empty;

    // Resolve configured path → if empty, fall back to Rhino default.
    var fullPath = string.IsNullOrEmpty(raw)
      ? VFileTypeTemplatePlugIn.ResolveTemplatePath(raw)
      : ResolveFullTemplatePath(raw);

    if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
    {
      var msg = string.IsNullOrEmpty(raw)
        ? "No template configured for this entry and no Rhino default template found."
        : $"Template file not found:\n{fullPath}";
      MessageBox.Show(msg, "File Type Template", MessageBoxButtons.OK, MessageBoxIcon.Warning);
      return;
    }

    // Open the .3dm in a new Rhino instance (find Rhino.exe from the running process).
    try
    {
      var rhinoExe = Process.GetCurrentProcess().MainModule?.FileName;
      if (!string.IsNullOrEmpty(rhinoExe) && File.Exists(rhinoExe))
        Process.Start(new ProcessStartInfo
        {
          FileName = rhinoExe,
          Arguments = $"\"{fullPath}\"",
          UseShellExecute = false,
        });
      else
        Process.Start(new ProcessStartInfo { FileName = fullPath, UseShellExecute = true });
    }
    catch (Exception ex)
    {
      MessageBox.Show($"Could not open template:\n{ex.Message}",
        "File Type Template", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
  }
}

// ---------------------------------------------------------------------------
// Custom DataGridView: prevents Enter/Esc from closing the host native dialog.
// ---------------------------------------------------------------------------

internal sealed class GridView : DataGridView
{
  // Win32 constants for DLGC_WANTALLKEYS at the DataGridView level.
  private const int WM_GETDLGCODE    = 0x0087;
  private const int WM_KEYDOWN       = 0x0100;
  private const int DLGC_WANTALLKEYS = 0x0004;
  private const int VK_RETURN        = 0x0D;

  // DataGridView internally checks `this.Focused` during cell painting to choose
  // between active (Highlight) and inactive (Control) selection colours.  It does
  // this AFTER OnCellPainting and AFTER DefaultCellStyle — there is no other hook.
  // Returning true whenever any row is selected makes all selected rows always
  // paint with the active highlight colour, regardless of which control actually
  // holds keyboard focus.
  public override bool Focused => base.Focused || ContainsFocus || SelectedRows.Count > 0;

  protected override void WndProc(ref Message m)
  {
    // Enter key while not in edit mode — start editing the current cell.
    // Must intercept here: ProcessDialogKey is not reliably called in Rhino’s
    // native Win32 dialog context.
    if (m.Msg == WM_KEYDOWN && (int)m.WParam == VK_RETURN &&
        !IsCurrentCellInEditMode && CurrentCell != null)
    {
      BeginEdit(true);
      return; // consume — editing control must not see this Enter
    }

    base.WndProc(ref m);
    if (m.Msg == WM_GETDLGCODE)
    {
      VFileTypeTemplatePlugIn.TryLog($"[GridView] WM_GETDLGCODE base={m.Result} → adding DLGC_WANTALLKEYS");
      m.Result = (IntPtr)(m.Result.ToInt32() | DLGC_WANTALLKEYS);
    }
  }

  protected override bool ProcessDialogKey(Keys keyData)
  {
    switch (keyData & Keys.KeyCode)
    {
      case Keys.Enter:
      case Keys.Escape:
        return true;   // consumed — dialog stays open
    }
    return base.ProcessDialogKey(keyData);
  }

  /// <summary>Commits current edit and exits edit mode.</summary>
  internal void CommitAndExit()
  {
    if (!IsCurrentCellInEditMode) return;
    CommitEdit(DataGridViewDataErrorContexts.Commit);
    EndEdit();
  }

  /// <summary>Cancels current edit and exits edit mode.</summary>
  internal void CancelAndExit()
  {
    if (!IsCurrentCellInEditMode) return;
    CancelEdit();
    EndEdit();
  }

  /// <summary>Moves CurrentCell one column forward or backward, wrapping to the next/prev row.</summary>
  internal void NavigateCell(bool backward)
  {
    if (CurrentCell == null) return;
    int row = CurrentCell.RowIndex;
    int col = CurrentCell.ColumnIndex;
    if (backward) { col--; if (col < 0) { col = ColumnCount - 1; row--; } }
    else          { col++; if (col >= ColumnCount) { col = 0; row++; } }
    if (row < 0 || row >= RowCount) return;
    var cell = Rows[row].Cells[col];
    // EndEdit first (safe in BeginInvoke — we're outside WndProc) then navigate.
    BeginInvoke((Action)(() => { try { EndEdit(); CurrentCell = cell; BeginEdit(true); } catch { } }));
  }
}

// ---------------------------------------------------------------------------
// Custom editing control.
//
// WM_GETDLGCODE: returns DLGC_WANTALLKEYS so the native Rhino dialog's
//   IsDialogMessage loop delivers Tab/Enter/Esc as WM_KEYDOWN instead of
//   treating them as dialog-navigation keystrokes.
//
// WndProc intercepts WM_KEYDOWN for Tab/Enter/Esc BEFORE calling base, so
//   the native EDIT control never processes them (which would ring the bell).
// ---------------------------------------------------------------------------

internal sealed class GridTextBoxEditingControl : DataGridViewTextBoxEditingControl
{
  private const int  WM_GETDLGCODE    = 0x0087;
  private const int  WM_KEYDOWN       = 0x0100;
  private const int  WM_CHAR          = 0x0102;
  private const int  DLGC_WANTALLKEYS = 0x0004;
  private const int  VK_TAB           = 0x09;
  private const uint EM_SETCUEBANNER  = 0x1501;

  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);

  public void SetCueBanner(string text)
  {
    if (IsHandleCreated)
      SendMessage(Handle, EM_SETCUEBANNER, IntPtr.Zero, text);
  }

  /// <summary>
  /// Tell the DataGridView that the editing control handles cursor keys itself,
  /// so the grid never steals them for row/column navigation while in edit mode.
  /// </summary>
  public override bool EditingControlWantsInputKey(Keys keyData, bool dataGridViewWantsInputKey)
  {
    switch (keyData & Keys.KeyCode)
    {
      case Keys.Left: case Keys.Right: // keep cursor movement — never navigate columns
      case Keys.Up:   case Keys.Down:  // do nothing in single-line edit; don’t navigate rows
      case Keys.Home: case Keys.End:
        return true;
    }
    return base.EditingControlWantsInputKey(keyData, dataGridViewWantsInputKey);
  }

  protected override void WndProc(ref Message m)
  {
    // Eat WM_CHAR for Tab so the native EDIT doesn't ring the bell.
    if (m.Msg == WM_CHAR && (int)m.WParam == VK_TAB)
    {
      m.Result = IntPtr.Zero;
      return;
    }

    if (m.Msg == WM_KEYDOWN && (int)m.WParam == VK_TAB)
    {
      VFileTypeTemplatePlugIn.TryLog("[EditCtrl] Tab → NavigateCell");
      if (EditingControlDataGridView is GridView gv)
        gv.NavigateCell((Control.ModifierKeys & Keys.Shift) != 0);
      m.Result = IntPtr.Zero;
      return; // do NOT call base
    }

    base.WndProc(ref m);

    if (m.Msg == WM_GETDLGCODE)
    {
      VFileTypeTemplatePlugIn.TryLog($"[EditCtrl] WM_GETDLGCODE base={m.Result} → adding DLGC_WANTALLKEYS");
      m.Result = (IntPtr)(m.Result.ToInt32() | DLGC_WANTALLKEYS);
    }
  }
}

internal sealed class GridTextBoxCell : DataGridViewTextBoxCell
{
  public override Type EditType => typeof(GridTextBoxEditingControl);
}

internal sealed class GridTextBoxColumn : DataGridViewTextBoxColumn
{
  public GridTextBoxColumn() { CellTemplate = new GridTextBoxCell(); }
}

// ---------------------------------------------------------------------------
// Thread-level WH_GETMESSAGE hook.
// Rhino's Options dialog loop consumes WM_KEYDOWN(VK_RETURN) and (VK_ESCAPE)
// before IsDialogMessage can deliver them to child windows, so DLGC_WANTALLKEYS
// alone cannot intercept them.  This hook sees every message as it is dequeued,
// before the dialog loop touches it.  When Enter or Esc arrives while our grid
// is in edit mode, the message is changed to WM_NULL (ignored by the dialog
// loop) and the appropriate grid action is posted via BeginInvoke.
// ---------------------------------------------------------------------------

internal sealed class KeyboardHook : IDisposable
{
  private const int WH_GETMESSAGE = 3;
  private const int HC_ACTION     = 0;
  private const int PM_REMOVE     = 1;
  private const int WM_KEYDOWN    = 0x0100;
  private const int WM_NULL       = 0x0000;
  private const int VK_RETURN     = 0x0D;
  private const int VK_ESCAPE     = 0x1B;

  [StructLayout(LayoutKind.Sequential)]
  private struct MSG
  {
    public IntPtr hwnd;
    public int    message;
    public IntPtr wParam;
    public IntPtr lParam;
    public uint   time;
    public int    ptX, ptY;
  }

  private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

  [DllImport("user32.dll", SetLastError = true)]
  private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
  [DllImport("user32.dll")]
  private static extern bool UnhookWindowsHookEx(IntPtr hhk);
  [DllImport("user32.dll")]
  private static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);
  [DllImport("kernel32.dll")]
  private static extern uint GetCurrentThreadId();

  private readonly GridView _grid;
  private readonly HookProc _proc; // held to prevent GC collection
  private IntPtr _hook;

  public KeyboardHook(GridView grid)
  {
    _grid = grid;
    _proc = Callback;
    _hook = SetWindowsHookEx(WH_GETMESSAGE, _proc, IntPtr.Zero, GetCurrentThreadId());
    VFileTypeTemplatePlugIn.TryLog($"[Hook] installed handle=0x{_hook:X}");
  }

  private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
  {
    if (code == HC_ACTION && (int)wParam == PM_REMOVE)
    {
      var msg = Marshal.PtrToStructure<MSG>(lParam);
      if (msg.message == WM_KEYDOWN && _grid.IsCurrentCellInEditMode)
      {
        int vk = (int)msg.wParam;
        if (vk == VK_RETURN || vk == VK_ESCAPE)
        {
          // Nullify so IsDialogMessage doesn’t close the dialog.
          msg.message = WM_NULL;
          Marshal.StructureToPtr(msg, lParam, false);
          int capturedVk = vk;
          if (_grid.IsHandleCreated)
            _grid.BeginInvoke((Action)(() =>
            {
              VFileTypeTemplatePlugIn.TryLog($"[Hook] dispatch vk=0x{capturedVk:X2}");
              if (capturedVk == VK_RETURN) _grid.CommitAndExit();
              else                         _grid.CancelAndExit();
            }));
        }
      }
    }
    return CallNextHookEx(_hook, code, wParam, lParam);
  }

  public void Dispose()
  {
    if (_hook != IntPtr.Zero)
    {
      UnhookWindowsHookEx(_hook);
      VFileTypeTemplatePlugIn.TryLog("[Hook] uninstalled");
      _hook = IntPtr.Zero;
    }
  }
}
