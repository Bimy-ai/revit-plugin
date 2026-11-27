using System.IO;
using Autodesk.Revit.DB;
using BimyRevit.Models;

namespace BimyRevit.Services.Import;

/// <summary>
/// Payload element types → real Revit types.
///
/// Construction categories (wall/floor/ceiling/roof) become duplicated types
/// carrying the app's LAYERED compound structure — each layer's function,
/// thickness and material — so a BIMy "Brick cavity 200" edits in Revit exactly
/// like a native type. Elements without an assigned type get per-thickness
/// generated types ("BIMy Wall 200 mm"), bucketed so a project with three wall
/// depths produces three types, not one per instance.
///
/// Opening categories (door/window) resolve to FamilySymbols: the user's own
/// template families first, the Revit content library on disk second, and a
/// remembered null when a machine has neither — the opening then degrades to a
/// plain wall opening and the summary says so.
/// </summary>
internal static class TypeFactory
{
    public static void Build(BuildContext ctx)
    {
        foreach (var t in ctx.Model.ElementTypes)
            ctx.TypeDtos[t.Id] = t;

        foreach (var t in ctx.Model.ElementTypes)
        {
            try
            {
                switch (t.Category)
                {
                    case "wall": ctx.WallTypes[t.Id] = MakeWallType(ctx, t); break;
                    case "floor": ctx.FloorTypes[t.Id] = MakeFloorType(ctx, t); break;
                    case "ceiling": ctx.CeilingTypes[t.Id] = MakeCeilingType(ctx, t); break;
                    case "roof": ctx.RoofTypes[t.Id] = MakeRoofType(ctx, t); break;
                    // door/window symbols resolve lazily, per opening — the
                    // family probe is only worth paying when a door exists.
                }
            }
            catch (Exception ex)
            {
                ctx.Warn($"Type '{t.Name ?? t.Id}': {ex.Message}");
            }
        }
    }

    // ── Walls ────────────────────────────────────────────────────────────────

    public static WallType WallTypeFor(BuildContext ctx, WallDto wall)
    {
        if (wall.TypeId is not null && ctx.WallTypes.TryGetValue(wall.TypeId, out var typed))
            return typed;

        // Untyped: one generated single-layer type per (≈5 mm bucket, material).
        var bucket = Bucket(wall.ThicknessMm);
        var key = $"@wall:{bucket}:{wall.MaterialId ?? ""}";
        if (ctx.WallTypes.TryGetValue(key, out var cached)) return cached;

        var wt = DuplicateWallBase(ctx, $"BIMy Wall {bucket} mm");
        ApplyLayers(ctx, wt.GetCompoundStructure(), cs => wt.SetCompoundStructure(cs),
            SingleLayer(bucket, wall.MaterialId), $"BIMy Wall {bucket} mm");
        ctx.WallTypes[key] = wt;
        return wt;
    }

    private static WallType MakeWallType(BuildContext ctx, ElementTypeDto t)
    {
        var wt = DuplicateWallBase(ctx, t.Name ?? "BIMy Wall");
        ApplyLayers(ctx, wt.GetCompoundStructure(), cs => wt.SetCompoundStructure(cs),
            t.Layers, t.Name ?? t.Id);
        return wt;
    }

    private static WallType DuplicateWallBase(BuildContext ctx, string name)
    {
        var doc = ctx.Doc;
        var basic = new FilteredElementCollector(doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .Where(w => w.Kind == WallKind.Basic)
            .ToList();
        var baseType =
            (doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.WallType)) as WallType)
                is { Kind: WallKind.Basic } def
            ? def
            : basic.FirstOrDefault()
              ?? throw new InvalidOperationException("The template has no basic wall type to build from.");
        return (WallType)baseType.Duplicate(UniqueTypeName(doc, typeof(WallType), name));
    }

    // ── Floors / ceilings / roofs ────────────────────────────────────────────

    public static FloorType FloorTypeFor(BuildContext ctx, SlabDto slab)
    {
        if (slab.TypeId is not null && ctx.FloorTypes.TryGetValue(slab.TypeId, out var typed))
            return typed;
        var bucket = Bucket(slab.ThicknessMm);
        var key = $"@floor:{bucket}:{slab.MaterialId ?? ""}";
        if (ctx.FloorTypes.TryGetValue(key, out var cached)) return cached;
        var ft = MakeFloorType(ctx, new ElementTypeDto
        {
            Id = key,
            Category = "floor",
            Name = $"BIMy Floor {bucket} mm",
            Layers = SingleLayer(bucket, slab.MaterialId),
        });
        ctx.FloorTypes[key] = ft;
        return ft;
    }

    private static FloorType MakeFloorType(BuildContext ctx, ElementTypeDto t)
    {
        var doc = ctx.Doc;
        var baseType =
            (doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.FloorType)) as FloorType)
            ?? new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>()
                .FirstOrDefault(f => !f.IsFoundationSlab)
            ?? throw new InvalidOperationException("The template has no floor type to build from.");
        var ft = (FloorType)baseType.Duplicate(UniqueTypeName(doc, typeof(FloorType), t.Name ?? "BIMy Floor"));
        ApplyLayers(ctx, ft.GetCompoundStructure(), cs => ft.SetCompoundStructure(cs), t.Layers, t.Name ?? t.Id);
        return ft;
    }

    public static CeilingType CeilingTypeFor(BuildContext ctx, SlabDto slab)
    {
        if (slab.TypeId is not null && ctx.CeilingTypes.TryGetValue(slab.TypeId, out var typed))
            return typed;
        var bucket = Bucket(slab.ThicknessMm);
        var key = $"@ceiling:{bucket}:{slab.MaterialId ?? ""}";
        if (ctx.CeilingTypes.TryGetValue(key, out var cached)) return cached;
        var ct = MakeCeilingType(ctx, new ElementTypeDto
        {
            Id = key,
            Category = "ceiling",
            Name = $"BIMy Ceiling {bucket} mm",
            Layers = SingleLayer(bucket, slab.MaterialId),
        });
        ctx.CeilingTypes[key] = ct;
        return ct;
    }

    private static CeilingType MakeCeilingType(BuildContext ctx, ElementTypeDto t)
    {
        var doc = ctx.Doc;
        var baseType = new FilteredElementCollector(doc)
            .OfClass(typeof(CeilingType))
            .Cast<CeilingType>()
            .OrderByDescending(c => c.GetCompoundStructure() is not null) // compound-capable first
            .FirstOrDefault()
            ?? throw new InvalidOperationException("The template has no ceiling type to build from.");
        var ct = (CeilingType)baseType.Duplicate(UniqueTypeName(doc, typeof(CeilingType), t.Name ?? "BIMy Ceiling"));
        ApplyLayers(ctx, ct.GetCompoundStructure(), cs => ct.SetCompoundStructure(cs), t.Layers, t.Name ?? t.Id);
        return ct;
    }

    public static RoofType RoofTypeFor(BuildContext ctx, RoofDto roof)
    {
        if (roof.TypeId is not null && ctx.RoofTypes.TryGetValue(roof.TypeId, out var typed))
            return typed;
        var bucket = Bucket(roof.ThicknessMm, 250);
        var key = $"@roof:{bucket}:{roof.MaterialId ?? ""}";
        if (ctx.RoofTypes.TryGetValue(key, out var cached)) return cached;
        var rt = MakeRoofType(ctx, new ElementTypeDto
        {
            Id = key,
            Category = "roof",
            Name = $"BIMy Roof {bucket} mm",
            Layers = SingleLayer(bucket, roof.MaterialId),
        });
        ctx.RoofTypes[key] = rt;
        return rt;
    }

    private static RoofType MakeRoofType(BuildContext ctx, ElementTypeDto t)
    {
        var doc = ctx.Doc;
        var baseType = new FilteredElementCollector(doc)
            .OfClass(typeof(RoofType))
            .Cast<RoofType>()
            .OrderByDescending(r => r.GetCompoundStructure() is not null)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("The template has no roof type to build from.");
        var rt = (RoofType)baseType.Duplicate(UniqueTypeName(doc, typeof(RoofType), t.Name ?? "BIMy Roof"));
        ApplyLayers(ctx, rt.GetCompoundStructure(), cs => rt.SetCompoundStructure(cs), t.Layers, t.Name ?? t.Id);
        return rt;
    }

    // ── The layer build-up ───────────────────────────────────────────────────

    private static List<TypeLayerDto> SingleLayer(double thicknessMm, string? materialId) => new()
    {
        new TypeLayerDto { Function = "Structure", MaterialId = materialId, ThicknessMm = thicknessMm },
    };

    /// <summary>
    /// Replace a duplicated type's compound structure with the payload layers.
    /// Falls back to resizing the base type's core when Revit rejects the new
    /// structure (templates can lock layer rules) — the element still builds at
    /// the right TOTAL thickness, just without the layer split.
    /// </summary>
    private static void ApplyLayers(
        BuildContext ctx,
        CompoundStructure? current,
        Action<CompoundStructure> apply,
        IReadOnlyList<TypeLayerDto>? layers,
        string label)
    {
        if (current is null) return; // curtain-ish base type — leave as duplicated
        var wanted = (layers ?? new List<TypeLayerDto>())
            .Where(l => l.ThicknessMm > 0)
            .ToList();
        if (wanted.Count == 0) return;

        try
        {
            var built = wanted.Select(l => new CompoundStructureLayer(
                ImportGeometry.Ft(l.ThicknessMm),
                FunctionOf(l),
                ctx.MaterialId(l.MaterialId))).ToList();
            var cs = CompoundStructure.CreateSimpleCompoundStructure(built);
            // Mark the thickest layer the structural core, which is what the
            // app's "Structure" function means and what keeps Revit's wrapping
            // rules sensible.
            var coreAt = 0;
            for (var i = 1; i < wanted.Count; i++)
                if (wanted[i].ThicknessMm > wanted[coreAt].ThicknessMm) coreAt = i;
            try
            {
                cs.SetNumberOfShellLayers(ShellLayerType.Exterior, coreAt);
                cs.SetNumberOfShellLayers(ShellLayerType.Interior, wanted.Count - coreAt - 1);
            }
            catch { /* single-layer or locked shells — the default is fine */ }
            apply(cs);
        }
        catch (Exception ex)
        {
            ctx.Warn($"Type '{label}': layer structure rejected ({ex.Message}) — total thickness kept.");
            try
            {
                var total = ImportGeometry.Ft(wanted.Sum(l => l.ThicknessMm));
                var coreIdx = current.GetFirstCoreLayerIndex();
                if (coreIdx < 0 || coreIdx >= current.LayerCount) coreIdx = 0;
                current.SetLayerWidth(coreIdx, total);
                apply(current);
            }
            catch { /* the base type's own thickness is the last resort */ }
        }
    }

    private static MaterialFunctionAssignment FunctionOf(TypeLayerDto l) => l.Function switch
    {
        "Structure" => MaterialFunctionAssignment.Structure,
        "Insulation" => MaterialFunctionAssignment.Insulation,
        "Air gap" => MaterialFunctionAssignment.Insulation,
        "Finish" => MaterialFunctionAssignment.Finish1,
        "Substrate" => MaterialFunctionAssignment.Substrate,
        // Revit requires Membrane layers to be zero-width; the app's membranes
        // carry real thickness, so they land as substrate instead.
        "Membrane" => l.ThicknessMm <= 0 ? MaterialFunctionAssignment.Membrane : MaterialFunctionAssignment.Substrate,
        _ => MaterialFunctionAssignment.Structure,
    };

    // ── Door / window symbols ────────────────────────────────────────────────

    /// <summary>
    /// The FamilySymbol for one opening — duplicated per distinct size/type and
    /// sized via the family's own Width/Height parameters. Null when no door or
    /// window family can be found on this machine; the caller then cuts a plain
    /// opening instead (counted, never silent).
    /// </summary>
    public static FamilySymbol? OpeningSymbolFor(BuildContext ctx, OpeningDto op)
    {
        var isDoor = op.Kind == "door";
        var dto = op.TypeId is not null && ctx.TypeDtos.TryGetValue(op.TypeId, out var t) ? t : null;
        var w = dto?.WidthMm > 0 ? dto.WidthMm : op.WidthMm;
        var h = dto?.HeightMm > 0 ? dto.HeightMm : op.HeightMm;
        var key = $"{op.Kind}:{op.TypeId ?? $"{Math.Round(w)}x{Math.Round(h)}"}";
        if (ctx.OpeningSymbols.TryGetValue(key, out var cached)) return cached;

        var baseSymbol = FindBaseSymbol(ctx, isDoor);
        if (baseSymbol is null)
        {
            ctx.OpeningSymbols[key] = null;
            return null;
        }

        FamilySymbol symbol;
        try
        {
            var name = dto?.Name is { Length: > 0 } typedName
                ? typedName
                : $"BIMy {(isDoor ? "Door" : "Window")} {Math.Round(w)}×{Math.Round(h)}";
            symbol = (FamilySymbol)baseSymbol.Duplicate(UniqueTypeName(ctx.Doc, typeof(FamilySymbol), name));
        }
        catch
        {
            symbol = baseSymbol; // duplication refused — size the shared one
        }

        var widthSet = ParamSet.TrySetFirst(symbol,
            isDoor
                ? new[] { BuiltInParameter.DOOR_WIDTH, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM }
                : new[] { BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM },
            new[] { "Width", "Rough Width" },
            ImportGeometry.Ft(w));
        var heightSet = ParamSet.TrySetFirst(symbol,
            isDoor
                ? new[] { BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM }
                : new[] { BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM },
            new[] { "Height", "Rough Height" },
            ImportGeometry.Ft(h));
        if (!widthSet || !heightSet)
            ctx.Warn($"{(isDoor ? "Door" : "Window")} type '{symbol.Name}': the family exposes no "
                + $"{(widthSet ? "height" : "width")} parameter — instances keep the family's own size.");

        try { if (!symbol.IsActive) symbol.Activate(); } catch { /* non-fatal */ }
        ctx.OpeningSymbols[key] = symbol;
        return symbol;
    }

    private static FamilySymbol? FindBaseSymbol(BuildContext ctx, bool isDoor)
    {
        var cacheKey = isDoor ? "@base:door" : "@base:window";
        if (ctx.OpeningSymbols.TryGetValue(cacheKey, out var cached)) return cached;

        var category = isDoor ? BuiltInCategory.OST_Doors : BuiltInCategory.OST_Windows;
        var symbols = new FilteredElementCollector(ctx.Doc)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(category)
            .Cast<FamilySymbol>()
            .ToList();

        // Prefer the plainest family the template ships: a flush door / a fixed
        // window, not a double garage door that happens to sort first.
        var preferred = isDoor
            ? new[] { "flush", "single", "door" }
            : new[] { "fixed", "window" };
        var found = symbols
            .OrderBy(s =>
            {
                var name = (s.Family?.Name ?? s.Name).ToLowerInvariant();
                var at = Array.FindIndex(preferred, p => name.Contains(p));
                return at < 0 ? preferred.Length : at;
            })
            .FirstOrDefault();

        found ??= LoadLibraryFamily(ctx, isDoor);
        if (found is null)
            ctx.Warn(isDoor
                ? "No door family in the template or the Revit library — doors arrive as wall openings."
                : "No window family in the template or the Revit library — windows arrive as wall openings.");

        ctx.OpeningSymbols[cacheKey] = found;
        return found;
    }

    /// <summary>The stock Revit content library, when it is installed —
    /// `%ProgramData%\Autodesk\RVT &lt;year&gt;\Libraries\**`.</summary>
    private static FamilySymbol? LoadLibraryFamily(BuildContext ctx, bool isDoor)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Autodesk", $"RVT {ctx.Doc.Application.VersionNumber}", "Libraries");
            if (!Directory.Exists(root)) return null;

            var patterns = isDoor
                ? new[] { "*Single-Flush*.rfa", "*Door-Single*.rfa" }
                : new[] { "*Fixed*.rfa", "*Window-Fixed*.rfa" };
            var folderHint = isDoor ? "door" : "window";
            foreach (var pattern in patterns)
            {
                var file = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                    .OrderByDescending(f => f.Contains(folderHint, StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
                if (file is null) continue;
                if (!ctx.Doc.LoadFamily(file, out var family) || family is null) continue;
                var symbolId = family.GetFamilySymbolIds().FirstOrDefault();
                if (symbolId is null || symbolId == ElementId.InvalidElementId) continue;
                Log.Info($"Loaded {(isDoor ? "door" : "window")} family from the Revit library: {file}");
                return ctx.Doc.GetElement(symbolId) as FamilySymbol;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Library family probe failed: {ex.Message}");
        }
        return null;
    }

    // ── Shared ───────────────────────────────────────────────────────────────

    /// <summary>≈5 mm thickness buckets, so near-identical depths share a type.</summary>
    private static int Bucket(double mm, int fallback = 200)
    {
        if (!(mm > 0)) mm = fallback;
        return Math.Max(5, (int)Math.Round(mm / 5) * 5);
    }

    private static string UniqueTypeName(Document doc, Type elementType, string baseName)
    {
        var names = new FilteredElementCollector(doc)
            .OfClass(elementType)
            .Select(t => t.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName)) return baseName;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!names.Contains(candidate)) return candidate;
        }
        return $"{baseName} ({Guid.NewGuid():N})";
    }
}
