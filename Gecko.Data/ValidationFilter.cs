using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Gecko.Data;

/// <summary>
/// DataAnnotations validation for request bodies, returning 400 ValidationProblem.
///
/// WHY NOT builder.Services.AddValidation(): .NET 10's validation is a source
/// generator that intercepts Map* calls in the Web SDK project only. Endpoints
/// live in module class libraries, so it silently validated nothing — an empty
/// password reached Argon2 and came back as a 500. This runs wherever the
/// endpoint is declared.
/// </summary>
public static class ValidationFilter
{
    public static RouteHandlerBuilder Validate<TRequest>(this RouteHandlerBuilder builder) where TRequest : class
        => builder
            .AddEndpointFilter(async (context, next) =>
            {
                var request = context.Arguments.OfType<TRequest>().FirstOrDefault();
                if (request is null)
                    return TypedResults.Problem(title: "A request body is required.", statusCode: StatusCodes.Status400BadRequest);

                var results = new List<ValidationResult>();
                if (Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true))
                    return await next(context);

                var errors = results
                    .SelectMany(r => (r.MemberNames.Any() ? r.MemberNames : [""]).Select(member => (member, message: r.ErrorMessage ?? "Invalid value.")))
                    .GroupBy(e => JsonName(e.member), e => e.message)
                    .ToDictionary(g => g.Key, g => g.ToArray());

                return TypedResults.ValidationProblem(errors);
            })
            .ProducesValidationProblem();

    private static string JsonName(string member) =>
        string.IsNullOrEmpty(member) ? member : char.ToLowerInvariant(member[0]) + member[1..];
}
