using System.Globalization;

namespace BuilderPro.Catalog.Api.Domain;

public enum AttributeKind { Text, Number, Choice }

/// <summary>One attribute a product of a category may (or must) have.</summary>
public sealed record AttributeDefinition(string Key, string Label, AttributeKind Kind, bool Required = false, IReadOnlyList<string>? Options = null, string? Unit = null);

/// <summary>A typical project size for comparing products (2k), in the category's DefaultUnit.</summary>
public sealed record TypicalSize(decimal Quantity, string Description);

/// <summary>
/// A product category: what it's called, how it's usually sold, and which attributes describe it.
/// Scene names the default visualizer scene (2l) a product of this category is shown in.
/// Typical is the project size compare (2k) prices every product at; null = no typical size.
/// </summary>
public sealed record CategoryDefinition(string Id, string Name, string DefaultUnit, string Scene, IReadOnlyList<AttributeDefinition> Attributes, TypicalSize? Typical = null);

/// <summary>
/// The fixed category list (2h). Platform-owned on purpose: compare (2k), the visualizer (2l) and the
/// agents (3a) all rely on these attribute keys meaning the same thing for every retailer.
/// Retailers choose which categories they sell; they don't invent new attribute keys.
/// </summary>
public static class Categories
{
    private static AttributeDefinition Choice(string key, string label, bool required, params string[] options) =>
        new(key, label, AttributeKind.Choice, required, options);
    private static AttributeDefinition Number(string key, string label, string unit, bool required = false) =>
        new(key, label, AttributeKind.Number, required, null, unit);
    private static AttributeDefinition Text(string key, string label, bool required = false) =>
        new(key, label, AttributeKind.Text, required);

    public static readonly IReadOnlyList<CategoryDefinition> All =
    [
        new("countertops", "Countertops", "sqft", "kitchen",
        [
            Choice("material", "Material", true, "quartz", "granite", "marble", "butcher-block", "laminate", "solid-surface"),
            Text("color", "Colour", true),
            Choice("finish", "Finish", false, "polished", "honed", "leathered", "matte", "oiled"),
            Number("thicknessCm", "Thickness", "cm"),
        ], new(30m, "an average kitchen (30 sq ft)")),
        new("roofing", "Roofing", "bundle", "house-roof",
        [
            Choice("type", "Type", true, "shingle", "underlayment", "flashing", "ventilation", "accessory"),
            Choice("style", "Shingle style", false, "architectural", "3-tab", "designer"),
            Text("color", "Colour"),
            Number("windRatingMph", "Wind rating", "mph"),
            Text("coverage", "Coverage"),
        ], new(72m, "a 24-square roof (72 bundles)")),
        new("sheds", "Sheds", "each", "backyard",
        [
            Choice("material", "Material", true, "wood", "resin", "metal"),
            Text("size", "Size", true),
            Choice("roofStyle", "Roof style", false, "gable", "gambrel", "lean-to"),
            Choice("floorIncluded", "Floor included", false, "yes", "no"),
        ], new(1m, "one shed")),
        new("flooring", "Flooring", "sqft", "room-floor",
        [
            Choice("material", "Material", true, "vinyl-plank", "laminate", "tile", "hardwood", "carpet"),
            Text("color", "Colour", true),
            Number("thicknessMm", "Thickness", "mm"),
        ], new(200m, "a 200 sq ft room")),
        new("decking", "Decking", "each", "backyard",
        [
            Choice("material", "Material", true, "cedar", "pressure-treated", "composite"),
            Text("color", "Colour"),
            Number("lengthFt", "Board length", "ft"),
        ], new(60m, "a 12 x 16 ft deck (about 60 boards)")),
        new("cabinets", "Cabinets", "each", "kitchen",
        [
            Choice("style", "Door style", true, "shaker", "flat-panel", "raised-panel"),
            Text("color", "Colour", true),
            Number("widthIn", "Width", "in"),
        ], new(10m, "a 10-cabinet kitchen")),
        new("paint", "Paint", "each", "room-wall",
        [
            Choice("finish", "Finish", true, "flat", "eggshell", "satin", "semi-gloss", "gloss"),
            Text("color", "Colour", true),
            Number("coverageSqFt", "Coverage per gallon", "sq ft"),
        ], new(2m, "one room, two coats (2 gallons)")),
        new("windows-doors", "Windows & doors", "each", "house-front",
        [
            Choice("type", "Type", true, "double-hung", "casement", "sliding", "entry-door", "patio-door"),
            Choice("frame", "Frame", false, "vinyl", "wood", "fiberglass", "aluminium"),
            Text("size", "Size"),
        ], new(1m, "one opening")),
        new("fencing", "Fencing", "each", "backyard",
        [
            Choice("material", "Material", true, "cedar", "vinyl", "chain-link", "aluminium"),
            Number("heightFt", "Height", "ft"),
            Number("panelWidthFt", "Panel width", "ft"),
        ], new(12m, "about 100 ft of fence (12 panels)")),
        new("labour", "Installation & labour", "hour", "none",
        [
            Choice("trade", "Trade", true, "general", "roofing", "countertops", "flooring", "painting", "carpentry"),
        ]),
        new("other", "Other", "each", "none", []),
    ];

    private static readonly Dictionary<string, CategoryDefinition> ById = All.ToDictionary(c => c.Id, StringComparer.Ordinal);

    public static CategoryDefinition? Find(string? id) => id is not null && ById.TryGetValue(id, out var c) ? c : null;

    /// <summary>Every problem with a product's category and attributes (empty = valid).</summary>
    public static IReadOnlyList<string> Validate(string category, IReadOnlyDictionary<string, string> attributes)
    {
        var def = Find(category);
        if (def is null) return [$"unknown category '{category}'"];

        var errors = new List<string>();
        var defs = def.Attributes.ToDictionary(a => a.Key, StringComparer.Ordinal);
        foreach (var (key, value) in attributes)
        {
            if (!defs.TryGetValue(key, out var a)) { errors.Add($"'{key}' is not an attribute of {category}"); continue; }
            if (string.IsNullOrWhiteSpace(value) || value.Length > 120) { errors.Add($"'{key}' must be 1-120 characters"); continue; }
            if (a.Kind == AttributeKind.Choice && !a.Options!.Contains(value, StringComparer.Ordinal))
                errors.Add($"'{key}' must be one of {string.Join(", ", a.Options!)}");
            if (a.Kind == AttributeKind.Number && !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                errors.Add($"'{key}' must be a number");
        }
        errors.AddRange(def.Attributes.Where(a => a.Required && !attributes.ContainsKey(a.Key)).Select(a => $"'{a.Key}' is required for {category}"));
        return errors;
    }
}
