namespace TimeToDo.Domain.ValueObjects;

/// <summary>
/// Value object representing an aggregate version for optimistic concurrency.
/// </summary>
public readonly record struct AggregateVersion
{
    public int Value { get; }

    public AggregateVersion(int value)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Version cannot be negative");
        Value = value;
    }

    public static AggregateVersion Initial => new(1);

    public AggregateVersion Increment() => new(Value + 1);

    public static implicit operator int(AggregateVersion version) => version.Value;
    public static implicit operator AggregateVersion(int value) => new(value);

    public override string ToString() => Value.ToString();
}
