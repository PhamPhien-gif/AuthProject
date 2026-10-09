IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE TABLE [OutboxMessages] (
        [Id] uniqueidentifier NOT NULL,
        [EventId] uniqueidentifier NOT NULL,
        [Kind] varchar(16) NOT NULL,
        [ProtectedPayload] nvarchar(max) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [NextAttemptAt] datetimeoffset NOT NULL,
        [Attempts] int NOT NULL,
        [ProcessedAt] datetimeoffset NULL,
        [LeaseUntil] datetimeoffset NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_OutboxMessages] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_OutboxMessages_Attempts] CHECK ([Attempts] >= 0)
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE TABLE [Roles] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(64) NOT NULL,
        [NormalizedName] nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [IsSystem] bit NOT NULL,
        CONSTRAINT [PK_Roles] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE TABLE [Users] (
        [Id] uniqueidentifier NOT NULL,
        [Email] nvarchar(254) NOT NULL,
        [NormalizedEmail] nvarchar(254) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [DisplayName] nvarchar(100) NOT NULL,
        [PasswordHash] nvarchar(512) NULL,
        [EmailConfirmedAt] datetimeoffset NULL,
        [Status] varchar(32) NOT NULL,
        [MfaEmailEnabled] bit NOT NULL,
        [AuthVersion] bigint NOT NULL,
        [FailedLoginCount] int NOT NULL,
        [LockoutEnd] datetimeoffset NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_Users] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Users_Counters] CHECK ([AuthVersion] >= 1 AND [FailedLoginCount] >= 0),
        CONSTRAINT [CK_Users_Email] CHECK (LEN([Email]) > 0 AND LEN([NormalizedEmail]) > 0),
        CONSTRAINT [CK_Users_Status] CHECK ([Status] IN ('PendingVerification','Active','Disabled'))
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE TABLE [AuditEvents] (
        [Id] uniqueidentifier NOT NULL,
        [ActorUserId] uniqueidentifier NULL,
        [SubjectId] uniqueidentifier NULL,
        [EventType] varchar(64) NOT NULL,
        [Outcome] varchar(16) NOT NULL,
        [ReasonCode] varchar(32) NOT NULL,
        [OccurredAt] datetimeoffset NOT NULL,
        [TraceId] varchar(64) NOT NULL,
        [SafeMetadata] nvarchar(1024) NOT NULL,
        CONSTRAINT [PK_AuditEvents] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_AuditEvents_Metadata] CHECK (ISJSON([SafeMetadata]) = 1),
        CONSTRAINT [FK_AuditEvents_Users_ActorUserId] FOREIGN KEY ([ActorUserId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE TABLE [AuthChallenges] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [Purpose] varchar(32) NOT NULL,
        [SecretDigest] char(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [KeyId] varchar(64) NULL,
        [ContextId] uniqueidentifier NOT NULL,
        [Version] int NOT NULL,
        [AuthVersion] bigint NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [ExpiresAt] datetimeoffset NOT NULL,
        [Attempts] int NOT NULL,
        [SendCount] int NOT NULL,
        [NextSendAt] datetimeoffset NULL,
        [ConsumedAt] datetimeoffset NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_AuthChallenges] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_AuthChallenges_Counters] CHECK ([Attempts] >= 0 AND [SendCount] >= 0 AND [Version] >= 1 AND [AuthVersion] >= 1),
        CONSTRAINT [CK_AuthChallenges_Digest] CHECK ([SecretDigest] NOT LIKE '%[^0-9A-F]%' AND LEN([SecretDigest]) = 64),
        CONSTRAINT [CK_AuthChallenges_Expiry] CHECK ([ExpiresAt] > [CreatedAt]),
        CONSTRAINT [FK_AuthChallenges_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE TABLE [AuthSessions] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [AuthVersion] bigint NOT NULL,
        [AuthenticatedAt] datetimeoffset NOT NULL,
        [MfaVerifiedAt] datetimeoffset NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [LastSeenAt] datetimeoffset NOT NULL,
        [IdleExpiresAt] datetimeoffset NOT NULL,
        [AbsoluteExpiresAt] datetimeoffset NOT NULL,
        [RevokedAt] datetimeoffset NULL,
        [RevokeReason] varchar(64) NULL,
        [ClientInfo] nvarchar(256) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_AuthSessions] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_AuthSessions_Lifetime] CHECK ([AuthVersion] >= 1 AND [AbsoluteExpiresAt] > [CreatedAt] AND [IdleExpiresAt] <= [AbsoluteExpiresAt] AND [IdleExpiresAt] > [CreatedAt]),
        CONSTRAINT [FK_AuthSessions_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE TABLE [ExternalLogins] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [Provider] varchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [ProviderSubject] nvarchar(255) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_ExternalLogins] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ExternalLogins_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE TABLE [RecoveryCodes] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [CodeHash] char(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UsedAt] datetimeoffset NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_RecoveryCodes] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_RecoveryCodes_Hash] CHECK ([CodeHash] NOT LIKE '%[^0-9A-F]%' AND LEN([CodeHash]) = 64),
        CONSTRAINT [FK_RecoveryCodes_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE TABLE [UserRoles] (
        [UserId] uniqueidentifier NOT NULL,
        [RoleId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_UserRoles] PRIMARY KEY ([UserId], [RoleId]),
        CONSTRAINT [FK_UserRoles_Roles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [Roles] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_UserRoles_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE TABLE [LoginHistories] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NULL,
        [Method] varchar(32) NOT NULL,
        [Outcome] varchar(16) NOT NULL,
        [ReasonCode] varchar(32) NOT NULL,
        [OccurredAt] datetimeoffset NOT NULL,
        [IpAddress] varchar(45) NULL,
        [UserAgent] nvarchar(256) NULL,
        [SessionId] uniqueidentifier NULL,
        [TraceId] varchar(64) NOT NULL,
        CONSTRAINT [PK_LoginHistories] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_LoginHistories_AuthSessions_SessionId] FOREIGN KEY ([SessionId]) REFERENCES [AuthSessions] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_LoginHistories_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE TABLE [RefreshTokens] (
        [Id] uniqueidentifier NOT NULL,
        [SessionId] uniqueidentifier NOT NULL,
        [TokenHash] char(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [ExpiresAt] datetimeoffset NOT NULL,
        [ConsumedAt] datetimeoffset NULL,
        [RevokedAt] datetimeoffset NULL,
        [ReplacedById] uniqueidentifier NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_RefreshTokens] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_RefreshTokens_Expiry] CHECK ([ExpiresAt] > [CreatedAt]),
        CONSTRAINT [CK_RefreshTokens_Hash] CHECK ([TokenHash] NOT LIKE '%[^0-9A-F]%' AND LEN([TokenHash]) = 64),
        CONSTRAINT [FK_RefreshTokens_AuthSessions_SessionId] FOREIGN KEY ([SessionId]) REFERENCES [AuthSessions] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_RefreshTokens_RefreshTokens_ReplacedById] FOREIGN KEY ([ReplacedById]) REFERENCES [RefreshTokens] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'IsSystem', N'Name', N'NormalizedName') AND [object_id] = OBJECT_ID(N'[Roles]'))
        SET IDENTITY_INSERT [Roles] ON;
    EXEC(N'INSERT INTO [Roles] ([Id], [IsSystem], [Name], [NormalizedName])
    VALUES (''43c28b39-b52b-42eb-9c74-2f59caeaa428'', CAST(1 AS bit), N''Admin'', N''ADMIN''),
    (''cb6a0875-a601-4371-8c71-3613fa83e745'', CAST(1 AS bit), N''User'', N''USER'')');
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'IsSystem', N'Name', N'NormalizedName') AND [object_id] = OBJECT_ID(N'[Roles]'))
        SET IDENTITY_INSERT [Roles] OFF;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE INDEX [IX_AuditEvents_ActorUserId_OccurredAt] ON [AuditEvents] ([ActorUserId], [OccurredAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE INDEX [IX_AuditEvents_EventType_OccurredAt] ON [AuditEvents] ([EventType], [OccurredAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE INDEX [IX_AuditEvents_OccurredAt] ON [AuditEvents] ([OccurredAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE INDEX [IX_AuthChallenges_ExpiresAt] ON [AuthChallenges] ([ExpiresAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE INDEX [IX_AuthChallenges_UserId_Purpose_ContextId] ON [AuthChallenges] ([UserId], [Purpose], [ContextId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE INDEX [IX_AuthSessions_AbsoluteExpiresAt] ON [AuthSessions] ([AbsoluteExpiresAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE INDEX [IX_AuthSessions_UserId_RevokedAt] ON [AuthSessions] ([UserId], [RevokedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ExternalLogins_Provider_ProviderSubject] ON [ExternalLogins] ([Provider], [ProviderSubject]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ExternalLogins_UserId_Provider] ON [ExternalLogins] ([UserId], [Provider]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE INDEX [IX_LoginHistories_SessionId] ON [LoginHistories] ([SessionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE INDEX [IX_LoginHistories_UserId_OccurredAt] ON [LoginHistories] ([UserId], [OccurredAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE UNIQUE INDEX [IX_OutboxMessages_EventId_Kind] ON [OutboxMessages] ([EventId], [Kind]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_OutboxMessages_NextAttemptAt] ON [OutboxMessages] ([NextAttemptAt]) WHERE [ProcessedAt] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RecoveryCodes_CodeHash] ON [RecoveryCodes] ([CodeHash]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE INDEX [IX_RecoveryCodes_UserId_UsedAt] ON [RecoveryCodes] ([UserId], [UsedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE INDEX [IX_RefreshTokens_ExpiresAt] ON [RefreshTokens] ([ExpiresAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE INDEX [IX_RefreshTokens_ReplacedById] ON [RefreshTokens] ([ReplacedById]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_RefreshTokens_SessionId] ON [RefreshTokens] ([SessionId]) WHERE [ConsumedAt] IS NULL AND [RevokedAt] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE INDEX [IX_RefreshTokens_SessionId_CreatedAt] ON [RefreshTokens] ([SessionId], [CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RefreshTokens_TokenHash] ON [RefreshTokens] ([TokenHash]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Roles_NormalizedName] ON [Roles] ([NormalizedName]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE INDEX [IX_UserRoles_RoleId] ON [UserRoles] ([RoleId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Users_NormalizedEmail] ON [Users] ([NormalizedEmail]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045104_InitialAuth'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261009045104_InitialAuth', N'10.0.12');
END;

COMMIT;
GO

