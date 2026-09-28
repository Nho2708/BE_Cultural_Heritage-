using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DSVHVN.Infrastructure.Persistence.Configurations;

/// <summary>Quy ước cột dùng chung cho mọi bảng của CSDL v3.</summary>
internal static class EnumColumn
{
    public const int Length = 20;

    /// <summary>Cột enum: nvarchar(20), lưu tên thành viên viết HOA. CHECK khai báo ở bảng bằng <see cref="CheckSql{TEnum}"/>.</summary>
    public static PropertyBuilder<TEnum> AsEnumText<TEnum>(this PropertyBuilder<TEnum> property, int maxLength = Length)
        => property.HasConversion<string>().HasMaxLength(maxLength).IsUnicode();

    /// <summary>CHECK liệt kê đúng giá trị của enum, cùng thứ tự với khai báo enum.</summary>
    /// <param name="column">Tên cột trong CSDL (snake_case).</param>
    public static string CheckSql<TEnum>(string column) where TEnum : struct, Enum
    {
        var values = string.Join(", ", Enum.GetNames<TEnum>().Select(n => $"'{n}'"));
        return $"[{column}] IN ({values})";
    }

    /// <summary>Thêm CHECK của cột enum, tên <c>CK_{bảng}_{cột}</c>.</summary>
    public static TableBuilder<TEntity> HasEnumCheck<TEntity, TEnum>(this TableBuilder<TEntity> table, string tableName, string column)
        where TEntity : class where TEnum : struct, Enum
    {
        table.HasCheckConstraint($"CK_{tableName}_{column}", CheckSql<TEnum>(column));
        return table;
    }

    /// <summary>
    /// Cột bit NOT NULL có DEFAULT 0 hoặc 1 như CSDL v3. Ứng dụng luôn gửi giá trị khi thêm dòng (không để CSDL tự điền)
    /// nên <c>false</c> của C# không bị nhầm với "chưa đặt".
    /// </summary>
    public static PropertyBuilder<bool> BitWithDefault(this PropertyBuilder<bool> property, bool defaultValue)
        => property.IsRequired().HasDefaultValueSql(defaultValue ? "1" : "0").ValueGeneratedNever();

    /// <summary>
    /// Ràng buộc không trùng thường, không lọc (như UNIQUE của CSDL v3). SQL Server provider tự thêm bộ lọc
    /// <c>IS NOT NULL</c> cho cột được NULL, nên phải bỏ bộ lọc đó đi.
    /// </summary>
    public static IndexBuilder<TEntity> IsPlainUnique<TEntity>(this IndexBuilder<TEntity> index, string name)
        => index.IsUnique().HasFilter(null).HasDatabaseName(name);
}
