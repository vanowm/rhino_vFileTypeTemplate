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
      _control?.ReloadConfig();
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
      EditMode = DataGridViewEditMode.EditOnEnter,
      AllowUserToResizeRows = false,   // prevents SizeNS cursor in edit mode
    };
    _grid.DefaultCellStyle.BackColor = SystemColors.Window;
    _grid.DefaultCellStyle.ForeColor = SystemColors.ControlText;
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

      if (e.ColumnIndex == _grid.Columns["Extension"].Index)
      {
        var val = e.Value?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(val) && !isCellEditing)
        {
          e.Value = ".ext";
          e.CellStyle.ForeColor = SystemColors.GrayText;
          e.FormattingApplied = true;
        }
      }
      else if (e.ColumnIndex == _grid.Columns["TemplatePath"].Index)
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

    // Suppress the DataError dialog — errors during formatting are benign.
    _grid.DataError += (s, e) => { e.ThrowException = false; };

    // Commit edit when focus leaves the grid (e.g. clicking checkbox or a button).
    // Also remove any rows where Extension is blank.
    _grid.Leave += (s, e) =>
    {
      _grid.EndEdit();
      RemoveEmptyRows();
    };

    // Remove empty-extension rows when a cell edit ends.
    _grid.CellEndEdit += (s, e) =>
    {
      BeginInvoke((Action)RemoveEmptyRows);
    };

    // Show "<Default>" cue text in the TemplatePath editing control while empty.
    _grid.EditingControlShowing += (s, e) =>
    {
      if (_grid.CurrentCell?.ColumnIndex == _grid.Columns["TemplatePath"].Index &&
          e.Control is GridTextBoxEditingControl tb)
        tb.SetCueBanner("<Default>");
    };

    // Double-click on empty grid area → add new row (skip if empty-extension row exists).
    _grid.MouseDoubleClick += (s, e) =>
    {
      var hit = _grid.HitTest(e.X, e.Y);
      if (hit.RowIndex < 0 && hit.Type != DataGridViewHitTestType.ColumnHeader)
        OnAdd(null, EventArgs.Empty);
    };

    // ---- Buttons ----
    _addBtn    = MakeButton("Add",      OnAdd);
    _removeBtn = MakeButton("Remove",   OnRemove);
    _browseBtn = MakeButton("Browse…",  OnBrowse);
    _editBtn   = MakeButton("Edit…",    OnEditTemplate);

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
    var btn = new Button { Text = text, Size = new Size(86, 26), Margin = new Padding(0, 0, 6, 0) };
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
      return Path.GetFileName(fullPath);
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

  private void RemoveEmptyRows()
  {
    // Remove rows where both Extension and TemplatePath are blank, but not the
    // row currently being edited (to allow the user to type in a new row).
    var toRemove = _grid.Rows.Cast<DataGridViewRow>()
      .Where(r => !r.IsNewRow &&
                  string.IsNullOrWhiteSpace(r.Cells["Extension"].Value?.ToString()) &&
                  string.IsNullOrWhiteSpace(r.Cells["TemplatePath"].Value?.ToString()) &&
                  !(_grid.IsCurrentCellInEditMode && _grid.CurrentCell?.RowIndex == r.Index))
      .ToList();
    foreach (var r in toRemove)
      _grid.Rows.Remove(r);
  }

  private void OnAdd(object? sender, EventArgs e)
  {
    // Navigate to existing empty-extension row instead of adding a duplicate.
    var existing = _grid.Rows.Cast<DataGridViewRow>()
      .FirstOrDefault(r => !r.IsNewRow &&
        string.IsNullOrEmpty(r.Cells["Extension"].Value?.ToString()));
    if (existing != null)
    {
      _grid.CurrentCell = existing.Cells["Extension"];
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
    if (_grid.SelectedRows.Count == 0 && _grid.Rows.Count > 0)
      _grid.Rows[0].Selected = true;
    if (_grid.SelectedRows.Count == 0) return;

    var row = _grid.SelectedRows[0];
    using var dlg = new System.Windows.Forms.OpenFileDialog
    {
      Title = "Select template file",
      Filter = "Rhino 3DM files (*.3dm)|*.3dm",
      CheckFileExists = true,
    };

    var existing = ResolveFullTemplatePath(row.Cells["TemplatePath"].Value?.ToString() ?? string.Empty);
    if (!string.IsNullOrEmpty(existing) && File.Exists(existing))
      dlg.InitialDirectory = Path.GetDirectoryName(existing);
    else
    {
      var tplDir = GetTemplateDir();
      if (!string.IsNullOrEmpty(tplDir))
        dlg.InitialDirectory = tplDir;
    }

    if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
      row.Cells["TemplatePath"].Value = ShortenTemplatePath(dlg.FileName);
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
  // Set when Esc is handled to suppress EditOnEnter from immediately re-entering edit mode.
  private bool _suppressNextEnterEdit;

  // Win32 constants for DLGC_WANTALLKEYS at the DataGridView level.
  private const int WM_GETDLGCODE    = 0x0087;
  private const int DLGC_WANTALLKEYS = 0x0004;

  protected override void WndProc(ref Message m)
  {
    base.WndProc(ref m);
    // Tell IsDialogMessage to deliver Tab/Enter/Esc as WM_KEYDOWN to us,
    // not treat them as dialog-navigation keystrokes.
    if (m.Msg == WM_GETDLGCODE)
      m.Result = (IntPtr)(m.Result.ToInt32() | DLGC_WANTALLKEYS);
  }

  protected override bool ProcessDialogKey(Keys keyData)
  {
    // These are handled in GridTextBoxEditingControl.WndProc when in edit mode.
    // This handles the non-editing-mode case (e.g. Enter/Esc when not editing).
    switch (keyData & Keys.KeyCode)
    {
      case Keys.Enter:
      case Keys.Escape:
        return true;   // consumed — dialog stays open
    }
    return base.ProcessDialogKey(keyData);
  }

  /// <summary>Called by editing control to commit and move to next row.</summary>
  internal void CommitAndMoveNext()
  {
    CommitEdit(DataGridViewDataErrorContexts.Commit);
    EndEdit();
    if (CurrentCell == null) return;
    int nextRow = CurrentCell.RowIndex + 1;
    if (nextRow < RowCount)
      CurrentCell = Rows[nextRow].Cells[0];
    // else stay on last row, no re-entry into edit
  }

  /// <summary>Called by editing control to cancel edit without re-entering edit mode.</summary>
  internal void CancelAndExit()
  {
    _suppressNextEnterEdit = true;
    CancelEdit();
    EndEdit();
  }

  protected override void OnCellEnter(DataGridViewCellEventArgs e)
  {
    if (_suppressNextEnterEdit)
    {
      _suppressNextEnterEdit = false;
      // Skip base — don't trigger EditOnEnter for this one cell-enter event.
      return;
    }
    base.OnCellEnter(e);
  }

  /// <summary>Moves CurrentCell one column forward or backward, wrapping to the next/prev row.</summary>
  internal void NavigateCell(bool backward)
  {
    if (CurrentCell == null) return;
    int row = CurrentCell.RowIndex;
    int col = CurrentCell.ColumnIndex;
    if (backward) { col--; if (col < 0) { col = ColumnCount - 1; row--; } }
    else          { col++; if (col >= ColumnCount) { col = 0; row++; } }
    if (row >= 0 && row < RowCount)
      CurrentCell = Rows[row].Cells[col];
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
  private const int  DLGC_WANTALLKEYS = 0x0004;
  private const int  VK_TAB           = 0x09;
  private const int  VK_RETURN        = 0x0D;
  private const int  VK_ESCAPE        = 0x1B;
  private const uint EM_SETCUEBANNER  = 0x1501;

  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);

  /// <summary>Sets the grey placeholder text shown when the TextBox is empty.</summary>
  public void SetCueBanner(string text)
  {
    if (IsHandleCreated)
      SendMessage(Handle, EM_SETCUEBANNER, IntPtr.Zero, text);
  }

  protected override void WndProc(ref Message m)
  {
    if (m.Msg == WM_KEYDOWN)
    {
      int vk = (int)m.WParam;
      if (vk == VK_TAB || vk == VK_RETURN || vk == VK_ESCAPE)
      {
        // Handle here before calling base — prevents the native EDIT proc ringing the bell.
        if (EditingControlDataGridView is GridView gv)
        {
          switch (vk)
          {
            case VK_TAB:
              gv.CommitEdit(DataGridViewDataErrorContexts.Commit);
              gv.EndEdit();
              gv.NavigateCell((Control.ModifierKeys & Keys.Shift) != 0);
              break;
            case VK_RETURN:
              gv.CommitAndMoveNext();
              break;
            case VK_ESCAPE:
              gv.CancelAndExit();
              break;
          }
        }
        m.Result = IntPtr.Zero;
        return; // do NOT call base
      }
    }

    base.WndProc(ref m);

    if (m.Msg == WM_GETDLGCODE)
      m.Result = (IntPtr)(m.Result.ToInt32() | DLGC_WANTALLKEYS);
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
