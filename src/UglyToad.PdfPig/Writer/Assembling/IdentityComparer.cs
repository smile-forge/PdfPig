namespace UglyToad.PdfPig.Writer.Assembling;

using System.Collections.Generic;
using System.Runtime.CompilerServices;

/// <summary>
/// Compares by reference: tokens compare by value, and two equal direct dictionaries in a source are still two objects.
/// </summary>
internal sealed class IdentityComparer<T> : IEqualityComparer<T> where T : class
{
    public static readonly IdentityComparer<T> Instance = new();

    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

    public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
}
