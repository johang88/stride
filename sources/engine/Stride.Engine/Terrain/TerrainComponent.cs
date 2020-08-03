using System;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;
using Stride.Engine.Design;
using Stride.Engine;
using Stride.Rendering;
using Stride.Core.Annotations;

namespace Stride.Terrain
{
    [DataContract]
    [DefaultEntityComponentRenderer(typeof(TerrainProcessor))]
    public class TerrainComponent : EntityComponent
    {
        private TerrainData _terrain;
        /// <summary>
        /// Terrain asset
        /// </summary>
        [DataMember(10)]
        public TerrainData Terrain
        {
            get { return _terrain; }
            set
            {
                _terrain = value;

                // Force mesh to be recreated
                Invalidate(true, true);
            }
        }

        [DataMember(30)]
        public bool CastShadows { get; set; }

        [DataMember(40)]
        public Material Material { get; set; }

        [DataMember(50)]
        public TerrainTools Tools { get; set; } = new TerrainTools();

        [DataMemberIgnore]
        internal bool VerticesInvalidated { get; set; }
        [DataMemberIgnore]
        internal bool NormalsInvalidated { get; set; }

        /// <summary>
        /// Force recaluclation of heights / and normals
        /// </summary>
        public void Invalidate(bool vertices, bool normals)
        {
            VerticesInvalidated = vertices;
            NormalsInvalidated = normals;
        }
    }
}
