using AuthProject.Configuration;
using AuthProject.DTOs;
using AuthProject.Exceptions;
using AuthProject.Repositories.Interfaces;
using AuthProject.Security.Interfaces;
using AuthProject.Security;
using AuthProject.Services.Interfaces;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace AuthProject.Services;

public sealed class RegistrationService(IRegistrationStore store, IPasswordHashService passwords, IOptions<RegistrationOptions> options,
    IValidator<RegisterRequest> registerValidator, IValidator<EmailRequest> emailValidator, IValidator<VerifyEmailRequest> verifyValidator) : IRegistrationService
{
    private void EnsureEnabled() { if (!options.Value.Enabled) throw new AppException("SERVICE_UNAVAILABLE"); }

    public async Task RegisterAsync(RegisterRequest request, string traceId, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        if (!(await registerValidator.ValidateAsync(request, cancellationToken)).IsValid) throw new AppException("VALIDATION_FAILED");
        var hash = passwords.Hash(new AuthProject.Entities.User(), request.Password!); // Same hashing work for duplicate accounts; preserve password whitespace.
        await store.RegisterAsync(request.Email!.Trim(), request.DisplayName!.Trim(), hash, traceId, cancellationToken);
    }

    public async Task ResendAsync(EmailRequest request, string traceId, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        if (!(await emailValidator.ValidateAsync(request, cancellationToken)).IsValid) throw new AppException("VALIDATION_FAILED");
        await store.ResendAsync(request.Email!.Trim(), traceId, cancellationToken);
    }

    public async Task VerifyAsync(VerifyEmailRequest request, string traceId, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        if (!(await verifyValidator.ValidateAsync(request, cancellationToken)).IsValid) throw new AppException("VALIDATION_FAILED");
        var digest = EmailVerificationToken.Digest(request.Token);
        if (digest is null || !await store.VerifyAsync(digest, traceId, cancellationToken)) throw new AppException("AUTH_CHALLENGE_INVALID");
    }
}
