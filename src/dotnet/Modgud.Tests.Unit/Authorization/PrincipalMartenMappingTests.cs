using Marten;
using Modgud.Authorization.Setup;

namespace Modgud.Tests.Unit.Authorization;

public class PrincipalMartenMappingTests
{
    [Fact]
    public void Principal_table_keeps_numeric_projection_revisions()
    {
        using var store = DocumentStore.For(options =>
        {
            options.Connection("Host=localhost;Database=modgud_mapping_test;Username=test;Password=test");
            options.UseModgudAuthorization();
        });
        var schema = store.Storage.ToDatabaseScript();

        var tableNameIndex = schema.IndexOf("mt_doc_principal", StringComparison.OrdinalIgnoreCase);
        Assert.True(tableNameIndex >= 0, "Principal table was not present in generated Marten DDL.");

        var tableEnd = schema.IndexOf(';', tableNameIndex);
        Assert.True(tableEnd > tableNameIndex, "Principal table DDL was incomplete.");
        var principalTable = schema[tableNameIndex..tableEnd];

        Assert.Matches(@"\bmt_version\s+bigint\b", principalTable);
        Assert.DoesNotMatch(@"\bmt_version\s+uuid\b", principalTable);
    }
}
