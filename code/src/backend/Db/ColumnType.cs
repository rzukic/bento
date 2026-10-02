namespace Repere.Api.Db;

/// <summary>
/// All column types supported by the platform (R1.5).
/// </summary>
public enum ColumnType
{
    Text,
    Integer,
    Float,
    Boolean,
    Date,
    DateTime,
    Json,
    Reference,
    Computed,
    Currency,
    Enum,
    Uuid,
    File
}

/// <summary>
/// Whether a type conversion is safe, lossy, or impossible.
/// </summary>
public enum CoercionKind
{
    /// <summary>No conversion needed (same type).</summary>
    Identity,
    /// <summary>Conversion is always lossless.</summary>
    Safe,
    /// <summary>Conversion may lose data or fail for some values.</summary>
    Lossy,
    /// <summary>Conversion is not supported.</summary>
    Impossible
}

public static class ColumnTypes
{
    // ── Postgres DDL types ──────────────────────────────────────

    public static string ToPostgres(ColumnType type) => type switch
    {
        ColumnType.Text => "TEXT",
        ColumnType.Integer => "BIGINT",
        ColumnType.Float => "DOUBLE PRECISION",
        ColumnType.Boolean => "BOOLEAN",
        ColumnType.Date => "DATE",
        ColumnType.DateTime => "TIMESTAMPTZ",
        ColumnType.Json => "JSONB",
        ColumnType.Reference => "BIGINT",          // FK column
        ColumnType.Currency => "NUMERIC(19,4)",
        ColumnType.Enum => "TEXT",                  // checked by app, not DB constraint
        ColumnType.Uuid => "UUID",
        ColumnType.File => "TEXT",                  // stores file path / S3 key
        ColumnType.Computed => throw new InvalidOperationException("Computed columns have no physical DB type"),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    // ── SQLite DDL types ────────────────────────────────────────

    public static string ToSqlite(ColumnType type) => type switch
    {
        ColumnType.Text => "TEXT",
        ColumnType.Integer => "INTEGER",
        ColumnType.Float => "REAL",
        ColumnType.Boolean => "INTEGER",            // 0/1
        ColumnType.Date => "TEXT",                  // ISO 8601
        ColumnType.DateTime => "TEXT",              // ISO 8601
        ColumnType.Json => "TEXT",
        ColumnType.Reference => "INTEGER",          // FK column
        ColumnType.Currency => "REAL",
        ColumnType.Enum => "TEXT",
        ColumnType.Uuid => "TEXT",
        ColumnType.File => "TEXT",
        ColumnType.Computed => throw new InvalidOperationException("Computed columns have no physical DB type"),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    /// <summary>
    /// Get the DDL type string for the given provider.
    /// </summary>
    public static string ToDdl(ColumnType type, DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.Postgres => ToPostgres(type),
        DatabaseProvider.Sqlite => ToSqlite(type),
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    /// <summary>
    /// Whether this type maps to a physical DB column.
    /// Computed columns are virtual (formula engine handles them).
    /// </summary>
    public static bool HasPhysicalColumn(ColumnType type) => type != ColumnType.Computed;

    /// <summary>
    /// Whether this type creates a foreign key constraint.
    /// </summary>
    public static bool IsForeignKey(ColumnType type) => type == ColumnType.Reference;

    // ── Default values ──────────────────────────────────────────

    /// <summary>
    /// Default value for a non-nullable column of this type.
    /// Returns the SQL literal (not parameterized).
    /// </summary>
    public static string DefaultLiteral(ColumnType type) => type switch
    {
        ColumnType.Text => "''",
        ColumnType.Integer => "0",
        ColumnType.Float => "0.0",
        ColumnType.Boolean => "FALSE",
        ColumnType.Date => "CURRENT_DATE",
        ColumnType.DateTime => "CURRENT_TIMESTAMP",
        ColumnType.Json => "'{}'",
        ColumnType.Currency => "0.0",
        ColumnType.Enum => "''",
        ColumnType.Uuid => $"'00000000-0000-0000-0000-000000000000'",
        ColumnType.File => "''",
        ColumnType.Reference => throw new InvalidOperationException("Reference columns should be nullable or have an explicit default"),
        ColumnType.Computed => throw new InvalidOperationException("Computed columns have no default"),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    // ── Coercion matrix ─────────────────────────────────────────

    /// <summary>
    /// Determines how safe it is to convert from one type to another.
    /// Used by the migration engine when a column type changes.
    /// </summary>
    public static CoercionKind GetCoercion(ColumnType from, ColumnType to)
    {
        if (from == to) return CoercionKind.Identity;

        // Computed columns can't be coerced — they have no stored data
        if (from == ColumnType.Computed || to == ColumnType.Computed)
            return CoercionKind.Impossible;

        return (from, to) switch
        {
            // ── To Text: everything can become text (safe) ──
            (_, ColumnType.Text) => CoercionKind.Safe,

            // ── Numeric conversions ──
            (ColumnType.Integer, ColumnType.Float) => CoercionKind.Safe,
            (ColumnType.Integer, ColumnType.Currency) => CoercionKind.Safe,
            (ColumnType.Integer, ColumnType.Boolean) => CoercionKind.Lossy, // 0→false, non-0→true
            (ColumnType.Float, ColumnType.Integer) => CoercionKind.Lossy,   // truncates decimal
            (ColumnType.Float, ColumnType.Currency) => CoercionKind.Lossy,  // precision loss
            (ColumnType.Currency, ColumnType.Float) => CoercionKind.Safe,
            (ColumnType.Currency, ColumnType.Integer) => CoercionKind.Lossy,
            (ColumnType.Boolean, ColumnType.Integer) => CoercionKind.Safe,  // false→0, true→1

            // ── Text to typed: parsing required (lossy) ──
            (ColumnType.Text, ColumnType.Integer) => CoercionKind.Lossy,
            (ColumnType.Text, ColumnType.Float) => CoercionKind.Lossy,
            (ColumnType.Text, ColumnType.Boolean) => CoercionKind.Lossy,
            (ColumnType.Text, ColumnType.Date) => CoercionKind.Lossy,
            (ColumnType.Text, ColumnType.DateTime) => CoercionKind.Lossy,
            (ColumnType.Text, ColumnType.Currency) => CoercionKind.Lossy,
            (ColumnType.Text, ColumnType.Json) => CoercionKind.Lossy,
            (ColumnType.Text, ColumnType.Enum) => CoercionKind.Lossy,
            (ColumnType.Text, ColumnType.Uuid) => CoercionKind.Lossy,

            // ── Date/DateTime ──
            (ColumnType.Date, ColumnType.DateTime) => CoercionKind.Safe,    // adds 00:00:00
            (ColumnType.DateTime, ColumnType.Date) => CoercionKind.Lossy,   // loses time

            // ── File ──
            (ColumnType.Text, ColumnType.File) => CoercionKind.Safe,        // text is a valid path

            // ── Reference is an ID (integer) ──
            (ColumnType.Reference, ColumnType.Integer) => CoercionKind.Safe,
            (ColumnType.Integer, ColumnType.Reference) => CoercionKind.Lossy, // FK validity unknown

            // ── Everything else ──
            _ => CoercionKind.Impossible
        };
    }

    // ── Validation ──────────────────────────────────────────────

    /// <summary>
    /// Validates a value against the column type rules.
    /// Returns null if valid, or an error message if invalid.
    /// allowedValues is only used for Enum type (the set of valid values).
    /// </summary>
    public static string? Validate(ColumnType type, object? value, string[]? allowedValues = null)
    {
        if (value is null) return null; // null is always valid (nullability checked separately)

        return type switch
        {
            ColumnType.Text => value is string ? null : "Expected a string value",
            ColumnType.Integer => IsInteger(value) ? null : "Expected an integer value",
            ColumnType.Float => IsNumeric(value) ? null : "Expected a numeric value",
            ColumnType.Boolean => value is bool ? null : "Expected a boolean value",
            ColumnType.Date => IsDateString(value) ? null : "Expected a date in YYYY-MM-DD format",
            ColumnType.DateTime => IsDateTimeString(value) ? null : "Expected an ISO 8601 datetime",
            ColumnType.Json => IsJsonString(value) ? null : "Expected a valid JSON string",
            ColumnType.Currency => IsNumeric(value) ? null : "Expected a numeric value",
            ColumnType.Enum => ValidateEnum(value, allowedValues),
            ColumnType.Uuid => IsUuid(value) ? null : "Expected a UUID string",
            ColumnType.File => value is string ? null : "Expected a file path string",
            ColumnType.Reference => IsInteger(value) || IsUuid(value) ? null : "Expected an integer ID or UUID string",
            ColumnType.Computed => "Computed columns cannot have values set directly",
            _ => null
        };
    }

    private static bool IsInteger(object value) =>
        value is int or long or short or byte or sbyte or uint or ulong or ushort;

    private static bool IsNumeric(object value) =>
        value is int or long or float or double or decimal or short or byte;

    private static bool IsDateString(object value) =>
        value is string s && DateOnly.TryParse(s, out _);

    private static bool IsDateTimeString(object value) =>
        value is string s && DateTimeOffset.TryParse(s, out _);

    private static bool IsUuid(object value) =>
        value is string s && Guid.TryParse(s, out _);

    private static bool IsJsonString(object value)
    {
        if (value is not string s) return false;
        s = s.Trim();
        return (s.StartsWith('{') && s.EndsWith('}')) ||
               (s.StartsWith('[') && s.EndsWith(']'));
    }

    private static string? ValidateEnum(object value, string[]? allowedValues)
    {
        if (value is not string s)
            return "Expected a string value for enum";
        if (allowedValues is null || allowedValues.Length == 0)
            return null; // no constraint defined yet
        return Array.Exists(allowedValues, v => v == s)
            ? null
            : $"Value '{s}' is not in allowed set: [{string.Join(", ", allowedValues)}]";
    }
}
