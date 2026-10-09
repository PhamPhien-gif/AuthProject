using AuthProject.Entities;

namespace AuthProject.DTOs;

// Safe read model; credential hashes and mutable entities never cross the API boundary.
public sealed record UserProfile(Guid Id, string Email, string DisplayName, bool EmailConfirmed,
    UserStatus Status, bool MfaEmailEnabled, string[] Roles);
