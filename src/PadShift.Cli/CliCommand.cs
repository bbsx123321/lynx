namespace PadShift.Cli;

/// <summary>Verbs exposed by the PadShift command line, mirroring the reference CLI.</summary>
public enum CliVerb
{
    Help,
    Version,
    Apply,
    SelectSlot,
    ClearSlot,
    Remap,
}

/// <summary>One parsed command. Slot is 1-based (the reference <c>Slot1</c>..<c>Slot4</c>).</summary>
public sealed record CliCommand(
    CliVerb Verb,
    string? ConfigPath = null,
    int? Slot = null,
    Guid? Controller = null,
    bool RemapOn = false,
    int? Port = null);

/// <summary>Parser outcome: either a command or a human-readable Chinese usage error.</summary>
public sealed record CliParseResult(CliCommand? Command, string? Error)
{
    public bool Success => Command is not null;

    public static CliParseResult Ok(CliCommand command) => new(command, null);

    public static CliParseResult Fail(string error) => new(null, error);
}
