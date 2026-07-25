using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using RuntimeMetricsLab.Experiments.Coordination;

namespace RuntimeMetricsLab.ApiService.Diagnostics;

/// <summary>
/// Maps every <see cref="ExperimentException"/> raised by <see cref="ExperimentCoordinator"/> onto
/// a proper RFC 9457 <see cref="ProblemDetails"/> response, with the stable machine-readable
/// <c>errorCode</c> always present in <c>Extensions</c> and case-specific extra fields where useful
/// (e.g. <c>cooldownUntil</c>, <c>errors</c>).
/// </summary>
public sealed partial class ExperimentExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ExperimentException experimentException)
        {
            return false;
        }

        var (statusCode, title) = experimentException switch
        {
            ExperimentNotFoundException => (StatusCodes.Status404NotFound, "Experiment not found"),
            PresetNotFoundException => (StatusCodes.Status404NotFound, "Preset not found"),
            ExperimentRunAlreadyActiveException => (StatusCodes.Status409Conflict, "Experiment run already active"),
            ExperimentCoolingDownException => (StatusCodes.Status429TooManyRequests, "Experiment is cooling down"),
            ExperimentValidationException => (StatusCodes.Status400BadRequest, "Invalid experiment parameters"),
            ExperimentNotRunningException => (StatusCodes.Status409Conflict, "No active run"),
            _ => (StatusCodes.Status500InternalServerError, "Experiment error"),
        };

        httpContext.Response.StatusCode = statusCode;

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = experimentException.Message,
            Type = $"https://runtimemetricslab.dev/errors/{ToKebabCase(experimentException.ErrorCode.ToString())}",
        };
        problemDetails.Extensions["errorCode"] = experimentException.ErrorCode.ToString();

        switch (experimentException)
        {
            case ExperimentCoolingDownException cooling:
                problemDetails.Extensions["cooldownUntil"] = cooling.CooldownUntil;
                break;
            case ExperimentValidationException validation:
                problemDetails.Extensions["errors"] = validation.Errors;
                break;
        }

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
            Exception = exception,
        });
    }

    private static string ToKebabCase(string value) => KebabCasePattern().Replace(value, "-$1").ToLowerInvariant().TrimStart('-');

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex KebabCasePattern();
}
