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
    private readonly HashSet<string> _declared;
    private readonly Dictionary<string, Guid> _assigned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<Guid>> _applied = new(StringComparer.Ordinal);

    private ManifestIdentity(HashSet<string> declared) => _declared = declared;

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
        var declared = new HashSet<string>(StringComparer.Ordinal);

        void Declare(string? id, string ctx)
        {
            if (ManifestHandle.IsMalformed(id))
            {
                errors.Add(Error.Validation("Manifest.MalformedHandle",
                    $"{ctx}: '#' on its own is not a handle — give it a name, e.g. '#author'."));
                return;
            }
            if (!ManifestHandle.Is(id)) return;
            if (!declared.Add(id!))
                errors.Add(Error.Validation("Manifest.DuplicateHandle",
                    $"{ctx}: the handle '{id}' is declared more than once — a handle names exactly one entity in this manifest."));
        }

        foreach (var a in manifest.Apps) Declare(a.Id, $"app '{a.Slug}'");
        foreach (var a in manifest.Apis) Declare(a.Id, $"api '{a.Name}'");
        foreach (var s in manifest.Scopes) Declare(s.Id, $"scope '{s.Name}'");
        foreach (var c in manifest.Clients) Declare(c.Id, $"client '{c.ClientId}'");
        foreach (var p in manifest.LoginProviders) Declare(p.Id, $"login provider '{p.Slug}'");
        foreach (var r in manifest.Roles) Declare(r.Id, $"role '{r.NaturalKey}'");
        foreach (var u in manifest.Users) Declare(u.Id, $"user '{u.Email}'");
        foreach (var s in manifest.ServiceAccounts) Declare(s.Id, $"service account '{s.AccountName}'");
        foreach (var g in manifest.Groups) Declare(g.Id, $"group '{g.Name}'");
        foreach (var p in manifest.Positions) Declare(p.Id, $"position '{p.AccountName}'");

        void CheckRef(ManifestRef? reference, string what, string ctx)
        {
            if (reference is null) return;
            if (reference.Handle is { } handle)
            {
                if (!declared.Contains(handle))
                    errors.Add(Error.Validation("Manifest.UnknownHandle",
                        $"{ctx}: '{handle}' is not declared in this manifest. A '#handle' only works inside the file that declares it — declare the {what} with \"Id\": \"{handle}\", or reference an existing {what} by its real id."));
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
            foreach (var m in g.Members ?? []) CheckRef(m, "user", $"group '{g.Name}' member '{m}'");
            foreach (var r in g.Roles ?? []) CheckRef(r, "role", $"group '{g.Name}' role '{r}'");
        }
        foreach (var p in manifest.Positions)
            foreach (var grant in p.Grants ?? [])
                CheckRef(grant, "user", $"position '{p.AccountName}' grant '{grant}'");

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

        return errors.Count > 0 ? errors : new ManifestIdentity(declared);
    }

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
        public const string Positions = "positions";
    }
}
