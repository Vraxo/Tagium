using System.Data;
using AwesomeAssertions;
using Tagium.Core;

namespace Tagium.Core.Tests.Core;

public class SqliteGuidTypeHandlerTests
{
    private sealed class FakeParameter : IDbDataParameter
    {
        public DbType DbType { get; set; }
        public ParameterDirection Direction { get; set; }
        public bool IsNullable { get; set; }
        public string ParameterName { get; set; } = "";
        public string SourceColumn { get; set; } = "";
        public DataRowVersion SourceVersion { get; set; }
        public int Size { get; set; }
        public byte Precision { get; set; }
        public byte Scale { get; set; }
        public object? Value { get; set; }
    }

    [Fact]
    public void SetValue_StoresGuidAsCompactStringWithoutDashes()
    {
        SqliteGuidTypeHandler handler = new();
        Guid guid = Guid.NewGuid();
        FakeParameter parameter = new();

        handler.SetValue(parameter, guid);

        parameter.Value!.Should().Be(guid.ToString("N"));
    }

    [Fact]
    public void Parse_RoundTripsCompactAndDashedStringForms()
    {
        SqliteGuidTypeHandler handler = new();
        Guid guid = Guid.NewGuid();

        handler.Parse(guid.ToString("N")).Should().Be(guid);
        handler.Parse(guid.ToString()).Should().Be(guid);
    }
}
