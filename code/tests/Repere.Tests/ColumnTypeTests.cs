using Repere.Api.Db;

namespace Repere.Tests;

public class ColumnTypeTests
{
    // ── DDL mappings ────────────────────────────────────────────

    public static IEnumerable<object[]> PhysicalTypes =>
        System.Enum.GetValues<ColumnType>()
            .Where(t => t != ColumnType.Computed)
            .Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(PhysicalTypes))]
    public void ToPostgres_ReturnsNonEmpty_ForAllPhysicalTypes(ColumnType type)
    {
        var ddl = ColumnTypes.ToPostgres(type);
        Assert.False(string.IsNullOrWhiteSpace(ddl));
    }

    [Theory]
    [MemberData(nameof(PhysicalTypes))]
    public void ToSqlite_ReturnsNonEmpty_ForAllPhysicalTypes(ColumnType type)
    {
        var ddl = ColumnTypes.ToSqlite(type);
        Assert.False(string.IsNullOrWhiteSpace(ddl));
    }

    [Fact]
    public void ToPostgres_Throws_ForComputed()
    {
        Assert.Throws<InvalidOperationException>(() => ColumnTypes.ToPostgres(ColumnType.Computed));
    }

    [Fact]
    public void ToSqlite_Throws_ForComputed()
    {
        Assert.Throws<InvalidOperationException>(() => ColumnTypes.ToSqlite(ColumnType.Computed));
    }

    [Fact]
    public void ToDdl_SelectsCorrectProvider()
    {
        Assert.Equal("BIGINT", ColumnTypes.ToDdl(ColumnType.Integer, DatabaseProvider.Postgres));
        Assert.Equal("INTEGER", ColumnTypes.ToDdl(ColumnType.Integer, DatabaseProvider.Sqlite));
    }

    // ── Specific DDL values ─────────────────────────────────────

    [Theory]
    [InlineData(ColumnType.Text, "TEXT")]
    [InlineData(ColumnType.Integer, "BIGINT")]
    [InlineData(ColumnType.Float, "DOUBLE PRECISION")]
    [InlineData(ColumnType.Boolean, "BOOLEAN")]
    [InlineData(ColumnType.Date, "DATE")]
    [InlineData(ColumnType.DateTime, "TIMESTAMPTZ")]
    [InlineData(ColumnType.Json, "JSONB")]
    [InlineData(ColumnType.Reference, "BIGINT")]
    [InlineData(ColumnType.Currency, "NUMERIC(19,4)")]
    [InlineData(ColumnType.Enum, "TEXT")]
    [InlineData(ColumnType.File, "TEXT")]
    public void PostgresMappings_AreCorrect(ColumnType type, string expected)
    {
        Assert.Equal(expected, ColumnTypes.ToPostgres(type));
    }

    [Theory]
    [InlineData(ColumnType.Text, "TEXT")]
    [InlineData(ColumnType.Integer, "INTEGER")]
    [InlineData(ColumnType.Float, "REAL")]
    [InlineData(ColumnType.Boolean, "INTEGER")]
    [InlineData(ColumnType.Date, "TEXT")]
    [InlineData(ColumnType.DateTime, "TEXT")]
    [InlineData(ColumnType.Json, "TEXT")]
    [InlineData(ColumnType.Reference, "INTEGER")]
    [InlineData(ColumnType.Currency, "REAL")]
    [InlineData(ColumnType.Enum, "TEXT")]
    [InlineData(ColumnType.File, "TEXT")]
    public void SqliteMappings_AreCorrect(ColumnType type, string expected)
    {
        Assert.Equal(expected, ColumnTypes.ToSqlite(type));
    }

    // ── HasPhysicalColumn / IsForeignKey ────────────────────────

    [Fact]
    public void Computed_HasNoPhysicalColumn()
    {
        Assert.False(ColumnTypes.HasPhysicalColumn(ColumnType.Computed));
    }

    [Fact]
    public void AllNonComputedTypes_HavePhysicalColumn()
    {
        foreach (var type in System.Enum.GetValues<ColumnType>())
        {
            if (type == ColumnType.Computed) continue;
            Assert.True(ColumnTypes.HasPhysicalColumn(type), $"{type} should have a physical column");
        }
    }

    [Fact]
    public void OnlyReference_IsForeignKey()
    {
        foreach (var type in System.Enum.GetValues<ColumnType>())
        {
            Assert.Equal(type == ColumnType.Reference, ColumnTypes.IsForeignKey(type));
        }
    }

    // ── Coercion matrix ─────────────────────────────────────────

    [Fact]
    public void SameType_IsIdentity()
    {
        foreach (var type in System.Enum.GetValues<ColumnType>())
        {
            Assert.Equal(CoercionKind.Identity, ColumnTypes.GetCoercion(type, type));
        }
    }

    [Theory]
    [InlineData(ColumnType.Integer, ColumnType.Text)]
    [InlineData(ColumnType.Float, ColumnType.Text)]
    [InlineData(ColumnType.Boolean, ColumnType.Text)]
    [InlineData(ColumnType.Date, ColumnType.Text)]
    [InlineData(ColumnType.DateTime, ColumnType.Text)]
    [InlineData(ColumnType.Json, ColumnType.Text)]
    [InlineData(ColumnType.Currency, ColumnType.Text)]
    [InlineData(ColumnType.Enum, ColumnType.Text)]
    [InlineData(ColumnType.File, ColumnType.Text)]
    [InlineData(ColumnType.Reference, ColumnType.Text)]
    public void AnyTypeToText_IsSafe(ColumnType from, ColumnType to)
    {
        Assert.Equal(CoercionKind.Safe, ColumnTypes.GetCoercion(from, to));
    }

    [Theory]
    [InlineData(ColumnType.Integer, ColumnType.Float)]
    [InlineData(ColumnType.Integer, ColumnType.Currency)]
    [InlineData(ColumnType.Boolean, ColumnType.Integer)]
    [InlineData(ColumnType.Currency, ColumnType.Float)]
    [InlineData(ColumnType.Date, ColumnType.DateTime)]
    [InlineData(ColumnType.Reference, ColumnType.Integer)]
    [InlineData(ColumnType.Text, ColumnType.File)]
    public void SafeConversions(ColumnType from, ColumnType to)
    {
        Assert.Equal(CoercionKind.Safe, ColumnTypes.GetCoercion(from, to));
    }

    [Theory]
    [InlineData(ColumnType.Float, ColumnType.Integer)]
    [InlineData(ColumnType.DateTime, ColumnType.Date)]
    [InlineData(ColumnType.Text, ColumnType.Integer)]
    [InlineData(ColumnType.Text, ColumnType.Boolean)]
    [InlineData(ColumnType.Integer, ColumnType.Reference)]
    public void LossyConversions(ColumnType from, ColumnType to)
    {
        Assert.Equal(CoercionKind.Lossy, ColumnTypes.GetCoercion(from, to));
    }

    [Theory]
    [InlineData(ColumnType.Boolean, ColumnType.Date)]
    [InlineData(ColumnType.Date, ColumnType.Boolean)]
    [InlineData(ColumnType.Json, ColumnType.Integer)]
    public void ImpossibleConversions(ColumnType from, ColumnType to)
    {
        Assert.Equal(CoercionKind.Impossible, ColumnTypes.GetCoercion(from, to));
    }

    [Fact]
    public void Computed_CoercionIsAlwaysImpossible()
    {
        foreach (var type in System.Enum.GetValues<ColumnType>())
        {
            if (type == ColumnType.Computed) continue;
            Assert.Equal(CoercionKind.Impossible, ColumnTypes.GetCoercion(ColumnType.Computed, type));
            Assert.Equal(CoercionKind.Impossible, ColumnTypes.GetCoercion(type, ColumnType.Computed));
        }
    }

    // ── Validation ──────────────────────────────────────────────

    [Fact]
    public void NullValue_AlwaysValid()
    {
        foreach (var type in System.Enum.GetValues<ColumnType>())
        {
            Assert.Null(ColumnTypes.Validate(type, null));
        }
    }

    [Theory]
    [InlineData(ColumnType.Text, "hello")]
    [InlineData(ColumnType.Integer, 42L)]
    [InlineData(ColumnType.Float, 3.14)]
    [InlineData(ColumnType.Boolean, true)]
    [InlineData(ColumnType.File, "/uploads/photo.jpg")]
    [InlineData(ColumnType.Reference, 1L)]
    public void ValidValues_ReturnNull(ColumnType type, object value)
    {
        Assert.Null(ColumnTypes.Validate(type, value));
    }

    [Fact]
    public void Date_ValidFormat_ReturnsNull()
    {
        Assert.Null(ColumnTypes.Validate(ColumnType.Date, "2026-04-04"));
    }

    [Fact]
    public void Date_InvalidFormat_ReturnsError()
    {
        Assert.NotNull(ColumnTypes.Validate(ColumnType.Date, "not-a-date"));
    }

    [Fact]
    public void DateTime_ValidFormat_ReturnsNull()
    {
        Assert.Null(ColumnTypes.Validate(ColumnType.DateTime, "2026-04-04T12:00:00Z"));
    }

    [Fact]
    public void Json_ValidObject_ReturnsNull()
    {
        Assert.Null(ColumnTypes.Validate(ColumnType.Json, "{\"key\": \"value\"}"));
    }

    [Fact]
    public void Json_ValidArray_ReturnsNull()
    {
        Assert.Null(ColumnTypes.Validate(ColumnType.Json, "[1, 2, 3]"));
    }

    [Fact]
    public void Json_InvalidString_ReturnsError()
    {
        Assert.NotNull(ColumnTypes.Validate(ColumnType.Json, "not json"));
    }

    [Fact]
    public void Enum_ValidValue_ReturnsNull()
    {
        Assert.Null(ColumnTypes.Validate(ColumnType.Enum, "active", new[] { "active", "inactive" }));
    }

    [Fact]
    public void Enum_InvalidValue_ReturnsError()
    {
        var error = ColumnTypes.Validate(ColumnType.Enum, "deleted", new[] { "active", "inactive" });
        Assert.NotNull(error);
        Assert.Contains("deleted", error);
        Assert.Contains("active", error);
    }

    [Fact]
    public void Enum_NoAllowedValues_AcceptsAnyString()
    {
        Assert.Null(ColumnTypes.Validate(ColumnType.Enum, "anything"));
    }

    [Fact]
    public void Currency_AcceptsNumericValues()
    {
        Assert.Null(ColumnTypes.Validate(ColumnType.Currency, 19.99));
        Assert.Null(ColumnTypes.Validate(ColumnType.Currency, 100));
    }

    [Fact]
    public void Computed_AlwaysReturnsError()
    {
        Assert.NotNull(ColumnTypes.Validate(ColumnType.Computed, "anything"));
    }

    [Fact]
    public void WrongType_ReturnsError()
    {
        Assert.NotNull(ColumnTypes.Validate(ColumnType.Integer, "not a number"));
        Assert.NotNull(ColumnTypes.Validate(ColumnType.Boolean, 42));
        Assert.NotNull(ColumnTypes.Validate(ColumnType.Text, 123));
    }

    // ── Default literals ────────────────────────────────────────

    [Theory]
    [MemberData(nameof(PhysicalTypes))]
    public void DefaultLiteral_ReturnsNonEmpty_ForMostTypes(ColumnType type)
    {
        if (type == ColumnType.Reference) return; // reference throws (intentional)
        var literal = ColumnTypes.DefaultLiteral(type);
        Assert.False(string.IsNullOrWhiteSpace(literal));
    }

    [Fact]
    public void DefaultLiteral_Throws_ForReference()
    {
        Assert.Throws<InvalidOperationException>(() => ColumnTypes.DefaultLiteral(ColumnType.Reference));
    }

    [Fact]
    public void DefaultLiteral_Throws_ForComputed()
    {
        Assert.Throws<InvalidOperationException>(() => ColumnTypes.DefaultLiteral(ColumnType.Computed));
    }
}
