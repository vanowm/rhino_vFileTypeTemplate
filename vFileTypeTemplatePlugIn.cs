using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.PlugIns;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VFileTypeTemplate;

[System.Runtime.InteropServices.Guid("b7e4d21a-3c8f-4b9e-92d1-5f8a6c0e3d47")]
public sealed class VFileTypeTemplatePlugIn : PlugIn
{
  public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;
  protected override string LocalPlugInName => "vFileTypeTemplate";

  public VFileTypeTemplatePlugIn()
  {
    Instance = this;
  }

  public static VFileTypeTemplatePlugIn Instance { get; private set; } = null!;

  protected override void OptionsDialogPages(System.Collections.Generic.List<Rhino.UI.OptionsDialogPage> pages)
  {
    pages.Add(new VFileTypeTemplateOptionsPage());
  }

  protected override LoadReturnCode OnLoad(ref string errorMessage)
  {
    RhinoDoc.EndOpenDocument += OnEndOpenDocument;
    var asm = GetType().Assembly;
    var version = (!string.IsNullOrEmpty(asm.Location)
      ? System.Diagnostics.FileVersionInfo.GetVersionInfo(asm.Location).FileVersion
      : null) ?? asm.GetName().Version?.ToString() ?? "unknown";
    Log.Initialize();
    Log.Write($"startup  rhino={RhinoApp.Version}  version={version}  dll={asm.Location}");
    RhinoApp.WriteLine($"vFileTypeTemplate v{version} loaded.");
    return LoadReturnCode.Success;
  }

  protected override void OnShutdown()
  {
    RhinoDoc.EndOpenDocument -= OnEndOpenDocument;
  }

  // -----------------------------------------------------------------------
  // Event handler
  // -----------------------------------------------------------------------

  private static void OnEndOpenDocument(object? sender, DocumentOpenEventArgs e)
  {
    // Only handle plain opens (not Import/Merge and not Reference files).
    if (e.Merge || e.Reference)
      return;

    var fileName = e.FileName ?? string.Empty;
    var ext = Path.GetExtension(fileName);
    if (string.IsNullOrEmpty(ext))
      return;

    var doc = e.Document;
    if (doc == null)
      return;

    var config = VFileTypeTemplateConfig.Load();
    if (!config.Enabled)
      return;

    var mapping = config.Mappings.FirstOrDefault(m =>
      VFileTypeTemplateConfig.SplitExtensions(m.Extension)
        .Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)));
    if (mapping == null)
      return;

    var templatePath = ResolveTemplatePath(mapping.TemplatePath);
    if (string.IsNullOrEmpty(templatePath))
    {
      Log.Write($"No template resolved for extension {ext}; skipping.");
      return;
    }

    Log.Write($"Applying template '{templatePath}' to '{fileName}'");
    RhinoApp.WriteLine($"vFileTypeTemplate: detected {Path.GetFileName(fileName)} — queuing template '{Path.GetFileName(templatePath)}'");

    // Defer apply to Rhino's next idle tick so the open-document command has
    // fully completed. This lets BeginUndoRecord work correctly and makes
    // the whole template apply undoable with Ctrl+Z.
    var capturedDoc      = doc;
    var capturedTemplate = templatePath;
    var capturedSourcePath  = fileName;
    RhinoApp.Idle += OnIdleApplyTemplate;

    void OnIdleApplyTemplate(object? s, EventArgs args)
    {
      RhinoApp.Idle -= OnIdleApplyTemplate;
      if (!capturedDoc.IsAvailable) return;
      ApplyTemplate(capturedDoc, capturedTemplate, capturedSourcePath);
    }
  }

  // -----------------------------------------------------------------------
  // Template resolution
  // -----------------------------------------------------------------------

  /// <summary>
  /// Returns the template path to use: the configured path if it exists, otherwise
  /// the Rhino default template, otherwise empty string.
  /// </summary>
  internal static string ResolveTemplatePath(string configuredPath)
  {
    if (!string.IsNullOrWhiteSpace(configuredPath))
    {
      if (File.Exists(configuredPath))
        return configuredPath;

      // Bare filename? Try resolving within the Rhino template directory.
      if (!Path.IsPathRooted(configuredPath))
      {
        try
        {
          var tplDir = Path.GetDirectoryName(Rhino.ApplicationSettings.FileSettings.TemplateFile);
          if (!string.IsNullOrEmpty(tplDir))
          {
            var candidate = Path.Combine(tplDir, configuredPath);
            if (File.Exists(candidate)) return candidate;
            // Try appending .3dm when no extension was given
            if (string.IsNullOrEmpty(Path.GetExtension(configuredPath)))
            {
              candidate = Path.Combine(tplDir, configuredPath + ".3dm");
              if (File.Exists(candidate)) return candidate;
            }
          }
        }
        catch { }
      }

      // Configured path specified but file not found — skip entirely; do not fall back.
      Log.Write($"ResolveTemplatePath: configured template not found '{configuredPath}'; skipping.");
      return string.Empty;
    }

    // Empty/blank configured path → use Rhino default template.
    try
    {
      var rhinoDefault = Rhino.ApplicationSettings.FileSettings.TemplateFile;
      if (!string.IsNullOrWhiteSpace(rhinoDefault) && File.Exists(rhinoDefault))
        return rhinoDefault;
    }
    catch { }

    return string.Empty;
  }

  // -----------------------------------------------------------------------
  // Template application
  // -----------------------------------------------------------------------

  /// <summary>
  /// Reads the template file and applies all settings to the document.
  /// dxfPath is the path to the DXF file being opened; used to read DXF-encoded
  /// properties (e.g. $INSUNITS) that take priority over the template.
  /// </summary>
  internal static void ApplyTemplate(RhinoDoc doc, string templatePath, string sourceFilePath = "")
  {
    // Read $INSUNITS from the DXF header — only applicable for .dxf files.
    bool isDxfFile = !string.IsNullOrEmpty(sourceFilePath) &&
      Path.GetExtension(sourceFilePath).Equals(".dxf", StringComparison.OrdinalIgnoreCase);
    int dxfInsUnitsCode = isDxfFile ? ReadDxfInsUnits(sourceFilePath) : 0;
    var dxfModelUnits = DxfInsUnitsToRhino(dxfInsUnitsCode);
    Log.Write($"ApplyTemplate: source file units — $INSUNITS={dxfInsUnitsCode} → {dxfModelUnits}");

    // Snapshot doc properties before applying so the user can undo everything.
    var beforeSnapshot = TakeDocSnapshot(doc);

    // Open an undo record — table additions (layers, linetypes, etc.) are captured automatically.
uint undoSn = doc.BeginUndoRecord("Apply file type template");

    File3dm? templateFile = null;
    RhinoDoc? headlessDoc = null;
    try
    {
      // Phase 1: Apply tables and static settings from File3dm.
      templateFile = File3dm.Read(templatePath);
      if (templateFile == null)
      {
        Log.Write($"Failed to read template: {templatePath}");
        return;
      }

      ApplyDocumentSettings(doc, templateFile);
      ApplyNotes(doc, templateFile);
      ApplyLocation(doc, templateFile);
      ApplyDocumentStrings(doc, templateFile);
      ApplyLayers(doc, templateFile);
      ApplyLinetypes(doc, templateFile);
      ApplyHatchPatterns(doc, templateFile);
      ApplyDimStyles(doc, templateFile);
      ApplyMaterials(doc, templateFile);
      ApplyNamedViews(doc, templateFile);
      ApplyNamedCPlanes(doc, templateFile);

      // Phase 2: Apply runtime-only settings via a headless RhinoDoc.
      headlessDoc = RhinoDoc.CreateHeadless(templatePath);
      if (headlessDoc == null)
        Log.Write("CreateHeadless returned null — runtime settings not applied.");
      else
        ApplyRuntimeSettings(doc, headlessDoc);

      // Restore DXF unit system — DXF $INSUNITS takes priority over the template.
      RestoreDxfUnitSystem(doc, dxfModelUnits);

      // Register undo/redo callback for doc properties (not auto-captured by undo record).
      if (undoSn > 0)
      {
        var afterSnapshot = TakeDocSnapshot(doc);
        doc.AddCustomUndoEvent("Apply file type template", OnDocSettingsUndoRedo,
                               new[] { beforeSnapshot, afterSnapshot });
      }
    }
    catch (Exception ex)
    {
      Log.Write($"Error applying template: {ex.Message}");
    }
    finally
    {
      templateFile?.Dispose();
      headlessDoc?.Dispose();
      if (undoSn > 0)
        doc.EndUndoRecord(undoSn);
    }

    doc.Views.Redraw();
    Log.Write($"Template applied successfully from: {templatePath}");
    RhinoApp.WriteLine($"vFileTypeTemplate: template '{Path.GetFileName(templatePath)}' applied to {Path.GetFileName(sourceFilePath)}.");
  }

  // -----------------------------------------------------------------------
  // DXF header parsing
  // -----------------------------------------------------------------------

  /// <summary>
  /// Parses the DXF file header and returns the integer value of $INSUNITS.
  /// Returns 0 (unspecified) if not found, file is binary DXF, or any error.
  /// </summary>
  private static int ReadDxfInsUnits(string sourceFilePath)
  {
    try
    {
      // Binary DXF starts with "AutoCAD Binary DXF" — not parseable as text.
      var sig = new byte[18];
      using (var fs = File.OpenRead(sourceFilePath))
        fs.ReadExactly(sig);
      if (System.Text.Encoding.ASCII.GetString(sig).StartsWith("AutoCAD Binary DXF"))
      {
        Log.Write("ReadDxfInsUnits: binary DXF — $INSUNITS not parsed, template units apply.");
        return 0;
      }
    }
    catch { return 0; }

    try
    {
      // Read $INSUNITS from the DXF file — only applicable if sourceFilePath is a .dxf file.
      using var reader = new StreamReader(sourceFilePath, System.Text.Encoding.Latin1);
      bool inHeader = false;
      bool sawInsUnits = false;

      while (true)
      {
        var codeLine = reader.ReadLine();
        if (codeLine == null) break;
        var valueLine = reader.ReadLine();
        if (valueLine == null) break;

        codeLine = codeLine.Trim();
        valueLine = valueLine.Trim();

        if (!int.TryParse(codeLine, out int groupCode)) continue;

        if (!inHeader)
        {
          // HEADER section starts when group code 2 = "HEADER" follows code 0.
          if (groupCode == 2 && valueLine.Equals("HEADER", StringComparison.OrdinalIgnoreCase))
            inHeader = true;
          continue;
        }

        // End of HEADER section.
        if (groupCode == 0 && valueLine.Equals("ENDSEC", StringComparison.OrdinalIgnoreCase))
          break;

        if (sawInsUnits && groupCode == 70)
        {
          if (int.TryParse(valueLine, out int insUnits))
            return insUnits;
          sawInsUnits = false;
        }
        else if (groupCode == 9 && valueLine.Equals("$INSUNITS", StringComparison.OrdinalIgnoreCase))
        {
          sawInsUnits = true;
        }
        else
        {
          sawInsUnits = false;
        }
      }
    }
    catch { }
    return 0;
  }

  /// <summary>Maps the DXF $INSUNITS integer to the RhinoCommon UnitSystem.</summary>
  private static Rhino.UnitSystem DxfInsUnitsToRhino(int insUnits) => insUnits switch
  {
    1  => Rhino.UnitSystem.Inches,
    2  => Rhino.UnitSystem.Feet,
    3  => Rhino.UnitSystem.Miles,
    4  => Rhino.UnitSystem.Millimeters,
    5  => Rhino.UnitSystem.Centimeters,
    6  => Rhino.UnitSystem.Meters,
    7  => Rhino.UnitSystem.Kilometers,
    8  => Rhino.UnitSystem.Microinches,
    9  => Rhino.UnitSystem.Mils,
    10 => Rhino.UnitSystem.Yards,
    _  => Rhino.UnitSystem.None,  // 0 = unspecified, or unknown code → use template units
  };

  /// <summary>
  /// Restores the DXF unit system after template application.
  /// If the template set a different unit system, scales ModelAbsoluteTolerance
  /// so it remains meaningful in the DXF's unit context.
  /// If dxfModelUnits is None (unspecified in DXF), template units are kept.
  /// </summary>
  private static void RestoreDxfUnitSystem(RhinoDoc doc, Rhino.UnitSystem dxfModelUnits)
  {
    try
    {
      if (dxfModelUnits == Rhino.UnitSystem.None)
      {
        Log.Write($"RestoreDxfUnits: DXF had no explicit units ($INSUNITS=0) — template units kept ({doc.ModelUnitSystem}).");
        return;
      }

      var templateUnits = doc.ModelUnitSystem;
      if (templateUnits == dxfModelUnits)
      {
        Log.Write($"RestoreDxfUnits: DXF and template units match ({dxfModelUnits}) — no change.");
        return;
      }

      // Scale model absolute tolerance to the DXF's unit system.
      double scale = RhinoMath.UnitScale(templateUnits, dxfModelUnits);
      var scaledTol = doc.ModelAbsoluteTolerance * scale;
      doc.ModelAbsoluteTolerance = scaledTol;
      doc.ModelUnitSystem = dxfModelUnits;
      Log.Write($"RestoreDxfUnits: {templateUnits}→{dxfModelUnits} scale={scale:G6}, ModelAbsTol={scaledTol:G6}, ModelUnits={doc.ModelUnitSystem}");
    }
    catch (Exception ex) { Log.Write($"RestoreDxfUnitSystem failed: {ex.Message}"); }
  }

  private static void ApplyDocumentSettings(RhinoDoc doc, File3dm templateFile)
  {
    try
    {
      var s = templateFile.Settings;
      Log.Write($"ApplyDocumentSettings: template ModelUnits={s.ModelUnitSystem} AbsTol={s.ModelAbsoluteTolerance} ModelRelTol={s.ModelRelativeTolerance} AngleTol={s.ModelAngleToleranceDegrees} PageUnits={s.PageUnitSystem} PageAbsTol={s.PageAbsoluteTolerance} PageRelTol={s.PageRelativeTolerance} PageAngleTol={s.PageAngleToleranceDegrees}");
      Log.Write($"ApplyDocumentSettings: doc BEFORE ModelUnits={doc.ModelUnitSystem} AbsTol={doc.ModelAbsoluteTolerance} RelTol={doc.ModelRelativeTolerance} AngleTol={doc.ModelAngleToleranceDegrees}");

      doc.ModelUnitSystem = s.ModelUnitSystem;
      doc.ModelAbsoluteTolerance = s.ModelAbsoluteTolerance;
      doc.ModelRelativeTolerance = s.ModelRelativeTolerance;
      doc.ModelAngleToleranceDegrees = s.ModelAngleToleranceDegrees;
      doc.PageUnitSystem = s.PageUnitSystem;
      doc.PageAbsoluteTolerance = s.PageAbsoluteTolerance;
      doc.PageRelativeTolerance = s.PageRelativeTolerance;
      doc.PageAngleToleranceDegrees = s.PageAngleToleranceDegrees;

      Log.Write($"ApplyDocumentSettings: doc AFTER  ModelUnits={doc.ModelUnitSystem} AbsTol={doc.ModelAbsoluteTolerance} RelTol={doc.ModelRelativeTolerance} AngleTol={doc.ModelAngleToleranceDegrees}");

      // NOTE: ModelDistanceDisplayPrecision, display format, and mesh quality are handled
      // in Phase 2 via a headless RhinoDoc (not accessible from File3dm).
    }
    catch (Exception ex)
    {
      Log.Write($"ApplyDocumentSettings failed: {ex.Message}\n{ex.StackTrace}");
    }
  }

  private static void ApplyRuntimeSettings(RhinoDoc doc, RhinoDoc templateDoc)
  {
    // Display precision
    try
    {
      Log.Write($"ApplyRuntimeSettings: template ModelDistPrec={templateDoc.ModelDistanceDisplayPrecision} PageDistPrec={templateDoc.PageDistanceDisplayPrecision}");
      doc.ModelDistanceDisplayPrecision = templateDoc.ModelDistanceDisplayPrecision;
      doc.PageDistanceDisplayPrecision = templateDoc.PageDistanceDisplayPrecision;
      Log.Write($"ApplyRuntimeSettings: doc AFTER ModelDistPrec={doc.ModelDistanceDisplayPrecision} PageDistPrec={doc.PageDistanceDisplayPrecision}");
    }
    catch (Exception ex) { Log.Write($"ApplyRuntimeSettings display precision failed: {ex.Message}"); }

    // Display format (Decimal / Feet / FeetAndInches) — accessed via internal API reflection.
    try
    {
      int modelMode = GetDistanceDisplayMode(templateDoc, false);
      int pageMode  = GetDistanceDisplayMode(templateDoc, true);
      SetDistanceDisplayMode(doc, modelMode, false);
      SetDistanceDisplayMode(doc, pageMode,  true);
      Log.Write($"ApplyRuntimeSettings: DistanceDisplayMode model={modelMode} page={pageMode}");
    }
    catch (Exception ex) { Log.Write($"ApplyRuntimeSettings display format failed: {ex.Message}"); }

    // Mesh quality
    try
    {
      var style = templateDoc.MeshingParameterStyle;
      Log.Write($"ApplyRuntimeSettings: MeshingStyle={style}");
      doc.MeshingParameterStyle = style;
      if (style == MeshingParameterStyle.Custom)
        doc.SetCustomMeshingParameters(templateDoc.GetCurrentMeshingParameters());
    }
    catch (Exception ex) { Log.Write($"ApplyRuntimeSettings meshing failed: {ex.Message}"); }

    // Annotation and hatch scaling
    try
    {
      doc.ModelSpaceAnnotationScalingEnabled = templateDoc.ModelSpaceAnnotationScalingEnabled;
      doc.ModelSpaceTextScale = templateDoc.ModelSpaceTextScale;
      doc.ModelSpaceHatchScalingEnabled = templateDoc.ModelSpaceHatchScalingEnabled;
      doc.ModelSpaceHatchScale = templateDoc.ModelSpaceHatchScale;
      doc.LayoutSpaceAnnotationScalingEnabled = templateDoc.LayoutSpaceAnnotationScalingEnabled;
      Log.Write($"ApplyRuntimeSettings: AnnotScaling={doc.ModelSpaceAnnotationScalingEnabled} TextScale={doc.ModelSpaceTextScale} HatchScaling={doc.ModelSpaceHatchScalingEnabled}");
    }
    catch (Exception ex) { Log.Write($"ApplyRuntimeSettings annotation/hatch scaling failed: {ex.Message}"); }

    // SubD appearance
    try
    {
      doc.SubDAppearance = templateDoc.SubDAppearance;
      Log.Write($"ApplyRuntimeSettings: SubDAppearance={doc.SubDAppearance}");
    }
    catch (Exception ex) { Log.Write($"ApplyRuntimeSettings SubD failed: {ex.Message}"); }

    // Render settings
    try
    {
      doc.RenderSettings = templateDoc.RenderSettings;
      Log.Write("ApplyRuntimeSettings: RenderSettings applied.");
    }
    catch (Exception ex) { Log.Write($"ApplyRuntimeSettings RenderSettings failed: {ex.Message}"); }

    // Ground plane
    try
    {
#pragma warning disable CS0612
      var srcGp = templateDoc.GroundPlane;
      var dstGp = doc.GroundPlane;
#pragma warning restore CS0612
      dstGp.BeginChange(Rhino.Render.RenderContent.ChangeContexts.Program);
      dstGp.Enabled = srcGp.Enabled;
      dstGp.Altitude = srcGp.Altitude;
      dstGp.AutoAltitude = srcGp.AutoAltitude;
      dstGp.ShadowOnly = srcGp.ShadowOnly;
      dstGp.EndChange();
      Log.Write($"ApplyRuntimeSettings: GroundPlane Enabled={srcGp.Enabled} Altitude={srcGp.Altitude} AutoAlt={srcGp.AutoAltitude}");
    }
    catch (Exception ex) { Log.Write($"ApplyRuntimeSettings GroundPlane failed: {ex.Message}"); }

    // Grid — read from headless doc viewports (they carry the template's CPlane grid data)
    try
    {
      var templateViews = templateDoc.Views.GetStandardRhinoViews();
      Log.Write($"ApplyRuntimeSettings: headless doc has {templateViews.Length} views");

      ConstructionPlane? srcGrid = null;
      foreach (var tv in templateViews)
      {
        var cp = tv.ActiveViewport.GetConstructionPlane();
        if (srcGrid == null) srcGrid = cp;
        if (tv.ActiveViewport.Name != null &&
            tv.ActiveViewport.Name.IndexOf("Top", StringComparison.OrdinalIgnoreCase) >= 0)
        { srcGrid = cp; break; }
      }

      if (srcGrid != null)
      {
        Log.Write($"ApplyRuntimeSettings: grid source GridSpacing={srcGrid.GridSpacing} SnapSpacing={srcGrid.SnapSpacing} GridLineCount={srcGrid.GridLineCount} ThickLineFreq={srcGrid.ThickLineFrequency}");
        foreach (var view in doc.Views)
        {
          try
          {
            var vp = view.ActiveViewport;
            var cplane = vp.GetConstructionPlane();
            cplane.GridSpacing = srcGrid.GridSpacing;
            cplane.SnapSpacing = srcGrid.SnapSpacing;
            cplane.GridLineCount = srcGrid.GridLineCount;
            cplane.ThickLineFrequency = srcGrid.ThickLineFrequency;
            cplane.ShowGrid = srcGrid.ShowGrid;
            cplane.ShowAxes = srcGrid.ShowAxes;
            vp.SetConstructionPlane(cplane);
            var after = vp.GetConstructionPlane();
            Log.Write($"ApplyRuntimeSettings: grid '{vp.Name}' GridSpacing={after.GridSpacing} SnapSpacing={after.SnapSpacing} GridLineCount={after.GridLineCount}");
          }
          catch (Exception vpEx) { Log.Write($"ApplyRuntimeSettings grid viewport error: {vpEx.Message}"); }
        }
      }
      else
      {
        // Headless doc had no views — fall back to grid defaults
        var defaults = templateDoc.GetGridDefaults();
        doc.SetGridDefaults(defaults);
        Log.Write("ApplyRuntimeSettings: grid applied via GetGridDefaults (headless doc had no views).");
      }
    }
    catch (Exception ex) { Log.Write($"ApplyRuntimeSettings grid failed: {ex.Message}"); }
  }

  private static void ApplyNotes(RhinoDoc doc, File3dm templateFile)
  {
    try
    {
      var notesText = templateFile.Notes?.Notes;
      if (string.IsNullOrEmpty(notesText))
      {
        Log.Write("ApplyNotes: template has no notes.");
        return;
      }
      // Only apply if the document doesn't already have notes (DXF files don't have notes).
      if (!string.IsNullOrEmpty(doc.Notes))
      {
        Log.Write($"ApplyNotes: doc already has notes ({doc.Notes.Length} chars), skipped.");
        return;
      }
      doc.Notes = notesText;
      Log.Write($"ApplyNotes: applied ({notesText.Length} chars).");
    }
    catch (Exception ex)
    {
      Log.Write($"ApplyNotes failed: {ex.Message}");
    }
  }

  private static void ApplyLocation(RhinoDoc doc, File3dm templateFile)
  {
    try
    {
      // Earth anchor point (Document Properties > Location)
      var eap = templateFile.EarthAnchorPoint;
      if (eap != null && eap.EarthLocationIsSet())
      {
        Log.Write($"ApplyLocation: EarthAnchorPoint lat={eap.EarthBasepointLatitude} lon={eap.EarthBasepointLongitude}");
        doc.EarthAnchorPoint = eap;
      }
      else
      {
        Log.Write("ApplyLocation: template EarthAnchorPoint not set.");
      }

      // Model basepoint (used when inserting this model as a block)
      var bp = templateFile.Settings.ModelBasepoint;
      doc.ModelBasepoint = bp;
      Log.Write($"ApplyLocation: ModelBasepoint={bp}");
    }
    catch (Exception ex)
    {
      Log.Write($"ApplyLocation failed: {ex.Message}");
    }
  }

  private static void ApplyDocumentStrings(RhinoDoc doc, File3dm templateFile)
  {
    try
    {
      var templateStrings = templateFile.Strings;
      if (templateStrings == null || templateStrings.Count == 0)
      {
        Log.Write("ApplyDocumentStrings: template has no document user strings.");
        return;
      }

      int added = 0, skipped = 0;
      for (var i = 0; i < templateStrings.Count; i++)
      {
        var key = templateStrings.GetKey(i);
        var value = templateStrings.GetValue(i);
        if (string.IsNullOrEmpty(key))
          continue;
        // Don't overwrite strings already present in the DXF doc.
        if (doc.Strings.GetValue(key) != null)
        { skipped++; continue; }
        doc.Strings.SetString(key, value);
        added++;
      }
      Log.Write($"ApplyDocumentStrings: added={added} skipped={skipped}");
    }
    catch (Exception ex)
    {
      Log.Write($"ApplyDocumentStrings failed: {ex.Message}");
    }
  }

  private static void ApplyLayers(RhinoDoc doc, File3dm templateFile)
  {
    try
    {
      int totalLayers = 0, addedLayers = 0, skippedLayers = 0;
      foreach (var templateLayer in templateFile.AllLayers)
      {
        totalLayers++;
        if (templateLayer == null || templateLayer.IsDeleted)
          continue;

        var fullPath = templateLayer.FullPath;
        if (string.IsNullOrEmpty(fullPath))
          continue;

        var parentId = templateLayer.ParentLayerId;

        // Check if a layer with the same full path already exists.
        var existing = doc.Layers.FindByFullPath(fullPath, -1);
        if (existing >= 0)
        {
          skippedLayers++;
          continue; // Layer already present; do not overwrite DXF layers.
        }

        // Clone to avoid modifying the template object, then clear its index
        // so Rhino assigns a fresh one.
        try
        {
          var newLayer = new Rhino.DocObjects.Layer
          {
            Name = templateLayer.Name,
            Color = templateLayer.Color,
            PlotColor = templateLayer.PlotColor,
            PlotWeight = templateLayer.PlotWeight,
            IsVisible = templateLayer.IsVisible,
            IsLocked = templateLayer.IsLocked,
            LinetypeIndex = -1, // resolved separately after linetype transfer
          };

          // Resolve parent layer by attempting to match the parent full path.
          if (parentId != Guid.Empty)
          {
            var parentLayer = doc.Layers.FindId(parentId);
            if (parentLayer != null)
              newLayer.ParentLayerId = parentLayer.Id;
          }

          doc.Layers.Add(newLayer);
          addedLayers++;
        }
        catch (Exception layerEx) { Log.Write($"ApplyLayers: failed to add '{fullPath}': {layerEx.Message}"); }
      }
      Log.Write($"ApplyLayers: total={totalLayers} added={addedLayers} skipped(already exist)={skippedLayers}");
    }
    catch (Exception ex)
    {
      Log.Write($"ApplyLayers failed: {ex.Message}");
    }
  }

  private static void ApplyLinetypes(RhinoDoc doc, File3dm templateFile)
  {
    try
    {
      int added = 0, skipped = 0;
      foreach (var linetype in templateFile.AllLinetypes)
      {
        if (linetype == null || linetype.IsDeleted)
          continue;

        var name = linetype.Name;
        if (string.IsNullOrEmpty(name))
          continue;

        // Skip if already present in the document.
        if (doc.Linetypes.Find(name) >= 0)
        {
          skipped++;
          continue;
        }

        try { doc.Linetypes.Add(linetype); added++; }
        catch (Exception ex2) { Log.Write($"ApplyLinetypes: failed to add '{name}': {ex2.Message}"); }
      }
      Log.Write($"ApplyLinetypes: added={added} skipped={skipped}");
    }
    catch (Exception ex)
    {
      Log.Write($"ApplyLinetypes failed: {ex.Message}");
    }
  }

  private static void ApplyHatchPatterns(RhinoDoc doc, File3dm templateFile)
  {
    try
    {
      int added = 0, skipped = 0;
      foreach (var hp in templateFile.AllHatchPatterns)
      {
        if (hp == null || hp.IsDeleted)
          continue;

        var name = hp.Name;
        if (string.IsNullOrEmpty(name))
          continue;

        if (doc.HatchPatterns.FindName(name) != null)
        {
          skipped++;
          continue;
        }

        try { doc.HatchPatterns.Add(hp); added++; }
        catch (Exception ex2) { Log.Write($"ApplyHatchPatterns: failed to add '{name}': {ex2.Message}"); }
      }
      Log.Write($"ApplyHatchPatterns: added={added} skipped={skipped}");
    }
    catch (Exception ex)
    {
      Log.Write($"ApplyHatchPatterns failed: {ex.Message}");
    }
  }

  private static void ApplyDimStyles(RhinoDoc doc, File3dm templateFile)
  {
    try
    {
      int added = 0, updated = 0;
      foreach (var style in templateFile.AllDimStyles)
      {
        if (style == null || style.IsDeleted)
          continue;

        var name = style.Name;
        if (string.IsNullOrEmpty(name))
          continue;

        var existing = doc.DimStyles.FindName(name);
        if (existing != null)
        {
          // Update the existing style with the template's settings.
          try { doc.DimStyles.Modify(style, existing.Index, true); updated++; }
          catch (Exception ex2) { Log.Write($"ApplyDimStyles: failed to update '{name}': {ex2.Message}"); }
        }
        else
        {
          try { doc.DimStyles.Add(style, false); added++; }
          catch (Exception ex2) { Log.Write($"ApplyDimStyles: failed to add '{name}': {ex2.Message}"); }
        }
      }
      Log.Write($"ApplyDimStyles: added={added} updated={updated}");
    }
    catch (Exception ex)
    {
      Log.Write($"ApplyDimStyles failed: {ex.Message}");
    }
  }

  private static void ApplyMaterials(RhinoDoc doc, File3dm templateFile)
  {
    try
    {
      int added = 0, skipped = 0;
      foreach (var material in templateFile.AllMaterials)
      {
        if (material == null || material.IsDeleted)
          continue;

        var name = material.Name;
        if (string.IsNullOrEmpty(name))
          continue;

        // Skip if a material with the same name already exists.
        if (doc.Materials.Find(name, true) >= 0)
        {
          skipped++;
          continue;
        }

        try { doc.Materials.Add(material); added++; }
        catch (Exception ex2) { Log.Write($"ApplyMaterials: failed to add '{name}': {ex2.Message}"); }
      }
      Log.Write($"ApplyMaterials: added={added} skipped={skipped}");
    }
    catch (Exception ex)
    {
      Log.Write($"ApplyMaterials failed: {ex.Message}");
    }
  }

  private static void ApplyNamedViews(RhinoDoc doc, File3dm templateFile)
  {
    try
    {
      var templateViews = templateFile.AllNamedViews;
      if (templateViews == null || templateViews.Count == 0)
      {
        Log.Write("ApplyNamedViews: no named views in template.");
        return;
      }

      int added = 0, skipped = 0;
      for (var i = 0; i < templateViews.Count; i++)
      {
        var view = templateViews[i];
        if (view == null || string.IsNullOrEmpty(view.Name))
          continue;

        if (doc.NamedViews.FindByName(view.Name) >= 0)
        {
          skipped++;
          continue;
        }

        try { doc.NamedViews.Add(view); added++; }
        catch (Exception ex2) { Log.Write($"ApplyNamedViews: failed to add '{view.Name}': {ex2.Message}"); }
      }
      Log.Write($"ApplyNamedViews: added={added} skipped={skipped}");
    }
    catch (Exception ex)
    {
      Log.Write($"ApplyNamedViews failed: {ex.Message}");
    }
  }

  private static void ApplyNamedCPlanes(RhinoDoc doc, File3dm templateFile)
  {
    try
    {
      var namedCPlanes = templateFile.AllNamedConstructionPlanes;
      if (namedCPlanes == null || namedCPlanes.Count == 0)
      {
        Log.Write("ApplyNamedCPlanes: no named cplanes in template.");
        return;
      }

      int addedCp = 0, skippedCp = 0;
      for (var i = 0; i < namedCPlanes.Count; i++)
      {
        var cp = namedCPlanes[i];
        if (cp == null || string.IsNullOrEmpty(cp.Name))
          continue;

        // Skip if already present.
        bool exists = false;
        for (var j = 0; j < doc.NamedConstructionPlanes.Count; j++)
        {
          if (string.Equals(doc.NamedConstructionPlanes[j]?.Name, cp.Name,
                            StringComparison.OrdinalIgnoreCase))
          { exists = true; break; }
        }
        if (exists) { skippedCp++; continue; }

        try { doc.NamedConstructionPlanes.Add(cp); addedCp++; }
        catch (Exception ex2) { Log.Write($"ApplyNamedCPlanes: failed to add '{cp.Name}': {ex2.Message}"); }
      }
      Log.Write($"ApplyNamedCPlanes: added={addedCp} skipped={skippedCp}");
    }
    catch (Exception ex)
    {
      Log.Write($"ApplyNamedCPlanes failed: {ex.Message}");
    }
  }

  // -----------------------------------------------------------------------
  // Undo/redo support — doc property snapshot
  // -----------------------------------------------------------------------

  private sealed class DocSettingsSnapshot
  {
    // Units + tolerances
    public Rhino.UnitSystem ModelUnitSystem;
    public double ModelAbsoluteTolerance;
    public double ModelRelativeTolerance;
    public double ModelAngleToleranceDegrees;
    public Rhino.UnitSystem PageUnitSystem;
    public double PageAbsoluteTolerance;
    public double PageRelativeTolerance;
    public double PageAngleToleranceDegrees;
    // Display precision + format
    public int ModelDistanceDisplayPrecision;
    public int PageDistanceDisplayPrecision;
    public int ModelDistanceDisplayMode;
    public int PageDistanceDisplayMode;
    // Meshing
    public MeshingParameterStyle MeshingParameterStyle;
    public MeshingParameters? CustomMeshingParameters;
    // Annotation / hatch scaling
    public bool ModelSpaceAnnotationScalingEnabled;
    public double ModelSpaceTextScale;
    public bool ModelSpaceHatchScalingEnabled;
    public double ModelSpaceHatchScale;
    public bool LayoutSpaceAnnotationScalingEnabled;
    // SubD
    public Rhino.Geometry.SubDComponentLocation SubDAppearance;
    // Render
    public Rhino.Render.RenderSettings? RenderSettings;
    // Ground plane
    public bool GroundPlaneEnabled;
    public double GroundPlaneAltitude;
    public bool GroundPlaneAutoAltitude;
    public bool GroundPlaneShadowOnly;
    // Grid per viewport
    public List<(Guid ViewportId, ConstructionPlane CPlane)> ViewportGrids = new();
    // Notes, location
    public string Notes = string.Empty;
    public bool EarthLocationIsSet;
    public double EarthLat;
    public double EarthLon;
    public double EarthElevation;
    public Rhino.Geometry.Point3d ModelBasepoint;
  }

  private static DocSettingsSnapshot TakeDocSnapshot(RhinoDoc doc)
  {
    var snap = new DocSettingsSnapshot
    {
      ModelUnitSystem                     = doc.ModelUnitSystem,
      ModelAbsoluteTolerance              = doc.ModelAbsoluteTolerance,
      ModelRelativeTolerance              = doc.ModelRelativeTolerance,
      ModelAngleToleranceDegrees          = doc.ModelAngleToleranceDegrees,
      PageUnitSystem                      = doc.PageUnitSystem,
      PageAbsoluteTolerance               = doc.PageAbsoluteTolerance,
      PageRelativeTolerance               = doc.PageRelativeTolerance,
      PageAngleToleranceDegrees           = doc.PageAngleToleranceDegrees,
      ModelDistanceDisplayPrecision       = doc.ModelDistanceDisplayPrecision,
      PageDistanceDisplayPrecision        = doc.PageDistanceDisplayPrecision,
      ModelDistanceDisplayMode            = GetDistanceDisplayMode(doc, false),
      PageDistanceDisplayMode             = GetDistanceDisplayMode(doc, true),
      MeshingParameterStyle               = doc.MeshingParameterStyle,
      CustomMeshingParameters             = doc.MeshingParameterStyle == MeshingParameterStyle.Custom
                                              ? doc.GetCurrentMeshingParameters() : null,
      ModelSpaceAnnotationScalingEnabled  = doc.ModelSpaceAnnotationScalingEnabled,
      ModelSpaceTextScale                 = doc.ModelSpaceTextScale,
      ModelSpaceHatchScalingEnabled       = doc.ModelSpaceHatchScalingEnabled,
      ModelSpaceHatchScale                = doc.ModelSpaceHatchScale,
      LayoutSpaceAnnotationScalingEnabled = doc.LayoutSpaceAnnotationScalingEnabled,
      SubDAppearance                      = doc.SubDAppearance,
      RenderSettings                      = doc.RenderSettings,
      Notes                               = doc.Notes ?? string.Empty,
      ModelBasepoint                      = doc.ModelBasepoint,
    };

    try
    {
#pragma warning disable CS0612
      var gp = doc.GroundPlane;
#pragma warning restore CS0612
      snap.GroundPlaneEnabled     = gp.Enabled;
      snap.GroundPlaneAltitude    = gp.Altitude;
      snap.GroundPlaneAutoAltitude = gp.AutoAltitude;
      snap.GroundPlaneShadowOnly  = gp.ShadowOnly;
    }
    catch { }

    try
    {
      var eap = doc.EarthAnchorPoint;
      snap.EarthLocationIsSet = eap?.EarthLocationIsSet() ?? false;
      if (snap.EarthLocationIsSet && eap != null)
      {
        snap.EarthLat       = eap.EarthBasepointLatitude;
        snap.EarthLon       = eap.EarthBasepointLongitude;
        snap.EarthElevation = eap.EarthBasepointElevation;
      }
    }
    catch { }

    try
    {
      foreach (var view in doc.Views)
        try { snap.ViewportGrids.Add((view.ActiveViewport.Id, view.ActiveViewport.GetConstructionPlane())); }
        catch { }
    }
    catch { }

    return snap;
  }

  private static void RestoreDocSnapshot(RhinoDoc doc, DocSettingsSnapshot snap)
  {
    try { doc.ModelUnitSystem = snap.ModelUnitSystem; } catch { }
    try { doc.ModelAbsoluteTolerance = snap.ModelAbsoluteTolerance; } catch { }
    try { doc.ModelRelativeTolerance = snap.ModelRelativeTolerance; } catch { }
    try { doc.ModelAngleToleranceDegrees = snap.ModelAngleToleranceDegrees; } catch { }
    try { doc.PageUnitSystem = snap.PageUnitSystem; } catch { }
    try { doc.PageAbsoluteTolerance = snap.PageAbsoluteTolerance; } catch { }
    try { doc.PageRelativeTolerance = snap.PageRelativeTolerance; } catch { }
    try { doc.PageAngleToleranceDegrees = snap.PageAngleToleranceDegrees; } catch { }
    try { doc.ModelDistanceDisplayPrecision = snap.ModelDistanceDisplayPrecision; } catch { }
    try { doc.PageDistanceDisplayPrecision = snap.PageDistanceDisplayPrecision; } catch { }
    try { SetDistanceDisplayMode(doc, snap.ModelDistanceDisplayMode, false); } catch { }
    try { SetDistanceDisplayMode(doc, snap.PageDistanceDisplayMode, true); } catch { }
    try
    {
      doc.MeshingParameterStyle = snap.MeshingParameterStyle;
      if (snap.MeshingParameterStyle == MeshingParameterStyle.Custom && snap.CustomMeshingParameters != null)
        doc.SetCustomMeshingParameters(snap.CustomMeshingParameters);
    }
    catch { }
    try { doc.ModelSpaceAnnotationScalingEnabled = snap.ModelSpaceAnnotationScalingEnabled; } catch { }
    try { doc.ModelSpaceTextScale = snap.ModelSpaceTextScale; } catch { }
    try { doc.ModelSpaceHatchScalingEnabled = snap.ModelSpaceHatchScalingEnabled; } catch { }
    try { doc.ModelSpaceHatchScale = snap.ModelSpaceHatchScale; } catch { }
    try { doc.LayoutSpaceAnnotationScalingEnabled = snap.LayoutSpaceAnnotationScalingEnabled; } catch { }
    try { doc.SubDAppearance = snap.SubDAppearance; } catch { }
    try { if (snap.RenderSettings != null) doc.RenderSettings = snap.RenderSettings; } catch { }
    try
    {
#pragma warning disable CS0612
      var gp = doc.GroundPlane;
#pragma warning restore CS0612
      gp.BeginChange(Rhino.Render.RenderContent.ChangeContexts.Program);
      gp.Enabled      = snap.GroundPlaneEnabled;
      gp.Altitude     = snap.GroundPlaneAltitude;
      gp.AutoAltitude = snap.GroundPlaneAutoAltitude;
      gp.ShadowOnly   = snap.GroundPlaneShadowOnly;
      gp.EndChange();
    }
    catch { }
    try { doc.Notes = snap.Notes; } catch { }
    try { doc.ModelBasepoint = snap.ModelBasepoint; } catch { }
    try
    {
      if (snap.EarthLocationIsSet)
      {
        doc.EarthAnchorPoint = new Rhino.DocObjects.EarthAnchorPoint
        {
          EarthBasepointLatitude  = snap.EarthLat,
          EarthBasepointLongitude = snap.EarthLon,
          EarthBasepointElevation = snap.EarthElevation,
        };
      }
    }
    catch { }
    try
    {
      foreach (var view in doc.Views)
      {
        try
        {
          var vp    = view.ActiveViewport;
          var entry = snap.ViewportGrids.FirstOrDefault(x => x.ViewportId == vp.Id);
          if (entry.ViewportId != Guid.Empty)
            vp.SetConstructionPlane(entry.CPlane);
        }
        catch { }
      }
    }
    catch { }
    doc.Views.Redraw();
  }

  private static void OnDocSettingsUndoRedo(object? sender, Rhino.Commands.CustomUndoEventArgs e)
  {
    try
    {
      if (e.Tag is DocSettingsSnapshot[] snapshots && snapshots.Length == 2)
      {
        // snapshots[0] = before-apply state, snapshots[1] = after-apply state.
        RestoreDocSnapshot(e.Document, e.CreatedByRedo ? snapshots[1] : snapshots[0]);
        Log.Write($"OnDocSettingsUndoRedo: {(e.CreatedByRedo ? "redo" : "undo")} applied.");
      }
    }
    catch (Exception ex) { Log.Write($"OnDocSettingsUndoRedo failed: {ex.Message}"); }
  }

  // -----------------------------------------------------------------------
  // Reflection helpers — DistanceDisplayMode (no public property on RhinoDoc)
  // -----------------------------------------------------------------------

  private static MethodInfo? _getDistanceDisplayMode;
  private static MethodInfo? _setDistanceDisplayMode;

  private static void EnsureDisplayModeReflection()
  {
    if (_getDistanceDisplayMode != null) return;
    try
    {
      var asm        = typeof(RhinoDoc).Assembly;
      var flags      = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
      var unsafeType = asm.GetTypes().FirstOrDefault(t => t.Name == "UnsafeNativeMethods");
      if (unsafeType == null) return;
      _getDistanceDisplayMode = unsafeType.GetMethod("CRhinoDocProperties_GetDistanceDisplayMode", flags);
      _setDistanceDisplayMode = unsafeType.GetMethod("CRhinoDocProperties_SetDistanceDisplayMode", flags);
    }
    catch { }
  }

  private static int GetDistanceDisplayMode(RhinoDoc doc, bool usePageUnits)
  {
    try
    {
      EnsureDisplayModeReflection();
      if (_getDistanceDisplayMode == null) return 0;
      return _getDistanceDisplayMode.Invoke(null, new object[] { doc.RuntimeSerialNumber, usePageUnits }) is int v ? v : 0;
    }
    catch { return 0; }
  }

  private static void SetDistanceDisplayMode(RhinoDoc doc, int mode, bool usePageUnits)
  {
    try
    {
      EnsureDisplayModeReflection();
      _setDistanceDisplayMode?.Invoke(null, new object[] { doc.RuntimeSerialNumber, mode, usePageUnits });
    }
    catch { }
  }
}

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

/// <summary>One extension-to-template mapping entry.</summary>
public sealed class FileTypeMapping
{
  /// <summary>File extension including the dot, e.g. ".dxf".</summary>
  public string Extension { get; set; } = string.Empty;

  /// <summary>
  /// Absolute path to the template .3dm file. Empty means use Rhino's default
  /// template as reported by <c>FileSettings.TemplateFile</c>.
  /// </summary>
  public string TemplatePath { get; set; } = string.Empty;
}

/// <summary>
/// Persisted settings for the vFileTypeTemplate plug-in, backed by
/// vFileTypeTemplate.config.json located next to the assembly.
/// </summary>
public sealed class VFileTypeTemplateConfig
{
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
  };

  /// <summary>When false the plugin skips all file-open events.</summary>
  public bool Enabled { get; set; } = true;

  /// <summary>List of file-extension → template mappings.</summary>
  public List<FileTypeMapping> Mappings { get; set; } = new();

  /// <summary>
  /// Captures unknown JSON keys so we can migrate the old single-field format
  /// (<c>"templatePath"</c>) to the new <c>Mappings</c> list on first load.
  /// </summary>
  [System.Text.Json.Serialization.JsonExtensionData]
  public Dictionary<string, System.Text.Json.JsonElement>? ExtraProperties { get; set; }

  // ---- Load / Save ----

  private static string ConfigFilePath()
  {
    var dir = Path.GetDirectoryName(typeof(VFileTypeTemplateConfig).Assembly.Location) ?? ".";
    return Path.Combine(dir, "vFileTypeTemplate.config.json");
  }

  public static VFileTypeTemplateConfig Load()
  {
    try
    {
      var path = ConfigFilePath();
      if (!File.Exists(path))
        return new VFileTypeTemplateConfig();

      var json = File.ReadAllText(path);
      var config = JsonSerializer.Deserialize<VFileTypeTemplateConfig>(json, JsonOptions)
                   ?? new VFileTypeTemplateConfig();

      // Migrate old single-field format: { "templatePath": "..." } → Mappings entry for .dxf
      if (config.Mappings.Count == 0 &&
          config.ExtraProperties?.TryGetValue("templatePath", out var legacyEl) == true)
      {
        var legacyPath = legacyEl.ValueKind == System.Text.Json.JsonValueKind.String
          ? legacyEl.GetString() ?? string.Empty
          : string.Empty;
        config.Mappings.Add(new FileTypeMapping { Extension = ".dxf", TemplatePath = legacyPath });
        config.ExtraProperties = null;
        config.Save();
        Log.Write("Config migrated from legacy templatePath format to Mappings.");
      }

      return config;
    }
    catch
    {
      return new VFileTypeTemplateConfig();
    }
  }

  public bool Save()
  {
    try
    {
      var path = ConfigFilePath();
      // Clear migration bag before serialising so it is not written back out.
      ExtraProperties = null;
      var json = JsonSerializer.Serialize(this, JsonOptions);
      File.WriteAllText(path, json);
      return true;
    }
    catch
    {
      return false;
    }
  }

  /// <summary>
  /// Finds the configured template path for the given file extension.
  /// Returns <c>null</c> if no mapping is configured for this extension.
  /// Returns an empty string if a mapping exists but has no path (use Rhino default).
  /// </summary>
  public string? FindTemplatePath(string extension)
  {
    var mapping = Mappings.FirstOrDefault(m =>
      SplitExtensions(m.Extension)
        .Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase)));
    return mapping?.TemplatePath;
  }

  /// <summary>
  /// Splits a raw extension string on , ; | — normalises each token
  /// (lowercase, adds leading dot if missing). Yields nothing for blank input.
  /// </summary>
  public static System.Collections.Generic.IEnumerable<string> SplitExtensions(string raw)
  {
    if (string.IsNullOrWhiteSpace(raw)) yield break;
    foreach (var part in raw.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
    {
      var ext = part.Trim().ToLowerInvariant();
      if (string.IsNullOrEmpty(ext)) continue;
      if (!ext.StartsWith(".")) ext = "." + ext;
      yield return ext;
    }
  }
}
