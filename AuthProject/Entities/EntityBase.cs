namespace AuthProject.Entities;

public abstract class EntityBase
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

public abstract class VersionedEntity : EntityBase
{
    public byte[] RowVersion { get; set; } = [];
}

public enum UserStatus { PendingVerification, Active, Disabled }
public enum ChallengePurpose { VerifyEmail, ResetPassword, LoginMfa, EnrollMfa, StepUp, GoogleCompletion }
public enum AuthenticationMethod { Password, Google, EmailOtp, RecoveryCode }
public enum AuditOutcome { Success, Failure, Pending }
public enum AuditEventType
{
    Registered, EmailVerified, EmailVerificationRequested, LoginFailed, PasswordVerified, LoginCompleted, MfaChallenged, MfaFailed, MfaVerified,
    RefreshRotated, RefreshReused, SessionRevoked, PasswordChanged, PasswordReset,
    RoleChanged, UserStatusChanged, GoogleLinked, GoogleUnlinked, AccessDenied, AccountLocked,
    PasswordResetRequested, Reauthenticated
}
public enum AuditReason { None, InvalidCredentials, Expired, Reused, Revoked, Forbidden, Locked, Disabled, Unverified }
public enum OutboxKind { Audit, Email, PasswordEmail }
