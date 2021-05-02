using Stride.Core.Reflection;

namespace Stride.Core.Assets
{
    /// <summary>
    /// An interface used to identify classes that can be used as a generic data asset.
    /// </summary>
    [AssemblyScan]
    public interface IDataAsset
    {
    }

    /// <summary>
    /// An interface used to identify design time classes that can be converted to an <see cref="IDataAsset"/>.
    /// </summary>
    /// <typeparam name="TData">Runtime data type <see cref="IDataAsset"/>.</typeparam>
    public interface IDataAssetDesign<TData> where TData : IDataAsset
    {
    }

    /// <summary>
    /// Converter between design time data into runtime data.
    /// </summary>
    /// <typeparam name="TData">Runtime data type <see cref="IDataAsset"/>.</typeparam>
    public interface IDataAssetConverter<TData>
        where TData : IDataAsset
    {
        /// <summary>
        /// Converts design time data into runtime data for the <see cref="TData"/> data asset.
        /// </summary>
        /// <param name="designData">Design time data.</param>
        /// <returns>Converted data.</returns>
        TData Convert(IDataAssetDesign<TData> designData);
    }
}
