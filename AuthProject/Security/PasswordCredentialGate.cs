using AuthProject.DTOs;
using AuthProject.Entities;
using AuthProject.Exceptions;
using AuthProject.Security.Interfaces;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;

namespace AuthProject.Security;

public sealed class DummyPasswordHash(IOptions<PasswordHasherOptions> options)
{
    public string Hash { get; } = new PasswordHashService(options).Hash(new User(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
}

public sealed class PasswordCredentialGate(IPasswordAttemptStore store, IPasswordHashService hasher,
    DummyPasswordHash dummy, IValidator<LoginRequest> validator) : IPasswordCredentialGate
{
    public async Task<PasswordAcceptance> CheckAsync(string email, string password, string traceId, CancellationToken cancellationToken = default)
    {
        if (!(await validator.ValidateAsync(new LoginRequest(email, password), cancellationToken)).IsValid)
            throw new AppException("VALIDATION_FAILED");
        var account = await store.FindAsync(email, cancellationToken);
        // Always pay a password verification cost, including locked/unknown/Google-only users.
        var check = hasher.Verify(account ?? new User(), account?.PasswordHash ?? dummy.Hash, password);
        return await store.CompleteAsync(account, check, traceId, cancellationToken)
            ?? throw new AppException("AUTH_INVALID_CREDENTIALS");
    }
}
