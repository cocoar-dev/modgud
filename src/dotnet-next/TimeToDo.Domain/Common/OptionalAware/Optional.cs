using System.Diagnostics.CodeAnalysis;

namespace TimeToDo.Domain.Common;

/// <summary>
/// Immutable optional value as a VALUE TYPE.
/// Differentiates between "none" (HasValue=false) and "some" (HasValue=true).
/// </summary>
[Serializable]
public readonly struct Optional<T> : IOptional
{
    /// <summary>
    /// True if this optional contains a value.
    /// </summary>
    public bool HasValue { get; }

    /// <summary>
    /// The contained value; only valid if HasValue=true.
    /// </summary>
    [MaybeNull]
    public readonly T Value;

    /// <summary>
    /// Create a "some" optional.
    /// </summary>
    public Optional([AllowNull] T value)
    {
        HasValue = true;
        // [AllowNull] on the parameter and [MaybeNull] on the field correctly document
        // that Optional<T> can wrap null even for non-nullable T. The compiler cannot
        // verify this invariant through attributes alone, so the suppression is intentional.
        Value = value!;
    }

    /// <summary>
    /// The canonical "none" optional.
    /// </summary>
    public static Optional<T> None => default;

    /// <summary>
    /// Implicitly wrap a T into Optional&lt;T&gt;;
    /// if you pass null (for reference T), it's still treated as "some null."
    /// </summary>
    public static implicit operator Optional<T>([AllowNull] T value)
        => new Optional<T>(value);

    /// <summary>
    /// If HasValue, returns Value, else returns your defaultValue.
    /// </summary>
    public T OrDefault(T defaultValue = default!)
        => HasValue ? Value! : defaultValue;
}
