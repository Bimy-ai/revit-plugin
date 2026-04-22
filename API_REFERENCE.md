# Revit API parameter reference (BIMy plug-in)

Quick-pick reference for the parameters this plug-in actually reads/writes plus the most common adjacent ones you're likely to want when extending the import. Not a full dump of `BuiltInParameter` — the enum has thousands of entries; this is the practical subset for walls, floors, ceilings, levels, views, and materials.

---

## Pattern: read / write a parameter

```csharp
// Read
var p = element.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET);
double value = p?.AsDouble() ?? 0.0;   // feet (Revit internal units)

// Write — always guard: Parameter may be null, read-only, or Set() may throw.
if (p is not null && !p.IsReadOnly)
    p.Set(MmToFeet(250));
```

This codebase wraps the pattern in `Services/ParamSet.cs` (`ParamSet.TrySet`) for int / double / ElementId / string — use it instead of inline guards.

Custom (non-builtin) parameters:

```csharp
var p = element.LookupParameter("My Custom Param");  // by display name
// or
var gp = element.get_Parameter(new Guid("..."));     // shared-parameter GUID
```

---

## Units

Revit stores everything in **internal units** regardless of project unit settings. Convert at the boundary:

| Quantity  | Internal unit  | Convert from mm                                                               |
| --------- | -------------- | ----------------------------------------------------------------------------- |
| Length    | feet           | `UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters)`                |
| Angle     | radians        | `UnitUtils.ConvertToInternalUnits(deg, UnitTypeId.Degrees)`                   |
| Area      | sq ft          | `UnitUtils.ConvertToInternalUnits(sqm, UnitTypeId.SquareMeters)`              |
| Volume    | cu ft          | `UnitUtils.ConvertToInternalUnits(cum, UnitTypeId.CubicMeters)`               |

`WallBuilder.MmToFeet` is the local helper used throughout.

---

## Direct properties vs. `BuiltInParameter`

Some things are cleaner as typed CLR properties than going through the parameter API:

| Thing                  | Direct property                                   | Equivalent parameter             |
| ---------------------- | ------------------------------------------------- | -------------------------------- |
| Level elevation        | `level.Elevation` (double, feet)                  | `LEVEL_ELEV`                     |
| Element name           | `element.Name` (write usually allowed)            | `SYMBOL_NAME_PARAM` / varies     |
| Wall flipped           | `wall.Flip()` (method, toggles)                   | no direct param                  |
| View crop box on       | `viewPlan.CropBoxActive` (bool)                   | `VIEWER_CROP_REGION`             |
| View crop box visible  | `viewPlan.CropBoxVisible` (bool)                  | `VIEWER_CROP_REGION_VISIBLE`     |
| Material color         | `material.Color` (`Autodesk.Revit.DB.Color`)      | no direct param                  |
| Material transparency  | `material.Transparency` (int 0–100)               | `MATERIAL_PARAM_TRANSPARENCY`    |

Prefer the direct property when both exist — it's checked at compile time.

---

## Walls (`Wall` instance)

| BuiltInParameter                      | UI label                    | Type        | Notes                                                                                  |
| ------------------------------------- | --------------------------- | ----------- | -------------------------------------------------------------------------------------- |
| `WALL_BASE_CONSTRAINT`                | Base Constraint             | `ElementId` | Level id. Usually set via `Wall.Create(..., levelId, ...)`.                            |
| `WALL_BASE_OFFSET`                    | Base Offset                 | `double`    | Feet. Positive lifts wall off its base level.                                          |
| `WALL_HEIGHT_TYPE`                    | Top Constraint (Up to Level)| `ElementId` | Level id for "Up to level: …". **Used by this plug-in.**                               |
| `WALL_TOP_OFFSET`                     | Top Offset                  | `double`    | Feet. Combine with `WALL_HEIGHT_TYPE`. **Used by this plug-in.**                       |
| `WALL_USER_HEIGHT_PARAM`              | Unconnected Height          | `double`    | Feet. Only effective when top is **not** level-constrained.                            |
| `WALL_KEY_REF_PARAM`                  | Location Line               | `int`       | `WallLocationLine` enum. **Used by this plug-in** (`FinishFaceExterior`).              |
| `WALL_STRUCTURAL_USAGE_PARAM`         | Structural Usage            | `int`       | `WallUsage` enum (NonBearing / Bearing / Shear / etc.).                                |
| `WALL_ATTR_ROOM_BOUNDING`             | Room Bounding               | `int`       | 0/1.                                                                                   |
| `WALL_ATTR_HEIGHT_PARAM`              | Length                      | `double`    | Usually read-only (derived from the location curve).                                   |
| `ALL_MODEL_INSTANCE_COMMENTS`         | Comments                    | `string`    | **Used by this plug-in** as the import tag.                                            |
| `ALL_MODEL_MARK`                      | Mark                        | `string`    | Free-form identifier shown on tags.                                                    |
| `PHASE_CREATED`                       | Phase Created               | `ElementId` | Phase id.                                                                              |
| `PHASE_DEMOLISHED`                    | Phase Demolished            | `ElementId` | Phase id or `ElementId.InvalidElementId`.                                              |

### Wall types (`WallType`)

| BuiltInParameter                      | UI label                    | Type          | Notes                                                        |
| ------------------------------------- | --------------------------- | ------------- | ------------------------------------------------------------ |
| `WALL_ATTR_WIDTH_PARAM`               | Width (total thickness)     | `double`      | Read-only; driven by `CompoundStructure`.                    |
| `FUNCTION_PARAM`                      | Function                    | `int`         | `WallFunction` enum (Interior / Exterior / …).               |
| `ALL_MODEL_TYPE_COMMENTS`             | Type Comments               | `string`      | **Used by this plug-in** as the imported-type brand.         |
| `ALL_MODEL_TYPE_MARK`                 | Type Mark                   | `string`      |                                                              |
| `ALL_MODEL_URL`                       | URL                         | `string`      |                                                              |
| `ALL_MODEL_MANUFACTURER`              | Manufacturer                | `string`      |                                                              |
| `ALL_MODEL_MODEL`                     | Model                       | `string`      |                                                              |
| `ALL_MODEL_COST`                      | Cost                        | `double`      |                                                              |
| `STRUCTURAL_MATERIAL_PARAM`           | Structural Material         | `ElementId`   | Material id.                                                 |

The structural layer's thickness + material is on the type's `CompoundStructure`, not on a parameter. See `WallTypeProvider.ApplySingleLayer` for the pattern.

---

## Floors (`Floor`)

| BuiltInParameter                      | UI label                    | Type        | Notes                                                             |
| ------------------------------------- | --------------------------- | ----------- | ----------------------------------------------------------------- |
| `LEVEL_PARAM`                         | Level                       | `ElementId` | Usually set via `Floor.Create` level argument.                    |
| `FLOOR_HEIGHTABOVELEVEL_PARAM`        | Height Offset From Level    | `double`    | Feet.                                                             |
| `FLOOR_PARAM_IS_STRUCTURAL`           | Structural                  | `int`       | 0/1.                                                              |
| `FLOOR_ATTR_THICKNESS_PARAM`          | Thickness                   | `double`    | Derived; read on the instance.                                    |
| `ROOM_BOUNDING`                       | Room Bounding               | `int`       | 0/1.                                                              |
| `ALL_MODEL_INSTANCE_COMMENTS`         | Comments                    | `string`    | **Used by this plug-in.**                                         |

`FloorType` shares the common type params (`ALL_MODEL_TYPE_COMMENTS`, `ALL_MODEL_TYPE_MARK`, etc.).

---

## Ceilings (`Ceiling`)

| BuiltInParameter                      | UI label                    | Type        | Notes                                                             |
| ------------------------------------- | --------------------------- | ----------- | ----------------------------------------------------------------- |
| `LEVEL_PARAM`                         | Level                       | `ElementId` |                                                                   |
| `CEILING_HEIGHTABOVELEVEL_PARAM`      | Height Offset From Level    | `double`    | Feet. **Used by this plug-in.**                                   |
| `CEILING_ATTR_DEFAULT_THICKNESS_PARAM`| Thickness                   | `double`    | Usually read-only on the instance (comes from the type).          |
| `CEILING_ATTR_SYSTEM_NAME_KEY`        | System Name                 | `string`    | For sloped / suspended systems.                                   |
| `ALL_MODEL_INSTANCE_COMMENTS`         | Comments                    | `string`    | **Used by this plug-in.**                                         |

`CeilingType` shares the common type params (see `CeilingTypeProvider`).

---

## Levels (`Level`)

Almost never go through parameters — use direct properties:

```csharp
level.Elevation = UnitUtils.ConvertToInternalUnits(3000, UnitTypeId.Millimeters);
level.Name      = "Level 1";
```

Useful parameters when you *need* them:

| BuiltInParameter                      | UI label                 | Type      | Notes                                       |
| ------------------------------------- | ------------------------ | --------- | ------------------------------------------- |
| `LEVEL_ELEV`                          | Elevation                | `double`  | Prefer `level.Elevation`.                   |
| `LEVEL_IS_BUILDING_STORY`             | Building Story           | `int`     | 0/1. Affects schedules / section tagging.   |
| `LEVEL_IS_STRUCTURAL`                 | Structural               | `int`     | 0/1.                                        |

---

## Views (`View`, `ViewPlan`, `View3D`)

Names are usually set via `view.Name = "..."` (wrapped in try/catch in `RevitLookup.TryRenameView` because Revit enforces uniqueness).

| BuiltInParameter                      | UI label                    | Type        | Notes                                              |
| ------------------------------------- | --------------------------- | ----------- | -------------------------------------------------- |
| `VIEW_NAME`                           | View Name                   | `string`    | Prefer `view.Name`.                                |
| `VIEW_DISCIPLINE`                     | Discipline                  | `int`       | `ViewDiscipline` enum.                             |
| `VIEW_DETAIL_LEVEL`                   | Detail Level                | `int`       | `ViewDetailLevel` enum (Coarse / Medium / Fine).   |
| `VIEW_SCALE`                          | View Scale                  | `int`       | 1 : *n*.                                           |
| `VIEWER_CROP_REGION`                  | Crop View                   | `int`       | 0/1. Prefer `view.CropBoxActive`.                  |
| `VIEWER_CROP_REGION_VISIBLE`          | Crop Region Visible         | `int`       | 0/1. Prefer `view.CropBoxVisible`.                 |
| `VIEWER_BOUND_OFFSET_TOP`             | View Range: Top             | `double`    | Feet.                                              |
| `VIEWER_BOUND_OFFSET_BOTTOM`          | View Range: Bottom          | `double`    | Feet.                                              |
| `PLAN_VIEW_LEVEL`                     | Associated Level            | `string`    | Level name. Typically read-only.                   |

For associating a plan to a level on creation, use `ViewPlan.Create(doc, viewFamilyTypeId, levelId)`; see `RevitLookup.CreateLevel`.

---

## Materials (`Material`)

Color and graphics are direct properties; metadata is on parameters:

```csharp
mat.Color                            = new Color(217, 70, 239);
mat.UseRenderAppearanceForShading   = false;
mat.Transparency                     = 0;
mat.SurfaceForegroundPatternId       = solidFillPatternId;
mat.SurfaceForegroundPatternColor    = mat.Color;
mat.CutForegroundPatternId           = solidFillPatternId;
mat.CutForegroundPatternColor        = mat.Color;
```

| BuiltInParameter                      | UI label                    | Type        | Notes                                                     |
| ------------------------------------- | --------------------------- | ----------- | --------------------------------------------------------- |
| `ALL_MODEL_DESCRIPTION`               | Description                 | `string`    | **Used by this plug-in** to brand imported materials.     |
| `MATERIAL_PARAM_TRANSPARENCY`         | Transparency                | `int`       | Prefer `mat.Transparency`.                                |
| `MATERIAL_PARAM_CLASS`                | Class                       | `string`    |                                                           |
| `MATERIAL_PARAM_KEYWORDS`             | Keywords                    | `string`    |                                                           |

---

## Universal parameters (on pretty much every element / type)

| BuiltInParameter                      | UI label           | Type        | Where it lives                              |
| ------------------------------------- | ------------------ | ----------- | ------------------------------------------- |
| `ALL_MODEL_INSTANCE_COMMENTS`         | Comments           | `string`    | Instance.                                   |
| `ALL_MODEL_MARK`                      | Mark               | `string`    | Instance.                                   |
| `ALL_MODEL_TYPE_COMMENTS`             | Type Comments      | `string`    | Type.                                       |
| `ALL_MODEL_TYPE_MARK`                 | Type Mark          | `string`    | Type.                                       |
| `ALL_MODEL_TYPE_NAME`                 | Type Name          | `string`    | Type. Usually read-only — use `type.Name`.  |
| `ALL_MODEL_DESCRIPTION`               | Description        | `string`    | Type.                                       |
| `ALL_MODEL_URL`                       | URL                | `string`    | Type.                                       |
| `ALL_MODEL_MANUFACTURER`              | Manufacturer       | `string`    | Type.                                       |
| `ALL_MODEL_MODEL`                     | Model              | `string`    | Type.                                       |
| `ALL_MODEL_COST`                      | Cost               | `double`    | Type.                                       |
| `ALL_MODEL_KEYNOTE`                   | Keynote            | `string`    | Type.                                       |
| `ALL_MODEL_IMAGE`                     | Type Image         | `ElementId` | Type. References an `ImageType`.            |
| `PHASE_CREATED`                       | Phase Created      | `ElementId` | Instance (model elements).                  |
| `PHASE_DEMOLISHED`                    | Phase Demolished   | `ElementId` | Instance (model elements).                  |

---

## Gotchas

- **Always inside an open `Transaction`.** Setting any parameter outside a transaction throws.
- `Parameter.IsReadOnly` can be true even for writable-looking params (e.g. type-driven values on instances, computed lengths). Check before `Set`.
- `Set(double)` always takes **internal units** (feet / radians / …). Never pass mm or degrees directly.
- `Set(ElementId)` requires the referenced element to exist in the same document.
- Parameter storage type matters: use `p.StorageType` (`Double` / `Integer` / `String` / `ElementId`) when you're unsure. `AsValueString()` returns a formatted display string but you can't set with it reliably — Revit may not parse mixed-unit input.
- `LookupParameter(name)` matches by the **localized** display name. For shared parameters, prefer the GUID overload.
- Warnings fired during `Set` are caught by the transaction's `IFailuresPreprocessor` — see `SuppressWarningsPreprocessor` in `ImportRunner`.

---

## Where the plug-in actually sets parameters

| File                                    | What it sets                                                                       |
| --------------------------------------- | ---------------------------------------------------------------------------------- |
| `Services/WallBuilder.cs`               | `WALL_KEY_REF_PARAM`, `WALL_HEIGHT_TYPE`, `WALL_TOP_OFFSET`, instance comments.    |
| `Services/WallTypeProvider.cs`          | `ALL_MODEL_TYPE_COMMENTS` on imported wall types.                                  |
| `Services/FloorBuilder.cs`              | Instance comments on imported floors.                                              |
| `Services/FloorTypeProvider.cs`         | `ALL_MODEL_TYPE_COMMENTS` on imported floor types.                                 |
| `Services/CeilingBuilder.cs`            | `CEILING_HEIGHTABOVELEVEL_PARAM`, instance comments.                               |
| `Services/CeilingTypeProvider.cs`       | `ALL_MODEL_TYPE_COMMENTS` on imported ceiling types.                               |
| `Services/ImportMaterialProvider.cs`    | `ALL_MODEL_DESCRIPTION` + direct `Color` / pattern properties on materials.        |
| `Services/RevitLookup.cs`               | `level.Elevation`, `level.Name`, `view.Name` (direct properties only).             |
