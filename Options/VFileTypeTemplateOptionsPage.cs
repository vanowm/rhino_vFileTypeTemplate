using Rhino.UI;
using System;
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
      // Fix background color
      BackgroundColor = SystemColors.Window,
      GridColor = SystemColors.ControlLight,
      BorderStyle = BorderStyle.FixedSingle,
      EnableHeadersVisualStyles = true,
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

    // Intercept Enter so it commits the cell edit instead of closing the dialog
    _grid.KeyDown += (s, e) =>
    {
      if (e.KeyCode == Keys.Enter && _grid.IsCurrentCellInEditMode)
      {
        _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        _grid.EndEdit();
        e.Handled = true;
        e.SuppressKeyPress = true;
      }
    };

    // ---- Buttons ----
    _addBtn    = MakeButton("Add",      OnAdd);
    _removeBtn = MakeButton("Remove",   OnRemove);
    _browseBtn = MakeButton("Browse…",  OnBrowse);

    var buttonPanel = new FlowLayoutPanel
    {
      AutoSize = true,
      Dock = DockStyle.Fill,
      FlowDirection = FlowDirection.LeftToRight,
      Margin = new Padding(0, 0, 0, 4),
    };
    buttonPanel.Controls.AddRange(new Control[] { _addBtn, _removeBtn, _browseBtn });

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

  private static Button MakeButton(string text, EventHandler handler)
  {
    var btn = new Button { Text = text, Size = new Size(86, 26), Margin = new Padding(0, 0, 6, 0) };
    btn.Click += handler;
    return btn;
  }

  // ---- Load / Save ----

  public void ReloadConfig()
  {
    var config = VFileTypeTemplateConfig.Load();
    _enabledCheck.Checked = config.Enabled;
    _grid.Rows.Clear();
    foreach (var m in config.Mappings)
      _grid.Rows.Add(m.Extension, m.TemplatePath);
  }

  public void SaveConfig()
  {
    // Commit any cell that is still being edited
    _grid.EndEdit();

    var config = new VFileTypeTemplateConfig { Enabled = _enabledCheck.Checked };

    foreach (DataGridViewRow row in _grid.Rows)
    {
      if (row.IsNewRow) continue;
      var rawExt  = row.Cells["Extension"].Value?.ToString()?.Trim() ?? string.Empty;
      var path    = row.Cells["TemplatePath"].Value?.ToString()?.Trim() ?? string.Empty;
      if (string.IsNullOrEmpty(rawExt)) continue;

      // Normalise: split, add leading dot, lowercase, deduplicate, rejoin with ", "
      var parts = rawExt
        .Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Trim().ToLowerInvariant())
        .Where(p => !string.IsNullOrEmpty(p))
        .Select(p => p.StartsWith(".") ? p : "." + p)
        .Distinct()
        .ToList();
      if (parts.Count == 0) continue;

      config.Mappings.Add(new FileTypeMapping
      {
        Extension    = string.Join(", ", parts),
        TemplatePath = path,
      });
    }

    config.Save();
    VFileTypeTemplatePlugIn.TryLog($"Options saved: Enabled={config.Enabled} Mappings={config.Mappings.Count}");
  }

  // ---- Button handlers ----

  private void OnAdd(object? sender, EventArgs e)
  {
    var idx = _grid.Rows.Add(".ext", string.Empty);
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

    var existing = row.Cells["TemplatePath"].Value?.ToString() ?? string.Empty;
    if (!string.IsNullOrEmpty(existing) && File.Exists(existing))
      dlg.InitialDirectory = Path.GetDirectoryName(existing);

    if (dlg.ShowDialog(this.FindForm()) == DialogResult.OK)
      row.Cells["TemplatePath"].Value = dlg.FileName;
  }
}

