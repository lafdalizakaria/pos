using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Application.Abstractions;

public sealed class NotFoundException(string entity, object key)
    : DomainException("not_found", $"{entity} '{key}' was not found.");

/// <summary>The caller is authenticated but lacks the role or the company/site scope.</summary>
public sealed class ForbiddenException(string message) : DomainException("forbidden", message);

/// <summary>A unique business key (code, reference, badge number...) is already used.</summary>
public sealed class ConflictException(string message) : DomainException("conflict", message);
