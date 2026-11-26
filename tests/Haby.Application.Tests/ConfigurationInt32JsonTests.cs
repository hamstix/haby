using System.Text;
using System.Text.Json;
using Haby.Domain;
using Haby.Manifest;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Haby.Application.Tests;

public sealed class ConfigurationInt32JsonTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("-0.00e999", 0)]
    [InlineData("-1", -1)]
    [InlineData("-1e0", -1)]
    [InlineData("200.0", 200)]
    [InlineData("2e2", 200)]
    [InlineData("2147483647", int.MaxValue)]
    [InlineData("-2147483648", int.MinValue)]
    [InlineData("1e9", 1000000000)]
    public void External_numbers_are_plain_Int32_tokens_for_stock_consumers(string text, int expected)
    {
        var value = new ConfigurationObject(new Dictionary<string, ConfigurationValue> { ["Count"] = ConfigurationNumber.Parse(text) });
        var json = ConfigurationJson.Serialize(value).Json!;
        Assert.Equal("{\"Count\":" + expected.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}", json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, document.RootElement.GetProperty("Count"u8).GetInt32());
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        Assert.Equal(expected, configuration.GetValue<int>("Count"));
    }

    [Fact]
    public void Stock_binder_accepts_real_serialized_negative_integer()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ConfigurationJson.Serialize(new ConfigurationObject(new Dictionary<string, ConfigurationValue> { ["Count"] = ConfigurationNumber.Parse("-1") })).Json!));
        Assert.Equal(-1, new ConfigurationBuilder().AddJsonStream(stream).Build().GetValue<int>("Count"));
    }
    [Fact]
    public void GetInt32_accepts_real_serialized_negative_integer()
    {
        using var document = JsonDocument.Parse(ConfigurationJson.Serialize(ConfigurationNumber.Parse("-1")).Json!);
        Assert.Equal(-1, document.RootElement.GetInt32());
    }

    [Theory]
    [InlineData("-0.1")]
    [InlineData("0.9999999999999999999999999999999999999999")]
    [InlineData("-1.0000000000000000000000000000000000000001")]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("1e999999999999999999999999999999")]
    [InlineData("-1e-999999999999999999999999999999")]
    public void Non_Int32_numbers_keep_exact_compact_canonical_tokens(string input)
    {
        var number = ConfigurationNumber.Parse(input);
        var text = ConfigurationJson.Serialize(number).Json!;
        Assert.Equal(number.CanonicalText, text);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(number, ConfigurationValueMapper.FromValidatedJson(json.RootElement));
        Assert.Equal(Encoding.UTF8.GetByteCount(text), ConfigurationJson.MeasureResolvedValue(number, default));
        Assert.Equal(text, Encoding.UTF8.GetString(ConfigurationJson.SerializeDelivery(number)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Expanded_Int32_measurement_and_write_agree_at_exact_byte_limit(int over)
    {
        var overhead = "{\"Count\":1000000000,\"Padding\":\"\"}"u8.Length;
        var value = new ConfigurationObject(new Dictionary<string, ConfigurationValue>
        {
            ["Count"] = ConfigurationNumber.Parse("1e9"),
            ["Padding"] = new ConfigurationString(new string('x', ConfigurationDocumentCodec.MaxBytes - overhead + over))
        });
        if (over == 0)
        {
            Assert.Equal(ConfigurationDocumentCodec.MaxBytes, ConfigurationJson.MeasureResolvedValue(value, default));
            Assert.Equal(ConfigurationDocumentCodec.MaxBytes, ConfigurationJson.SerializeDelivery(value).Length);
        }
        else
        {
            Assert.Throws<ConfigurationDocumentLimitException>(() => ConfigurationJson.MeasureResolvedValue(value, default));
            Assert.Throws<ConfigurationDocumentLimitException>(() => ConfigurationJson.SerializeDelivery(value));
        }
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ConfigurationJson.MeasureResolvedValue(value, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => ConfigurationJson.SerializeDelivery(value, cancellation.Token));
    }
}
