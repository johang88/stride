// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core;
using Stride.Engine;
using Stride.Rendering;

namespace Stride.Terrain.Decals
{
    /// <summary>
    /// Keeps the decals of the <see cref="TerrainDecalComponent"/>s in the <see cref="TerrainDecalRegistry"/>, following their entities.
    /// </summary>
    public sealed class TerrainDecalProcessor : EntityProcessor<TerrainDecalComponent, TerrainDecalProcessor.DecalData>
    {
        public sealed class DecalData
        {
            internal TerrainDecal Decal;
            internal int Hash;
        }

        private TerrainDecalRegistry registry;

        public TerrainDecalProcessor() : base(typeof(TransformComponent))
        {
        }

        protected override void OnSystemAdd()
        {
            registry = Services.GetOrCreate<TerrainDecalRegistry>();
        }

        protected override DecalData GenerateComponentData(Entity entity, TerrainDecalComponent component) => new() { Decal = new TerrainDecal(component.Surface) };

        protected override bool IsAssociatedDataValid(Entity entity, TerrainDecalComponent component, DecalData associatedData) => true;

        protected override void OnEntityComponentRemoved(Entity entity, TerrainDecalComponent component, DecalData data)
        {
            registry.Remove(data.Decal);
            data.Decal.Dispose();
        }

        // After the transforms are updated, before the virtual textures render their pages
        public override void Draw(RenderContext context)
        {
            foreach (var (component, data) in ComponentDatas)
            {
                var decal = data.Decal;
                if (!component.Enabled || component.Surface == null)
                {
                    registry.Remove(decal);
                    data.Hash = 0;
                    continue;
                }

                var hash = ComputeHash(component);
                if (hash == data.Hash && decal.Registry == registry)
                    continue;
                data.Hash = hash;

                decal.Surface = component.Surface;
                decal.Order = component.Order;
                decal.Shape = component.Shape;
                registry.Add(decal);
                decal.SetRectangle(component.Entity.Transform.WorldMatrix, component.Size, component.Tiling);
            }
        }

        private static int ComputeHash(TerrainDecalComponent component)
        {
            var hash = new HashCode();
            hash.Add(component.Entity.Transform.WorldMatrix);
            hash.Add(component.Size);
            hash.Add(component.Shape);
            hash.Add(component.Tiling);
            hash.Add(component.Order);
            hash.Add(component.Surface);
            hash.Add(component.Surface.ComputeHash());
            return hash.ToHashCode();
        }
    }
}
