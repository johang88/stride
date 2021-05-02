using Stride.Core;
using Stride.Core.Assets;

namespace Stride.Assets
{
    /// <summary>
    /// Default converter between <see cref="IData{TData}"/> and <see cref="Data{TData}"/> used for <see cref="IDataAsset"/> compilation.
    /// </summary>
    /// <typeparam name="TData">User defined type.</typeparam>
    [DataContract]
    public class DefaultDataAssetConverter<TData> : IDataAssetConverter<Data<TData>>
        where TData : IData<TData>
    {
        public Data<TData> Convert(IDataAssetDesign<Data<TData>> designData)
        {
            var design = (TData)designData;
            return new Data<TData>
            {
                Value = design,
            };
        }
    }
}
