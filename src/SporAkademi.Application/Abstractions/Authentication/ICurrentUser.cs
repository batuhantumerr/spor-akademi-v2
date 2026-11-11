namespace SporAkademi.Application.Abstractions.Authentication;

public enum Role
{
    PlatformAdmin,
    SchoolAdmin,
    Coach,

    /// <summary>P3-01 (DEC-382): sees only the students of the guardian whose account it is.</summary>
    Parent,
}

/// <summary>The caller of the current request, from the access token. Empty for anonymous calls and background work.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    Guid? UserId { get; }

    /// <summary>The caller's school. <c>null</c> for the Platform Admin and for anonymous calls.</summary>
    Guid? TenantId { get; }

    Role? Role { get; }
}
