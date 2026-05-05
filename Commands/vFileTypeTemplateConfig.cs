using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;
using System.IO;
using System.Linq;

namespace VFileTypeTemplate.Commands;

/// <summary>
/// Interactive command to configure vFileTypeTemplate settings.
/// Use the Rhino Options dialog (Tools > Options > vFileTypeTemplate) for
/// a full graphical editor.
/// </summary>
public sealed class vFileTypeTemplateConfig : Command
{
  public override string EnglishName => "vFileTypeTemplateConfig";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var config = VFileTypeTemplateConfig.Load();
    var enabledToggle = new OptionToggle(config.Enabled, "No", "Yes");

    var go = new GetOption();
    go.AcceptNothing(true);

    while (true)
    {
      go.ClearCommandOptions();

      go.SetCommandPrompt($"vFileTypeTemplate  Mappings={config.Mappings.Count}");
      go.AddOptionToggle("Enabled", ref enabledToggle);
      go.AddOption("ListMappings");
      go.AddOption("AddMapping");
      if (config.Mappings.Count > 0)
      {
        go.AddOption("EditMapping");
        go.AddOption("RemoveMapping");
      }
      go.AddOption("ApplyNow");

      var res = go.Get();
      if (res == GetResult.Nothing || res == GetResult.Cancel)
        break;
      if (res != GetResult.Option)
        break;

      config.Enabled = enabledToggle.CurrentValue;

      var opt = go.Option();
      if (opt == null)
        break;

      switch (opt.EnglishName)
      {
        case "ListMappings":
          if (config.Mappings.Count == 0)
            RhinoApp.WriteLine("vFileTypeTemplate: no mappings configured.");
          else
            for (int i = 0; i < config.Mappings.Count; i++)
            {
              var m = config.Mappings[i];
              var tpl = string.IsNullOrWhiteSpace(m.TemplatePath) ? "<Rhino default>" : m.TemplatePath;
              RhinoApp.WriteLine($"  [{i}] {m.Extension}  \u2192  {tpl}");
            }
          break;

        case "AddMapping":
          AddMappingInteractive(config);
          break;

        case "EditMapping":
          EditMappingInteractive(config);
          break;

        case "RemoveMapping":
          RemoveMappingInteractive(config);
          break;

        case "ApplyNow":
          ApplyForCurrentDoc(doc, config);
          break;
      }

      config.Save();
    }

    config.Enabled = enabledToggle.CurrentValue;
    config.Save();
    return Result.Success;
  }

  // ---- Add ----

  private static void AddMappingInteractive(VFileTypeTemplateConfig config)
  {
    var gsExt = new GetString();
    gsExt.SetCommandPrompt("File extension to map (e.g. .dxf)");
    gsExt.AcceptNothing(false);
    if (gsExt.Get() != GetResult.String) return;

    var ext = NormaliseExtension(gsExt.StringResult() ?? string.Empty);
    if (string.IsNullOrEmpty(ext))
    {
      RhinoApp.WriteLine("vFileTypeTemplate: invalid extension.");
      return;
    }

    if (config.Mappings.Any(m => string.Equals(m.Extension, ext, System.StringComparison.OrdinalIgnoreCase)))
    {
      RhinoApp.WriteLine($"vFileTypeTemplate: a mapping for {ext} already exists. Use EditMapping to change it.");
      return;
    }

    var templatePath = PromptTemplatePath();
    config.Mappings.Add(new FileTypeMapping { Extension = ext, TemplatePath = templatePath });
    var display = string.IsNullOrWhiteSpace(templatePath) ? "<Rhino default>" : templatePath;
    RhinoApp.WriteLine($"vFileTypeTemplate: added {ext}  \u2192  {display}");
  }

  // ---- Edit ----

  private static void EditMappingInteractive(VFileTypeTemplateConfig config)
  {
    if (config.Mappings.Count == 0) return;
    for (int i = 0; i < config.Mappings.Count; i++)
      RhinoApp.WriteLine($"  [{i}] {config.Mappings[i].Extension}");

    var gsIdx = new GetString();
    gsIdx.SetCommandPrompt("Mapping number to edit");
    if (gsIdx.Get() != GetResult.String) return;
    if (!int.TryParse(gsIdx.StringResult(), out int idx) || idx < 0 || idx >= config.Mappings.Count)
    {
      RhinoApp.WriteLine("vFileTypeTemplate: invalid index.");
      return;
    }

    var templatePath = PromptTemplatePath(config.Mappings[idx].TemplatePath);
    config.Mappings[idx].TemplatePath = templatePath;
    var display = string.IsNullOrWhiteSpace(templatePath) ? "<Rhino default>" : templatePath;
    RhinoApp.WriteLine($"vFileTypeTemplate: updated [{idx}] {config.Mappings[idx].Extension}  \u2192  {display}");
  }

  // ---- Remove ----

  private static void RemoveMappingInteractive(VFileTypeTemplateConfig config)
  {
    if (config.Mappings.Count == 0) return;
    for (int i = 0; i < config.Mappings.Count; i++)
      RhinoApp.WriteLine($"  [{i}] {config.Mappings[i].Extension}");

    var gsIdx = new GetString();
    gsIdx.SetCommandPrompt("Mapping number to remove");
    if (gsIdx.Get() != GetResult.String) return;
    if (!int.TryParse(gsIdx.StringResult(), out int idx) || idx < 0 || idx >= config.Mappings.Count)
    {
      RhinoApp.WriteLine("vFileTypeTemplate: invalid index.");
      return;
    }

    var removed = config.Mappings[idx].Extension;
    config.Mappings.RemoveAt(idx);
    RhinoApp.WriteLine($"vFileTypeTemplate: removed mapping for {removed}.");
  }

  // ---- Apply ----

  private static void ApplyForCurrentDoc(RhinoDoc doc, VFileTypeTemplateConfig config)
  {
    var ext = Path.GetExtension(doc.Path ?? string.Empty);
    var templatePath = string.IsNullOrEmpty(ext)
      ? string.Empty
      : VFileTypeTemplatePlugIn.ResolveTemplatePath(config.FindTemplatePath(ext) ?? string.Empty);

    if (string.IsNullOrEmpty(templatePath) && config.Mappings.Count > 0)
      templatePath = VFileTypeTemplatePlugIn.ResolveTemplatePath(config.Mappings[0].TemplatePath);

    if (string.IsNullOrEmpty(templatePath))
    {
      RhinoApp.WriteLine("vFileTypeTemplate: no template resolved; cannot apply.");
      return;
    }

    VFileTypeTemplatePlugIn.ApplyTemplate(doc, templatePath, doc.Path ?? string.Empty);
    RhinoApp.WriteLine($"vFileTypeTemplate: template applied from '{templatePath}'.");
  }

  // ---- Helpers ----

  private static string PromptTemplatePath(string current = "")
  {
    var gs = new GetString();
    gs.SetCommandPrompt("Template .3dm file path (Enter to use Rhino default)");
    if (!string.IsNullOrEmpty(current))
      gs.SetDefaultString(current);
    gs.AcceptNothing(true);

    var res = gs.Get();
    if (res == GetResult.Nothing)
      return string.Empty;
    if (res != GetResult.String)
      return current;

    var path = (gs.StringResult() ?? string.Empty).Trim().Trim('"');
    if (string.IsNullOrWhiteSpace(path))
      return string.Empty;

    if (!File.Exists(path))
    {
      RhinoApp.WriteLine($"vFileTypeTemplate: file not found: {path}");
      return current;
    }

    return path;
  }

  private static string NormaliseExtension(string ext)
  {
    ext = ext.Trim().ToLowerInvariant();
    if (!ext.StartsWith("."))
      ext = "." + ext;
    return ext.Length > 1 ? ext : string.Empty;
  }
}

/// <summary>
/// Applies the configured template for the current document's file type on demand.
/// </summary>
public sealed class vFileTypeTemplateApply : Command
{
  public override string EnglishName => "vFileTypeTemplateApply";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var config = VFileTypeTemplateConfig.Load();

    if (!config.Enabled)
    {
      RhinoApp.WriteLine("vFileTypeTemplate is disabled. Enable it first with vFileTypeTemplateConfig.");
      return Result.Cancel;
    }

    var ext = Path.GetExtension(doc.Path ?? string.Empty);
    string? configuredPath = string.IsNullOrEmpty(ext) ? null : config.FindTemplatePath(ext);

    if (configuredPath == null && config.Mappings.Count > 0)
      configuredPath = config.Mappings[0].TemplatePath;

    var templatePath = VFileTypeTemplatePlugIn.ResolveTemplatePath(configuredPath ?? string.Empty);
    if (string.IsNullOrEmpty(templatePath))
    {
      RhinoApp.WriteLine("vFileTypeTemplate: no template resolved. Configure mappings with vFileTypeTemplateConfig.");
      return Result.Cancel;
    }

    VFileTypeTemplatePlugIn.ApplyTemplate(doc, templatePath, doc.Path ?? string.Empty);
    RhinoApp.WriteLine($"vFileTypeTemplate: template applied from '{templatePath}'.");
    return Result.Success;
  }
}

