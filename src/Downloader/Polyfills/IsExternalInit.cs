#if NETFRAMEWORK || NETSTANDARD2_0
using System.ComponentModel;

// ReSharper disable once CheckNamespace
namespace System.Runtime.CompilerServices;

/// <summary>
/// Polyfill marker required by the compiler to emit C# 9 <c>init</c> accessors and
/// <c>record</c> types on targets whose BCL predates them (net462 / netstandard2.0).
/// The attribute is never referenced at runtime; it only needs to exist at compile time.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
internal static class IsExternalInit { }
#endif
