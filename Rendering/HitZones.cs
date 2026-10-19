using Live2DPet.Cubism;
using Live2DPet.App;
using Live2DPet.Model;

namespace Live2DPet.Rendering;

/// <summary>
/// The named regions of a model that can be poked.
///
/// Nothing here is hardcoded to a character. Cubism models declare their own
/// regions in <c>.model3.json</c>:
///
///     "HitAreas": [ { "Name": "Head", "Id": "D_SomeMesh1" },
///                   { "Name": "Body", "Id": "D_SomeMesh2" } ]
///
/// Each entry names a drawable that acts as the region's shape - usually an
/// invisible quad covering the area, not the artwork itself. A model with a tail
/// declares a tail area; a model without one simply never mentions it, and
/// nothing in the engine has to know that tails exist.
///
/// Models that declare no hit areas at all still work: the region falls back to
/// the *part* the clicked drawable belongs to, which every moc carries. That is
/// a coarser answer - parts are the deformer groups, not the artist's regions -
/// but it keeps a bare model interactive instead of silent.
/// </summary>
internal sealed class HitZones
{
    private readonly CubismModel _model;
    private readonly Zone[] _areas;

    public HitZones(CubismModel model, IReadOnlyList<Model3Json.HitArea> declared)
    {
        _model = model;

        var areas = new List<Zone>();

        foreach (var area in declared)
        {
            if (string.IsNullOrWhiteSpace(area.Name) || string.IsNullOrWhiteSpace(area.Id))
            {
                continue;
            }

            var drawableIndex = model.GetDrawableIndex(area.Id);
            if (drawableIndex < 0)
            {
                Log.Warn($"hit area '{area.Name}' names drawable '{area.Id}', which the moc does not have");
                continue;
            }

            areas.Add(new Zone(area.Name!, drawableIndex));
        }

        _areas = areas.ToArray();
    }

    /// <summary>Declared areas, in the order the model lists them.</summary>
    public IReadOnlyList<Zone> Areas => _areas;

    public bool IsEmpty => _areas.Length == 0;

    /// <summary>
    /// What a person decided a part is called, if the rules file says.
    ///
    /// Regions come from the model, so before this existed a model that never
    /// declared `HitAreas` could only ever have one zone: everything is
    /// `SomePart`, `AnotherPart`, `SomeMesh_Skinning`, and nobody can write a rule
    /// against a name like that. An alias is the missing half - it does not
    /// touch the model, it just means the engine calls the part what its owner
    /// calls it, everywhere the raw name would have appeared.
    /// </summary>
    public Func<string, string?>? Aliases { get; set; }

    /// <summary>
    /// Whether the rules file already has a zone by this name.
    ///
    /// The second half of the same idea: a raw name is normally not a region,
    /// because reporting `AnotherPart` as one looks like a feature while carrying
    /// no meaning. But a file that says `AnotherPart` *is* a zone is a file whose owner
    /// decided that - a name in use is a name, whatever shape it has, and
    /// refusing it here would make a binding that looks like it works never
    /// fire.
    /// </summary>
    public Func<string, bool>? KnownNames { get; set; }

    public readonly record struct Zone(string Name, int DrawableIndex);

    /// <summary>
    /// Names the region a model space point falls in.
    ///
    /// Areas are tested in declaration order and the first match wins, matching
    /// what the Cubism samples do. The test is on geometry only, deliberately:
    /// an area shape is usually invisible, so its alpha says nothing about
    /// whether the user meant to hit it. Whether the point is on the *artwork*
    /// is a separate question, answered by <see cref="HitTester"/>.
    /// </summary>
    public string? NameAt(System.Numerics.Vector2 point, int fallbackDrawable)
    {
        foreach (var area in _areas)
        {
            if (HitTester.CoversPoint(_model.Drawables[area.DrawableIndex], point))
            {
                return area.Name;
            }
        }

        // No declared region covers this, so fall back to the part the artwork
        // belongs to. Part ids are things like "Head", "Body", "HairFront".
        if (fallbackDrawable >= 0)
        {
            var partIndex = _model.Drawables[fallbackDrawable].PartIndex;
            if ((uint)partIndex < (uint)_model.PartIds.Length)
            {
                var part = _model.PartIds[partIndex];

                // A name somebody chose is a name, whatever shape it has.
                if (Aliases?.Invoke(part) is { Length: > 0 } alias)
                {
                    return alias;
                }

                // So is a name the caller already uses: a diagnostic can offer
                // parts as they are clicked, so a rule can be written against
                // `SomePart` without anybody having to invent a prettier name
                // for it first.
                if (KnownNames?.Invoke(part) == true)
                {
                    return part;
                }

                // Only if the artist actually named it. Exports routinely leave
                // parts as "SomePart" and "SomeMesh_Skinning", and reporting
                // those as regions is worse than admitting there is no region:
                // it would look like a feature while carrying no meaning, and
                // no motion group is ever named after them.
                if (!LooksGenerated(part))
                {
                    return part;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a name is the exporter's placeholder rather than something a
    /// person chose. Deliberately narrow: it only rejects the two shapes the
    /// Cubism editor generates, so an unusual but real name survives.
    /// </summary>
    private static bool LooksGenerated(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        if (name.StartsWith("ArtMesh", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!name.StartsWith("Part", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (name.Length == 4)
        {
            return false;
        }

        for (var i = 4; i < name.Length; i++)
        {
            if (!char.IsAsciiDigit(name[i]))
            {
                return false;
            }
        }

        return true;
    }
}
