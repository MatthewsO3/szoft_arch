using Microsoft.AspNetCore.Mvc;

namespace QuizGame.Server.Controllers;

/// <summary>RFC 7807 ProblemDetails with a stable machine-readable "code" extension (api-contract §3).</summary>
public static class ApiProblem
{
    public static ObjectResult Create(int status, string code, string detail) =>
        new(new ProblemDetails
        {
            Status = status,
            Title = code,
            Detail = detail,
            Extensions = { ["code"] = code },
        })
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
}
