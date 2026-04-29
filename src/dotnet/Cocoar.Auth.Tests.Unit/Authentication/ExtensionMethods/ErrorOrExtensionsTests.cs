using Cocoar.Auth.Authentication.ExtensionMethods;
using ErrorOr;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Cocoar.Auth.Tests.Unit.Authentication.ExtensionMethods;

/// <summary>
/// Pins how <see cref="ErrorOrExtensions"/> maps an <see cref="ErrorOr{T}"/> to a
/// minimal-API <see cref="IResult"/>. Every error type maps to a specific status
/// code and shape — admin endpoints rely on these to surface inline form errors,
/// so a regression that downgrades a 400 to a 500 hides validation messages.
/// </summary>
public class ErrorOrExtensionsTests
{
    public class ToResult
    {
        [Fact]
        public void Success_without_factory_returns_ok_with_value()
        {
            ErrorOr<string> input = "hello";

            var result = input.ToResult();

            var ok = Assert.IsType<Ok<string>>(result);
            Assert.Equal("hello", ok.Value);
        }

        [Fact]
        public void Success_with_factory_returns_factory_result()
        {
            ErrorOr<int> input = 42;

            var result = input.ToResult(v => Results.Accepted($"/items/{v}"));

            Assert.IsType<Accepted>(result);
        }

        [Fact]
        public void Error_ignores_factory_and_maps_via_error_type()
        {
            ErrorOr<int> input = Error.NotFound(description: "missing");
            var factoryWasCalled = false;

            var result = input.ToResult(_ => { factoryWasCalled = true; return Results.Ok(); });

            Assert.False(factoryWasCalled);
            AssertStatus(result, StatusCodes.Status404NotFound);
        }
    }

    public class ToCreatedResult
    {
        [Fact]
        public void Success_returns_created_with_location_from_factory()
        {
            ErrorOr<Guid> input = Guid.Parse("11111111-1111-1111-1111-111111111111");

            var result = input.ToCreatedResult(id => $"/users/{id}");

            var created = Assert.IsType<Created<Guid>>(result);
            Assert.Equal("/users/11111111-1111-1111-1111-111111111111", created.Location);
            Assert.Equal(input.Value, created.Value);
        }

        [Fact]
        public void Error_skips_location_factory_and_maps_via_error_type()
        {
            ErrorOr<Guid> input = Error.Validation(description: "bad");
            var factoryWasCalled = false;

            var result = input.ToCreatedResult(_ => { factoryWasCalled = true; return "/x"; });

            Assert.False(factoryWasCalled);
            AssertStatus(result, StatusCodes.Status400BadRequest);
        }
    }

    public class ToNoContentResult
    {
        [Fact]
        public void Success_returns_no_content()
        {
            ErrorOr<Success> input = Result.Success;

            var result = input.ToNoContentResult();

            Assert.IsType<NoContent>(result);
        }

        [Fact]
        public void Error_maps_via_error_type()
        {
            ErrorOr<Success> input = Error.Conflict(description: "duplicate");

            var result = input.ToNoContentResult();

            AssertStatus(result, StatusCodes.Status409Conflict);
        }
    }

    public class ErrorTypeMapping
    {
        [Fact]
        public void Not_found_maps_to_404()
        {
            ErrorOr<string> input = Error.NotFound(description: "missing");

            AssertStatus(input.ToResult(), StatusCodes.Status404NotFound);
        }

        [Fact]
        public void Validation_maps_to_400()
        {
            ErrorOr<string> input = Error.Validation(description: "bad input");

            AssertStatus(input.ToResult(), StatusCodes.Status400BadRequest);
        }

        [Fact]
        public void Unauthorized_maps_to_401()
        {
            ErrorOr<string> input = Error.Unauthorized(description: "no auth");

            // UnauthorizedHttpResult exposes 401 via IStatusCodeHttpResult.
            Assert.IsType<UnauthorizedHttpResult>(input.ToResult());
        }

        [Fact]
        public void Forbidden_maps_to_403()
        {
            ErrorOr<string> input = Error.Forbidden(description: "denied");

            // Results.Forbid() returns ForbidHttpResult — note: it does NOT carry a
            // status code property (status is set by the auth handler), so we just
            // pin the type.
            Assert.IsType<ForbidHttpResult>(input.ToResult());
        }

        [Fact]
        public void Conflict_maps_to_409()
        {
            ErrorOr<string> input = Error.Conflict(description: "already exists");

            AssertStatus(input.ToResult(), StatusCodes.Status409Conflict);
        }

        [Fact]
        public void Failure_maps_to_problem_500()
        {
            ErrorOr<string> input = Error.Failure(description: "boom");

            var result = input.ToResult();

            var problem = Assert.IsType<ProblemHttpResult>(result);
            Assert.Equal(500, problem.StatusCode);
            Assert.Equal("boom", problem.ProblemDetails.Detail);
        }

        [Fact]
        public void Unexpected_maps_to_problem_500()
        {
            ErrorOr<string> input = Error.Unexpected(description: "weird");

            var result = input.ToResult();

            var problem = Assert.IsType<ProblemHttpResult>(result);
            Assert.Equal(500, problem.StatusCode);
        }

        [Fact]
        public void First_error_in_list_drives_the_status_code()
        {
            // The mapper inspects errors[0] — the OAuth flow occasionally returns
            // multiple errors in a single ErrorOr; pin that the order matters.
            var combined = ErrorOr<string>.From(
                new List<Error>
                {
                    Error.NotFound(description: "first"),
                    Error.Validation(description: "second"),
                });

            AssertStatus(combined.ToResult(), StatusCodes.Status404NotFound);
        }
    }

    private static void AssertStatus(IResult result, int expected)
    {
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(expected, status.StatusCode);
    }
}
