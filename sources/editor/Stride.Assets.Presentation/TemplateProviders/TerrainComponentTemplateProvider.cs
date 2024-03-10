using Stride.Core.Presentation.Quantum.ViewModels;
using Stride.Terrain;
using Stride.Core.Assets.Editor.View;

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
