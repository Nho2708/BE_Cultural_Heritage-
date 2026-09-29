using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DSVHVN.Infrastructure.Persistence.Configurations;

/// <summary>Quy ước cột enum: nvarchar(20) + CHECK liệt kê đúng giá trị của enum.</summary>
internal static class EnumColumn
{
    public const int Length = 20;

    public static PropertyBuilder<TEnum> AsEnumText<TEnum>(this PropertyBuilder<TEnum> property, int maxLength = Length)
        => property.HasConversion<string>().HasMaxLength(maxLength).IsUnicode();

    /// <param name="column">Tên cột trong CSDL (snake_case).</param>
    public static string CheckSql<TEnum>(string column) where TEnum : struct, Enum
    {
        var values = string.Join(", ", Enum.GetNames<TEnum>().Select(n => $"'{n}'"));
        return $"[{column}] IN ({values})";
    }
}
