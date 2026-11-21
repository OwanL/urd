using Urd.Core.Sessions;
using static Urd.Server.SessionRoutes;

namespace Urd.Server;

internal static class SessionUserQuestionRoutes {
  internal static void MapSessionUserQuestionRoutes(this RouteGroupBuilder routes, SessionManager sessions) {
    routes.MapGet("/{sessionId}/user-questions", (HttpContext http, Guid sessionId) =>
        WithSessionAsync(http, sessions, sessionId, async session =>
            Results.Ok(await session.ReadUserQuestionsAsync(http.RequestAborted))));
    routes.MapPost("/{sessionId}/user-questions/{questionId}/answer", (HttpContext http, Guid sessionId, Guid questionId) =>
        WithSessionAsync(http, sessions, sessionId, async session => {
          UserAnswerRequest? request = await http.Request.ReadFromJsonAsync<UserAnswerRequest>(http.RequestAborted);
          if (request?.Answer is not { } answer || questionId == Guid.Empty)
            return Results.BadRequest();

          // Once accepted, delivering the answer does not depend on the browser's connection.
          return await session.AnswerUserQuestionAsync(questionId, answer, http.RequestAborted)
              ? Results.Ok(new { answer }) : Results.Conflict();
        }));
  }

  private sealed record UserAnswerRequest(string? Answer);
}
