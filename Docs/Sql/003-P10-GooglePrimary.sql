BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261010114841_AddPrimaryAuthenticationMethod'
)
BEGIN
    ALTER TABLE [AuthSessions] ADD [PrimaryMethod] varchar(16) NOT NULL DEFAULT 'Password';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261010114841_AddPrimaryAuthenticationMethod'
)
BEGIN
    ALTER TABLE [AuthChallenges] ADD [PrimaryMethod] varchar(16) NOT NULL DEFAULT 'Password';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261010114841_AddPrimaryAuthenticationMethod'
)
BEGIN
    EXEC(N'ALTER TABLE [AuthSessions] ADD CONSTRAINT [CK_AuthSessions_PrimaryMethod] CHECK ([PrimaryMethod] IN (''Password'', ''Google''))');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261010114841_AddPrimaryAuthenticationMethod'
)
BEGIN
    EXEC(N'ALTER TABLE [AuthChallenges] ADD CONSTRAINT [CK_AuthChallenges_PrimaryMethod] CHECK ([PrimaryMethod] IN (''Password'', ''Google''))');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261010114841_AddPrimaryAuthenticationMethod'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261010114841_AddPrimaryAuthenticationMethod', N'10.0.12');
END;

COMMIT;
GO

