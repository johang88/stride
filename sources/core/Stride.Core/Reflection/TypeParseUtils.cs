using System;
using System.Collections.Generic;
using Stride.Core.Annotations;

namespace Stride.Core.Reflection
{
    public static class TypeParseUtils
    {
        /// <summary>
        /// Parses non-generic type name.
        /// </summary>
        public static void ParseType(string typeFullName, out string typeName, out string assemblyName)
        {
            var typeNameEnd = typeFullName.IndexOf(',');
            var assemblyNameStart = typeNameEnd;
            if (assemblyNameStart != -1 && typeFullName[++assemblyNameStart] == ' ') // Skip first comma and check if we have a space
                assemblyNameStart++; // Skip first space

            // Extract assemblyName and readjust typeName to not include assemblyName anymore
            if (assemblyNameStart != -1)
            {
                var assemblyNameEnd = typeFullName.IndexOf(',', assemblyNameStart);
                assemblyName = assemblyNameEnd != -1
                    ? typeFullName.Substring(assemblyNameStart, assemblyNameEnd - assemblyNameStart)
                    : typeFullName.Substring(assemblyNameStart);

                typeName = typeFullName.Substring(0, typeNameEnd);
            }
            else
            {
                typeName = typeFullName;
                assemblyName = null;
            }
        }

        /// <summary>
        /// Split the given short assembly-qualified type name into a generic definition type and a collection of generic argument types, and retrieve the dimension of the array if the type is an array type.
        /// </summary>
        /// <param name="shortAssemblyQualifiedName">The given short assembly-qualified type name to split.</param>
        /// <param name="genericArguments">The generic argument types extracted, if the given type was generic. Otherwise null.</param>
        /// <param name="arrayNesting">The number of arrays that are nested if the type is an array type.</param>
        /// <returns>The corresponding generic definition type.</returns>
        /// <remarks>If the given type is not generic, this method sets <paramref name="genericArguments"/> to null and returns <paramref name="shortAssemblyQualifiedName"/>.</remarks>
        [NotNull]
        public static string GetGenericArgumentsAndArrayDimension([NotNull] string shortAssemblyQualifiedName, [CanBeNull] out List<string> genericArguments, out int arrayNesting)
        {
            if (shortAssemblyQualifiedName == null) throw new ArgumentNullException(nameof(shortAssemblyQualifiedName));
            var firstBracket = int.MaxValue;
            var lastBracket = int.MinValue;
            var bracketLevel = 0;
            genericArguments = null;
            arrayNesting = 0;
            var startIndex = 0;
            for (var i = 0; i < shortAssemblyQualifiedName.Length; ++i)
            {
                if (shortAssemblyQualifiedName[i] == '[')
                {
                    firstBracket = Math.Min(firstBracket, i);
                    ++bracketLevel;
                    if (bracketLevel == 2)
                    {
                        startIndex = i + 1;
                    }
                }
                if (shortAssemblyQualifiedName[i] == ']')
                {
                    lastBracket = Math.Max(lastBracket, i);
                    --bracketLevel;
                    if (bracketLevel == 1)
                    {
                        if (genericArguments == null)
                            genericArguments = new List<string>();

                        genericArguments.Add(shortAssemblyQualifiedName.Substring(startIndex, i - startIndex));
                    }
                    if (bracketLevel == 0 && i > 0)
                    {
                        if (shortAssemblyQualifiedName[i - 1] == '[')
                        {
                            ++arrayNesting;
                        }
                    }
                }
            }
            if (genericArguments != null || arrayNesting > 0)
            {
                var genericType = shortAssemblyQualifiedName.Substring(0, firstBracket) + shortAssemblyQualifiedName.Substring(lastBracket + 1);
                return genericType;
            }
            return shortAssemblyQualifiedName;
        }
    }
}
