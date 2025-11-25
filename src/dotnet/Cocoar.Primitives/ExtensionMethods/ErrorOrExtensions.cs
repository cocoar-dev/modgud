using ErrorOr;

namespace Cocoar.Primitives.ExtensionMethods
{
    public static class ErrorOrExtensions
    {
        public static ErrorOr<ShortGuid> ToShortGuid(this ErrorOr<Guid> errorOr) => errorOr.Then(g => new ShortGuid(g));

        public static async Task<ErrorOr<ShortGuid>> ToShortGuidAsync(this Task<ErrorOr<Guid>> errorOr)
        {
            return (await errorOr).Then(g => new ShortGuid(g));
        }
    }
}
