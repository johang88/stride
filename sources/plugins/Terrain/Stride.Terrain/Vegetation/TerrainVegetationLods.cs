// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

#nullable enable
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Rendering;
using System.Collections.Generic;

namespace Stride.Terrain.Vegetation;

/// <summary>
/// Splits a model imported from one of the Models_Lods FBXs into one Model per LOD level.
///
/// Stride has no notion of an FBX LOD group, but the importer does bring every reduced mesh
/// through - a LOD chain arrives as a flat mesh list rather than being discarded. The meshes are
/// laid out LOD major, one per material slot:
///
///     [ LOD0 slot0, LOD0 slot1, LOD0 slot2, LOD1 slot0, ... ]
///
/// Two details make the obvious splits wrong, both found by running the importer over every
/// variant in the set rather than just one:
///
///   - The last mesh is the impostor card UE bakes into the chain: two triangles with a material
///     index past the real slots. It has to be dropped, the impostor here is our own baked atlas.
///   - Material order inside a LOD is not stable. Poplar_06_Forest LOD1 arrives as slots 1, 0, 2,
///     so splitting on "material index returned to zero" silently mis-groups it. Slicing by index
///     range is order independent and does not care.
/// </summary>
public static class TerrainVegetationLods
{
    /// <summary>
    /// Returns one Model per LOD level, highest detail first. Returns a single entry when the
    /// source has no LOD chain, so callers can treat both cases the same way.
    /// </summary>
    public static List<Model> Split(Model source)
    {
        // Nothing here reads source.Materials.Count. An earlier version did, both to size the LOD
        // groups and to reject UE's impostor card, and bailed out returning the source unsplit when
        // the count did not look right. That failure is silent and vicious: LodModels[0] becomes the
        // entire LOD chain, the impostor bake then renders every level stacked on one origin, and
        // every impostor comes out uniformly dark while the LOD levels still look fine because one
        // level is drawing the whole model. The mesh material indices already say everything needed,
        // and unlike the material list they come straight off the geometry.
        //
        // The impostor card is rejected on triangle count instead - it is always a two triangle
        // quad, and no real LOD ever is.
        var usable = new List<Mesh>(source.Meshes.Count);
        foreach (var mesh in source.Meshes)
        {
            if (TriangleCount(mesh) > 2)
                usable.Add(mesh);
        }

        if (usable.Count == 0)
            return [source];

        // Bucket per material slot, then order each bucket by triangle count descending. The n-th
        // entry of every bucket together forms LOD n.
        //
        // This deliberately ignores mesh order. Two earlier attempts keyed off it - a fixed stride
        // of Materials.Count, then "a repeated material index starts the next level" - and both
        // depend on the meshes arriving grouped by LOD. That holds for the raw importer output but
        // not necessarily for the compiled model, and when it does not hold the first level
        // degenerates to a single trunk mesh: LODs lose their foliage and the impostor, which bakes
        // from level 0, comes out with no leaves at all.
        //
        // Decreasing triangle count is the one property a LOD chain guarantees by construction, and
        // it survives any reordering. It also handles the irregular chains: Poplar_01/02 have only
        // three meshes on one slot where the others have four, so that slot simply drops out of the
        // last level, which is what the source data actually says.
        var buckets = new Dictionary<int, List<Mesh>>();
        foreach (var mesh in usable)
        {
            if (!buckets.TryGetValue(mesh.MaterialIndex, out var bucket))
            {
                bucket = [];
                buckets[mesh.MaterialIndex] = bucket;
            }

            bucket.Add(mesh);
        }

        var lodCount = 0;
        foreach (var bucket in buckets.Values)
        {
            bucket.Sort(static (a, b) => TriangleCount(b).CompareTo(TriangleCount(a)));
            if (bucket.Count > lodCount)
                lodCount = bucket.Count;
        }

        if (lodCount <= 1)
        {
            // A model with many meshes but no per material chain is not a LOD model. Say so rather
            // than quietly handing back everything - falling through unsplit is what put the whole
            // chain into the impostor bake once already, and it is invisible from the outside.
            if (source.Meshes.Count > buckets.Count)
            {
                GlobalLogger.GetLogger(nameof(TerrainVegetationLods))
                    .Warning($"Model has {source.Meshes.Count} meshes across {buckets.Count} materials but no LOD chain; using it unsplit.");
            }

            return [source];
        }

        var result = new List<Model>(lodCount);
        for (var lod = 0; lod < lodCount; lod++)
        {
            var model = new Model();

            foreach (var material in source.Materials)
                model.Materials.Add(material);

            foreach (var bucket in buckets.Values)
            {
                if (lod < bucket.Count)
                    model.Meshes.Add(bucket[lod]);
            }

            SetBounds(model);
            result.Add(model);
        }

        return result;
    }

    /// <summary>
    /// Distances at which each LOD level hands over to the next, ending at the impostor takeover.
    ///
    /// Authored distances win when supplied. The fallback spaces levels quadratically rather than
    /// evenly: screen coverage falls off with the square of distance, so equal spacing would burn
    /// most of the chain within a few metres of the camera and leave the cheap levels covering
    /// almost nothing.
    /// </summary>
    public static void GetLodDistances(List<float> authored, int lodCount, float impostorDistance, List<float> result)
    {
        result.Clear();

        if (authored != null && authored.Count >= lodCount)
        {
            for (var i = 0; i < lodCount; i++)
                result.Add(authored[i]);
            return;
        }

        for (var i = 0; i < lodCount; i++)
        {
            var t = (i + 1) / (float)lodCount;
            result.Add(impostorDistance * t * t);
        }

        // The last level always runs to the impostor handover, whatever the curve produced.
        result[lodCount - 1] = impostorDistance;
    }

    private static int TriangleCount(Mesh mesh) => (mesh.Draw?.DrawCount ?? 0) / 3;

    private static void SetBounds(Model model)
    {
        var box = BoundingBox.Empty;
        foreach (var mesh in model.Meshes)
        {
            var meshBox = mesh.BoundingBox;
            BoundingBox.Merge(ref box, ref meshBox, out box);
        }

        model.BoundingBox = box;
        model.BoundingSphere = BoundingSphere.FromBox(box);
    }
}
