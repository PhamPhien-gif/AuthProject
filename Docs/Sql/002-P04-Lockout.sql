BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009145707_AddLockoutFailureWindow'
)
BEGIN
    ALTER TABLE [Users] ADD [FailedLoginWindowStart] datetimeoffset NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009145707_AddLockoutFailureWindow'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261009145707_AddLockoutFailureWindow', N'10.0.12');
END;

COMMIT;
GO

