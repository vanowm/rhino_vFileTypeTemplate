using Rhino.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
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
  private readonly DataGridView _grid;
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
    _grid = new DataGridView
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
      // Enter edit mode as soon as a cell is selected (Tab navigates + immediately editable)
      EditMode = DataGridViewEditMode.EditOnEnter,
    };
    _grid.DefaultCellStyle.BackColor = SystemColors.Window;
    _grid.DefaultCellStyle.ForeColor = SystemColors.ControlText;
    _grid.AlternatingRowsDefaultCellStyle.BackColor = SystemColors.Window;

    _grid.Columns.Add(new DataGridViewTextBoxColumn
    {
      HeaderText = "Extensions (e.g. .dxf, .dwg)",
      Name = "Extension",
      FillWeight = 28,
      MinimumWidth = 100,
    });
    _grid.Columns.Add(new DataGridViewTextBoxColumn
    {
      HeaderText = "Template File (.3dm) — blank = Rhino default",
      Name = "TemplatePath",
      FillWeight = 72,
      MinimumWidth = 180,
    });

    // CellFormatting: shorten template path display + highlight rows with missing template files.
    _grid.CellFormatting += (s, e) =>
    {
      if (e.RowIndex < 0) return;
      var row = _grid.Rows[e.RowIndex];

      // Check if this row's template path is configured but the file is missing.
      var tplRaw = row.Cells["TemplatePath"].Value?.ToString()?.Trim() ?? string.Empty;
      bool missing = !string.IsNullOrEmpty(tplRaw) && !File.Exists(ResolveFullTemplatePath(tplRaw));
      if (missing)
      {
        e.CellStyle.BackColor = Color.MistyRose;
        e.CellStyle.SelectionBackColor = Color.LightCoral;
        e.CellStyle.SelectionForeColor = SystemColors.ControlText;
      }

      // Shorten display of the template path column.
      if (e.ColumnIndex == _grid.Columns["TemplatePath"].Index &&
          e.Value is string path && !string.IsNullOrEmpty(path))
      {
        e.Value = ShortenTemplatePath(path);
        e.FormattingApplied = true;
      }
    };

    // --- Enter/Esc handling ---
    // When the grid has focus but the editing control is not yet showing
    // (e.g. header row selected), swallow Enter/Esc so they don't close the dialog.
    _grid.PreviewKeyDown += (s, e) =>
    {
      if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Escape)
        e.IsInputKey = true;
    };
    _grid.KeyDown += (s, e) =>
    {
      if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Escape)
      {
        if (e.KeyCode == Keys.Enter)
          _grid.EndEdit();
        else
          _grid.CancelEdit();
        e.Handled = true;
        e.SuppressKeyPress = true;
      }
    };

    // When the embedded TextBox editing control is shown, attach our handler to it
    // so Enter/Esc in the TextBox commit/cancel the edit WITHOUT closing the dialog.
    _grid.EditingControlShowing += (s, e) =>
    {
      e.Control.PreviewKeyDown -= OnEditControlPreviewKeyDown;
      e.Control.PreviewKeyDown += OnEditControlPreviewKeyDown;
      e.Control.KeyDown -= OnEditingControlKeyDown;
      e.Control.KeyDown += OnEditingControlKeyDown;
    };

    // Double-click on any non-row area (empty space below rows, or column header area
    // while Rows.Count == 0) → add a new entry.
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

  // ---- Key handlers for the embedded editing control ----

  private void OnEditControlPreviewKeyDown(object? sender, PreviewKeyDownEventArgs e)
  {
    // Mark Enter and Esc as regular input keys so they don't bubble up
    // as dialog keys (which would trigger the OK / Cancel buttons).
    if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Escape)
      e.IsInputKey = true;
  }

  private void OnEditingControlKeyDown(object? sender, KeyEventArgs e)
  {
    switch (e.KeyCode)
    {
      case Keys.Enter:
        _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        _grid.EndEdit();
        e.Handled = true;
        e.SuppressKeyPress = true;
        break;

      case Keys.Escape:
        _grid.CancelEdit();
        _grid.EndEdit();
        e.Handled = true;
        e.SuppressKeyPress = true;
        break;
    }
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
    if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path)) return path;
    var dir = GetTemplateDir();
    if (!string.IsNullOrEmpty(dir))
    {
      var candidate = Path.Combine(dir, path);
      if (File.Exists(candidate)) return candidate;
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

  private void OnAdd(object? sender, EventArgs e)
  {
    var idx = _grid.Rows.Add(string.Empty, string.Empty);
    _grid.CurrentCell = _grid.Rows[idx].Cells["Extension"];
    // EditOnEnter handles entering edit mode automatically
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
      row.Cells["TemplatePath"].Value = dlg.FileName;
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
