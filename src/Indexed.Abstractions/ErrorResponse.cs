namespace Indexed.Abstractions;

/// <summary>
/// Body shape for every non-2xx HTTP response from the Indexed service.
/// </summary>
/// <remarks>
/// <para>
/// The envelope separates a machine-stable <see cref="Code"/> from a
/// human-readable <see cref="Message"/>. Agents should branch on
/// <see cref="Code"/>; <see cref="Message"/> may be localized or reworded
/// between versions.
/// </para>
/// <para>
/// <see cref="Details"/> is deliberately free-form: it may contain a
/// correlation id (for <see cref="IndexedErrorCode.Internal"/>), a regex error
/// position (for <see cref="IndexedErrorCode.PatternInvalid"/>), or the name
/// of the failing precondition (for <see cref="IndexedErrorCode.BadRequest"/>).
/// Consumers must treat it as opaque diagnostic text.
/// </para>
/// </remarks>
/// <param name="Code">Machine-stable failure category.</param>
/// <param name="Message">
/// Short human-readable explanation, suitable for surfacing in CLI output.
/// </param>
/// <param name="Details">
/// Optional diagnostic string; see remarks. <c>null</c> when no additional
/// context is available.
/// </param>
public sealed record ErrorResponse(
    IndexedErrorCode Code,
    string Message,
    string? Details = null);
