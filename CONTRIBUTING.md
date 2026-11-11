# Contributing to Classic Corkboard

Build on Windows using the .NET 8 SDK. See the [README](README.md#quick-start) for the commands.

1. Create a focused branch from `main`, such as `fix/tray-restore` or `feature/note-search`.
2. Build with `dotnet build .\src\Corkboard\Corkboard.csproj -c Release`.
3. Try the change with a separate notes directory. Set `$env:CORKBOARD_DATA_DIR = Join-Path $PWD 'test-data'` before launching the app.
4. Open a pull request against `main`. Describe the problem, explain the change, and list what you tested. Include screenshots for visible changes when useful.

Keep the app usable offline and avoid ongoing background work for features that only need to respond to user input. Include sample data you can share publicly. Use the [security policy](SECURITY.md) for vulnerability reports.
