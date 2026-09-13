using BuildingBlocks.Helper;
using ErrorOr;

namespace Modgud.Api.Features.Admin.Provisioning;

/// <summary>
/// The manifest's identity bookkeeping for one apply (ADR 0024): which entity every entry
/// and every reference means.
///
/// <para>Identity is the id. An entry matches an existing entity only through its
/// <c>Id</c>, and a reference resolves only through an id or a document-local
/// <c>#handle</c> — no name is ever resolved against the realm. This type holds the two
/// things that makes possible:</para>
/// <list type="bullet">
///   <item>the HANDLE TABLE — every <c>#handle</c> the file declares, and the real id the
///   apply assigned to it, so a reference written before the entity existed still lands;</item>
///   <item>the APPLIED IDS per section — every entity this apply created or updated, which
///   is exactly what prune must keep (an entity created moments ago carries no id the
///   manifest could list, and pruning by NAME would delete a renamed entity).</item>
/// </list>
///
/// <para><see cref="Validate"/> runs BEFORE anything is written: a handle declared twice, a
/// reference to a handle the file never declares, and a reference that carries only a name
/// are all contradictions inside the document itself, and a document that contradicts
/// itself should never reach a transaction. What cannot be decided up front — whether the
/// REALM has the entity a real id names — stays where it belongs, at apply time, as a
/// reported skip.</para>
/// </summary>
public sealed class ManifestIdentity
{
    /// <summary>handle → the section that declared it. The SECTION matters as much as the
    /// handle: without it, <c>"Members": ["#platform"]</c> pointing at a group would resolve
    /// to that group's id and be stored as a member, because the handle path never loads a
    /// document and so never learns the type the real-id path checks for free.</summary>
    private readonly Dictionary<string, string> _declared;
    private readonly Dictionary<string, Guid> _assigned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<Guid>> _applied = new(StringComparer.Ordinal);

    private ManifestIdentity(Dictionary<string, string> declared) => _declared = declared;

    /// <summary>The section a handle was declared in, or null when nothing declared it.</summary>
    public string? SectionOf(string handle) => _declared.GetValueOrDefault(handle);

    /// <summary>Handle → the real id the apply gave it. What the response reports back so a
    /// hand-written file can be made idempotent without exporting the realm.</summary>
    public IReadOnlyDictionary<string, Guid> Assigned => _assigned;

    /// <summary>Binds a declared handle to the id the create actually produced.</summary>
    public void Assign(string? handle, Guid id)
    {
        if (ManifestHandle.Is(handle)) _assigned[handle!] = id;
    }

    /// <summary>The id behind a handle, or null while the entity has not been created yet
    /// (sections apply in dependency order, so a forward reference is a manifest bug the
    /// validation already caught — this returns null only for a handle never assigned).</summary>
    public Guid? Resolve(string handle) => _assigned.TryGetValue(handle, out var id) ? id : null;

    /// <summary>Records that this apply created or updated <paramref name="id"/> in
    /// <paramref name="section"/> — the entity is represented in the manifest and prune
    /// must therefore keep it.</summary>
    public void Applied(string section, Guid id)
    {
        if (!_applied.TryGetValue(section, out var set))
            _applied[section] = set = [];
        set.Add(id);
    }

    /// <summary>Every entity this apply created or updated in a section. Prune's keep-set:
    /// "represented in the manifest" is an identity question, not a name question.</summary>
    public IReadOnlySet<Guid> AppliedIn(string section)
        => _applied.TryGetValue(section, out var set) ? set : (IReadOnlySet<Guid>)new HashSet<Guid>();

    /// <summary>True while the apply itself has already written this id — cheaper and more
    /// reliable than re-reading a projection that was written moments ago in the same
    /// transaction.</summary>
    public bool WasApplied(string section, Guid id)
        => _applied.TryGetValue(section, out var set) && set.Contains(id);

    /// <summary>
    /// Checks the manifest against itself: handle declarations are unique, every referenced
    /// handle is declared, and no reference names an entity by name alone. Returns the
    /// identity map seeded with the declared handles, or every contradiction found — all of
    /// them at once, because fixing a file one error per round trip is miserable.
    /// </summary>
    public static ErrorOr<ManifestIdentity> Validate(RealmManifest manifest)
    {
        var errors = new List<Error>();
        var declared = new Dictionary<string, string>(StringComparer.Ordinal);

        void Declare(string? id, string section, string ctx)
        {
            if (ManifestHandle.IsMalformed(id))
            {
                errors.Add(Error.Validation("Manifest.MalformedHandle",
                    $"{ctx}: '#' on its own is not a handle — give it a name, e.g. '#author'."));
                return;
            }
            if (!ManifestHandle.Is(id)) return;
            if (!declared.TryAdd(id!, section))
                errors.Add(Error.Validation("Manifest.DuplicateHandle",
                    $"{ctx}: the handle '{id}' is declared more than once — a handle names exactly one entity in this manifest."));
        }

        foreach (var a in manifest.Apps) Declare(a.Id, Sections.Apps, $"app '{a.Slug}'");
        foreach (var a in manifest.Apis) Declare(a.Id, Sections.Apis, $"api '{a.Name}'");
        foreach (var s in manifest.Scopes) Declare(s.Id, Sections.Scopes, $"scope '{s.Name}'");
        foreach (var c in manifest.Clients) Declare(c.Id, Sections.Clients, $"client '{c.ClientId}'");
        foreach (var p in manifest.LoginProviders) Declare(p.Id, Sections.LoginProviders, $"login provider '{p.Slug}'");
        foreach (var r in manifest.Roles) Declare(r.Id, Sections.Roles, $"role '{r.NaturalKey}'");
        foreach (var u in manifest.Users) Declare(u.Id, Sections.Users, $"user '{u.Email}'");
        foreach (var s in manifest.ServiceAccounts) Declare(s.Id, Sections.ServiceAccounts, $"service account '{s.AccountName}'");
        foreach (var g in manifest.Groups) Declare(g.Id, Sections.Groups, $"group '{g.Name}'");
        foreach (var p in manifest.Positions) Declare(p.Id, Sections.Positions, $"position '{p.AccountName}'");

        // A declared handle is a PROMISE that the reference resolves. Two things can
        // break that promise, and neither shows up at apply time as anything but a
        // missing member: the handle names an entity of the wrong kind, or one whose
        // section is applied later. A real id needs neither check — loading the document
        // proves the type, and the realm already holds it. A handle proves nothing, so
        // the declaration site is the only place either can be caught.
        void CheckRef(ManifestRef? reference, string what, string wantedSection, string fromSection, string ctx)
        {
            if (reference is null) return;
            if (reference.Handle is { } handle)
            {
                if (!declared.TryGetValue(handle, out var declaredIn))
                {
                    errors.Add(Error.Validation("Manifest.UnknownHandle",
                        $"{ctx}: '{handle}' is not declared in this manifest. A '#handle' only works inside the file that declares it — declare the {what} with \"Id\": \"{handle}\", or reference an existing {what} by its real id."));
                    return;
                }
                if (!wantedSection.Split('|').Contains(declaredIn))
                {
                    errors.Add(Error.Validation("Manifest.HandleKindMismatch",
                        $"{ctx}: '{handle}' is declared in '{declaredIn}', but this reference needs a {what}."));
                    return;
                }
                if (ApplyOrder(declaredIn) > ApplyOrder(fromSection))
                {
                    errors.Add(Error.Validation("Manifest.HandleAppliedTooLate",
                        $"{ctx}: '{handle}' names a {what} in the '{declaredIn}' section, which is applied AFTER '{fromSection}' — it would not exist yet. Reference that {what} by its real id instead."));
                }
                return;
            }
            if (ManifestHandle.IsMalformed(reference.Id))
            {
                errors.Add(Error.Validation("Manifest.MalformedHandle",
                    $"{ctx}: '#' on its own is not a handle — give it a name, e.g. '#author'."));
                return;
            }
            if (reference.Id is { Length: > 0 } raw)
            {
                if (!ShortGuid.TryParse(raw, out Guid _))
                    errors.Add(Error.Validation("Manifest.InvalidReferenceId",
                        $"{ctx}: '{raw}' is neither a valid id (ShortGuid or Guid) nor a '#handle'."));
                return;
            }
            errors.Add(Error.Validation("Manifest.ReferenceByName",
                $"{ctx}: a {what} is referenced by its Id, never by name (ADR 0024) — a name can already mean something else in the target realm. Export this realm to get the ids, or use a '#handle' for a {what} this same manifest creates."));
        }

        foreach (var g in manifest.Groups)
        {
            // A member may be a user, a nested group, or a service account. Service
            // accounts apply AFTER groups, so one of those has to be named by its real id
            // — the order check says so up front rather than dropping it at apply time.
            foreach (var m in g.Members ?? [])
                CheckRef(m, "user, group or service account",
                    $"{Sections.Users}|{Sections.Groups}|{Sections.ServiceAccounts}",
                    Sections.Groups, $"group '{g.Name}' member '{m}'");
            foreach (var r in g.Roles ?? [])
                CheckRef(r, "role", Sections.Roles, Sections.Groups, $"group '{g.Name}' role '{r}'");
        }
        foreach (var p in manifest.Positions)
            foreach (var grant in p.Grants ?? [])
                CheckRef(grant, "user", Sections.Users, Sections.Positions,
                    $"position '{p.AccountName}' grant '{grant}'");

        // App slugs, scope names and API audiences are VOCABULARY, not identity: they are
        // what tokens and permission strings carry, and an entity is required to have one,
        // so a manifest that creates an app already gives every later reference the name to
        // use. A handle there is an author reaching for the wrong tool — and it would
        // otherwise resolve to nothing and be silently skipped, which is the one outcome
        // this whole decision exists to prevent.
        void CheckVocabulary(string? value, string what, string ctx)
        {
            if (!ManifestHandle.Is(value) && !ManifestHandle.IsMalformed(value)) return;
            errors.Add(Error.Validation("Manifest.HandleNotAllowed",
                $"{ctx}: '{value}' is a '#handle', and a {what} is named by its own name, not by identity — "
                + "the name is written on the entity, so it is already the right way to point at one. Use the plain name."));
        }

        foreach (var a in manifest.Apis)
        {
            CheckVocabulary(a.App.HasValue ? a.App.Value : null, "app", $"api '{a.Name}'");
            foreach (var s in a.Scopes ?? []) CheckVocabulary(s, "scope", $"api '{a.Name}'");
        }
        foreach (var s in manifest.Scopes)
        {
            CheckVocabulary(s.App.HasValue ? s.App.Value : null, "app", $"scope '{s.Name}'");
            foreach (var r in s.Resources ?? []) CheckVocabulary(r, "API audience", $"scope '{s.Name}'");
        }
        foreach (var c in manifest.Clients)
        {
            foreach (var a in c.Apps ?? []) CheckVocabulary(a, "app", $"client '{c.ClientId}'");
            foreach (var s in c.Scopes ?? []) CheckVocabulary(s, "scope", $"client '{c.ClientId}'");
            foreach (var r in c.Roles ?? []) CheckVocabulary(r, "role", $"client '{c.ClientId}'");
        }
        foreach (var r in manifest.Roles)
            CheckVocabulary(r.App, "app", $"role '{r.NaturalKey}'");
        foreach (var g in manifest.Groups)
            foreach (var b in g.BoundTo ?? []) CheckVocabulary(b, "app", $"group '{g.Name}'");
        foreach (var j in manifest.Jobs)
            CheckVocabulary(j.Key, "scheduled job", $"job '{j.Key}'");

        return errors.Count > 0 ? errors : new ManifestIdentity(declared);
    }

    /// <summary>
    /// Position of a section in the applier's dependency order — the order
    /// <c>ApplyTenantUpdateSectionsAsync</c> actually runs them in. A handle can only be
    /// referenced from a section at or after the one that declares it; anything else
    /// resolves to nothing, and quietly.
    /// </summary>
    private static int ApplyOrder(string section) => section switch
    {
        Sections.Apps => 1,
        Sections.Apis => 2,
        Sections.Scopes => 3,
        Sections.Clients => 4,
        Sections.LoginProviders => 5,
        Sections.Roles => 6,
        Sections.Users => 7,
        Sections.Groups => 8,
        Sections.ServiceAccounts => 9,
        Sections.Positions => 10,
        _ => int.MaxValue,
    };

    /// <summary>The manifest sections, as the plan, the prune sweep and the staged-delete
    /// targets all spell them. Named once so the applier's keep-sets and the planner's
    /// section names cannot drift apart.</summary>
    public static class Sections
    {
        public const string Apps = "apps";
        public const string Apis = "apis";
        public const string Scopes = "scopes";
        public const string Clients = "clients";
        public const string LoginProviders = "loginProviders";
        public const string Roles = "roles";
        public const string Users = "users";
        public const string Groups = "groups";
        public const string ServiceAccounts = "serviceAccounts";
        public const string Positions = "positions";
    }
}
