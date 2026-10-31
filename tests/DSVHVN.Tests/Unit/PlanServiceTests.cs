using DSVHVN.Application.Billing;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Enums;
using DSVHVN.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Unit;

/// <summary>Quản lý gói dịch vụ của quản trị hệ thống: tạo, sửa, bật tắt bán, gói Miễn phí cố định, tên không trùng, nhật ký, số trường đang dùng.</summary>
public sealed class PlanServiceTests : IDisposable
{
    private readonly BillingHarness _b = new();

    public void Dispose() => _b.Dispose();

    private Task<T> Plans<T>(Func<PlanService, Task<T>> action) => _b.H.Service(action);

    private static PlanRequest Req(string name = "Gói Trường học", decimal price = 800_000m, int days = 180, int teachers = 8, int ai = 250) =>
        new(name, price, days, teachers, ai);

    private async Task<AppException> Fails(Func<PlanService, Task> action) =>
        await Assert.ThrowsAsync<AppException>(() => _b.H.Service(action));

    private Task<int> FreePlanId() => _b.H.QueryAsync(db => db.Plans.Where(p => p.Price == 0m).Select(p => p.Id).SingleAsync());

    [Fact]
    public async Task Admin_creates_a_paid_plan_on_sale_and_the_creation_is_logged()
    {
        var plan = await Plans(s => s.CreateAsync(Req(), _b.Admin, default));

        Assert.Equal(("Gói Trường học", 800_000m, 180, 8, 250, true, false, 0),
            (plan.Name, plan.Price, plan.DurationDays, plan.MaxTeachers, plan.MaxAiRequests, plan.IsActive, plan.IsFree, plan.SchoolCount));
        var log = await _b.H.QueryAsync(db => db.AuditLogs.SingleAsync(l => l.Action == AuditAction.PLAN_UPDATED && l.TargetId == plan.Id));
        Assert.Equal((AuditTargets.Plans, _b.Admin.UserId, ActorRole.ADMIN), (log.TargetType, log.UserId!.Value, log.ActorRole));
        Assert.Contains("Gói Trường học", log.NewValue);
        Assert.Null(log.OldValue);
    }

    [Theory]
    [InlineData(null, "500000", 30, 5, 100, "name", "Vui lòng nhập tên gói.")]
    [InlineData("  ", "500000", 30, 5, 100, "name", "Vui lòng nhập tên gói.")]
    [InlineData("Gói", null, 30, 5, 100, "price", "Vui lòng nhập giá gói.")]
    [InlineData("Gói", "-1", 30, 5, 100, "price", "Giá gói không hợp lệ: phải là số nguyên đồng từ 0 đến 999.999.999.")]
    [InlineData("Gói", "500000.5", 30, 5, 100, "price", "Giá gói không hợp lệ: phải là số nguyên đồng từ 0 đến 999.999.999.")]
    [InlineData("Gói", "1000000000", 30, 5, 100, "price", "Giá gói không hợp lệ: phải là số nguyên đồng từ 0 đến 999.999.999.")]
    [InlineData("Gói", "500000", null, 5, 100, "durationDays", "Vui lòng nhập thời hạn.")]
    [InlineData("Gói", "500000", 0, 5, 100, "durationDays", "Thời hạn không hợp lệ: phải là số nguyên từ 1 đến 730 ngày.")]
    [InlineData("Gói", "500000", 731, 5, 100, "durationDays", "Thời hạn không hợp lệ: phải là số nguyên từ 1 đến 730 ngày.")]
    [InlineData("Gói", "500000", 30, 0, 100, "maxTeachers", "Số giáo viên tối đa không hợp lệ: phải là số nguyên từ 1 đến 10000.")]
    [InlineData("Gói", "500000", 30, 5, -1, "maxAiRequests", "Số lượt AI mỗi kỳ không hợp lệ: phải là số nguyên từ 0 đến 1000000.")]
    public async Task Invalid_requests_are_reported_per_field_in_vietnamese(string? name, string? price, int? days, int? teachers, int? ai, string field, string message)
    {
        var ex = await Fails(s => s.CreateAsync(new PlanRequest(name, price is null ? null : decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture), days, teachers, ai), _b.Admin, default));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal(message, ex.Errors![field]);
        Assert.Equal(2, await _b.H.QueryAsync(db => db.Plans.CountAsync(p => p.Price > 0m)));   // chỉ hai gói dựng sẵn
    }

    [Fact]
    public async Task Zero_ai_requests_are_allowed_and_the_name_is_limited_to_100_characters()
    {
        var zero = await Plans(s => s.CreateAsync(Req(name: "Không AI", ai: 0), _b.Admin, default));
        Assert.Equal(0, zero.MaxAiRequests);

        var ex = await Fails(s => s.CreateAsync(Req(name: new string('x', 101)), _b.Admin, default));
        Assert.Equal("Tên gói không hợp lệ: tối đa 100 ký tự.", ex.Errors!["name"]);
    }

    [Fact]
    public async Task Plan_names_are_unique_ignoring_case_and_accents()
    {
        var ex = await Fails(s => s.CreateAsync(Req(name: "  co BAN "), _b.Admin, default));

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("Tên gói \"co BAN\" đã tồn tại trong hệ thống.", ex.Errors!["name"]);
    }

    [Fact]
    public async Task The_free_plan_is_the_only_zero_price_plan()
    {
        var create = await Fails(s => s.CreateAsync(Req(price: 0m), _b.Admin, default));
        Assert.Equal(400, create.StatusCode);
        Assert.StartsWith("Giá gói không hợp lệ", create.Errors!["price"]);

        var update = await Fails(s => s.UpdateAsync(_b.BasicId, Req(name: "Cơ bản", price: 0m), _b.Admin, default));
        Assert.StartsWith("Giá gói không hợp lệ", update.Errors!["price"]);
    }

    [Fact]
    public async Task Updating_a_plan_changes_it_logs_old_and_new_values_and_does_not_touch_existing_invoices()
    {
        var (_, actor) = await _b.NewOrgAsync(withFreePlan: false);
        var bought = await _b.BuyAsync(actor, _b.BasicId);

        var updated = await Plans(s => s.UpdateAsync(_b.BasicId, Req(name: "Cơ bản", price: 600_000m, days: 45, teachers: 7, ai: 150), _b.Admin, default));

        Assert.Equal((600_000m, 45, 7, 150), (updated.Price, updated.DurationDays, updated.MaxTeachers, updated.MaxAiRequests));
        Assert.Equal(BillingHarness.BasicPrice, (await _b.InvoiceAsync(bought.Invoice.Id)).Amount);     // đổi giá không hồi tố
        var log = await _b.H.QueryAsync(db => db.AuditLogs.Where(l => l.Action == AuditAction.PLAN_UPDATED && l.TargetId == _b.BasicId).OrderByDescending(l => l.Id).FirstAsync());
        Assert.Contains("500000", log.OldValue);
        Assert.Contains("600000", log.NewValue);
    }

    [Fact]
    public async Task Unknown_plans_are_not_found()
    {
        Assert.Equal(404, (await Fails(s => s.GetAsync(9999, default))).StatusCode);
        Assert.Equal(404, (await Fails(s => s.UpdateAsync(9999, Req(), _b.Admin, default))).StatusCode);
        Assert.Equal(404, (await Fails(s => s.DisableAsync(9999, _b.Admin, default))).StatusCode);
    }

    [Fact]
    public async Task The_free_plan_keeps_its_name_price_and_sale_but_its_limits_can_change()
    {
        var id = await FreePlanId();

        var price = await Fails(s => s.UpdateAsync(id, Req(name: "Miễn phí", price: 100_000m), _b.Admin, default));
        Assert.StartsWith("Giá gói không hợp lệ", price.Errors!["price"]);

        var rename = await Fails(s => s.UpdateAsync(id, Req(name: "Dùng thử", price: 0m), _b.Admin, default));
        Assert.StartsWith("Tên gói không hợp lệ", rename.Errors!["name"]);

        var disable = await Fails(s => s.DisableAsync(id, _b.Admin, default));
        Assert.Equal((422, "Không thể tắt bán gói. Gói Miễn phí không tắt được."), (disable.StatusCode, disable.Message));

        var limits = await Plans(s => s.UpdateAsync(id, Req(name: "Miễn phí", price: 0m, days: 60, teachers: 3, ai: 40), _b.Admin, default));
        Assert.Equal(("Miễn phí", true, true, 60, 3, 40), (limits.Name, limits.IsFree, limits.IsActive, limits.DurationDays, limits.MaxTeachers, limits.MaxAiRequests));
    }

    [Fact]
    public async Task Disabling_and_enabling_sale_are_logged_and_repeating_them_changes_nothing()
    {
        var logged = await _b.AuditCountAsync(AuditAction.PLAN_UPDATED);

        var off = await Plans(s => s.DisableAsync(_b.ProId, _b.Admin, default));
        Assert.False(off.IsActive);
        await Plans(s => s.DisableAsync(_b.ProId, _b.Admin, default));
        Assert.Equal(logged + 1, await _b.AuditCountAsync(AuditAction.PLAN_UPDATED));

        var on = await Plans(s => s.EnableAsync(_b.ProId, _b.Admin, default));
        await Plans(s => s.EnableAsync(_b.ProId, _b.Admin, default));
        Assert.True(on.IsActive);
        Assert.Equal(logged + 2, await _b.AuditCountAsync(AuditAction.PLAN_UPDATED));
    }

    [Fact]
    public async Task Disabled_plans_stay_with_the_schools_that_already_have_them_until_they_expire()
    {
        var (org, actor) = await _b.NewOrgAsync(withFreePlan: false);
        await _b.BuyAsync(actor, _b.BasicId);
        await Plans(s => s.DisableAsync(_b.BasicId, _b.Admin, default));

        var overview = await _b.OverviewAsync(actor);
        Assert.True(overview.HasEffectivePlan);
        Assert.Equal(("Cơ bản", true), (overview.Current!.PlanName, overview.Current.IsEffective));
        Assert.Equal(org.Id, (await _b.SubscriptionsAsync(org.Id)).Single().OrganizationId);
    }

    [Fact]
    public async Task The_list_counts_schools_with_an_effective_plan_and_orders_by_price()
    {
        var (_, a) = await _b.NewOrgAsync(withFreePlan: false);
        var (_, b) = await _b.NewOrgAsync(withFreePlan: false);
        var (_, c) = await _b.NewOrgAsync();           // gói Miễn phí
        await _b.BuyAsync(a, _b.BasicId);
        await _b.BuyAsync(b, _b.BasicId);
        _ = c;
        var (stopped, stoppedActor) = await _b.NewOrgAsync(withFreePlan: false);
        await _b.BuyAsync(stoppedActor, _b.ProId);
        await _b.H.QueryAsync(async db =>
        {
            (await db.Organizations.SingleAsync(o => o.Id == stopped.Id)).DeletedAt = DateTime.UtcNow;     // trường đã ngừng không tính
            return await db.SaveChangesAsync();
        });

        var list = await Plans(s => s.ListAsync(default));

        Assert.Equal(["Miễn phí", "Cơ bản", "Nâng cao"], list.Select(p => p.Name));
        Assert.Equal([1, 2, 0], list.Select(p => p.SchoolCount));
        Assert.Equal([true, false, false], list.Select(p => p.IsFree));
    }
}
