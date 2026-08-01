using System.Text.Json.Serialization;

namespace Oxide.CompilerServices.Types.Validation;

/// <summary>
/// JSON response shape returned by POST /validate. Kept independent of the
/// internal named-pipe wire protocol (CompilerMessage/CompilationResult) so
/// that protocol can keep changing without breaking the HTTP contract PHP relies on.
/// </summary>
public class ValidationResponseDto
{
    public bool Success { get; set; }

    public string FileName { get; set; } = string.Empty;

    public long ElapsedMilliseconds { get; set; }

    public List<ValidationErrorDto> Errors { get; set; } = new();
}

public class ValidationErrorDto
{
    public string Message { get; set; } = string.Empty;

    public string? File { get; set; }

    public int Line { get; set; }

    public int Position { get; set; }
}

[JsonSerializable(typeof(ValidationResponseDto))]
[JsonSerializable(typeof(ValidationErrorDto))]
public partial class ValidationJsonContext : JsonSerializerContext;
