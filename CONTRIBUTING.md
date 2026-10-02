# Contributing

Thanks for looking. This is a personal tool shared as is, so the bar is: keep
it working for its one real use, keep it small while it runs all day, and keep
it honest about its numbers.

- **Bugs and questions:** open an issue. Please leave out your own speeds,
  addresses and adapter names; describe the shape of the problem instead.
- **Small fixes:** a pull request is welcome.
- **Anything larger:** open an issue first, so we can agree it fits before you
  spend the time.
- **Security issues:** not in a public issue; see [SECURITY.md](SECURITY.md).

## Before sending a change

```powershell
dotnet build SpeedMeterNative.slnx
dotnet test --project tests\SpeedMeter.Core.Tests\SpeedMeter.Core.Tests.csproj
```

The tests need no admin rights, no network and no real data.

## Conventions

- **Conventional commit messages** (`feat:`, `fix:`, `docs:`), one concern per
  commit.
- **The history format does not change.** The binary logs and
  `speedtests.jsonl` are read by every version of this app and its
  predecessors; a new field goes in the reserved bytes or as a new optional
  JSON field, never by moving an old one.
- **The meter never loads WinUI.** It is the part that runs all day; anything
  it needs from the dashboard goes through `Meter\MeterLink.cs`.
- **The speed test is the only traffic.** Nothing else may open a connection.
- **Keep every `.ps1` pure ASCII.** Windows PowerShell 5.1 misreads a UTF-8
  dash in a BOM-less script, and silently changes its logic.
- **Never commit recorded data.** `.gitignore` blocks the logs, the speed
  tests, CSVs and the `screenshots/` folder.
- **Screenshots come from the demo data only.** Make it with
  `dotnet run --project src\SpeedMeter.Cli -- demo-data demo-data` and render
  with `tools\Snapshot.ps1`, rather than photographing your own history.
- **No new dependencies without a reason.** The app is meant to build years
  from now from what is on disk.
