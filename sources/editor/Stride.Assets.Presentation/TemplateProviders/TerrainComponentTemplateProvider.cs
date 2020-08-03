using Stride.Core.Assets.Editor.View.TemplateProviders;
using Stride.Engine;
using Stride.Core.Presentation.Quantum;
using Stride.Core.Presentation.Quantum.ViewModels;
using Stride.Rendering;
using Stride.Terrain;
using Stride.Core.Presentation.Quantum.View;

namespace Stride.Assets.Presentation.TemplateProviders
{
    public class TerrainComponentTemplateProvider : NodeViewModelTemplateProvider
    {
        public override string Name => "TerrainComponent";

        public override bool MatchNode(NodeViewModel node)
        {
            return node.NodeValue is TerrainComponent;
                //&& node.Parent?.NodeValue is TerrainComponent;
        }
    }
}
