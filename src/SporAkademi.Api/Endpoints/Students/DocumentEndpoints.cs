using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using SporAkademi.Api.Errors;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Students.Documents;
using SporAkademi.Domain.Students;

namespace SporAkademi.Api.Endpoints.Students;

/// <summary>
/// Student documents (P2-F6): health reports and photos. Uploads stream through the API so the content can be
/// inspected before anything is stored (DEC-321); reads hand out short-lived signed links (DEC-323).
/// </summary>
internal static class DocumentEndpoints
{
    /// <summary>The largest file plus room for the multipart envelope; every other endpoint keeps 1 MB (NFR-SEC-12).</summary>
    public const long UploadRequestLimit = DocumentRules.MaxBytesOfAnyType + (64 * 1024);

    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1").WithTags("Documents").RequireRole(Role.SchoolAdmin);
        admin.MapPost("/students/{id:guid}/documents", Upload)
            .Produces<StudentDocumentDto>(StatusCodes.Status201Created)
            .ProducesProblems(400, 404, 409, 422)
            .WithMetadata(new RequestSizeLimit(UploadRequestLimit))
            .WithFormOptions(bufferBody: false, memoryBufferThreshold: (int)UploadRequestLimit, multipartBodyLengthLimit: UploadRequestLimit)
            // Bearer tokens only, no cookie authentication on this endpoint, so there is no CSRF to defend against (DEC-321).
            .DisableAntiforgery();
        admin.MapGet("/students/{id:guid}/documents", List).Produces<IReadOnlyList<StudentDocumentDto>>().ProducesProblems(404);
        admin.MapGet("/documents/{id:guid}/link", Link).Produces<DocumentLinkDto>().ProducesProblems(404);
        admin.MapDelete("/documents/{id:guid}", Delete).Produces(StatusCodes.Status204NoContent).ProducesProblems(404);

        // DEC-324: the coach of one of the student's active groups sees the photo; anyone else gets 404.
        var shared = app.MapGroup("/api/v1").WithTags("Documents").RequireRole(Role.SchoolAdmin, Role.Coach);
        shared.MapGet("/students/{id:guid}/photo", Photo).Produces<DocumentLinkDto>().ProducesProblems(404);

        return app;
    }

    private static async Task<IResult> Upload(
        Guid id,
        [FromForm] string? type,
        IFormFile? file,
        ICommandHandler<UploadStudentDocumentCommand, StudentDocumentDto> handler,
        CancellationToken cancellationToken)
    {
        byte[]? content = null;
        if (file is not null)
        {
            if (file.Length > DocumentRules.MaxBytesOfAnyType)
            {
                return ProblemResults.Problem(DocumentErrors.TooLarge);
            }

            content = new byte[file.Length];
            await using var stream = file.OpenReadStream();
            await stream.ReadExactlyAsync(content, cancellationToken);
        }

        var result = await handler.Handle(new UploadStudentDocumentCommand(StudentId.From(id), type, file?.FileName, content), cancellationToken);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/students/{id}/documents", result.Value)
            : ProblemResults.Problem(result.Error);
    }

    private static async Task<IResult> List(
        Guid id,
        IQueryHandler<ListStudentDocumentsQuery, IReadOnlyList<StudentDocumentDto>> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new ListStudentDocumentsQuery(StudentId.From(id)), cancellationToken)).OkOrProblem();

    private static async Task<IResult> Link(
        Guid id,
        IQueryHandler<GetDocumentLinkQuery, DocumentLinkDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetDocumentLinkQuery(StudentDocumentId.From(id)), cancellationToken)).OkOrProblem();

    private static async Task<IResult> Delete(
        Guid id,
        ICommandHandler<DeleteStudentDocumentCommand, Unit> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new DeleteStudentDocumentCommand(StudentDocumentId.From(id)), cancellationToken)).NoContentOrProblem();

    private static async Task<IResult> Photo(
        Guid id,
        IQueryHandler<GetStudentPhotoLinkQuery, DocumentLinkDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetStudentPhotoLinkQuery(StudentId.From(id)), cancellationToken)).OkOrProblem();
}

/// <summary>Raises the request body limit for one endpoint; read by <see cref="RequestSizeLimitMiddleware"/>.</summary>
internal sealed record RequestSizeLimit(long? MaxRequestBodySize) : IRequestSizeLimitMetadata;
