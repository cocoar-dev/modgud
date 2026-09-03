namespace Modgud.Domain.Users.Events;

/// <summary>
/// ADR 0006 — appended right after <see cref="UserCreatedEvent"/> when an account is
/// materialised by the registration pipeline, i.e. after the person proved control of
/// the address. It is the first (and only) trace of the sign-up in the user's history:
/// everything before the proof lived in a hard-deleted pending document and is not
/// history. <paramref name="Source"/> names the path (see
/// <c>Modgud.Authentication.Registration.RegistrationSources</c>), <paramref name="ProofKind"/>
/// the proof (<c>Code</c> / <c>Link</c> / <c>None</c> for realms that opted out of verification).
/// </summary>
public record UserRegisteredEvent(Guid Id, string Source, string ProofKind);
