using Rhino.UI;
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace VFileTypeTemplate;

// ---------------------------------------------------------------------------
// Rhino Options page — shown under Tools > Options > vFileTypeTemplate
// ---------------------------------------------------------------------------

/// <summary>
/// Rhino Options page for configuring vFileTypeTemplate extension-to-template mappings.
/// Registered automatically via <see cref="VFileTypeTemplatePlugIn.OptionsPages"/>.
/// </summary>
public sealed class VFileTypeTemplateOptionsPage : OptionsDialogPage
{
  private VFileTypeTemplateOptionsControl? _control;

  public VFileTypeTemplateOptionsPage() : base("vFileTypeTemplate") { }

  public override string LocalPageTitle => "vFileTypeTemplate";

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
    Padding = new Padding(8);
    AutoScroll = true;

    // ---- Enabled checkbox ----
    _enabledCheck = new CheckBox
    {
      Text = "Enabled (automatically apply template when a mapped file type is opened)",
      AutoSize = true,
      Location = new Point(8, 8),
    };

    // ---- Grid ----
    _grid = new DataGridView
    {
      Location = new Point(8, 36),
      Size = new Size(620, 240),
      AllowUserToAddRows = false,
      AllowUserToDeleteRows = false,
      RowHeadersVisible = false,
      SelectionMode = DataGridViewSelectionMode.FullRowSelect,
      MultiSelect = false,
      AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
      Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };

    var extCol = new DataGridViewTextBoxColumn
    {
      HeaderText = "File Extension",
      Name = "Extension",
      FillWeight = 20,
      MinimumWidth = 80,
    };
    var tplCol = new DataGridViewTextBoxColumn
    {
      HeaderText = "Template File (.3dm)  — leave blank to use Rhino default",
      Name = "TemplatePath",
      FillWeight = 80,
      MinimumWidth = 200,
    };
    _grid.Columns.Add(extCol);
    _grid.Columns.Add(tplCol);

    // ---- Buttons ----
    _addBtn = new Button
    {
      Text = "Add",
      Size = new Size(80, 26),
      Location = new Point(8, 284),
    };
    _addBtn.Click += OnAdd;

    _removeBtn = new Button
    {
      Text = "Remove",
      Size = new Size(80, 26),
      Location = new Point(96, 284),
    };
    _removeBtn.Click += OnRemove;

    _browseBtn = new Button
    {
      Text = "Browse...",
      Size = new Size(88, 26),
      Location = new Point(184, 284),
    };
    _browseBtn.Click += OnBrowse;

    // ---- Info label ----
    var infoLabel = new Label
    {
      Text = "Add one entry per file type. Extension must start with a dot (e.g. .dxf).\n" +
             "Leave template path empty to apply Rhino's current default template.",
      AutoSize = true,
      Location = new Point(8, 320),
      ForeColor = SystemColors.GrayText,
    };

    Controls.AddRange(new Control[]
    {
      _enabledCheck, _grid, _addBtn, _removeBtn, _browseBtn, infoLabel
    });

    ReloadConfig();
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
    var config = new VFileTypeTemplateConfig
    {
      Enabled = _enabledCheck.Checked,
    };

    foreach (DataGridViewRow row in _grid.Rows)
    {
      if (row.IsNewRow) continue;
      var ext = row.Cells["Extension"].Value?.ToString()?.Trim() ?? string.Empty;
      var path = row.Cells["TemplatePath"].Value?.ToString()?.Trim() ?? string.Empty;

      if (string.IsNullOrEmpty(ext)) continue;

      // Normalise extension
      if (!ext.StartsWith(".", StringComparison.Ordinal))
        ext = "." + ext;

      config.Mappings.Add(new FileTypeMapping { Extension = ext.ToLowerInvariant(), TemplatePath = path });
    }

    config.Save();
    VFileTypeTemplatePlugIn.TryLog($"Options saved: Enabled={config.Enabled} Mappings={config.Mappings.Count}");
  }

  // ---- Button handlers ----

  private void OnAdd(object? sender, EventArgs e)
  {
    var rowIdx = _grid.Rows.Add(".ext", string.Empty);
    _grid.CurrentCell = _grid.Rows[rowIdx].Cells["Extension"];
    _grid.BeginEdit(true);
  }

  private void OnRemove(object? sender, EventArgs e)
  {
    if (_grid.SelectedRows.Count == 0) return;
    foreach (DataGridViewRow row in _grid.SelectedRows)
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

    var existingPath = row.Cells["TemplatePath"].Value?.ToString() ?? string.Empty;
    if (!string.IsNullOrEmpty(existingPath) && File.Exists(existingPath))
      dlg.InitialDirectory = Path.GetDirectoryName(existingPath);

    if (dlg.ShowDialog(this.FindForm()) == DialogResult.OK)
      row.Cells["TemplatePath"].Value = dlg.FileName;
  }
}
