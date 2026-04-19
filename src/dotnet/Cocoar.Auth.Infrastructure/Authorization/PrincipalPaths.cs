namespace Cocoar.Auth.Infrastructure.Authorization;

/// <summary>
/// Prefix used by <c>MembershipEvaluator.CollectDependencies&lt;PrincipalDirectory&gt;</c>
/// when storing script dependency paths. Keeping the sender side (auto-membership
/// sync handlers) in sync with the collector side.
/// </summary>
public static class PrincipalPaths
{
    public const string Prefix = "PrincipalDirectory.";

    public const string Type = Prefix + "Type";
    public const string IsActive = Prefix + "IsActive";
    public const string IsDeleted = Prefix + "IsDeleted";
    public const string Email = Prefix + "Email";
    public const string NormalizedEmail = Prefix + "NormalizedEmail";

    public const string Person = Prefix + "Person";
    public const string PersonFirstname = Prefix + "Person.Firstname";
    public const string PersonLastname = Prefix + "Person.Lastname";
    public const string PersonUserName = Prefix + "Person.UserName";
    public const string PersonNormalizedUserName = Prefix + "Person.NormalizedUserName";
    public const string PersonPhoneNumber = Prefix + "Person.PhoneNumber";

    public const string Group = Prefix + "Group";
    public const string GroupName = Prefix + "Group.Name";
    public const string GroupEmailMode = Prefix + "Group.EmailMode";
}
