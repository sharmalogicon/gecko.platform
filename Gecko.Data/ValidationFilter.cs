using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
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
///
/// Validator.TryValidateObject stops at the top-level properties, so the rows of
/// a list (charge variants, order-type steps, booking requirements) were never
/// checked: a [Range(0, 365)] credit term of 999 went straight to the database.
/// Rows are validated too, and their errors are keyed the way a screen finds the
/// cell: <c>variants[1].creditTermDays</c>.
/// </summary>
public static class ValidationFilter
{
    private const int MaxDepth = 4;

    public static RouteHandlerBuilder Validate<TRequest>(this RouteHandlerBuilder builder) where TRequest : class
        => builder
            .AddEndpointFilter(async (context, next) =>
            {
                var request = context.Arguments.OfType<TRequest>().FirstOrDefault();
                if (request is null)
                    return TypedResults.Problem(title: "A request body is required.", statusCode: StatusCodes.Status400BadRequest);

                var errors = new List<(string Field, string Message)>();
                Collect(request, prefix: "", errors, depth: 0);
                if (errors.Count == 0)
                    return await next(context);

                return TypedResults.ValidationProblem(errors
                    .GroupBy(e => e.Field, e => e.Message)
                    .ToDictionary(g => g.Key, g => g.ToArray()));
            })
            .ProducesValidationProblem();

    private static void Collect(object instance, string prefix, List<(string Field, string Message)> errors, int depth)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true);
        foreach (var result in results)
            foreach (var member in result.MemberNames.DefaultIfEmpty(""))
                errors.Add((member.Length == 0 ? prefix.TrimEnd('.') : prefix + JsonName(member), result.ErrorMessage ?? "Invalid value."));

        if (depth >= MaxDepth) return;

        foreach (var property in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || property.PropertyType == typeof(string)) continue;
            if (property.GetValue(instance) is not IEnumerable items) continue;

            var index = 0;
            foreach (var item in items)
            {
                if (item is not null && IsRow(item.GetType()))
                    Collect(item, $"{prefix}{JsonName(property.Name)}[{index}].", errors, depth + 1);
                index++;
            }
        }
    }

    /// <summary>A request row is one of our own classes — never a string, a primitive or a framework type.</summary>
    private static bool IsRow(Type type) =>
        type.IsClass && type != typeof(string) && type.Namespace?.StartsWith("Gecko.", StringComparison.Ordinal) == true;

    private static string JsonName(string member) =>
        string.IsNullOrEmpty(member) ? member : char.ToLowerInvariant(member[0]) + member[1..];
}
