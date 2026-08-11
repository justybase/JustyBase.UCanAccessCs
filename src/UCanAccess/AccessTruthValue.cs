namespace UCanAccess;

/// <summary>
/// SQL's three-valued boolean result. A WHERE clause selects a row only when
/// the final value is <see cref="True"/>; <see cref="Unknown"/> behaves like
/// NULL rather than like false followed by a normal C# negation.
/// </summary>
internal enum AccessTruthValue
{
    False,
    True,
    Unknown,
}

internal static class AccessTruth
{
    public static AccessTruthValue Not(AccessTruthValue value)
        => value switch
        {
            AccessTruthValue.True => AccessTruthValue.False,
            AccessTruthValue.False => AccessTruthValue.True,
            _ => AccessTruthValue.Unknown,
        };

    public static AccessTruthValue And(AccessTruthValue left, AccessTruthValue right)
        => left == AccessTruthValue.False || right == AccessTruthValue.False
            ? AccessTruthValue.False
            : left == AccessTruthValue.Unknown || right == AccessTruthValue.Unknown
                ? AccessTruthValue.Unknown
                : AccessTruthValue.True;

    public static AccessTruthValue Or(AccessTruthValue left, AccessTruthValue right)
        => left == AccessTruthValue.True || right == AccessTruthValue.True
            ? AccessTruthValue.True
            : left == AccessTruthValue.Unknown || right == AccessTruthValue.Unknown
                ? AccessTruthValue.Unknown
                : AccessTruthValue.False;

    public static bool IsTrue(this AccessTruthValue value) => value == AccessTruthValue.True;
}
