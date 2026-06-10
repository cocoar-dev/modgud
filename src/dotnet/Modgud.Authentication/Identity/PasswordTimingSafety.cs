using Microsoft.AspNetCore.Identity;
using Modgud.Authentication.Domain;

namespace Modgud.Authentication.Identity;

/// <summary>
/// Closes the login timing oracle (audit M3). The password-login handler returns
/// 401 immediately for an unknown / inactive / locked account — before any
/// password hashing — while a valid account pays the full PBKDF2 verify cost.
/// That latency difference lets an attacker enumerate valid active usernames
/// without ever guessing a password (IP rate-limiting is deliberately absent on
/// this endpoint, so probing is cheap).
///
/// <para><see cref="EqualizeFailure"/> performs one throwaway hash verify against
/// a fixed dummy hash so the no-such-user path costs the same as a real
/// wrong-password verify. The dummy hash is produced by the SAME injected
/// <see cref="IPasswordHasher{T}"/> as real user hashes, so its embedded
/// iteration count matches and the work is equivalent — not a guessed delay.</para>
/// </summary>
public static class PasswordTimingSafety
{
    // Default PasswordHasher<T> ignores the user argument in HashPassword /
    // VerifyHashedPassword, so a single throwaway instance is safe to reuse.
    private static readonly ApplicationUser DummyUser = new("__timing__", "__timing__@invalid");

    private static readonly Lock Gate = new();
    private static volatile string? _dummyHash;

    /// <summary>
    /// Verify <paramref name="password"/> against a fixed dummy hash and discard
    /// the result, equalizing the failure-path latency with a real verify.
    /// </summary>
    public static void EqualizeFailure(IPasswordHasher<ApplicationUser> hasher, string? password)
    {
        var dummy = _dummyHash;
        if (dummy is null)
        {
            lock (Gate)
            {
                dummy = _dummyHash ??= hasher.HashPassword(
                    DummyUser, "timing-equalization-placeholder-not-a-real-password");
            }
        }

        _ = hasher.VerifyHashedPassword(DummyUser, dummy, password ?? string.Empty);
    }
}
