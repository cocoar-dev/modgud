using Cocoar.Auth.Authentication;

namespace Cocoar.Auth.Api;

internal sealed class NoOpDemoSeedService : IDemoSeedService
{
    public Task<object> ImportAsync(string jsonPath) =>
        Task.FromResult<object>(new { Skipped = true, Reason = "Demo seed not available in IdP baseline." });
}
