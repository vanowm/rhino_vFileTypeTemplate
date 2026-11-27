using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using System;
using System.Collections.Generic;
using System.Linq;

namespace VFileTypeTemplate;

internal static class ViewportSelection
{
  internal const string DisplayDefault = "(default)";

  internal static readonly string[] StandardNames =
  {
    "Perspective", "Top", "Bottom", "Front", "Back", "Left", "Right"
  };

  internal static string Normalize(string? value)
  {
    var name = value?.Trim() ?? string.Empty;
    if (string.IsNullOrEmpty(name) ||
        name.Equals(DisplayDefault, StringComparison.OrdinalIgnoreCase) ||
        name.Equals("default", StringComparison.OrdinalIgnoreCase))
    {
      return string.Empty;
    }

    return StandardNames.FirstOrDefault(standard =>
      standard.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
  }

  internal static void Apply(RhinoDoc doc, string viewport, bool maximize)
  {
    var name = Normalize(viewport);
    if (name.Length == 0) return;

    try
    {
      var views = doc.Views.GetStandardRhinoViews();
      var target = views.FirstOrDefault(view =>
        string.Equals(view.MainViewport.Name, name, StringComparison.OrdinalIgnoreCase));

      if (target == null)
      {
        if (!StandardNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
            !Enum.TryParse<DefinedViewportProjection>(name, out var projection))
        {
          Log.Write($"Viewport selection: model viewport '{name}' was not found; selection skipped.");
          return;
        }

        var activeId = doc.Views.ActiveView?.MainViewport.Id ?? Guid.Empty;
        target = views.FirstOrDefault(view => view.MainViewport.Id == activeId)
                 ?? views.FirstOrDefault();
        if (target == null)
        {
          Log.Write($"Viewport selection: no model viewport is available for '{name}'.");
          return;
        }

        if (!target.MainViewport.SetProjection(projection, name, true))
        {
          Log.Write($"Viewport selection: could not set model viewport projection to '{name}'.");
          return;
        }
      }

      // Restore any other maximized model window so the selected view can
      // become visible even when Maximize is unchecked.
      foreach (var view in views)
        if (view.MainViewport.Id != target.MainViewport.Id && view.Maximized)
          view.Maximized = false;

      doc.Views.ActiveView = target;
      target.Maximized = maximize;
      doc.Views.Redraw();
      Log.Write($"Viewport selection: activated '{target.MainViewport.Name}', maximized={maximize}.");
    }
    catch (Exception ex)
    {
      Log.Write($"Viewport selection failed for '{name}': {ex.Message}");
    }
  }

  internal sealed class Snapshot : IDisposable
  {
    internal Guid ActiveViewId;
    internal readonly List<ViewState> Views = new();

    public void Dispose()
    {
      foreach (var view in Views)
        view.Projection.Dispose();
      Views.Clear();
    }
  }

  internal sealed class ViewState
  {
    internal Guid Id;
    internal string Name = string.Empty;
    internal bool Maximized;
    internal ViewportInfo Projection = null!;
    internal ConstructionPlane ConstructionPlane = null!;
  }

  internal static Snapshot Capture(RhinoDoc doc)
  {
    var snapshot = new Snapshot();
    try
    {
      snapshot.ActiveViewId = doc.Views.ActiveView?.MainViewport.Id ?? Guid.Empty;
      foreach (var view in doc.Views.GetStandardRhinoViews())
      {
        var viewport = view.MainViewport;
        snapshot.Views.Add(new ViewState
        {
          Id = viewport.Id,
          Name = viewport.Name,
          Maximized = view.Maximized,
          Projection = new ViewportInfo(viewport),
          ConstructionPlane = viewport.GetConstructionPlane()
        });
      }
    }
    catch (Exception ex)
    {
      Log.Write($"Viewport snapshot capture failed: {ex.Message}");
    }

    return snapshot;
  }

  internal static void Restore(RhinoDoc doc, Snapshot snapshot)
  {
    try
    {
      var views = doc.Views.GetStandardRhinoViews();
      foreach (var state in snapshot.Views)
      {
        var view = views.FirstOrDefault(candidate => candidate.MainViewport.Id == state.Id);
        if (view == null) continue;

        view.Maximized = false;
        view.MainViewport.SetViewProjection(state.Projection, true);
        view.MainViewport.Name = state.Name;
        view.MainViewport.SetConstructionPlane(state.ConstructionPlane);
      }

      // ActiveView may have been a layout before selecting a model viewport.
      var activeView = doc.Views.Find(snapshot.ActiveViewId);
      if (activeView != null)
        doc.Views.ActiveView = activeView;

      foreach (var state in snapshot.Views.Where(state => state.Maximized))
      {
        var view = views.FirstOrDefault(candidate => candidate.MainViewport.Id == state.Id);
        if (view != null)
          view.Maximized = true;
      }

      doc.Views.Redraw();
    }
    catch (Exception ex)
    {
      Log.Write($"Viewport snapshot restore failed: {ex.Message}");
    }
  }
}
