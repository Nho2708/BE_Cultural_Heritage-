namespace DSVHVN.Domain.Common;

// Cột kỹ thuật theo đúng từng bảng của CSDL v3 (doc/database/dsvhvn-v3.dbml): không phải bảng nào cũng có đủ
// created_at, updated_at, deleted_at (vd password_reset_tokens không có updated_at, roles và plans không có created_at).
// CSDL v3 để created_at được NULL và không có giá trị mặc định; DbContext dựa vào các giao diện dưới đây để tự điền
// mốc thời gian UTC, nên dòng do ứng dụng ghi luôn có giá trị.

/// <summary>Bảng có cột <c>created_at</c>: DbContext điền giờ UTC khi thêm nếu chưa có giá trị.</summary>
public interface IHasCreatedAt
{
    DateTime? CreatedAt { get; set; }
}

/// <summary>Bảng có cột <c>updated_at</c>: DbContext điền giờ UTC mỗi lần sửa.</summary>
public interface IHasUpdatedAt
{
    DateTime? UpdatedAt { get; set; }
}

/// <summary>Bảng xóa mềm qua <c>deleted_at</c>. NULL nghĩa là chưa bị xóa.</summary>
public interface ISoftDeletable
{
    DateTime? DeletedAt { get; set; }
}
