// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Stride.Core;

namespace Stride.Updater
{
    /// <summary>
    /// Defines how to set and get values from a property for the <see cref="UpdateEngine"/>.
    /// </summary>
    public abstract class UpdatableProperty : UpdatablePropertyBase
    {
        public readonly IntPtr Getter;
        public readonly IntPtr Setter;
        public readonly bool VirtualDispatchGetter;
        public readonly bool VirtualDispatchSetter;

        protected UpdatableProperty(IntPtr getter, bool virtualDispatchGetter, IntPtr setter, bool virtualDispatchSetter)
        {
            Getter = getter;
            Setter = setter;
            VirtualDispatchGetter = virtualDispatchGetter;
            VirtualDispatchSetter = virtualDispatchSetter;
        }

        /// <summary>
        /// Gets a reference object from a property.
        /// </summary>
        /// <param name="obj">The object encoded as a native pointer (<see cref="UpdateEngine"/> will make sure it is pinned).</param>
        /// <returns>The object value from the property.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public object GetObject(IntPtr obj)
        {
#if IL
            // Note: IL is injected by UpdateEngineProcessor
            ldarg obj
            ldarg.0
            ldfld native int class Stride.Updater.UpdatableProperty::Getter
            ldarg.0
            ldfld bool Stride.Updater.UpdatableProperty::VirtualDispatchSetter
            brfalse nonVirtualCall
            calli object(native int) // Note: .NET 6 doesn't work with object, using IntPtr instead
            br end
        nonVirtualCall:
            calli instance object()
        end:
            ret
#endif
            throw new NotImplementedException();
        }

        /// <summary>
        /// Sets a reference object from a property.
        /// </summary>
        /// <param name="obj">The object encoded as a native pointer (<see cref="UpdateEngine"/> will make sure it is pinned).</param>
        /// <param name="data">The object value to set.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetObject(IntPtr obj, object data)
        {
#if IL
            // Note: IL is injected by UpdateEngineProcessor
            ldarg obj
            ldarg data
            ldarg.0
            ldfld native int class Stride.Updater.UpdatableProperty::Setter
            ldarg.0
            ldfld bool Stride.Updater.UpdatableProperty::VirtualDispatchSetter
            brfalse nonVirtualCall
            calli void(native int, object) // Note: .NET 6 doesn't work with object, using IntPtr instead
            br end
        nonVirtualCall:
            calli instance void(object)
        end:
            ret
#endif
            throw new NotImplementedException();
        }

        /// <inheritdoc/>
        internal override UpdateOperationType GetSetOperationType()
        {
            if (MemberType.GetTypeInfo().IsValueType)
            {
                if (BlittableHelper.IsBlittable(MemberType))
                    return UpdateOperationType.ConditionalSetBlittablePropertyBase;

                return UpdateOperationType.ConditionalSetStructPropertyBase;
            }
            else
            {
                return UpdateOperationType.ConditionalSetObjectProperty;
            }
        }

        /// <inheritdoc/>
        internal override UpdateOperationType GetEnterOperationType()
        {
            if (MemberType.GetTypeInfo().IsValueType)
            {
                return UpdateOperationType.EnterStructPropertyBase;
            }
            else
            {
                return UpdateOperationType.EnterObjectProperty;
            }
        }
    }
}
