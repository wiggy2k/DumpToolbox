# Development

## Requirements

- .NET 8 SDK
- Windows, Linux or another platform supported by Avalonia and the project's runtime dependencies

The repository pins its expected SDK through `global.json` and enables deterministic builds through `Directory.Build.props`.

## Build and test

```powershell
dotnet restore DumpToolbox.sln
dotnet build DumpToolbox.sln --configuration Release --no-restore
dotnet test DumpToolbox.sln --configuration Release --no-build
```

Run the desktop application during development with:

```powershell
dotnet run --project DumpToolbox/DumpToolbox.csproj
```

## Publish

Example self-contained Windows x64 single-file publish:

```powershell
dotnet publish DumpToolbox/DumpToolbox.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true
```

Use the matching runtime identifier for other platforms. External `ffmpeg`/`ffprobe` binaries are needed only for the audio formats described in the Audio guide.

## Project layout

```text
DumpToolbox/             Avalonia UI and application wiring
DumpToolbox.Core/        Conversion, parsing, hashing and recovery services
DumpToolbox.Core.Tests/  Core regression tests
docs/                    Current function-oriented documentation
CHANGELOG.md             Historical release notes
```

Large services use partial-class files grouped by responsibility. Shared binary-format algorithms should live in the Core project rather than being duplicated in UI or workflow services.
