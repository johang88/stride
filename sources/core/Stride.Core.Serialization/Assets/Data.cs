using Stride.Core.Reflection;
using Stride.Core.Serialization.Contents;

namespace Stride.Core.Assets
{
    /// <summary>
    /// Generic data container for <see cref="IDataAsset"/> use.
    /// </summary>
    /// <typeparam name="TData">User defined type.</typeparam>
    [DataContract]
    [ReferenceSerializer]
    [ContentSerializer(typeof(DataContentSerializerWithReuse<>))]
    public class Data<TData> : IDataAsset
        where TData : IData<TData>
    {
        public TData Value { get; set; }
    }

    /// <summary>
    /// Interface used for marking user classes that can be used with <see cref="Data{TData}"/>.
    /// </summary>
    /// <typeparam name="TData">User defined type.</typeparam>
    [AssemblyScan]
    public interface IData<TData> : IDataAssetDesign<Data<TData>>
        where TData : IData<TData>
    {
    }
}
