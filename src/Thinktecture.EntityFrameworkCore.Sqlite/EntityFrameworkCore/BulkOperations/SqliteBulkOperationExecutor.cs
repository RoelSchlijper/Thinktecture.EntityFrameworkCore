using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Thinktecture.EntityFrameworkCore.Data;
using Thinktecture.EntityFrameworkCore.TempTables;
using Thinktecture.Internal;

namespace Thinktecture.EntityFrameworkCore.BulkOperations;

/// <summary>
/// Executes bulk operations.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
[SuppressMessage("ReSharper", "EF1001")]
public sealed class SqliteBulkOperationExecutor
   : IBulkInsertExecutor, ITempTableBulkInsertExecutor, IBulkUpdateExecutor,
     IBulkInsertOrUpdateExecutor, ITruncateTableExecutor
{
   private readonly DbContext _ctx;
   private readonly IDiagnosticsLogger<SqliteDbLoggerCategory.BulkOperation> _logger;
   private readonly ISqlGenerationHelper _sqlGenerationHelper;
   private readonly ObjectPool<StringBuilder> _stringBuilderPool;

   private static class EventIds
   {
      public static readonly EventId Started = 0;
      public static readonly EventId Finished = 1;
   }

   /// <summary>
   /// Initializes new instance of <see cref="SqliteBulkOperationExecutor"/>.
   /// </summary>
   /// <param name="ctx">Current database context.</param>
   /// <param name="logger">Logger.</param>
   /// <param name="sqlGenerationHelper">SQL generation helper.</param>
   /// <param name="stringBuilderPool">String builder pool.</param>
   public SqliteBulkOperationExecutor(
      ICurrentDbContext ctx,
      IDiagnosticsLogger<SqliteDbLoggerCategory.BulkOperation> logger,
      ISqlGenerationHelper sqlGenerationHelper,
      ObjectPool<StringBuilder> stringBuilderPool)
   {
      ArgumentNullException.ThrowIfNull(ctx);

      _ctx = ctx.Context ?? throw new ArgumentNullException(nameof(ctx));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
      _sqlGenerationHelper = sqlGenerationHelper ?? throw new ArgumentNullException(nameof(sqlGenerationHelper));
      _stringBuilderPool = stringBuilderPool ?? throw new ArgumentNullException(nameof(stringBuilderPool));
   }

   /// <inheritdoc />
   IBulkInsertOptions IBulkInsertExecutor.CreateOptions(IEntityPropertiesProvider? propertiesToInsert)
   {
      return new SqliteBulkInsertOptions { PropertiesToInsert = propertiesToInsert };
   }

   /// <inheritdoc />
   ITempTableBulkInsertOptions ITempTableBulkInsertExecutor.CreateOptions(IEntityPropertiesProvider? propertiesToInsert)
   {
      return new SqliteTempTableBulkInsertOptions { PropertiesToInsert = propertiesToInsert };
   }

   /// <inheritdoc />
   IBulkUpdateOptions IBulkUpdateExecutor.CreateOptions(IEntityPropertiesProvider? propertiesToUpdate, IEntityPropertiesProvider? keyProperties)
   {
      return new SqliteBulkUpdateOptions
             {
                PropertiesToUpdate = propertiesToUpdate,
                KeyProperties = keyProperties
             };
   }

   /// <inheritdoc />
   IBulkInsertOrUpdateOptions IBulkInsertOrUpdateExecutor.CreateOptions(
      IEntityPropertiesProvider? propertiesToInsert,
      IEntityPropertiesProvider? propertiesToUpdate,
      IEntityPropertiesProvider? keyProperties)
   {
      return new SqliteBulkInsertOrUpdateOptions
             {
                PropertiesToInsert = propertiesToInsert,
                PropertiesToUpdate = propertiesToUpdate,
                KeyProperties = keyProperties
             };
   }

   /// <inheritdoc />
   public async Task BulkInsertAsync<T>(
      IEnumerable<T> entities,
      IBulkInsertOptions options,
      CancellationToken cancellationToken = default)
      where T : class
   {
      ArgumentNullException.ThrowIfNull(entities);
      ArgumentNullException.ThrowIfNull(options);

      var entityType = _ctx.Model.GetEntityType(typeof(T));
      var tableName = entityType.GetTableName() ?? throw new InvalidOperationException($"The entity '{entityType.Name}' has no table name.");

      if (options is not SqliteBulkInsertOptions sqliteOptions)
         sqliteOptions = new SqliteBulkInsertOptions(options);

      await BulkInsertAsync(entityType, entities, entityType.GetSchema(), tableName, sqliteOptions, cancellationToken);
   }

   private async Task BulkInsertAsync<T>(IEntityType entityType, IEnumerable<T> entities, string? schema, string tableName, SqliteBulkInsertOptions options, CancellationToken cancellationToken)
      where T : class
   {
      var properties = options.PropertiesToInsert.DeterminePropertiesForInsert(entityType, null);
      properties.EnsureNoSeparateOwnedTypesInsideCollectionOwnedType();

      var ctx = new BulkInsertContext(_ctx.GetService<IEntityDataReaderFactory>(),
                                      (SqliteConnection)_ctx.Database.GetDbConnection(),
                                      options,
                                      properties);

      await ExecuteBulkOperationAsync(entities, schema, tableName, ctx, cancellationToken);
   }

   /// <inheritdoc />
   public async Task<int> BulkUpdateAsync<T>(
      IEnumerable<T> entities,
      IBulkUpdateOptions options,
      CancellationToken cancellationToken = default)
      where T : class
   {
      ArgumentNullException.ThrowIfNull(entities);
      ArgumentNullException.ThrowIfNull(options);

      var entityType = _ctx.Model.GetEntityType(typeof(T));

      var ctx = new BulkUpdateContext(_ctx.GetService<IEntityDataReaderFactory>(),
                                      (SqliteConnection)_ctx.Database.GetDbConnection(),
                                      options.KeyProperties.DetermineKeyProperties(entityType, true),
                                      options.PropertiesToUpdate.DeterminePropertiesForUpdate(entityType, null));
      var tableName = entityType.GetTableName()
                      ?? throw new Exception($"The entity '{entityType.Name}' has no table name.");

      return await ExecuteBulkOperationAsync(entities, entityType.GetSchema(), tableName, ctx, cancellationToken);
   }

   /// <inheritdoc />
   public async Task<int> BulkInsertOrUpdateAsync<T>(
      IEnumerable<T> entities,
      IBulkInsertOrUpdateOptions options,
      CancellationToken cancellationToken = default)
      where T : class
   {
      ArgumentNullException.ThrowIfNull(entities);
      ArgumentNullException.ThrowIfNull(options);

      if (!(options is ISqliteBulkInsertOrUpdateOptions sqliteOptions))
         sqliteOptions = new SqliteBulkInsertOrUpdateOptions(options);

      var entityType = _ctx.Model.GetEntityType(typeof(T));
      var ctx = new BulkInsertOrUpdateContext(_ctx.GetService<IEntityDataReaderFactory>(),
                                              (SqliteConnection)_ctx.Database.GetDbConnection(),
                                              sqliteOptions.KeyProperties.DetermineKeyProperties(entityType, true),
                                              sqliteOptions.PropertiesToInsert.DeterminePropertiesForInsert(entityType, null),
                                              sqliteOptions.PropertiesToUpdate.DeterminePropertiesForUpdate(entityType, true));
      var tableName = entityType.GetTableName()
                      ?? throw new Exception($"The entity '{entityType.Name}' has no table name.");

      return await ExecuteBulkOperationAsync(entities, entityType.GetSchema(), tableName, ctx, cancellationToken);
   }

   private async Task<int> ExecuteBulkOperationAsync<T>(
      IEnumerable<T> entities,
      string? schema,
      string tableName,
      ISqliteBulkOperationContext bulkOperationContext,
      CancellationToken cancellationToken)
      where T : class
   {
      await _ctx.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

      try
      {
         var tableIdentifier = _sqlGenerationHelper.DelimitIdentifier(tableName, schema);

         using var reader = bulkOperationContext.ReaderFactory.Create(_ctx, entities, bulkOperationContext.Properties, bulkOperationContext.HasExternalProperties);
         var numberOfAffectedRows = await ExecuteBulkOperationAsync(reader, bulkOperationContext, tableIdentifier, cancellationToken).ConfigureAwait(false);

         if (bulkOperationContext.HasExternalProperties)
         {
            var readEntities = reader.GetReadEntities();
            numberOfAffectedRows += await ExecuteBulkOperationForSeparatedOwnedEntitiesAsync(readEntities, bulkOperationContext, cancellationToken);
         }

         return numberOfAffectedRows;
      }
      finally
      {
         await _ctx.Database.CloseConnectionAsync().ConfigureAwait(false);
      }
   }

   private async Task<int> ExecuteBulkOperationForSeparatedOwnedEntitiesAsync(
      IReadOnlyList<object> parentEntities,
      ISqliteBulkOperationContext parentBulkOperationContext,
      CancellationToken cancellationToken)
   {
      if (parentEntities.Count == 0)
         return 0;

      var numberOfAffectedRows = 0;

      foreach (var childContext in parentBulkOperationContext.GetChildren(parentEntities))
      {
         var childTableName = childContext.EntityType.GetTableName()
                              ?? throw new InvalidOperationException($"The entity '{childContext.EntityType.Name}' has no table name.");

         numberOfAffectedRows += await ExecuteBulkOperationAsync(childContext.Entities,
                                                                 childContext.EntityType.GetSchema(),
                                                                 childTableName,
                                                                 childContext,
                                                                 cancellationToken).ConfigureAwait(false);
      }

      return numberOfAffectedRows;
   }

   private async Task<int> ExecuteBulkOperationAsync(
      IEntityDataReader reader,
      ISqliteBulkOperationContext bulkOperationContext,
      string tableIdentifier,
      CancellationToken cancellationToken)
   {
      await using var command = bulkOperationContext.Connection.CreateCommand();

      command.CommandText = bulkOperationContext.CreateCommandBuilder().GetStatement(_sqlGenerationHelper, _stringBuilderPool, reader, tableIdentifier);
      var parameterInfos = CreateParameters(reader, command);

      try
      {
         command.Prepare();
      }
      catch (SqliteException ex)
      {
         throw new InvalidOperationException($"Error during bulk operation on table '{tableIdentifier}'. See inner exception for more details.", ex);
      }

      LogBulkOperationStart(command.CommandText);
      var stopwatch = Stopwatch.StartNew();
      var numberOfAffectedRows = 0;

      while (reader.Read())
      {
         for (var i = 0; i < reader.FieldCount; i++)
         {
            var paramInfo = parameterInfos[i];
            var value = reader.GetValue(i);

            if (bulkOperationContext.AutoIncrementBehavior == SqliteAutoIncrementBehavior.SetZeroToNull && paramInfo.IsAutoIncrementColumn && 0.Equals(value))
               value = null;

            paramInfo.Parameter.Value = value ?? DBNull.Value;
         }

         numberOfAffectedRows += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
      }

      stopwatch.Stop();
      LogBulkOperationEnd(command.CommandText, stopwatch.Elapsed);

      return numberOfAffectedRows;
   }

   private static ParameterInfo[] CreateParameters(IEntityDataReader reader, SqliteCommand command)
   {
      var parameters = new ParameterInfo[reader.Properties.Count];

      for (var i = 0; i < reader.Properties.Count; i++)
      {
         var property = reader.Properties[i];
         var index = reader.GetPropertyIndex(property);

         var parameter = command.CreateParameter();
         parameter.ParameterName = $"$p{index}";
         parameters[i] = new ParameterInfo(parameter, property.Property.IsAutoIncrement());
         command.Parameters.Add(parameter);
      }

      return parameters;
   }

   private void LogBulkOperationStart(string statement)
   {
      _logger.Logger.LogDebug(EventIds.Started, @"Executing DbCommand
{Statement}", statement);
   }

   private void LogBulkOperationEnd(string statement, TimeSpan duration)
   {
      _logger.Logger.LogInformation(EventIds.Finished, @"Executed DbCommand ({Duration}ms)
{Statement}", (long)duration.TotalMilliseconds, statement);
   }

   /// <inheritdoc />
   public async Task<ITempTableQuery<T>> BulkInsertIntoTempTableAsync<T>(
      IEnumerable<T> entities,
      ITempTableBulkInsertOptions options,
      CancellationToken cancellationToken = default)
      where T : class
   {
      ArgumentNullException.ThrowIfNull(entities);
      ArgumentNullException.ThrowIfNull(options);

      var entityType = _ctx.Model.GetEntityType(typeof(T));

      if (options is not SqliteTempTableBulkInsertOptions sqliteOptions)
         sqliteOptions = new SqliteTempTableBulkInsertOptions(options);

      return await BulkInsertIntoTempTableAsync(entityType, entities, sqliteOptions, cancellationToken);
   }

   private async Task<ITempTableQuery<T>> BulkInsertIntoTempTableAsync<T>(
      IEntityType entityType,
      IEnumerable<T> entities,
      SqliteTempTableBulkInsertOptions options,
      CancellationToken cancellationToken)
      where T : class
   {
      var selectedProperties = options.PropertiesToInsert.DeterminePropertiesForTempTable(entityType, null);

      if (selectedProperties.Any(p => !p.IsInlined))
         throw new NotSupportedException($"Bulk insert of separate owned types into temp tables is not supported. Properties of separate owned types: {String.Join(", ", selectedProperties.Where(p => !p.IsInlined))}");

      var tempTableCreator = _ctx.GetService<ITempTableCreator>();
      var tempTableCreationOptions = options.GetTempTableCreationOptions();
      var tempTableReference = await tempTableCreator.CreateTempTableAsync(entityType, tempTableCreationOptions, cancellationToken).ConfigureAwait(false);

      try
      {
         var bulkInsertOptions = options.GetBulkInsertOptions();
         await BulkInsertAsync(entityType, entities, null, tempTableReference.Name, bulkInsertOptions, cancellationToken).ConfigureAwait(false);

         var query = _ctx.Set<T>().FromTempTable(tempTableReference.Name);

         var pk = entityType.FindPrimaryKey();

         if (pk is not null && pk.Properties.Count != 0)
            query = query.AsNoTracking();

         return new TempTableQuery<T>(query, tempTableReference);
      }
      catch (Exception)
      {
         await tempTableReference.DisposeAsync().ConfigureAwait(false);
         throw;
      }
   }

   /// <inheritdoc />
   public async Task TruncateTableAsync<T>(CancellationToken cancellationToken = default)
      where T : class
   {
      var entityType = _ctx.Model.GetEntityType(typeof(T));
      var tableName = entityType.GetTableName()
                      ?? throw new InvalidOperationException($"The entity '{entityType.Name}' has no table name.");

      var tableIdentifier = _sqlGenerationHelper.DelimitIdentifier(tableName, entityType.GetSchema());
      var truncateStatement = $"DELETE FROM {tableIdentifier};";

      await _ctx.Database.ExecuteSqlRawAsync(truncateStatement, cancellationToken);
   }

   private readonly record struct ParameterInfo(SqliteParameter Parameter, bool IsAutoIncrementColumn);
}
