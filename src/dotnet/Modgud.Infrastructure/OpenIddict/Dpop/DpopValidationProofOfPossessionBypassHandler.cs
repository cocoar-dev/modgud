using OpenIddict.Validation;
using static OpenIddict.Validation.OpenIddictValidationEvents;
using static OpenIddict.Validation.OpenIddictValidationHandlers;

namespace Modgud.Infrastructure.OpenIddict.Dpop;

/// <summary>
/// MG-FT-05 — lets DPoP-bound access tokens authenticate through the
/// validation (Bearer) pipeline. OpenIddict 7.6's stock
/// <c>ValidateProofOfPossession</c> handler only understands mTLS-bound
/// tokens (<c>cnf.x5t#S256</c>) and hard-rejects any other confirmation
/// claim — including the <c>cnf.jkt</c> our <see cref="DpopConfirmationClaimHandler"/>
/// stamps onto DPoP-bound access tokens ("The specified token binding method
/// is invalid or not supported").
///
/// <para>This handler flags the per-context bypass so such tokens validate;
/// proof-of-possession is NOT dropped — every Modgud endpoint that accepts a
/// DPoP-bound token enforces the binding itself by validating the presented
/// <c>DPoP</c> proof against the expected key (the staffing begin
/// endpoint pins <c>TerminalEnrollment.DpopJkt</c>), and external resource
/// servers enforce <c>cnf.jkt</c> via <c>Modgud.AspNetCore.ResourceServer</c>.
/// Wiring generic DPoP-scheme extraction + enforcement into the validation
/// pipeline itself is a hardening follow-up.</para>
/// </summary>
public sealed class DpopValidationProofOfPossessionBypassHandler : IOpenIddictValidationHandler<ValidateTokenContext>
{
    public static OpenIddictValidationHandlerDescriptor Descriptor { get; }
        = OpenIddictValidationHandlerDescriptor.CreateBuilder<ValidateTokenContext>()
            .UseSingletonHandler<DpopValidationProofOfPossessionBypassHandler>()
            // Before the stock ValidateProofOfPossession reads the flag.
            .SetOrder(Protection.ValidateProofOfPossession.Descriptor.Order - 1)
            .SetType(OpenIddictValidationHandlerType.Custom)
            .Build();

    public ValueTask HandleAsync(ValidateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.DisableProofOfPossessionValidation = true;
        return default;
    }
}
