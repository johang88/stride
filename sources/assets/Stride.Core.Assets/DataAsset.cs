using Stride.Core.Annotations;

namespace Stride.Core.Assets
{
    /// <summary>
    /// A data asset for custom user classes.
    /// </summary>
    /// <typeparam name="TData">Serializable type.</typeparam>
    [AssetDescription(FileExtension, AllowArchetype = true)]
    [AssetFormatVersion("Stride", CurrentVersion, "1.0.0.0")]
    public sealed class DataAsset<TData> : Asset where TData : IDataAsset
    {
        private const string CurrentVersion = "1.0.0.0";
        public const string FileExtension = ".sddata";

        /// <summary>
        /// Design time data for <see cref="TData"/>.
        /// </summary>
        [DataMember(1), Display(Expand = ExpandRule.Always)]
        [MemberRequired]
        public IDataAssetDesign<TData> Data { get; set; }

        /// <summary>
        /// Instance of <see cref="IDataAssetConverter{TData}"/> that should be used to convert design time data into runtime data.
        /// </summary>
        /// <userdoc>Instance of IDataAssetConverter that should be used to convert design time data into runtime data.</userdoc>
        [DataMember(0)]
        [MemberRequired]
        public IDataAssetConverter<TData> Converter { get; set; }
    }
}
