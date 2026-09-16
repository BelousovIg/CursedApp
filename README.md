# CursedApp

A WPF addon manager for World of Warcraft, covering the part of the CurseForge
app that matters day to day: remember the game folder, list what is installed,
tell you what is out of date, and search the catalogue.

## Requirements

| | |
|---|---|
| .NET SDK | **9.0.317** or newer 9.0.x (pinned in `global.json`) |
| Target | `net9.0-windows`, C# 13 |
| IDE | **Visual Studio 2022** 17.12+, Rider, or VS Code + C# Dev Kit |
| OS | Windows 10 1809+ (the dark title bar uses DWM window attributes) |

Open `CursedApp.sln`.

### Why .NET 9 and not 10

Visual Studio 2022 ships MSBuild 17.x, and the .NET 10 SDK refuses to load
there:

```
Version 10.0.400 of the .NET SDK requires at least version 18.0.0 of MSBuild.
The current available version of MSBuild is 17.14.51.
error MSB4236: The SDK 'Microsoft.NET.Sdk' specified could not be found.
```

.NET 10 needs Visual Studio 2026 (MSBuild 18). This project targets .NET 9 so it
opens in VS 2022; nothing in the code needs anything newer. To move it back to
.NET 10, repin `global.json`, change the three `TargetFramework` values, and
raise the `Microsoft.Extensions.*` packages to `10.0.x`.

The solution is deliberately in the classic `.sln` format — `dotnet new sln` on
the .NET 10 SDK produces `.slnx`, which VS 2022 only opens with a preview
feature enabled.

### Why `LangVersion` is `preview`

`Directory.Build.props` sets `<LangVersion>preview</LangVersion>`. The view
models declare their observable state as partial properties:

```csharp
[ObservableProperty]
public partial bool IsExpanded { get; set; }
```

On the .NET 9 SDK, CommunityToolkit.Mvvm 8.4 only generates the implementations
for those when the language version is `preview`. With `latest` the build fails
with `CS9248: Partial property must have an implementation part` for every one
of them. The SDK is pinned, so the feature set this resolves to is fixed;
switching to `latest` means rewriting the view models to the older
private-field form of `[ObservableProperty]`.

## Build and run

```powershell
dotnet build                                         # whole solution
dotnet test tests/CursedApp.Tests                    # 95 tests
dotnet run --project src/CursedApp                   # start the app
```

## First run

1. **Settings → Game folder** — pick the folder containing `_retail_`,
   `_classic_era_` and friends. It is remembered between runs.
2. **Settings → CurseForge API key** — the CurseForge API has no anonymous
   access; create a free key at [console.curseforge.com](https://console.curseforge.com/).
   Without one the installed list still works, read from each addon's `.toc`,
   but versions cannot be checked and search is unavailable.

Settings and logs live under `%APPDATA%\CursedApp` — `settings.json` plus a
`logs\yyyy-MM-dd.txt` per day.

## How installed addons are identified

Folders under `Interface\AddOns` are matched against CurseForge in tiers, most
reliable first. Each tier only sees what the previous ones did not claim, and a
claim takes the whole folder set of the matched release — which is what turns a
21-folder bundle into a single row.

1. **Folder fingerprint** — murmur2 over the tocs and everything they load,
   posted to `/v1/fingerprints`, the same mechanism the real app uses.
2. **`## X-Curse-Project-ID`** — the tag some packagers write into the toc.
3. **Name search** — accepted only when one of the mod's releases installs a
   folder with exactly this name, which catches addons whose files do not hash
   to any catalogue release.
4. Otherwise the addon is listed from its toc alone and marked unknown.

Two details in tier 1 are easy to get wrong and cost most of the accuracy:

- **Read `partialMatches`, not just `exactMatches`.** "Exact" means every module
  of a release matched, so each folder of a multi-folder addon like DBM reports
  as a *partial* match unless the whole set lines up with one release. Reading
  only exact matches identified 8 folders out of 56 on a real install.
- **Fingerprint every `.toc` in the folder**, not only the one this flavor
  loads. A packaged addon ships its Mainline, Vanilla and Cata tocs side by
  side and the fingerprint covers the folder as shipped. Seeding from the single
  flavor toc identified 31 of 56; both fixes together identify 50.

`tools/FingerprintProbe` is the harness these numbers came from. It uses the
API as its own oracle — send one batch of fingerprints per candidate algorithm
and count how many folders each identifies — and `--solve <folder> <expected>`
brute-forces the recipe against a known-good value from the catalogue.

## Addons the author has locked

Some authors switch off third-party distribution (`allowModDistribution:
false`), and CurseForge then returns no download URL. The version comparison
still holds, so the status stays accurate and the Status column offers
**Update on site** instead, which opens the addon's page.

For those, download the zip yourself and let the app unpack it: **Settings →
Manually downloaded addons** watches a folder (your Downloads by default) and
installs any archive that contains a folder with a `.toc` file. Anything else in
that folder is ignored.
