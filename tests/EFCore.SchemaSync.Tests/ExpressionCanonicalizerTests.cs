using EFCore.SchemaSync.Conversion;

namespace EFCore.SchemaSync.Tests;

[TestClass]
public sealed class ExpressionCanonicalizerTests
{
    [TestMethod]
    [DataRow("[C] bit NOT NULL DEFAULT CAST(1 AS bit)", "[C] bit NOT NULL DEFAULT (CONVERT([bit], (1)))")]
    [DataRow("[C] bit NOT NULL DEFAULT CONVERT(bit, 0)", "[C] bit NOT NULL DEFAULT (CONVERT([bit], (0)))")]
    [DataRow("[C] float NOT NULL DEFAULT 1.5E0", "[C] float NOT NULL DEFAULT ((1.5000000000000000e+000))")]
    [DataRow("[C] float NOT NULL DEFAULT 0.0E0", "[C] float NOT NULL DEFAULT ((0.0000000000000000e+000))")]
    [DataRow("[C] float NOT NULL DEFAULT -1.5E0", "[C] float NOT NULL DEFAULT ((-1.5000000000000000e+000))")]
    [DataRow("[C] float NOT NULL DEFAULT 1.0E-3", "[C] float NOT NULL DEFAULT ((1.0000000000000000e-003))")]
    [DataRow("[C] float NOT NULL DEFAULT 12345678901.234567E0", "[C] float NOT NULL DEFAULT ((1.2345678901234568e+010))")]
    [DataRow("[C] int NOT NULL DEFAULT -1", "[C] int NOT NULL DEFAULT ((-1))")]
    [DataRow("[C] int NOT NULL DEFAULT 0", "[C] int NOT NULL DEFAULT ((0))")]
    [DataRow("[C] int NOT NULL DEFAULT (0)", "[C] int NOT NULL DEFAULT (0)")]
    [DataRow("[C] int NOT NULL DEFAULT ((0))", "[C] int NOT NULL DEFAULT ((0))")]
    [DataRow("[C] bigint NOT NULL DEFAULT 9000000000", "[C] bigint NOT NULL DEFAULT ((9000000000.))")]
    [DataRow("[C] bigint NOT NULL DEFAULT -9000000000", "[C] bigint NOT NULL DEFAULT ((-9000000000.))")]
    [DataRow("[C] bigint NOT NULL DEFAULT -2147483648", "[C] bigint NOT NULL DEFAULT ((-2147483648))")]
    [DataRow("[C] bigint NOT NULL DEFAULT -2147483649", "[C] bigint NOT NULL DEFAULT ((-2147483649.))")]
    [DataRow("[C] bigint NOT NULL DEFAULT CAST(9000000000 AS bigint)", "[C] bigint NOT NULL DEFAULT (CONVERT([bigint], (9000000000.)))")]
    [DataRow("[C] decimal(18,2) NOT NULL DEFAULT 0.0", "[C] decimal(18,2) NOT NULL DEFAULT ((0.0))")]
    [DataRow("[C] decimal(18,2) NOT NULL DEFAULT -1.25", "[C] decimal(18,2) NOT NULL DEFAULT ((-1.25))")]
    [DataRow("[C] nvarchar(10) NOT NULL DEFAULT N'x'", "[C] nvarchar(10) NOT NULL DEFAULT (N'x')")]
    [DataRow("[C] datetime2 NOT NULL DEFAULT (GETUTCDATE())", "[C] datetime2 NOT NULL DEFAULT (GETUTCDATE())")]
    [DataRow("[C] datetime2 NOT NULL DEFAULT '2024-01-02T03:04:05.0000000Z'", "[C] datetime2 NOT NULL DEFAULT ('2024-01-02T03:04:05.0000000Z')")]
    [DataRow("[C] int NOT NULL DEFAULT (NEXT VALUE FOR [sales].[Numbers])", "[C] int NOT NULL DEFAULT (NEXT VALUE FOR [sales].[Numbers])")]
    [DataRow("[B] int NOT NULL, [C] AS ([B] + 1)", "[B] int NOT NULL, [C] AS ([B] + (1))")]
    [DataRow("[B] int NOT NULL, [C] AS [B]", "[B] int NOT NULL, [C] AS ([B])")]
    [DataRow("[C] nvarchar(20) NOT NULL DEFAULT N'a' + N'b'", "[C] nvarchar(20) NOT NULL DEFAULT (N'a' + N'b')")]
    [DataRow("[B] decimal(18,2) NOT NULL, [C] AS CAST([B] * 2 AS decimal(18,2)) PERSISTED", "[B] decimal(18,2) NOT NULL, [C] AS (CONVERT([decimal](18,2), [B] * (2))) PERSISTED")]
    [DataRow("[B] int NOT NULL, [C] AS CAST([B] AS nvarchar(max))", "[B] int NOT NULL, [C] AS (CONVERT([nvarchar](max), [B]))")]
    [DataRow("[B] datetime2 NOT NULL, [C] AS CONVERT(nvarchar(30), [B], 126)", "[B] datetime2 NOT NULL, [C] AS (CONVERT([nvarchar](30), [B], (126)))")]
    [DataRow("[B] int NOT NULL, [C] AS TRY_CAST([B] AS tinyint)", "[B] int NOT NULL, [C] AS (TRY_CAST([B] AS [tinyint]))")]
    [DataRow("[B] datetime2 NOT NULL, [C] AS YEAR([B])", "[B] datetime2 NOT NULL, [C] AS (DATEPART(year, [B]))")]
    [DataRow("[B] datetime2 NOT NULL, [C] AS MONTH([B]) + DAY([B])", "[B] datetime2 NOT NULL, [C] AS (DATEPART(month, [B]) + DATEPART(day, [B]))")]
    [DataRow("[B] int NOT NULL, [C] AS IIF([B] > 0, 1, 0)", "[B] int NOT NULL, [C] AS (CASE WHEN [B] > (0) THEN (1) ELSE (0) END)")]
    [DataRow("[B] int NOT NULL, [C] AS CASE WHEN [B] > 0 THEN 1 ELSE 0 END", "[B] int NOT NULL, [C] AS (CASE WHEN [B] > (0) THEN (1) ELSE (0) END)")]
    [DataRow("[B] int NULL, [C] AS ISNULL([B], 0)", "[B] int NULL, [C] AS (ISNULL([B], (0)))")]
    [DataRow("[B] int NOT NULL, [C] AS -[B]", "[B] int NOT NULL, [C] AS (-[B])")]
    [DataRow("[B] float NOT NULL, [C] AS [B] * 1.5E0", "[B] float NOT NULL, [C] AS ([B] * (1.5000000000000000e+000))")]
    [DataRow("[B] nvarchar(10) NOT NULL, [C] AS [B] + N'!'", "[B] nvarchar(10) NOT NULL, [C] AS ([B] + N'!')")]
    [DataRow("[B] int NOT NULL, [C] AS CAST(CAST([B] AS bigint) * 2 AS int)", "[B] int NOT NULL, [C] AS (CONVERT([int], CONVERT([bigint], [B]) * (2)))")]
    [DataRow("[C] int NOT NULL, CONSTRAINT [CK] CHECK ([C] >= 0)", "[C] int NOT NULL, CONSTRAINT [CK] CHECK ([C] >= (0))")]
    [DataRow("[C] int NOT NULL, CONSTRAINT [CK] CHECK ([C] IN (1, 2, 3))", "[C] int NOT NULL, CONSTRAINT [CK] CHECK (([C]=(3) OR [C]=(2) OR [C]=(1)))")]
    [DataRow("[C] int NOT NULL, CONSTRAINT [CK] CHECK ([C] IN (1))", "[C] int NOT NULL, CONSTRAINT [CK] CHECK (([C]=(1)))")]
    [DataRow("[C] nvarchar(10) NOT NULL, CONSTRAINT [CK] CHECK ([C] NOT IN (N'a', N'b'))", "[C] nvarchar(10) NOT NULL, CONSTRAINT [CK] CHECK (NOT ([C]=N'b' OR [C]=N'a'))")]
    [DataRow("[C] int NOT NULL, CONSTRAINT [CK] CHECK ([C] BETWEEN 1 AND 10)", "[C] int NOT NULL, CONSTRAINT [CK] CHECK (([C]>=(1) AND [C]<=(10)))")]
    [DataRow("[C] int NOT NULL, CONSTRAINT [CK] CHECK ([C] NOT BETWEEN 1 AND 10)", "[C] int NOT NULL, CONSTRAINT [CK] CHECK (NOT ([C]>=(1) AND [C]<=(10)))")]
    [DataRow("[C] int NOT NULL, CONSTRAINT [CK] CHECK (NOT ([C] = 0))", "[C] int NOT NULL, CONSTRAINT [CK] CHECK (NOT [C] = (0))")]
    [DataRow("[C] int NOT NULL, CONSTRAINT [CK] CHECK (NOT ([C] = 0 OR [C] = 1))", "[C] int NOT NULL, CONSTRAINT [CK] CHECK (NOT ([C] = (0) OR [C] = (1)))")]
    [DataRow("[C] int NOT NULL, [D] int NOT NULL, CONSTRAINT [CK] CHECK ([C] > 0 AND ([D] > 0 OR [D] = -1))", "[C] int NOT NULL, [D] int NOT NULL, CONSTRAINT [CK] CHECK ([C] > (0) AND ([D] > (0) OR [D] = (-1)))")]
    [DataRow("[C] nvarchar(10) NOT NULL, CONSTRAINT [CK] CHECK ([C] LIKE N'a%')", "[C] nvarchar(10) NOT NULL, CONSTRAINT [CK] CHECK ([C] LIKE N'a%')")]
    [DataRow("[C] nvarchar(10) NOT NULL, CONSTRAINT [CK] CHECK (LEN([C]) > 0)", "[C] nvarchar(10) NOT NULL, CONSTRAINT [CK] CHECK (LEN([C]) > (0))")]
    [DataRow("[C] int NULL, INDEX [IX] ([C]) WHERE [C] IN (1, 2)", "[C] int NULL, INDEX [IX] ([C]) WHERE [C] IN ((1), (2))")]
    [DataRow("[C] int NULL, INDEX [IX] ([C]) WHERE [C] > 1 AND [C] < 10", "[C] int NULL, INDEX [IX] ([C]) WHERE [C] > (1) AND [C] < (10)")]
    [DataRow("[C] int NOT NULL IDENTITY(1000, 5)", "[C] int NOT NULL IDENTITY(1000, 5)")]
    [DataRow("[C] decimal(18,2) NOT NULL", "[C] decimal(18,2) NOT NULL")]
    [DataRow("[C] nvarchar(320) NULL", "[C] nvarchar(320) NULL")]
    public void Column_definitions_are_canonicalized(string input, string expected)
    {
        var script = $"CREATE TABLE [dbo].[T] (\n    [Id] int NOT NULL,\n    {input},\n    CONSTRAINT [PK_T] PRIMARY KEY ([Id])\n);";

        var normalized = SqlServerScriptNormalizer.Normalize(script);

        var statement = Assert.ContainsSingle(normalized.Statements);
        StringAssert.Contains(statement.Text, expected, $"\nActual statement:\n{statement.Text}");
    }

    [TestMethod]
    [DataRow("CREATE INDEX [IX] ON [T] ([C]) WHERE [C] IN (1, 2) AND [D] <> 0", "WHERE [C] IN ((1), (2)) AND [D] <> (0)")]
    [DataRow("CREATE INDEX [IX] ON [T] ([C]) WITH (FILLFACTOR = 80)", "WITH (FILLFACTOR = 80)")]
    [DataRow("CREATE UNIQUE INDEX [IX] ON [T] ([C] DESC) INCLUDE ([D]) WHERE [C] IS NOT NULL", "([C] DESC) INCLUDE ([D]) WHERE [C] IS NOT NULL")]
    public void Index_statements_are_canonicalized(string input, string expected)
    {
        var script = "CREATE TABLE [T] ([Id] int NOT NULL, [C] int NULL, [D] int NULL, CONSTRAINT [PK_T] PRIMARY KEY ([Id]));\nGO\n" + input + ";\nGO\n";

        var normalized = SqlServerScriptNormalizer.Normalize(script);

        Assert.AreEqual(2, normalized.Statements.Count);
        StringAssert.Contains(normalized.Statements[1].Text, expected, $"\nActual statement:\n{normalized.Statements[1].Text}");
    }

    [TestMethod]
    public void Real_literal_formatting_matches_sql_server()
    {
        Assert.AreEqual("1.5000000000000000e+000", ExpressionCanonicalizer.FormatReal("1.5E0"));
        Assert.AreEqual("0.0000000000000000e+000", ExpressionCanonicalizer.FormatReal("0.0E0"));
        Assert.AreEqual("2.5000000000000000e+001", ExpressionCanonicalizer.FormatReal("2.5E1"));
        Assert.AreEqual("1.0000000000000000e-003", ExpressionCanonicalizer.FormatReal("1.0E-3"));
        Assert.AreEqual("1.0000000000000001e-001", ExpressionCanonicalizer.FormatReal("0.1E0"), "17 significant digits, exactly as SQL Server prints the double nearest to 0.1");
        Assert.AreEqual("1.2345678901234568e+010", ExpressionCanonicalizer.FormatReal("12345678901.234567E0"));
    }

    [TestMethod]
    public void Integer_literal_formatting_matches_sql_server()
    {
        Assert.AreEqual("0", ExpressionCanonicalizer.FormatInteger("0", negative: false));
        Assert.AreEqual("2147483647", ExpressionCanonicalizer.FormatInteger("2147483647", negative: false));
        Assert.AreEqual("2147483648.", ExpressionCanonicalizer.FormatInteger("2147483648", negative: false));
        Assert.AreEqual("2147483648", ExpressionCanonicalizer.FormatInteger("2147483648", negative: true));
        Assert.AreEqual("2147483649.", ExpressionCanonicalizer.FormatInteger("2147483649", negative: true));
        Assert.AreEqual("12345678901234567890.", ExpressionCanonicalizer.FormatInteger("12345678901234567890", negative: false));
    }
}
