BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261010132626_AddAuditDeliveryState'
)
BEGIN
    ALTER TABLE [OutboxMessages] ADD [DeadLetteredAt] datetimeoffset NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261010132626_AddAuditDeliveryState'
)
BEGIN
    ALTER TABLE [OutboxMessages] ADD [LastErrorCode] varchar(32) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261010132626_AddAuditDeliveryState'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261010132626_AddAuditDeliveryState', N'10.0.12');
END;

COMMIT;
GO

