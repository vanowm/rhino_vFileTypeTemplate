# vFileTypeTemplate  ·  v26.7.23.1753

vFileTypeTemplate is a Rhino 8 and Rhino 9 plug-in that applies a chosen Rhino template automatically after a mapped file type is opened.

## Features

- Maps one or more file extensions to a Rhino template.
- Applies mappings after a normal document open while ignoring merge and reference operations.
- Synchronizes document settings, notes, document strings, layers, linetypes, hatch patterns, dimension styles, materials, named views, named construction planes, and runtime settings.
- Uses a DXF file's `$INSUNITS` value for document units when available.
- Applies the operation in a Rhino undo record.

## Commands

| Command | Purpose |
| --- | --- |
| `vFileTypeTemplateConfig` | Open the mapping editor and enable or disable automatic application. |
| `vFileTypeTemplateApply` | Apply the template mapped to the active document's file type. |

The configuration page is also available under Rhino's **Tools > Options > File Type Template**.

## Configuration

`vFileTypeTemplate.config.json` is stored beside the plug-in DLL. The repository includes these defaults:

```json
{
  "enabled": true,
  "mappings": [
    {
      "extension": ".dxf",
      "templatePath": ""
    }
  ]
}
```

An empty template path uses Rhino's default template. A bare template filename is resolved in Rhino's template folder, while an absolute path can point elsewhere. Missing configured templates are skipped without changing the opened document. Multiple comma-separated extensions can share one mapping in the configuration UI.

## Build

From the repository folder:

```powershell
.\build.ps1
```

The default Release build does not require Git and never commits or pushes. Maintainers can use `.\build.ps1 -Publish` to build, create a signed semantic commit when the DLL changes, push `master`, and publish a GitHub release containing separate Rhino 8/.NET 7 and Rhino 9/.NET 10 DLLs, plus any generated `.rui` files.

## Installation

The Release plug-ins are:

- `bin/Release/net7.0-windows/vFileTypeTemplate.dll` for Rhino 8
- `bin/Release/net10.0-windows/vFileTypeTemplate.dll` for Rhino 9 Load it with Rhino's Plug-in Manager and keep `vFileTypeTemplate.config.json` beside the DLL when deploying predefined mappings.

## Versioning

Build versions use `yy.m.d.hmm`, derived from the newest C# source file rather than the compile time.

## License

Released under the [MIT License](LICENSE).