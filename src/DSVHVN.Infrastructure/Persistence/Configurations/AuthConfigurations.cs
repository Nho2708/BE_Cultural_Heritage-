using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Organizations;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DSVHVN.Infrastructure.Persistence.Configurations;

// Nhóm Auth / Tổ chức của CSDL v3. Độ dài, NULL/NOT NULL, DEFAULT đúng DBML; riêng khóa ngoại nghiệp vụ bắt buộc
// khai báo NOT NULL (DBML để ngỏ, đặc tả mô hình dữ liệu chốt làm ở migration). Mọi FK Restrict (NO ACTION),
// riêng password_reset_tokens.user_id Cascade.

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> b)
    {
        b.ToTable("organizations");
        b.Property(x => x.Name).HasMaxLength(AccountRules.OrganizationNameMaxLength).IsRequired();
        b.Property(x => x.Address).HasMaxLength(AccountRules.OrganizationAddressMaxLength);
        b.Property(x => x.Phone).HasMaxLength(AccountRules.PhoneMaxLength);
        b.Property(x => x.Email).HasMaxLength(AccountRules.EmailMaxLength);
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.ToTable("roles");
        // roles.code: mã dạng chuỗi, không CHECK.
        b.Property(x => x.Code).HasConversion<string>().HasMaxLength(50).IsUnicode();
        b.Property(x => x.Name).HasMaxLength(100);
        b.HasIndex(x => x.Code).IsPlainUnique("UX_roles_code");

        // Dữ liệu khởi tạo cố định 4 vai trò, id khớp Roles.*Id.
        b.HasData(
            new Role { Id = Roles.AdminId, Code = RoleCode.ADMIN, Name = Roles.NameOf(RoleCode.ADMIN) },
            new Role { Id = Roles.OrgAdminId, Code = RoleCode.ORG_ADMIN, Name = Roles.NameOf(RoleCode.ORG_ADMIN) },
            new Role { Id = Roles.TeacherId, Code = RoleCode.TEACHER, Name = Roles.NameOf(RoleCode.TEACHER) },
            new Role { Id = Roles.StudentId, Code = RoleCode.STUDENT, Name = Roles.NameOf(RoleCode.STUDENT) });
    }
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users", t => t.HasEnumCheck<User, UserStatus>("users", "status"));
        b.Ignore(x => x.RoleCode);
        b.Ignore(x => x.IsStudent);

        b.Property(x => x.Username).HasMaxLength(100);
        b.Property(x => x.Email).HasMaxLength(AccountRules.EmailMaxLength);
        b.Property(x => x.PasswordHash).HasMaxLength(255);
        b.Property(x => x.MustChangePassword).BitWithDefault(false);
        b.Property(x => x.FullName).HasMaxLength(AccountRules.FullNameMaxLength);
        b.Property(x => x.Phone).HasMaxLength(AccountRules.PhoneMaxLength);
        b.Property(x => x.AvatarUrl).HasMaxLength(AccountRules.AvatarUrlMaxLength);
        b.Property(x => x.Status).AsEnumText();

        // UNIQUE có lọc: chỉ tài khoản chưa xóa mềm không được trùng; học sinh không có email nên email chỉ xét khi có giá trị.
        b.HasIndex(x => x.Username).IsUnique().HasFilter("[deleted_at] IS NULL").HasDatabaseName("UX_users_username");
        b.HasIndex(x => x.Email).IsUnique().HasFilter("[email] IS NOT NULL AND [deleted_at] IS NULL")
            .HasDatabaseName("UX_users_email");

        b.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).IsRequired().OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> b)
    {
        b.ToTable("password_reset_tokens");
        b.Property(x => x.TokenHash).HasMaxLength(255);
        b.HasIndex(x => x.TokenHash).IsPlainUnique("UX_password_reset_tokens_token_hash");

        // Ngoại lệ duy nhất được Cascade.
        b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).IsRequired().OnDelete(DeleteBehavior.Cascade);
    }
}
