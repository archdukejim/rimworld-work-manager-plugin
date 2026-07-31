// Polyfills.cs
// Copyright (c) 2026 archdukejim

// .NET Framework doesn't ship the attribute types the C# compiler needs for `init` accessors and
// `required` members. Colony Manager Redux defines its own copies internally, so this assembly
// needs its own; these are the standard shims, and they never appear in the mod's public surface.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }

    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field
            | AttributeTargets.Property,
        AllowMultiple = false,
        Inherited = false
    )]
    internal sealed class RequiredMemberAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute(string featureName) : Attribute
    {
        public string FeatureName { get; } = featureName;

        public bool IsOptional { get; init; }

        public const string RefStructs = nameof(RefStructs);
        public const string RequiredMembers = nameof(RequiredMembers);
    }
}

namespace WorkManager
{
    using System.Collections.Generic;

    /// <summary>.NET Framework's Dictionary predates <c>GetValueOrDefault</c>.</summary>
    internal static class DictionaryExtensions
    {
        public static TValue? GetValueOrDefault<TKey, TValue>(
            this Dictionary<TKey, TValue> dictionary,
            TKey key
        ) => dictionary.TryGetValue(key, out var value) ? value : default;
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute { }
}
