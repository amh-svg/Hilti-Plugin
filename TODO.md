# TODO

## Pending Investigation — 2026-07-24

Investigation only, nothing fixed yet. Findings for the 5 items flagged at end-of-session.

### 1. `AvControls.TitleBarWithSubtitle` — confirmed zero callers

Re-grepped the whole solution. Only hit is the method's own definition:

```
AVES/Core/AvControls.cs:109:        public static Border TitleBarWithSubtitle(string title, string? subtitle = null)
```

Safe to delete whenever the header-styling task's Step 3 is picked back up.

### 2. Pattern B breadcrumb occurrences (`"Title — extra"` same-line style)

- **`Cmds_Pulldown4.cs`** — none remaining. Line 406 still calls `AvControls.TitleBar("Batch Tag", "")`, but the breadcrumb argument is an empty string (already neutralized by the earlier drift-fix commit `6e3788f`), so nothing renders. Structurally still a 2-arg call, but no live text.
- **`Cmds_Button7.cs:574-581`** — live, dynamic breadcrumb:
  ```csharp
  Text = $"  — {processed} of {assembliesFound} assemblies processed",
  FontSize = 12,
  Foreground = AvTokens.B(AvTokens.TextFaint),
  ```
- **`Cmds_Button8.cs:1040-1047`** — live, dynamic breadcrumb:
  ```csharp
  Text = $"  — {processed} of {assembliesFound} assemblies processed · Total weight: {totalWeightSum:F3} kg",
  FontSize = 12,
  Foreground = AvTokens.B(AvTokens.TextFaint),
  ```

Both remaining occurrences show live progress/weight data, not static filler — same category as the two Pattern B instances already deliberately left alone during the header-styling task.

### 3. Phase 3 StatusPill/StatTile/button-technique reconciliation backlog

Source: the 7 "Migrate `Cmds_*.cs` to AVES.Core design tokens/controls" commit messages (full detail lives there, not in a separate doc). `Cmds_Button7.cs` (`8c7ba25`) routed cleanly with no flagged discrepancies — the backlog is actually 6 files:

- **`Cmds_PullDown.cs`** (`e8b95f7`) — `StatusPill` stays local: "Cache mismatch" wording + differing dot size/padding/corner-radius/font-size vs `AvControls.StatusPill`.
- **`Cmds_PullDown2.cs`** (`ee43189`) — `StatusPill` stays local: "Completed with errors" wording (Core says "Warning") + outer `Border` never sets `VerticalAlignment` (Core's does).
- **`Cmds_Pulldown3.cs`** (`c1c29af`) — `StatTile`: label `FontSize` 10.5 vs Core's 11, value margin-bottom 3 vs 4, no `"warn"` case in the accent-color switch (dead code, unreachable). `StatusPill`: "fail" label reads "Error" vs Core's "Failed" (geometry otherwise matches). Button trio: `GhostButton` padding hardcoded to `(16,9,16,9)` vs Core's `(14,9,14,9)`.
- **`Cmds_Pulldown4.cs`** (`d674c2f`) — `StatusPill`: "warn" label reads "Warn" (a third distinct wording variant) — confirmed **live**, not dead code — + missing `VerticalAlignment` on the outer `Border`.
- **`Cmds_Button6.cs`** (`c1e0691`) — `StatusPill`: different input domain entirely (`"Renamed"/"Skipped"/"Error"/"Already Correct"` vs Core's `ok/skip/fail/warn`), dot margin 5 vs 6, padding `(8,4,10,4)` vs `(8,5,10,5)`, extra outer `Margin(12,0,12,0)`. `StatTile`: value margin-bottom 3 vs 4, no `TextTrimming`/`ToolTip`, extra outer `Margin(0,0,10,0)`. `BrandTile`: decode sets `DecodePixelHeight`/`SnapsToDevicePixels`/`UseLayoutRounding`, none of which Core's plain `BrandTile` does. Buttons: a 4th distinct technique (`SetBinding` to `TemplatedParent.Background` instead of baking color into the template), padding `(20,8,20,8)`/`(16,8,16,8)` vs Core's uniform `(16,9,16,9)`, extra `MinWidth=80`.
- **`Cmds_Button9.cs`** (`5b8fcd3`) — `StatusPillConverter`: closest match to Core found (only "Exported" vs Core's "Done" differs), but architecturally an `IValueConverter`, not a static factory method. `BuildHeaderBar`: `Background` is `Surface` not `Surface2`, plus an extra timestamp column Core's shape doesn't have. `BuildButton`: a 5th distinct technique — every kind (including Primary/Ghost, which have no border in any Core family) gets an explicit border, uniform padding `(16,9,16,9)` for all kinds including Ghost (Core's Ghost uses `(14,9,14,9)`). `BuildStatTile`: no `"warn"` case at all (unreachable), label `FontSize` 10.5 vs 11, value margin-bottom 3 vs 4, no `TextTrimming`/`ToolTip`. `BuildLogoImage`: sets `DecodePixelHeight`, unique try/catch fallback pattern.

Net: 3 wording variants of the "warn" pill label alone ("Cache mismatch", "Completed with errors", "Warn"), plus 5 distinct button-construction techniques across the whole plugin (Core's 3 families + Button6's SetBinding technique + Button9's uniform-border technique). No reconciliation decision has been made on any of these yet.

### 4. `Cmds_Pulldown3.cs` uncommitted diff — now identified

Not the breadcrumb-clearing pattern. It's a title-text rename, isolated to 4 lines, all in the Vendor auto-dimension flow:

```diff
-                    "Vendor Dimension — Overall H×W×D", "Vendor");
+                    "Auto-Dimension Vendor H×W×D", "Vendor");
```
(appears 4 times: `ShowAssemblyPicker` call, `ShowDimensionTypePicker` call, and twice in `ShowLogWindow` title args)

Renames "Vendor Dimension — Overall H×W×D" to "Auto-Dimension Vendor H×W×D" — looks like a deliberate naming-consistency pass (matches the "Auto-Dimension MT Views" naming already used by the sibling MT flow), not accidental drift. Still uncommitted. Not part of today's work; nobody has acted on it.

### 5. Full `git status` snapshot (untracked/uncommitted, outside today's work)

```
 M AVES.slnx
 M AVES/AVES_Installer.iss
 D AVES/Commands/General/Cmds_Button.cs
 M AVES/Commands/General/Cmds_Pulldown3.cs
 D AVES/Resources/Icons16/Button_Button16.png
 M AVES/Resources/Icons16/PullDown16.png
 D AVES/Resources/Icons16/PullDown_1Button16.png
 D AVES/Resources/Icons16/PullDown_2Button16.png
 D AVES/Resources/Icons16/PullDown_3Button16.png
 D AVES/Resources/Icons32/Button_Button32.png
 M AVES/Resources/Icons32/PullDown32.png
 D AVES/Resources/Icons32/PullDown_1Button32.png
 D AVES/Resources/Icons32/PullDown_2Button32.png
 D AVES/Resources/Icons32/PullDown_3Button32.png
 D AVES/installer_output/AVES_Setup_v1.0.0.exe
?? "AVES/Resources/Hilti Logo/"
?? AVES/Resources/Icons16/Button10_Button16.png
?? AVES/Resources/Icons16/Button11_Button16.png
?? AVES/Resources/Icons16/Button6_Button16.png
?? AVES/Resources/Icons16/Button7_Button16.png
?? AVES/Resources/Icons16/Button8_Button16.png
?? AVES/Resources/Icons16/Button9_Button16.png
?? AVES/Resources/Icons16/PullDownAutoDimension16.png
?? AVES/Resources/Icons16/PullDownAutoDimension_MTQDrawings16.png
?? AVES/Resources/Icons16/PullDownAutoDimension_VendorDrawings16.png
?? AVES/Resources/Icons16/PullDownCreateSheets16.png
?? AVES/Resources/Icons16/PullDownCreateSheets_AssemblyDrawings16.png
?? AVES/Resources/Icons16/PullDownCreateSheets_MTQDrawings16.png
?? AVES/Resources/Icons16/PullDownCreateSheets_VendorDrawings16.png
?? AVES/Resources/Icons16/PullDown_BatchRemoveParams16.png
?? AVES/Resources/Icons16/PullDown_BatchSharedParams16.png
?? AVES/Resources/Icons16/PullDown_CsvImporter16.png
?? AVES/Resources/Icons32/Button10_Button32.png
?? AVES/Resources/Icons32/Button11_Button32.png
?? AVES/Resources/Icons32/Button6_Button32.png
?? AVES/Resources/Icons32/Button7_Button32.png
?? AVES/Resources/Icons32/Button8_Button32.png
?? AVES/Resources/Icons32/Button9_Button32.png
?? AVES/Resources/Icons32/PullDownAutoDimension32.png
?? AVES/Resources/Icons32/PullDownAutoDimension_MTDimensions32.png
?? AVES/Resources/Icons32/PullDownAutoDimension_VendorDimensions32.png
?? AVES/Resources/Icons32/PullDownAutoTag32.png
?? AVES/Resources/Icons32/PullDownAutoTag_MTTag32.png
?? AVES/Resources/Icons32/PullDownAutoTag_VendorTag32.png
?? AVES/Resources/Icons32/PullDownCreateSheets32.png
?? AVES/Resources/Icons32/PullDownCreateSheets_AssemblyDrawings32.png
?? AVES/Resources/Icons32/PullDownCreateSheets_MTDrawings32.png
?? AVES/Resources/Icons32/PullDownCreateSheets_VendorDrawings32.png
?? AVES/Resources/Icons32/PullDown_BatchRemoveParams32.png
?? AVES/Resources/Icons32/PullDown_BatchSharedParams32.png
?? AVES/Resources/Icons32/PullDown_CsvImporter32.png
?? AVES/installer_output/AVES_Setup_v1.0.1.exe
```

Note: some untracked icon filenames still say `MTQ` (e.g. `PullDownAutoDimension_MTQDrawings16.png`) while others already say `MT` (e.g. `PullDownAutoDimension_MTDimensions32.png`) — inconsistent, not addressed by today's MTQ->MT rename since icon assets were out of scope for that task.

## Pre-existing pile — investigated (2026-07-24)

Report only, nothing fixed or committed beyond this note. Covers the remaining items from the pile in section 5 above (icon renames already resolved separately in `5b815fb`/`7d1080a`).

### 1. `AVES.slnx` diff

Adds x64 platform support to the solution: two new `<Platform>` entries (`Any CPU`, `x64`) and a project-level platform mapping (`<Platform Solution="*|x64" Project="x64" />`) so the `x64` solution configuration resolves to the `x64` project configuration. Consistent with the installer change below, which now sources binaries from `bin\x64\Release R25\` instead of `bin\Release R25\`.

### 2. `AVES/AVES_Installer.iss` diff

Substantial rework, all installer-only (Inno Setup script, doesn't touch the plugin itself):
- Version bump 1.0.0 -> 1.0.1, adds a stable `AppId` GUID (explicitly commented "NEVER change this GUID" -- needed so future installers detect/upgrade prior installs instead of creating duplicate Add/Remove Programs entries).
- Adds upgrade-in-place support: `AppMutex`, `CloseApplications=yes` (closes Revit automatically before installing), `UsePreviousAppDir=no`.
- New `PreviousInstallDetected` / `CleanupAppDataLeftovers` functions -- the latter specifically deletes a stale `AVES.addin`/`AVES` folder from `{userappdata}\Autodesk\Revit\Addins\2025\` if present. This directly matches the dual-location `.addin`/DLL bug traced earlier in this session (the "Cmd_MTQDimensions" Revit load failure investigation) -- looks like a real fix for a real problem hit during that work, not speculative.
- File sources switched from `bin\Release R25\` to `bin\x64\Release R25\`, matching the new `.slnx` x64 platform.
- Tightened welcome-message copy, `uninsrestartdelete` flags added to all `[Files]` entries.

Net read: this is a legitimate, coherent installer improvement (version bump + upgrade-in-place + a fix for a bug this session actually found), not random drift.

### 3. `Cmds_Button.cs` (deleted, uncommitted)

`git show HEAD:AVES/Commands/General/Cmds_Button.cs` -- 570 lines, single class `AVES.Cmds_Button.Cmd_Button` (namespace `AVES.Cmds_Button`, distinct from `Cmds_Button6`/`7`/`8`/etc.). Contents: a standalone auto-tag-placement command (diagonal-direction tag placement logic, `LEADER_LENGTH`/`MIN_TAG_SPACING` constants, comment/group/assembly scanning) -- reads like an early/legacy version of what became the numbered `Cmds_Button*` command set.

- **Not registered in `Application.cs`**: zero hits for `Cmds_Button.` (exact, trailing dot) or the class's full name anywhere in `Application.cs`.
- **Not referenced anywhere else**: zero hits for `AVES.Cmds_Button.` across every `.cs`/`.addin`/`.xaml` file in the repo, and no other file declares the same namespace (`AVES.Cmds_Button`) that could collide or depend on it.

Fully orphaned. The uncommitted deletion looks correct and safe to commit whenever someone decides to act on it -- nothing currently depends on this file.

### 4. `Hilti Logo` folder (untracked)

Contents: exactly one file, `AVES/Resources/Hilti Logo/company_logo.png` (17,679 bytes).

Two separate findings, pulling in different directions:
- **The build currently depends on it.** `AVES.csproj` has an explicit (non-wildcard) `<EmbeddedResource Include="Resources\Hilti Logo\company_logo.png" />` entry. Since this folder is untracked, a fresh clone of this repo would be missing this file and the build would break (or silently produce an assembly without this embedded resource, depending on MSBuild's handling of a missing literal `Include` path) -- this is a real reproducibility gap, not cosmetic.
- **Nothing in the code appears to actually load it at runtime.** Searched for `GetManifestResourceStream` calls and any reference to `company_logo`/`Hilti Logo` by name -- the only `GetManifestResourceStream` call in the codebase is `Ribbon_Utils.GetIcon`'s dynamically-computed path, which only ever targets `Resources.Icons16.*`/`Resources.Icons32.*`, never this file. The actual runtime-rendered logo throughout the plugin's WPF windows is `AvTokens.LogoBase64`, a Base64 string baked directly into `AvTokens.cs` source (confirmed byte-identical across every migrated file earlier in this session's design-system work) -- unrelated to this embedded PNG.

Read: this file is a required build input (per the `.csproj`) that's currently un-tracked, but also appears to be dead weight at runtime (embedded into the assembly, never read back out by any code path) -- possibly the original source image `AvTokens.LogoBase64` was generated from, left wired into the build after that conversion. Worth a decision either way: track it (fixes the reproducibility gap) or remove both the file and its two `.csproj` entries (if genuinely unused).

### 5. `installer_output/*.exe` files

- `AVES_Setup_v1.0.0.exe` (2,394,432 bytes) is **currently tracked in git** (`git ls-files` confirms it), and shows as deleted-but-uncommitted in the working tree.
- `AVES_Setup_v1.0.1.exe` (2,394,426 bytes) is untracked, sitting alongside it as the new build output.
- The repo's root `.gitignore` is the standard GitHub `VisualStudio.gitignore` template (ignores `bin/`, `obj/`, etc.) -- it has **no entry** for `installer_output/` or `*.exe` build artifacts. Confirmed via `find`/`cat`, no `.gitignore` in `AVES/` either.

Read: this repo has been committing built installer binaries directly (v1.0.0 is proof), and continuing that practice means every future version bump adds another ~2.3MB binary to git history permanently -- there's no cleanup path for old versions once committed this way. Worth a decision: either keep tracking installer builds as releases happen (and accept the repo grows every version), or add `installer_output/` to `.gitignore` and distribute installers some other way (e.g. GitHub Releases) instead.

## PostBuild always deploys to the Revit 2025 folder

The `PostBuild` target in `AVES.csproj` hard-codes `%AppData%\Autodesk\Revit\Addins\2025\AVES`, and it runs for every Debug configuration. A `Debug R26` build therefore overwrites the 2025 test DLL with the R26 assembly. It should use `$(RevitVersion)` in the path. Not fixed yet; until then, stick to `Debug R25` when testing in Revit 2025.
