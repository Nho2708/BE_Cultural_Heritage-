using DSVHVN.Domain.Rules;

namespace DSVHVN.Tests.Unit;

/// <summary>Quy tắc thuần của mô hình 3D di sản: giấy phép mở, dòng ghi công, nhận ra tệp .glb trong kho tệp từ một đường dẫn.</summary>
public sealed class HeritageModelRulesTests
{
    private static readonly string Key = "models/" + new string('a', 32) + ".glb";

    [Theory]
    [InlineData("CC0")]
    [InlineData("cc0 1.0")]
    [InlineData("CC BY")]
    [InlineData("CC BY 4.0")]
    [InlineData("cc by 3.0 unported")]
    [InlineData("CC BY-SA")]
    [InlineData("CC BY-SA 4.0")]
    [InlineData("Public domain")]
    [InlineData("phạm vi công cộng")]
    [InlineData("Mô hình nhóm tự dựng")]
    [InlineData("nhóm tự dựng")]
    [InlineData("  CC BY 4.0  ")]
    public void Open_licenses_are_accepted(string license) => Assert.True(HeritageModelRules.IsAllowedLicense(license));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CC BY-NC")]
    [InlineData("CC BY-NC-SA 4.0")]
    [InlineData("CC BY-ND 4.0")]
    [InlineData("CC BY-SA-NC")]
    [InlineData("CC0X")]
    [InlineData("Bản quyền thuộc tác giả")]
    [InlineData("All rights reserved")]
    [InlineData("Ảnh nhóm tự chụp")]
    [InlineData("tôi dùng CC BY")]
    public void Restricted_or_unknown_licenses_are_refused(string? license) => Assert.False(HeritageModelRules.IsAllowedLicense(license));

    [Theory]
    [InlineData("Mô hình nhóm tự dựng", true)]
    [InlineData("nhóm tự dựng", true)]
    [InlineData("CC BY 4.0", false)]
    [InlineData("Public domain", false)]
    [InlineData(null, false)]
    public void Only_team_built_models_are_exempt_from_a_source_page(string? license, bool expected) =>
        Assert.Equal(expected, HeritageModelRules.IsTeamBuilt(license));

    [Fact]
    public void The_credit_has_three_parts_and_names_the_team_when_there_is_no_source_page()
    {
        Assert.Equal("Nguyễn Văn An · CC BY 4.0 · https://sketchfab.com/m/1",
            HeritageModelRules.ComposeCredit(" Nguyễn Văn An ", " CC BY 4.0 ", " https://sketchfab.com/m/1 "));
        Assert.Equal("Nhóm thực hiện · Mô hình nhóm tự dựng · Nhóm thực hiện", HeritageModelRules.ComposeCredit("Nhóm thực hiện", "Mô hình nhóm tự dựng", null));
        Assert.Equal("An · CC0 · Nhóm thực hiện", HeritageModelRules.ComposeCredit("An", "CC0", "  "));
        Assert.Equal(("An", "CC0", "Nhóm thực hiện"), HeritageRules.ParseCredit(HeritageModelRules.ComposeCredit("An", "CC0", null)));
    }

    [Theory]
    [InlineData("/files/{key}", true)]
    [InlineData("http://localhost:5080/files/{key}", true)]
    [InlineData("https://api.dsvhvn.example/files/{key}", true)]
    [InlineData("  /files/{key}  ", true)]
    [InlineData("https://res.cloudinary.com/demo/raw/upload/dsvhvn/{key}", true)]
    [InlineData("https://res.cloudinary.com/demo/raw/upload/v1700000000/dsvhvn/{key}", true)]
    [InlineData("https://res.cloudinary.com/demo/image/upload/dsvhvn/{key}", false)]
    [InlineData("https://sketchfab.com/models/{key}", false)]
    [InlineData("/files/{key}?download=1", false)]
    [InlineData("/files/{key}#x", false)]
    [InlineData("/files/../{key}", false)]
    [InlineData("/files/images/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg", false)]
    [InlineData("/files/models/aaaa.glb", false)]
    [InlineData("/files/models/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.glb", false)]
    [InlineData("/files/models/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.gltf", false)]
    [InlineData("files/{key}", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_glb_files_of_the_store_are_recognised(string? template, bool recognised)
    {
        var url = template?.Replace("{key}", Key);
        Assert.Equal(recognised ? Key : null, HeritageModelRules.StoredModelKey(url));
    }

    [Fact]
    public void The_size_limit_and_field_lengths_match_the_design()
    {
        Assert.Equal(15 * 1024 * 1024, StoredFileRules.MaxModelBytes);
        Assert.Equal((255, 500, 255, 100, 500), (HeritageModelRules.TitleMaxLength, HeritageModelRules.UrlMaxLength, HeritageModelRules.AuthorMaxLength,
            HeritageModelRules.LicenseMaxLength, HeritageModelRules.CreditMaxLength));
    }
}
