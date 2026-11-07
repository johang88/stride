// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using Stride.Core.Yaml.Serialization;

namespace Stride.Core.Reflection;

/// <summary>
/// A default implementation for the <see cref="ITypeDescriptorFactory"/>.
/// </summary>
public class TypeDescriptorFactory : ITypeDescriptorFactory
{
    private readonly IComparer<object> keyComparer;
    private readonly Dictionary<Type, ITypeDescriptor> registeredDescriptors = [];
    private readonly bool emitDefaultValues;
    private readonly IMemberNamingConvention namingConvention;

    /// <summary>
    /// The default type descriptor factory.
    /// </summary>
    public static readonly TypeDescriptorFactory Default = new();

    public TypeDescriptorFactory() : this(new AttributeRegistry())
    {
    }

    public TypeDescriptorFactory(IAttributeRegistry attributeRegistry)
        : this(attributeRegistry, false, new DefaultNamingConvention())
    {
    }

    public TypeDescriptorFactory(IAttributeRegistry attributeRegistry, bool emitDefaultValues, IMemberNamingConvention namingConvention)
        : this(attributeRegistry, emitDefaultValues, namingConvention, new DefaultMemberComparer())
    {
    }

    public TypeDescriptorFactory(IAttributeRegistry attributeRegistry, bool emitDefaultValues, IMemberNamingConvention namingConvention, IComparer<object> keyComparer)
    {
        ArgumentNullException.ThrowIfNull(attributeRegistry);
        this.keyComparer = keyComparer;
        AttributeRegistry = attributeRegistry;
        this.emitDefaultValues = emitDefaultValues;
        this.namingConvention = namingConvention;
    }

    public IAttributeRegistry AttributeRegistry { get; }

    [return: NotNullIfNotNull(nameof(type))]
    public ITypeDescriptor? Find(Type? type)
    {
        if (type is null)
            return null;

        // Without a lock once a descriptor is ready: visitors on many threads (validating thousands of assets in
        // parallel) contended on this lock for every object they visited
        if (readyDescriptors.TryGetValue(type, out var ready))
            return ready;

        // Caching is integrated in this class, avoiding a ChainedTypeDescriptorFactory
        ITypeDescriptor? descriptor;
        lock (registeredDescriptors)
        {
            if (!registeredDescriptors.TryGetValue(type, out descriptor))
            {
                descriptor = Create(type);

                // Register this descriptor (before initializing!)
                registeredDescriptors.Add(type, descriptor);
                pendingDescriptors.Add((type, descriptor));

                // Make sure the descriptor is initialized. Initializing can create the descriptors of other types, which
                // can see this one before it is initialized: all of them become ready once the outermost one is.
                ++creationDepth;
                try
                {
                    descriptor.Initialize(keyComparer);
                }
                finally
                {
                    if (--creationDepth == 0)
                    {
                        foreach (var created in pendingDescriptors)
                            readyDescriptors.TryAdd(created.Type, created.Descriptor);
                        pendingDescriptors.Clear();
                    }
                }
            }
        }

        return descriptor;
    }

    // The descriptors fully initialized, read without the lock; and, under the lock, the nesting of descriptor creations
    // with the descriptors they created
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, ITypeDescriptor> readyDescriptors = new();
    private readonly List<(Type Type, ITypeDescriptor Descriptor)> pendingDescriptors = [];
    private int creationDepth;

    /// <summary>
    /// Creates a type descriptor for the specified type.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>An instance of type descriptor.</returns>
    protected virtual ITypeDescriptor Create(Type type)
    {
        ITypeDescriptor descriptor;
        // The order of the descriptors here is important

        if (PrimitiveDescriptor.IsPrimitive(type))
        {
            descriptor = new PrimitiveDescriptor(this, type, emitDefaultValues, namingConvention);
        }
        else if (DictionaryDescriptor.IsDictionary(type)) // resolve dictionary before collections, as they are also collections
        {
            // IDictionary
            descriptor = new DictionaryDescriptor(this, type, emitDefaultValues, namingConvention);
        }
        else if (ListDescriptor.IsList(type))
        {
            // IList
            descriptor = new ListDescriptor(this, type, emitDefaultValues, namingConvention);
        }
        else if (SetDescriptor.IsSet(type))
        {
            // ISet
            descriptor = new SetDescriptor(this, type, emitDefaultValues, namingConvention);
        }
        else if (CollectionDescriptor.IsCollection(type))
        {
            // ICollection
            descriptor = new OldCollectionDescriptor(this, type, emitDefaultValues, namingConvention);
        }
        else if (type.IsArray)
        {
            if (type.GetArrayRank() == 1 && !type.GetElementType()!.IsArray)
            {
                // array[] - only single dimension array is supported
                descriptor = new ArrayDescriptor(this, type, emitDefaultValues, namingConvention);
            }
            else
            {
                // multi-dimension array to be treated as a 'standard' object
                descriptor = new NotSupportedObjectDescriptor(this, type, emitDefaultValues, namingConvention);
            }
        }
        else if (NullableDescriptor.IsNullable(type))
        {
            descriptor = new NullableDescriptor(this, type, emitDefaultValues, namingConvention);
        }
        else
        {
            // standard object (class or value type)
            descriptor = new ObjectDescriptor(this, type, emitDefaultValues, namingConvention);
        }

        return descriptor;
    }
}
