// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Recast;
using DotRecast.Recast.Toolset.Builder;
using Stride.BepuPhysics.Definitions;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.BepuPhysics.Navigation.Extensions;
using Stride.BepuPhysics.Systems;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;

namespace Stride.BepuPhysics.Navigation;

/// <summary>
/// Settings of a <see cref="TiledNavigationMesh"/>.
/// </summary>
[DataContract]
public sealed class TiledNavigationSettings
{
    public float CellSize { get; set; } = 0.25f;
    public float CellHeight { get; set; } = 0.2f;
    public float AgentHeight { get; set; } = 1.8f;
    public float AgentRadius { get; set; } = 0.4f;
    public float AgentMaxClimb { get; set; } = 0.5f;
    public float AgentMaxSlope { get; set; } = 45.0f;

    /// <summary>
    /// Tile size in cells (a tile is TileSize * CellSize meters wide).
    /// </summary>
    public int TileSize { get; set; } = 128;

    /// <summary>
    /// World origin of the tile grid (tiles must cover the playable area to the positive side of it).
    /// </summary>
    public Vector3 Origin { get; set; } = new(-2048, 0, -2048);

    /// <summary>
    /// Maximum number of tiles kept in memory (least recently used ones are dropped).
    /// </summary>
    public int MaxTiles { get; set; } = 1024;

    public int MaxConcurrentBuilds { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);
}

/// <summary>
/// A navigation mesh built tile by tile on demand, from the static collidables of the scene (terrains provide their
/// triangles for each tile only, see <see cref="INavigationGeometryProvider"/>). Agents request the area around them,
/// tiles are built on worker threads and added on the main thread.
/// </summary>
public sealed class TiledNavigationMesh : GameSystemBase
{
    private static readonly Stride.Core.Diagnostics.Logger Log = Stride.Core.Diagnostics.GlobalLogger.GetLogger(nameof(TiledNavigationMesh));

    private const int TileBits = 16;
    private const int MaxPathPolys = 512;

    private readonly SceneSystem sceneSystem;
    private readonly ShapeCacheSystem shapeCache;
    private readonly Dictionary<(int, int), TileState> tiles = new();
    private readonly ConcurrentQueue<(int X, int Z, DtMeshData Data, int Version, int Generation)> builtTiles = new();
    private readonly Dictionary<CollidableComponent, CachedGeometry> geometryCache = new();
    private readonly long[] pathPolys = new long[MaxPathPolys];
    private readonly DtStraightPath[] straightPath = new DtStraightPath[256];
    private DtNavMesh navMesh;
    private DtNavMeshQuery query;
    private int runningBuilds;
    // Incremented by Reset: builds started before are still counted when they finish, but dropped
    private int generation;
    private int frame;

    private sealed class TileState
    {
        public bool Building;
        public bool Built;
        public bool Dirty;
        public int Version;
        public int LastUsed;
        public long Ref;
    }

    private sealed class CachedGeometry
    {
        public Vector3[] Vertices = [];
        public int[] Indices = [];
        public BoundingBox Bounds;
        public Matrix WorldMatrix;
    }

    public TiledNavigationMesh(IServiceRegistry registry, TiledNavigationSettings settings = null) : base(registry)
    {
        Settings = settings ?? new TiledNavigationSettings();
        UpdateOrder = 19000;
        Enabled = true;
        sceneSystem = registry.GetSafeServiceAs<SceneSystem>();
        // Shared with the collidable processors, which may not exist yet
        shapeCache = registry.GetOrCreate<ShapeCacheSystem>();
        registry.AddService(this);
        Reset();
    }

    public TiledNavigationSettings Settings { get; }

    public float TileWorldSize => Settings.TileSize * Settings.CellSize;

    /// <summary>
    /// Number of tiles currently in the navigation mesh.
    /// </summary>
    public int BuiltTileCount => tiles.Values.Count(x => x.Built);

    /// <summary>
    /// Number of tiles being built.
    /// </summary>
    public int PendingTileCount => runningBuilds;

    /// <summary>
    /// Drops every tile (e.g. after loading another area).
    /// </summary>
    public void Reset()
    {
        tiles.Clear();
        geometryCache.Clear();
        generation++;
        var parameters = new DtNavMeshParams
        {
            orig = Settings.Origin.ToDotRecastVector(),
            tileWidth = TileWorldSize,
            tileHeight = TileWorldSize,
            maxTiles = 1 << TileBits,
            maxPolys = 1 << (22 - TileBits),
        };
        navMesh = new DtNavMesh();
        navMesh.Init(parameters, 6);
        query = new DtNavMeshQuery(navMesh);
    }

    /// <summary>
    /// Requests the tiles within a radius of a position (call every frame or so for active areas).
    /// </summary>
    public void RequestArea(Vector3 center, float radius)
    {
        var tileSize = TileWorldSize;
        var x0 = (int)MathF.Floor((center.X - radius - Settings.Origin.X) / tileSize);
        var z0 = (int)MathF.Floor((center.Z - radius - Settings.Origin.Z) / tileSize);
        var x1 = (int)MathF.Floor((center.X + radius - Settings.Origin.X) / tileSize);
        var z1 = (int)MathF.Floor((center.Z + radius - Settings.Origin.Z) / tileSize);
        for (int z = z0; z <= z1; z++)
        {
            for (int x = x0; x <= x1; x++)
            {
                if (x < 0 || z < 0)
                    continue;
                if (!tiles.TryGetValue((x, z), out var tile))
                    tiles[(x, z)] = tile = new TileState { Dirty = true };
                tile.LastUsed = frame;
            }
        }
    }

    /// <summary>
    /// Rebuilds the tiles overlapping a world box (e.g. a static obstacle was added or the terrain was edited).
    /// </summary>
    public void Invalidate(BoundingBox bounds)
    {
        geometryCache.Clear();
        var tileSize = TileWorldSize;
        foreach (var ((x, z), tile) in tiles)
        {
            var min = Settings.Origin + new Vector3(x * tileSize, 0, z * tileSize);
            if (bounds.Maximum.X < min.X || bounds.Minimum.X > min.X + tileSize || bounds.Maximum.Z < min.Z || bounds.Minimum.Z > min.Z + tileSize)
                continue;
            tile.Dirty = true;
        }
    }

    /// <summary>
    /// Whether the tiles under a position are built.
    /// </summary>
    public bool IsAreaReady(Vector3 position)
    {
        var (x, z) = GetTile(position);
        return tiles.TryGetValue((x, z), out var tile) && tile.Built;
    }

    /// <summary>
    /// Finds the corners of a path (start excluded, end included) over the built tiles.
    /// </summary>
    /// <returns>False if no path was found (tiles may not be built yet). A partial path ends at the closest reachable point.</returns>
    public bool TryFindPath(Vector3 start, Vector3 end, List<Vector3> corners, out bool partial)
    {
        corners.Clear();
        partial = false;
        var filter = new DtQueryDefaultFilter();
        var extents = new RcVec3f(2, 4, 2);
        var startPos = start.ToDotRecastVector();
        var endPos = end.ToDotRecastVector();
        query.FindNearestPoly(startPos, extents, filter, out var startRef, out var startNearest, out _);
        query.FindNearestPoly(endPos, extents, filter, out var endRef, out var endNearest, out _);
        if (startRef == 0)
            return false;
        if (endRef == 0)
        {
            // Wider search for targets slightly off the mesh
            query.FindNearestPoly(endPos, new RcVec3f(8, 16, 8), filter, out endRef, out endNearest, out _);
            if (endRef == 0)
                return false;
        }

        var status = query.FindPath(startRef, endRef, startNearest, endNearest, filter, pathPolys, out var polyCount, MaxPathPolys);
        if (status.Failed() || polyCount == 0)
            return false;
        partial = status.IsPartial() || pathPolys[polyCount - 1] != endRef;

        var target = endNearest;
        if (partial)
            query.ClosestPointOnPoly(pathPolys[polyCount - 1], endPos, out target, out _);

        status = query.FindStraightPath(startNearest, target, pathPolys, polyCount, straightPath, out var cornerCount, straightPath.Length, 0);
        if (status.Failed())
            return false;
        for (int i = 1; i < cornerCount; i++)
            corners.Add(straightPath[i].pos.ToStrideVector());
        return corners.Count > 0;
    }

    /// <summary>
    /// Finds the closest point on the navigation mesh.
    /// </summary>
    public bool TryGetClosestPoint(Vector3 position, Vector3 searchExtents, out Vector3 result)
    {
        query.FindNearestPoly(position.ToDotRecastVector(), searchExtents.ToDotRecastVector(), new DtQueryDefaultFilter(), out var polyRef, out var nearest, out _);
        result = nearest.ToStrideVector();
        return polyRef != 0;
    }

    public override void Update(GameTime gameTime)
    {
        frame++;

        // Add finished tiles
        while (builtTiles.TryDequeue(out var built))
        {
            runningBuilds--;
            if (built.Generation != generation || !tiles.TryGetValue((built.X, built.Z), out var tile) || tile.Version != built.Version)
                continue;
            tile.Building = false;
            if (tile.Ref != 0)
            {
                navMesh.RemoveTile(tile.Ref);
                tile.Ref = 0;
            }
            if (built.Data != null && navMesh.AddTile(built.Data, 0, 0, out var tileRef).Succeeded())
                tile.Ref = tileRef;
            tile.Built = true;
        }

        if (sceneSystem.SceneInstance == null)
            return;

        // Start builds for requested tiles, closest to recently used first
        foreach (var ((x, z), tile) in tiles.OrderByDescending(x => x.Value.LastUsed))
        {
            if (runningBuilds >= Settings.MaxConcurrentBuilds)
                break;
            if (!tile.Dirty || tile.Building || tile.LastUsed < frame - 2)
                continue;
            StartBuild(x, z, tile);
        }

        // Forget old tiles
        if (tiles.Count > Settings.MaxTiles)
        {
            foreach (var ((x, z), tile) in tiles.OrderBy(x => x.Value.LastUsed).Take(tiles.Count - Settings.MaxTiles).ToList())
            {
                if (tile.Building)
                    continue;
                if (tile.Ref != 0)
                    navMesh.RemoveTile(tile.Ref);
                tiles.Remove((x, z));
            }
        }
    }

    private (int X, int Z) GetTile(Vector3 position)
    {
        return ((int)MathF.Floor((position.X - Settings.Origin.X) / TileWorldSize), (int)MathF.Floor((position.Z - Settings.Origin.Z) / TileWorldSize));
    }

    private void StartBuild(int x, int z, TileState tile)
    {
        var settings = Settings;
        var tileSize = TileWorldSize;
        var border = settings.AgentRadius + 3 * settings.CellSize;
        var min = settings.Origin + new Vector3(x * tileSize - border, -10000, z * tileSize - border);
        var max = settings.Origin + new Vector3((x + 1) * tileSize + border, 10000, (z + 1) * tileSize + border);
        var bounds = new BoundingBox(min, max);

        var vertices = new List<Vector3>();
        var indices = new List<int>();
        CollectGeometry(bounds, vertices, indices);

        tile.Dirty = false;
        tile.Building = true;
        tile.Version++;
        var version = tile.Version;
        var buildGeneration = generation;
        runningBuilds++;

        Task.Run(() =>
        {
            DtMeshData data = null;
            try
            {
                data = BuildTile(settings, x, z, vertices, indices);
            }
            catch (Exception e)
            {
                // An empty tile; agents won't be able to walk there
                Log.Error($"Navigation tile {x},{z} failed to build", e);
            }
            builtTiles.Enqueue((x, z, data, version, buildGeneration));
        });
    }

    private void CollectGeometry(BoundingBox bounds, List<Vector3> vertices, List<int> indices)
    {
        var processor = sceneSystem.SceneInstance.Processors.Get<CollidableProcessor>();
        if (processor == null)
            return;

        for (var e = processor.ComponentDataEnumerator; e.MoveNext();)
        {
            var collidable = e.Current.Value;
            if (collidable is not StaticComponent || collidable.Collider == null)
                continue;

            if (collidable.Collider is INavigationGeometryProvider provider)
            {
                var providerBounds = provider.GetNavigationBounds(collidable);
                if (providerBounds.Intersects(in bounds))
                    provider.GetNavigationTriangles(collidable, bounds, vertices, indices);
                continue;
            }

            var cached = GetCachedGeometry(collidable);
            if (cached == null || !cached.Bounds.Intersects(in bounds))
                continue;
            for (int i = 0; i < cached.Indices.Length; i += 3)
            {
                var a = cached.Vertices[cached.Indices[i]];
                var b = cached.Vertices[cached.Indices[i + 1]];
                var c = cached.Vertices[cached.Indices[i + 2]];
                var triMin = Vector3.Min(a, Vector3.Min(b, c));
                var triMax = Vector3.Max(a, Vector3.Max(b, c));
                if (triMax.X < bounds.Minimum.X || triMin.X > bounds.Maximum.X || triMax.Z < bounds.Minimum.Z || triMin.Z > bounds.Maximum.Z)
                    continue;
                var start = vertices.Count;
                vertices.Add(a);
                vertices.Add(b);
                vertices.Add(c);
                indices.Add(start);
                indices.Add(start + 1);
                indices.Add(start + 2);
            }
        }
    }

    private CachedGeometry GetCachedGeometry(CollidableComponent collidable)
    {
        var world = collidable.Entity.Transform.WorldMatrix;
        if (geometryCache.TryGetValue(collidable, out var cached) && cached.WorldMatrix == world)
            return cached;

        var shapes = new List<BasicMeshBuffers>();
        collidable.Collider.AppendModel(shapes, shapeCache, out _);
        var transforms = new ShapeTransform[collidable.Collider.Transforms];
        collidable.Collider.GetLocalTransforms(collidable, transforms);

        world.Decompose(out _, out Matrix rotation, out var translation);
        rotation.TranslationVector = translation;

        var vertices = new List<Vector3>();
        var indices = new List<int>();
        for (int s = 0; s < shapes.Count && s < transforms.Length; s++)
        {
            var transform = transforms[s];
            Matrix.Transformation(ref transform.Scale, ref transform.RotationLocal, ref transform.PositionLocal, out var local);
            var matrix = local * rotation;
            var start = vertices.Count;
            foreach (var vertex in shapes[s].Vertices)
                vertices.Add(Vector3.TransformCoordinate(vertex.Position, matrix));
            // Stride meshes are clockwise, Recast expects counter-clockwise
            var shapeIndices = shapes[s].Indices;
            for (int i = 0; i < shapeIndices.Length; i += 3)
            {
                indices.Add(start + shapeIndices[i]);
                indices.Add(start + shapeIndices[i + 2]);
                indices.Add(start + shapeIndices[i + 1]);
            }
        }

        cached = new CachedGeometry { Vertices = vertices.ToArray(), Indices = indices.ToArray(), WorldMatrix = world };
        cached.Bounds = vertices.Count > 0 ? BoundingBox.FromPoints(cached.Vertices) : BoundingBox.Empty;
        geometryCache[collidable] = cached;
        return cached;
    }

    private static DtMeshData BuildTile(TiledNavigationSettings settings, int x, int z, List<Vector3> vertices, List<int> indices)
    {
        if (indices.Count == 0)
            return null;

        var floats = MemoryMarshal.Cast<Vector3, float>(CollectionsMarshal.AsSpan(vertices)).ToArray();
        var geom = new StrideGeomProvider(floats, indices.ToArray());

        var config = new RcConfig(
            useTiles: true,
            settings.TileSize,
            settings.TileSize,
            RcConfig.CalcBorder(settings.AgentRadius, settings.CellSize),
            RcPartitionType.WATERSHED.Value is var watershed ? RcPartitionType.OfValue(watershed) : default,
            settings.CellSize,
            settings.CellHeight,
            settings.AgentMaxSlope,
            settings.AgentHeight,
            settings.AgentRadius,
            settings.AgentMaxClimb,
            8 * 8 * settings.CellSize * settings.CellSize,
            20 * 20 * settings.CellSize * settings.CellSize,
            12.0f,
            1.3f,
            6,
            6.0f,
            1.0f,
            true,
            true,
            true,
            SampleAreaModifications.SAMPLE_AREAMOD_WALKABLE,
            buildMeshDetail: true);

        // Tile coordinates are relative to the grid origin, not to the geometry
        var bmin = settings.Origin.ToDotRecastVector();
        bmin.Y = geom.GetMeshBoundsMin().Y;
        var bmax = new RcVec3f(bmin.X + 65536, geom.GetMeshBoundsMax().Y, bmin.Z + 65536);
        var result = new RcBuilder().BuildTile(geom, config, bmin, bmax, x, z, new RcAtomicInteger(0), 1, false);
        if (result == null || result.Mesh == null || result.Mesh.npolys == 0)
            return null;

        var parameters = DemoNavMeshBuilder.GetNavMeshCreateParams(geom, settings.CellSize, settings.CellHeight, settings.AgentHeight, settings.AgentRadius, settings.AgentMaxClimb, result);
        parameters.tileX = x;
        parameters.tileZ = z;
        var data = DtNavMeshBuilder.CreateNavMeshData(parameters);
        return data != null ? DemoNavMeshBuilder.UpdateAreaAndFlags(data) : null;
    }
}
