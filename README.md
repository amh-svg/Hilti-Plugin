# AVES Revit Plugin

A productivity add-in for Autodesk Revit that automates common family management, sheet creation, dimensioning, and tagging workflows.

---

## Installation

1. Download the latest installer from the [Releases](https://github.com/amh-svg/first-repo-aves1/releases) page
2. Run `AVES_Setup_v1.0.1.exe`
3. The installer will automatically detect your Revit version and install the plugin
4. Restart Revit — the **AVES** tab will appear in the ribbon

**Supported Revit versions:** 2025

---

## Ribbon Overview

### 🗂️ Families

| Button | Description |
|---|---|
| **Add Shared Parameter** | Adds a shared parameter to selected families |
| **Remove Shared Parameters** | Removes shared parameters from selected families |
| **Fill Family Parameters** | Populates parameter values on selected family instances |
| **Rename Families Parameters** | Renames families and types based on the `Item Number` and `PMO_Description` parameters. Target format: `{Item Number} _ {PMO_Description}` |
| **Part List Format** | Lets the user pick one or more assemblies, then generates merged part-number/description strings and sequential row numbers for their Generic Model members, writing the results back onto the elements |
| **Assembly Weight** | Lets the user pick assemblies and a source weight parameter, totals the weight of each assembly's members, and writes the result to a chosen parameter on that assembly's sheet(s) |
| **Export Families** | Exports the parent Families of the currently selected family instances as .rfa files to a user-chosen folder |
| **Export GLB + BOM** | In the Family Editor, exports the active family as a GLB with one named node per subpart, plus a BOM JSON with the subpart quantities, for the current type or all types. Optional Meshopt compression needs Node.js |
| **Revisions & Title Block Fill** | Opens a sheet picker/tool window where the user selects sheets, chooses revisions to add, and resolves title block field values to apply |
| **Assembly Tag Completeness Checker** | Lets the user pick assembly views, scans the assemblies shown in them for family types with untagged members, and displays a report of which types are missing tags in each view |

---

### 🧰 Parameter & Data Tools

| Button | Description |
|---|---|
| **Batch Shared Parameters** | Adds a chosen set of shared parameters to families/categories in bulk, letting the user pick a group, the target parameters, binding type, and whether they're instance or type parameters |
| **Batch Remove Parameters** | Removes a chosen set of parameters from families/categories in bulk and reports how many were removed per family |
| **CSV Importer** | Imports a CSV keyed by "Item Number" and writes its columns as parameter values onto the types of the selected elements, logging matched/unmatched item numbers |

---

### 📄 Drawing Automations

| Button | Description |
|---|---|
| **Vendor Drawings** | Auto-generates a two-sheet (A3) vendor drawing set per assembly, with 3D and orthographic detail views placed from user-picked view templates and a titleblock |
| **Assembly Drawings** | Auto-generates a one-sheet vendor drawing per assembly that additionally clusters and calls out bolts by family and direction |
| **MT Drawings** | Auto-generates a single-sheet (A1) vendor drawing per assembly with a condensed set of 3D/orthographic views |

---

### 📐 Auto-Dimension

| Button | Description |
|---|---|
| **MT Drawings** | Adds full chain dimensioning (bay segments + overall) to each 2D view associated with the selected assemblies |
| **Vendor Drawings** | Adds simplified dimensioning — one overall Width/Height/Depth dimension per axis per view, with no bay/segment chains |

---

### 🏷️ Auto Tag

| Button | Description |
|---|---|
| **MT Tag** | Lets the user pick a Generic Model tag family and one or more 3D views on sheets, then auto-places tags on view elements with collision-avoiding placement (edge clearance, spacing, and tall/wide element handling) |
| **Vendor Tag** | Same batch view/tag picker and auto-placement logic as MT Tag, also targeting Generic Model tag families |

---

## Requirements

- Autodesk Revit 2022–2025
- Windows 10 or later
- .NET 8.0 or later

## License

© AVES. All rights reserved.
