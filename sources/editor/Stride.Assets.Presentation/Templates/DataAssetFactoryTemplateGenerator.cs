// Copyright (c) Stride contributors (https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Templates;
using Stride.Core.Presentation.Services;
using Stride.Core.Reflection;
using Stride.Core.Translation;

namespace Stride.Assets.Presentation.Templates
{
    public class DataAssetFactoryTemplateGenerator : AssetFactoryTemplateGenerator
    {
        private static readonly PropertyKey<Type> TypeKey = new PropertyKey<Type>("TypeKey", typeof(DataAssetFactoryTemplateGenerator));
        public new static readonly DataAssetFactoryTemplateGenerator Default = new DataAssetFactoryTemplateGenerator();

        public static readonly Guid TemplateId = new Guid("DF9320B9-F38A-457A-882B-C1031C9AC869");

        public override bool IsSupportingTemplate(TemplateDescription templateDescription)
        {
            if (templateDescription == null) throw new ArgumentNullException(nameof(templateDescription));
            return templateDescription.Id == TemplateId;
        }

        protected override async Task<bool> PrepareAssetCreation(AssetTemplateGeneratorParameters parameters)
        {
            if (!await base.PrepareAssetCreation(parameters))
                return false;

            var window = new DataAssetTypeWindow(parameters.Description.DefaultOutputName);

            await window.ShowModal();

            if (window.Result == DialogResult.Cancel)
                return false;

            if (string.IsNullOrWhiteSpace(window.AssetName))
            {
                parameters.Logger.Error(Tr._p("Log", "Empty asset name."));
                return false;
            }
            if (window.Type == null)
            {
                parameters.Logger.Error(Tr._p("Log", "No valid type has been selected."));
                return false;
            }

            parameters.Name = window.AssetName;
            parameters.SetTag(TypeKey, window.Type);
            return true;
        }

        protected override IEnumerable<AssetItem> CreateAssets(AssetTemplateGeneratorParameters parameters)
        {
            var userType = parameters.GetTag(TypeKey);
            var location = GenerateLocation(parameters);
            var asset = (Asset)ObjectFactoryRegistry.NewInstance(typeof(DataAsset<>).MakeGenericType(userType));
            yield return new AssetItem(location, asset);
        }
    }
}
