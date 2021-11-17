using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Thinktecture.EntityFrameworkCore.TempTables;

namespace Thinktecture.EntityFrameworkCore.Query
{
   /// <summary>
   /// Extends the capabilities of <see cref="RelationalQueryableMethodTranslatingExpressionVisitor"/>.
   /// </summary>
   [SuppressMessage("ReSharper", "EF1001")]
   public class ThinktectureSqlServerQueryableMethodTranslatingExpressionVisitor
      : RelationalQueryableMethodTranslatingExpressionVisitor
   {
      private readonly IRelationalTypeMappingSource _typeMappingSource;
      private readonly TableHintContextFactory _tableHintContextFactory;
      private readonly TempTableQueryContextFactory _tempTableQueryContextFactory;

      /// <inheritdoc />
      public ThinktectureSqlServerQueryableMethodTranslatingExpressionVisitor(
         QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
         RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies,
         QueryCompilationContext queryCompilationContext,
         IRelationalTypeMappingSource typeMappingSource)
         : base(dependencies, relationalDependencies, queryCompilationContext)
      {
         _typeMappingSource = typeMappingSource ?? throw new ArgumentNullException(nameof(typeMappingSource));
         _tableHintContextFactory = new TableHintContextFactory();
         _tempTableQueryContextFactory = new TempTableQueryContextFactory();
      }

      /// <inheritdoc />
      protected ThinktectureSqlServerQueryableMethodTranslatingExpressionVisitor(
         ThinktectureSqlServerQueryableMethodTranslatingExpressionVisitor parentVisitor,
         IRelationalTypeMappingSource typeMappingSource)
         : base(parentVisitor)
      {
         _typeMappingSource = typeMappingSource ?? throw new ArgumentNullException(nameof(typeMappingSource));
         _tableHintContextFactory = parentVisitor._tableHintContextFactory;
         _tempTableQueryContextFactory = parentVisitor._tempTableQueryContextFactory;
      }

      /// <inheritdoc />
      protected override QueryableMethodTranslatingExpressionVisitor CreateSubqueryVisitor()
      {
         return new ThinktectureSqlServerQueryableMethodTranslatingExpressionVisitor(this, _typeMappingSource);
      }

      /// <inheritdoc />
      protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
      {
         return this.TranslateRelationalMethods(methodCallExpression, QueryCompilationContext, _tableHintContextFactory) ??
                this.TranslateBulkMethods(methodCallExpression, _typeMappingSource, QueryCompilationContext, _tempTableQueryContextFactory) ??
                base.VisitMethodCall(methodCallExpression);
      }
   }
}
