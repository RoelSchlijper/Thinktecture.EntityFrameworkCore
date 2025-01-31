using Microsoft.EntityFrameworkCore.Metadata;
using Thinktecture.EntityFrameworkCore.Data;
using Thinktecture.TestDatabaseContext;

namespace Thinktecture.EntityFrameworkCore.BulkOperations.PropertiesProviderTests;

public class Exclude
{
   [Fact]
   public void Should_throw_if_expression_is_null()
   {
      Action action = () => IEntityPropertiesProvider.Exclude<TestEntity>(null!);
      action.Should().Throw<ArgumentNullException>();
   }

   [Fact]
   public void Should_throw_if_expression_return_constant()
   {
      Action action = () => IEntityPropertiesProvider.Exclude<TestEntity>(entity => null!);
      action.Should().Throw<NotSupportedException>();
   }

   [Fact]
   public void Should_return_all_properties_if_no_properties_provided()
   {
      var entityType = GetEntityType<TestEntity>();
      var propertiesProvider = IEntityPropertiesProvider.Exclude<TestEntity>(entity => new { });
      var properties = propertiesProvider.GetPropertiesForTempTable(entityType, null);

      properties.Should().HaveCount(entityType.GetProperties().Count());
   }

   [Fact]
   public void Should_extract_all_properties_besides_the_one_specified_by_property_accessor()
   {
      var entityType = GetEntityType<TestEntity>();
      var idProperty = new PropertyWithNavigations(entityType.FindProperty(nameof(TestEntity.Id))!, Array.Empty<INavigation>());

      var propertiesProvider = IEntityPropertiesProvider.Exclude<TestEntity>(entity => entity.Id);

      var properties = propertiesProvider.GetPropertiesForTempTable(entityType, null);
      properties.Should().HaveCount(entityType.GetProperties().Count() - 1);
      properties.Should().NotContain(idProperty);
   }

   [Fact]
   public void Should_extract_all_properties_besides_the_ones_specified_by_expression()
   {
      var entityType = GetEntityType<TestEntity>();
      var idProperty = new PropertyWithNavigations(entityType.FindProperty(nameof(TestEntity.Id))!, Array.Empty<INavigation>());
      var countProperty = new PropertyWithNavigations(entityType.FindProperty(nameof(TestEntity.Count))!, Array.Empty<INavigation>());

      var propertiesProvider = IEntityPropertiesProvider.Exclude<TestEntity>(entity => new { entity.Id, entity.Count });

      var properties = propertiesProvider.GetPropertiesForTempTable(entityType, null);
      properties.Should().HaveCount(entityType.GetProperties().Count() - 2);
      properties.Should().NotContain(idProperty);
      properties.Should().NotContain(countProperty);
   }

   private static IEntityType GetEntityType<T>()
   {
      var options = new DbContextOptionsBuilder<TestDbContext>().UseSqlite("DataSource=:memory:").Options;
      return new TestDbContext(options).Model.GetEntityType(typeof(T));
   }
}
