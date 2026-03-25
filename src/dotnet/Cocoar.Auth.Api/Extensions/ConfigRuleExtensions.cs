using Cocoar.Configuration.Fluent;
using Cocoar.Configuration.Providers;
using Cocoar.Configuration.Rules;

namespace Cocoar.Auth.Api.Extensions;

/// <summary>
/// Shorthand for the standard layered config pattern:
/// base file → environment-specific file → environment variables.
/// </summary>
public static class ConfigRuleExtensions
{
	public static AggregateRuleBuilder<T> Layered<T>(
		this TypedRuleBuilder<T> builder,
		string fileName,
		string envPrefix,
		string environment) where T : class
	{
		return builder.Aggregate(r => [
			r.FromFile($"configs/{fileName}.json"),
			r.FromFile($"configs/{fileName}.{environment}.json"),
			r.FromEnvironment(envPrefix),
		]);
	}
}
