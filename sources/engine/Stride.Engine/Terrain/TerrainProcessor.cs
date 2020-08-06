using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Collections;
using Stride.Core.Mathematics;
using Stride.Core.Shaders.Ast;
using Stride.Core.Threading;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.LightProbes;
using System;
using System.Collections.Generic;
using System.Text;

namespace Stride.Terrain
{
    public class TerrainProcessor : EntityProcessor<TerrainComponent, TerrainRenderData>, IEntityComponentRenderProcessor
    {
        public VisibilityGroup VisibilityGroup { get; set; }

        protected override TerrainRenderData GenerateComponentData([NotNull] Entity entity, [NotNull] TerrainComponent component)
        {
            return new TerrainRenderData
            {
            };
        }

        private void DestroyMesh(TerrainRenderData data)
        {
            data.ModelComponent?.Entity?.Remove(data.ModelComponent);

            if (data.Mesh != null)
            {
                var meshDraw = data.Mesh.Draw;
                meshDraw.IndexBuffer.Buffer.Dispose();
                meshDraw.VertexBuffers[0].Buffer.Dispose();

                data.Vertices = null;
                data.Mesh = null;

                foreach (var splatMap in data.SplatMaps.Values)
                {
                    splatMap.Texture.Dispose();
                }

                data.SplatMaps.Clear();
            }
        }

        protected override void OnEntityComponentRemoved(Entity entity, [NotNull] TerrainComponent component, [NotNull] TerrainRenderData data)
        {
            base.OnEntityComponentRemoved(entity, component, data);

            DestroyMesh(data);
        }

        public override void Draw(RenderContext context)
        {
            base.Draw(context);

            var graphicsDevice = Services.GetService<IGraphicsDeviceService>().GraphicsDevice;

            Dispatcher.ForEach(ComponentDatas, pair =>
            {
                var component = pair.Key;
                var data = pair.Value;

                ProcessComponent(graphicsDevice, component, data);
            });
        }

        private void ProcessComponent(GraphicsDevice graphicsDevice, TerrainComponent component, TerrainRenderData data)
        {
            if (component.Terrain == null || component.Terrain.Size.X <= 0.0f || component.Terrain.Size.Y <= 0.0f || component.Terrain.Size.Z <= 0.0f || component.Terrain.Heightmap == null)
            {
                DestroyMesh(data);
                return;
            }

            var game = Services.GetService<IGame>();
            var graphicsContext = game.GraphicsContext;

            // TODO: Get rid of the model component? It's only their for picking support which should be solvable without it ...
            // and the editing would be nicer without that darn wireframe :D

            // Sync material
            var material = component.Material ?? component.Terrain.Material;
            if (data.ModelComponent.Materials.Count == 0 || data.ModelComponent.Materials[0] != material)
            {
                data.ModelComponent.Materials.Clear();
                data.ModelComponent.Materials.Add(0, material);
            }

            // Allocate and update splat maps
            data.AllocateSplatMaps(graphicsDevice, component);
            data.UpdateSplatMaps(graphicsContext, component);

            // Sync splat maps to material
            foreach (var splatMap in data.SplatMaps.Values)
            {
                material.Passes[0].Parameters.Set(splatMap.ParameterKey, splatMap.Texture);
            }

            data.ModelComponent.IsShadowCaster = component.CastShadows;

            // Check if mesh has to (re)created or if vertex data has to be updated
            if (data.IsDirty(component))
            {
                data.Update(component);
                component.NormalsInvalidated = false;

                DestroyMesh(data);

                CreateMeshFromHeightMap(graphicsDevice, component.Terrain, data);
                data.ModelComponent.Model.Meshes[0] = data.Mesh;
                component.Entity.Add(data.ModelComponent);
            }
            else if (component.VerticesInvalidated || component.NormalsInvalidated || data.InvalidatesIndices.Count > 0)
            {
                // Update vertex buffer
                UpdateVertexData(graphicsContext, component.Terrain, data, component.VerticesInvalidated, component.NormalsInvalidated);

                component.VerticesInvalidated = false;
                component.NormalsInvalidated = false;
            }
        }

        private void UpdateVertexData(GraphicsContext graphicsContext, TerrainData terrain, TerrainRenderData renderData, bool updateHeights, bool updateNormals)
        {
            var tessellationX = terrain.Resolution.X;
            var tessellationY = terrain.Resolution.Y;

            if (updateHeights || updateNormals)
            {
                if (updateHeights)
                    UpdateVertexHeights(terrain, tessellationX, tessellationY, renderData.Vertices);
                if (updateNormals)
                    UpdateVertexNormals(terrain, tessellationX, tessellationY, renderData.Vertices);
            }
            else if (renderData.InvalidatesIndices.Count > 0)
            {
                UpdateInvalidatesIndices(terrain, tessellationX, tessellationY, renderData.Vertices, renderData.InvalidatesIndices);
            }

            renderData.InvalidatesIndices.Clear();

            renderData.VertexBuffer.SetData(graphicsContext.CommandList, renderData.Vertices);
        }

        /// <summary>
        /// Creates a tesselated plane for the terrain with normals and tangents
        /// Vertex Y position is retrieved from the height map
        /// </summary>
        /// <param name="graphicsDevice"></param>
        /// <param name="size"></param>
        /// <param name="heightmap"></param>
        /// <returns></returns>
        private void CreateMeshFromHeightMap(GraphicsDevice graphicsDevice, TerrainData terrain, TerrainRenderData renderData)
        {
            var tessellationX = terrain.Resolution.X;
            var tessellationY = terrain.Resolution.Y;
            var columnCount = (tessellationX + 1);
            var rowCount = (tessellationY + 1);
            var vertices = new VertexPositionNormalTangentTexture[columnCount * rowCount];
            var indices = new int[tessellationX * tessellationY * 6];

            SetVertexData(terrain, tessellationX, tessellationY, vertices);

            var points = new Vector3[columnCount * rowCount];
            for (var i = 0; i < vertices.Length; i++)
            {
                points[i] = vertices[i].Position;
            }

            var indexCount = 0;
            for (var y = 0; y < tessellationY; y++)
            {
                for (var x = 0; x < tessellationX; x++)
                {
                    var vbase = columnCount * y + x;
                    indices[indexCount++] = (vbase + 1);
                    indices[indexCount++] = (vbase + 1 + columnCount);
                    indices[indexCount++] = (vbase + columnCount);
                    indices[indexCount++] = (vbase + 1);
                    indices[indexCount++] = (vbase + columnCount);
                    indices[indexCount++] = (vbase);
                }
            }

            var vertexBuffer = Stride.Graphics.Buffer.Vertex.New(graphicsDevice, vertices, GraphicsResourceUsage.Dynamic);
            var indexBuffer = Stride.Graphics.Buffer.Index.New(graphicsDevice, indices);

            renderData.Vertices = vertices;
            renderData.VertexBuffer = vertexBuffer;
            renderData.Mesh = new Stride.Rendering.Mesh
            {
                Draw = new Stride.Rendering.MeshDraw
                {
                    PrimitiveType = Stride.Graphics.PrimitiveType.TriangleList,
                    DrawCount = indices.Length,
                    IndexBuffer = new IndexBufferBinding(indexBuffer, true, indices.Length),
                    VertexBuffers = new[] { new VertexBufferBinding(vertexBuffer, VertexPositionNormalTangentTexture.Layout, vertexBuffer.ElementCount) },
                },
                BoundingBox = BoundingBox.FromPoints(points),
                BoundingSphere = BoundingSphere.FromPoints(points)
            };
        }

        private static void SetVertexData(TerrainData terrain, int tessellationX, int tessellationY, VertexPositionNormalTangentTexture[] vertices)
        {
            var size = terrain.Size;

            var deltaX = size.X / tessellationX;
            var deltaY = size.Z / tessellationY;

            size.X /= 2.0f;
            size.Z /= 2.0f;

            var vertexCount = 0;

            for (var y = 0; y < (tessellationY + 1); y++)
            {
                for (var x = 0; x < (tessellationX + 1); x++)
                {
                    var height = terrain.GetHeightAt(x, y) * size.Y;

                    var position = new Vector3(-size.X + deltaX * x, height, -size.Z + deltaY * y);
                    var normal = terrain.GetNormal(x, y, size.Y);
                    var tangent = terrain.GetTangent(x, y);
                    var texCoord = new Vector2(x / (float)tessellationX, y / (float)tessellationY);

                    vertices[vertexCount++] = new VertexPositionNormalTangentTexture(position, normal, tangent, texCoord);
                }
            }
        }

        private static void UpdateVertexHeights(TerrainData terrain, int tessellationX, int tessellationY, VertexPositionNormalTangentTexture[] vertices)
        {
            var vertexCount = 0;
            for (var y = 0; y < (tessellationY + 1); y++)
            {
                for (var x = 0; x < (tessellationX + 1); x++)
                {
                    vertices[vertexCount].Position.Y = terrain.GetHeightAt(x, y) * terrain.Size.Y;
                    vertexCount++;
                }
            }
        }

        private static void UpdateVertexNormals(TerrainData terrain, int tessellationX, int tessellationY, VertexPositionNormalTangentTexture[] vertices)
        {
            var vertexCount = 0;
            for (var y = 0; y < (tessellationY + 1); y++)
            {
                for (var x = 0; x < (tessellationX + 1); x++)
                {
                    vertices[vertexCount].Normal = terrain.GetNormal(x, y, terrain.Size.Y);
                    vertexCount++;
                }
            }
        }

        private static void UpdateInvalidatesIndices(TerrainData terrain, int tessellationX, int tessellationY, VertexPositionNormalTangentTexture[] vertices, HashSet<int> invalidatesIndices)
        {
            var vertexCount = 0;
            for (var y = 0; y < (tessellationY + 1); y++)
            {
                for (var x = 0; x < (tessellationX + 1); x++)
                {
                    var index = y * terrain.Resolution.X + x;
                    if (invalidatesIndices.Contains(index))
                    {
                        vertices[vertexCount].Position.Y = terrain.GetHeightAt(x, y) * terrain.Size.Y;
                        vertices[vertexCount].Normal = terrain.GetNormal(x, y, terrain.Size.Y);
                    }
                    vertexCount++;
                }
            }
        }

        internal void Invalidate(TerrainComponent editableTerain, HashSet<int> editedIndices, bool splatMaps)
        {
            if (ComponentDatas.TryGetValue(editableTerain, out var renderData))
            {
                foreach (var index in editedIndices)
                {
                    renderData.InvalidatesIndices.Add(index);
                }

                renderData.SplatMapsInvalidated |= splatMaps;
            }
        }
    }
}
