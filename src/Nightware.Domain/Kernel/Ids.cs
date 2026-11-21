namespace Nightware.Domain.Kernel;

// Strongly-typed, adapter-opaque identifiers. Only the adapter that minted an
// id may parse it; everything else treats Value as an opaque string.

public readonly record struct MessageId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct FolderId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct AttachmentId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct EventId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct ContactId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>A contact folder (Graph <c>contactFolder</c>); opaque, minted by the adapter.</summary>
public readonly record struct ContactFolderId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>An event calendar (Graph <c>calendar</c>); opaque, minted by the adapter.</summary>
public readonly record struct CalendarId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct TaskListId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct TaskId(string Value)
{
    public override string ToString() => Value;
}
