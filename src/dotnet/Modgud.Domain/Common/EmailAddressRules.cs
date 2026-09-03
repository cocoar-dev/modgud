using System.Net.Mail;

namespace Modgud.Domain.Common;

/// <summary>
/// Shared rules for admin-configurable email addresses (realm and Application
/// email branding).
/// </summary>
public static class EmailAddressRules
{
    /// <summary>
    /// True when <paramref name="value"/> is a plain <c>local@domain</c> address
    /// carrying no display name. The display name is a separate setting
    /// (<c>FromName</c>); accepting a combined <c>"Name &lt;addr&gt;"</c> here would
    /// let a stray comma or line break smuggle extra headers into the envelope.
    /// </summary>
    public static bool IsBareAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.IndexOfAny(['<', '>', '"', ',', ';', '\r', '\n', ' ']) >= 0) return false;
        if (!MailAddress.TryCreate(value, out var parsed)) return false;
        // MailAddress tolerates "Name addr" forms; require the parsed address to be
        // exactly what was given so nothing was silently interpreted as a name.
        return string.Equals(parsed.Address, value, StringComparison.OrdinalIgnoreCase)
               && string.IsNullOrEmpty(parsed.DisplayName);
    }
}
