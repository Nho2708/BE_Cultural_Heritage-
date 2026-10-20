using DSVHVN.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;

namespace DSVHVN.Tests.Unit;

/// <summary>Cấu hình tệp di sản: danh sách <c>HeritagesFiles</c> nối sau khóa cũ <c>HeritagesFile</c>, bỏ trống và trùng, giữ thứ tự.</summary>
public sealed class HeritageSeedConfigTests
{
    private static IConfiguration Section(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>("Seed:HeritageData:" + p.Key, p.Value)))
            .Build().GetSection("Seed:HeritageData");

    [Fact]
    public void Old_single_key_still_read()
    {
        var files = DatabaseInitializer.HeritageFiles(Section(("HeritagesFile", "seed-dot-1.json")));
        Assert.Equal(["seed-dot-1.json"], files);
    }

    [Fact]
    public void List_key_keeps_order_after_old_key_and_drops_blank_and_duplicates()
    {
        var files = DatabaseInitializer.HeritageFiles(Section(
            ("HeritagesFile", "seed-dot-1.json"),
            ("HeritagesFiles:0", "seed-dot-1.json"),
            ("HeritagesFiles:1", "seed-dot-2.json"),
            ("HeritagesFiles:2", " "),
            ("HeritagesFiles:3", "seed-dot-3.json")));
        Assert.Equal(["seed-dot-1.json", "seed-dot-2.json", "seed-dot-3.json"], files);
    }

    [Fact]
    public void No_key_means_no_file()
    {
        Assert.Empty(DatabaseInitializer.HeritageFiles(Section(("ProvincesFile", "provinces.json"))));
    }
}
