using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace DSVHVN.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KhoiTaoCsdl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    phone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    email = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organizations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payment_providers",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    is_active = table.Column<bool>(type: "bit", nullable: false, defaultValueSql: "1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_providers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "plans",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    price = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: true),
                    duration_days = table.Column<int>(type: "int", nullable: true),
                    max_teachers = table.Column<int>(type: "int", nullable: true),
                    max_ai_requests = table.Column<int>(type: "int", nullable: true),
                    is_active = table.Column<bool>(type: "bit", nullable: false, defaultValueSql: "1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plans", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "provinces",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    code = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    region = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provinces", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    organization_id = table.Column<long>(type: "bigint", nullable: false),
                    plan_id = table.Column<int>(type: "int", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subscriptions", x => x.id);
                    table.CheckConstraint("CK_subscriptions_status", "[status] IN ('PENDING', 'ACTIVE', 'EXPIRED', 'CANCELLED')");
                    table.ForeignKey(
                        name: "fk_subscriptions_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_subscriptions_plan_id",
                        column: x => x.plan_id,
                        principalTable: "plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "heritages",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    province_id = table.Column<int>(type: "int", nullable: true),
                    name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    slug = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    short_description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    content = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    heritage_type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    recognition_level = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    recognized_year = table.Column<int>(type: "int", nullable: true),
                    address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    latitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    longitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    place_id = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    thumbnail_url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_heritages", x => x.id);
                    table.CheckConstraint("CK_heritages_heritage_type", "[heritage_type] IN ('TANGIBLE', 'INTANGIBLE', 'NATURAL', 'MIXED', 'DOCUMENTARY')");
                    table.CheckConstraint("CK_heritages_recognition_level", "[recognition_level] IN ('UNESCO', 'NATIONAL_SPECIAL', 'NATIONAL', 'PROVINCIAL', 'NONE')");
                    table.CheckConstraint("CK_heritages_status", "[status] IN ('DRAFT', 'PUBLISHED', 'ARCHIVED')");
                    table.ForeignKey(
                        name: "fk_heritages_province_id",
                        column: x => x.province_id,
                        principalTable: "provinces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    organization_id = table.Column<long>(type: "bigint", nullable: true),
                    role_id = table.Column<int>(type: "int", nullable: false),
                    username = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    email = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    password_hash = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    must_change_password = table.Column<bool>(type: "bit", nullable: false, defaultValueSql: "0"),
                    full_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    phone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    avatar_url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    last_login_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                    table.CheckConstraint("CK_users_status", "[status] IN ('ACTIVE', 'LOCKED', 'INACTIVE')");
                    table.ForeignKey(
                        name: "fk_users_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_users_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "invoices",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    organization_id = table.Column<long>(type: "bigint", nullable: false),
                    subscription_id = table.Column<long>(type: "bigint", nullable: false),
                    invoice_number = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    amount = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: true),
                    description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    issued_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    due_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoices", x => x.id);
                    table.CheckConstraint("CK_invoices_status", "[status] IN ('UNPAID', 'PAID', 'CANCELLED')");
                    table.ForeignKey(
                        name: "fk_invoices_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invoices_subscription_id",
                        column: x => x.subscription_id,
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "heritage_references",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    heritage_id = table.Column<long>(type: "bigint", nullable: false),
                    title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    source_type = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_heritage_references", x => x.id);
                    table.ForeignKey(
                        name: "fk_heritage_references_heritage_id",
                        column: x => x.heritage_id,
                        principalTable: "heritages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "timelines",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    heritage_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    start_year = table.Column<int>(type: "int", nullable: true),
                    end_year = table.Column<int>(type: "int", nullable: true),
                    sort_order = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_timelines", x => x.id);
                    table.ForeignKey(
                        name: "fk_timelines_heritage_id",
                        column: x => x.heritage_id,
                        principalTable: "heritages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: true),
                    actor_role = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    action = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    target_type = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    target_id = table.Column<long>(type: "bigint", nullable: true),
                    old_value = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    new_value = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ip_address = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_logs", x => x.id);
                    table.ForeignKey(
                        name: "fk_audit_logs_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "classes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    organization_id = table.Column<long>(type: "bigint", nullable: false),
                    teacher_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    grade = table.Column<byte>(type: "tinyint", nullable: false),
                    school_year = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_classes", x => x.id);
                    table.CheckConstraint("CK_classes_grade", "[grade] BETWEEN 1 AND 12");
                    table.ForeignKey(
                        name: "fk_classes_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_classes_teacher_id",
                        column: x => x.teacher_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "password_reset_tokens",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    token_hash = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    expires_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    used_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_password_reset_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_password_reset_tokens_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    invoice_id = table.Column<long>(type: "bigint", nullable: false),
                    provider_id = table.Column<int>(type: "int", nullable: false),
                    provider_transaction_id = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    amount = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    raw_response = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    paid_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payments", x => x.id);
                    table.CheckConstraint("CK_payments_status", "[status] IN ('PENDING', 'SUCCEEDED', 'FAILED', 'REFUNDED')");
                    table.ForeignKey(
                        name: "fk_payments_invoice_id",
                        column: x => x.invoice_id,
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payments_provider_id",
                        column: x => x.provider_id,
                        principalTable: "payment_providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    timeline_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    start_year = table.Column<int>(type: "int", nullable: false),
                    start_month = table.Column<short>(type: "smallint", nullable: true),
                    start_day = table.Column<short>(type: "smallint", nullable: true),
                    end_year = table.Column<int>(type: "int", nullable: true),
                    end_month = table.Column<short>(type: "smallint", nullable: true),
                    end_day = table.Column<short>(type: "smallint", nullable: true),
                    sort_order = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_events_timeline_id",
                        column: x => x.timeline_id,
                        principalTable: "timelines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "lessons",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    teacher_id = table.Column<long>(type: "bigint", nullable: false),
                    class_id = table.Column<long>(type: "bigint", nullable: false),
                    title = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    summary = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    key_points = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    access_code = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lessons", x => x.id);
                    table.CheckConstraint("CK_lessons_status", "[status] IN ('DRAFT', 'PUBLISHED', 'ARCHIVED')");
                    table.ForeignKey(
                        name: "fk_lessons_class_id",
                        column: x => x.class_id,
                        principalTable: "classes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_lessons_teacher_id",
                        column: x => x.teacher_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "students",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    class_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    student_code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    full_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    is_active = table.Column<bool>(type: "bit", nullable: false, defaultValueSql: "1"),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_students", x => x.id);
                    table.ForeignKey(
                        name: "fk_students_class_id",
                        column: x => x.class_id,
                        principalTable: "classes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_students_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "media",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    heritage_id = table.Column<long>(type: "bigint", nullable: true),
                    event_id = table.Column<long>(type: "bigint", nullable: true),
                    media_type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    caption = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    credit = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    sort_order = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media", x => x.id);
                    table.CheckConstraint("CK_media_owner", "(heritage_id IS NOT NULL AND event_id IS NULL) OR (heritage_id IS NULL AND event_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_media_event_id",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_media_heritage_id",
                        column: x => x.heritage_id,
                        principalTable: "heritages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ai_generations",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    task_type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    lesson_id = table.Column<long>(type: "bigint", nullable: false),
                    requested_by = table.Column<long>(type: "bigint", nullable: false),
                    input_snapshot = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    @params = table.Column<string>(name: "params", type: "nvarchar(max)", nullable: true),
                    model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    raw_output = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    error_message = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    completed_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_generations", x => x.id);
                    table.CheckConstraint("CK_ai_generations_status", "[status] IN ('PROCESSING', 'SUCCEEDED', 'FAILED')");
                    table.CheckConstraint("CK_ai_generations_task_type", "[task_type] IN ('LESSON_SUMMARY', 'QUIZ_GENERATION')");
                    table.ForeignKey(
                        name: "fk_ai_generations_lesson_id",
                        column: x => x.lesson_id,
                        principalTable: "lessons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ai_generations_requested_by",
                        column: x => x.requested_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "lesson_heritages",
                columns: table => new
                {
                    lesson_id = table.Column<long>(type: "bigint", nullable: false),
                    heritage_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lesson_heritages", x => new { x.lesson_id, x.heritage_id });
                    table.ForeignKey(
                        name: "fk_lesson_heritages_heritage_id",
                        column: x => x.heritage_id,
                        principalTable: "heritages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_lesson_heritages_lesson_id",
                        column: x => x.lesson_id,
                        principalTable: "lessons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "quizzes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    lesson_id = table.Column<long>(type: "bigint", nullable: false),
                    created_by = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    difficulty_level = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    time_limit_minutes = table.Column<int>(type: "int", nullable: true),
                    open_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    close_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    shuffle_questions = table.Column<bool>(type: "bit", nullable: false, defaultValueSql: "0"),
                    show_answers_after_submit = table.Column<bool>(type: "bit", nullable: false, defaultValueSql: "0"),
                    total_point = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    published_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quizzes", x => x.id);
                    table.CheckConstraint("CK_quizzes_difficulty_level", "[difficulty_level] IN ('EASY', 'MEDIUM', 'HARD')");
                    table.CheckConstraint("CK_quizzes_status", "[status] IN ('DRAFT', 'PUBLISHED', 'CLOSED')");
                    table.ForeignKey(
                        name: "fk_quizzes_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_quizzes_lesson_id",
                        column: x => x.lesson_id,
                        principalTable: "lessons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "questions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    heritage_id = table.Column<long>(type: "bigint", nullable: false),
                    created_by = table.Column<long>(type: "bigint", nullable: false),
                    ai_generation_id = table.Column<long>(type: "bigint", nullable: true),
                    content = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    question_type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    difficulty_level = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    explanation = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    review_status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    reviewed_by = table.Column<long>(type: "bigint", nullable: true),
                    reviewed_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_questions", x => x.id);
                    table.CheckConstraint("CK_questions_difficulty_level", "[difficulty_level] IN ('EASY', 'MEDIUM', 'HARD')");
                    table.CheckConstraint("CK_questions_question_type", "[question_type] IN ('SINGLE_CHOICE', 'TRUE_FALSE')");
                    table.CheckConstraint("CK_questions_review_status", "[review_status] IN ('PENDING', 'APPROVED', 'REJECTED')");
                    table.CheckConstraint("CK_questions_source", "[source] IN ('MANUAL', 'AI')");
                    table.ForeignKey(
                        name: "fk_questions_ai_generation_id",
                        column: x => x.ai_generation_id,
                        principalTable: "ai_generations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_questions_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_questions_heritage_id",
                        column: x => x.heritage_id,
                        principalTable: "heritages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_questions_reviewed_by",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "attempts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    quiz_id = table.Column<long>(type: "bigint", nullable: false),
                    student_id = table.Column<long>(type: "bigint", nullable: false),
                    started_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    submitted_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    duration_seconds = table.Column<int>(type: "int", nullable: true),
                    score = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: true),
                    total_point = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: true),
                    correct_count = table.Column<int>(type: "int", nullable: true),
                    total_questions = table.Column<int>(type: "int", nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attempts", x => x.id);
                    table.CheckConstraint("CK_attempts_status", "[status] IN ('IN_PROGRESS', 'SUBMITTED', 'EXPIRED')");
                    table.ForeignKey(
                        name: "fk_attempts_quiz_id",
                        column: x => x.quiz_id,
                        principalTable: "quizzes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_attempts_student_id",
                        column: x => x.student_id,
                        principalTable: "students",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "answers",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    question_id = table.Column<long>(type: "bigint", nullable: false),
                    content = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    is_correct = table.Column<bool>(type: "bit", nullable: false, defaultValueSql: "0"),
                    sort_order = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_answers", x => x.id);
                    table.ForeignKey(
                        name: "fk_answers_question_id",
                        column: x => x.question_id,
                        principalTable: "questions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "quiz_questions",
                columns: table => new
                {
                    quiz_id = table.Column<long>(type: "bigint", nullable: false),
                    question_id = table.Column<long>(type: "bigint", nullable: false),
                    point = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    sort_order = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quiz_questions", x => new { x.quiz_id, x.question_id });
                    table.ForeignKey(
                        name: "fk_quiz_questions_question_id",
                        column: x => x.question_id,
                        principalTable: "questions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_quiz_questions_quiz_id",
                        column: x => x.quiz_id,
                        principalTable: "quizzes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "attempt_answers",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    attempt_id = table.Column<long>(type: "bigint", nullable: false),
                    question_id = table.Column<long>(type: "bigint", nullable: false),
                    answer_id = table.Column<long>(type: "bigint", nullable: true),
                    is_correct = table.Column<bool>(type: "bit", nullable: false, defaultValueSql: "0"),
                    point_earned = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    answered_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attempt_answers", x => x.id);
                    table.ForeignKey(
                        name: "fk_attempt_answers_answer_id",
                        column: x => x.answer_id,
                        principalTable: "answers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_attempt_answers_attempt_id",
                        column: x => x.attempt_id,
                        principalTable: "attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_attempt_answers_question_id",
                        column: x => x.question_id,
                        principalTable: "questions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "roles",
                columns: new[] { "id", "code", "name" },
                values: new object[,]
                {
                    { 1, "ADMIN", "Quản trị hệ thống" },
                    { 2, "ORG_ADMIN", "Quản trị trường" },
                    { 3, "TEACHER", "Giáo viên" },
                    { 4, "STUDENT", "Học sinh" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_generations_lesson_id",
                table: "ai_generations",
                column: "lesson_id");

            migrationBuilder.CreateIndex(
                name: "IX_ai_generations_requested_by",
                table: "ai_generations",
                column: "requested_by");

            migrationBuilder.CreateIndex(
                name: "IX_answers_question_id",
                table: "answers",
                column: "question_id");

            migrationBuilder.CreateIndex(
                name: "IX_attempt_answers_answer_id",
                table: "attempt_answers",
                column: "answer_id");

            migrationBuilder.CreateIndex(
                name: "IX_attempt_answers_question_id",
                table: "attempt_answers",
                column: "question_id");

            migrationBuilder.CreateIndex(
                name: "UX_attempt_answers_attempt_question",
                table: "attempt_answers",
                columns: new[] { "attempt_id", "question_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attempts_student_id",
                table: "attempts",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "UX_attempts_quiz_student",
                table: "attempts",
                columns: new[] { "quiz_id", "student_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_created_at",
                table: "audit_logs",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_user_id",
                table: "audit_logs",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_classes_organization_id",
                table: "classes",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_classes_teacher_id",
                table: "classes",
                column: "teacher_id");

            migrationBuilder.CreateIndex(
                name: "IX_events_timeline_id",
                table: "events",
                column: "timeline_id");

            migrationBuilder.CreateIndex(
                name: "IX_heritage_references_heritage_id",
                table: "heritage_references",
                column: "heritage_id");

            migrationBuilder.CreateIndex(
                name: "IX_heritages_province_id",
                table: "heritages",
                column: "province_id");

            migrationBuilder.CreateIndex(
                name: "UX_heritages_slug",
                table: "heritages",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invoices_organization_id",
                table: "invoices",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_subscription_id",
                table: "invoices",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "UX_invoices_invoice_number",
                table: "invoices",
                column: "invoice_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_lesson_heritages_heritage_id",
                table: "lesson_heritages",
                column: "heritage_id");

            migrationBuilder.CreateIndex(
                name: "IX_lessons_class_id",
                table: "lessons",
                column: "class_id");

            migrationBuilder.CreateIndex(
                name: "IX_lessons_teacher_id",
                table: "lessons",
                column: "teacher_id");

            migrationBuilder.CreateIndex(
                name: "UX_lessons_access_code",
                table: "lessons",
                column: "access_code",
                unique: true,
                filter: "[access_code] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_media_event_id",
                table: "media",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "IX_media_heritage_id",
                table: "media",
                column: "heritage_id");

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_tokens_user_id",
                table: "password_reset_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "UX_password_reset_tokens_token_hash",
                table: "password_reset_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_payment_providers_code",
                table: "payment_providers",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payments_invoice_id",
                table: "payments",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "UX_payments_provider_txn",
                table: "payments",
                columns: new[] { "provider_id", "provider_transaction_id" },
                unique: true,
                filter: "[provider_transaction_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_provinces_code",
                table: "provinces",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_questions_ai_generation_id",
                table: "questions",
                column: "ai_generation_id");

            migrationBuilder.CreateIndex(
                name: "IX_questions_created_by",
                table: "questions",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_questions_heritage_id",
                table: "questions",
                column: "heritage_id");

            migrationBuilder.CreateIndex(
                name: "IX_questions_reviewed_by",
                table: "questions",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_quiz_questions_question_id",
                table: "quiz_questions",
                column: "question_id");

            migrationBuilder.CreateIndex(
                name: "IX_quizzes_created_by",
                table: "quizzes",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_quizzes_lesson_id",
                table: "quizzes",
                column: "lesson_id");

            migrationBuilder.CreateIndex(
                name: "UX_roles_code",
                table: "roles",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_students_class_id",
                table: "students",
                column: "class_id");

            migrationBuilder.CreateIndex(
                name: "UX_students_student_code",
                table: "students",
                column: "student_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_students_user_id",
                table: "students",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_organization_id",
                table: "subscriptions",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_plan_id",
                table: "subscriptions",
                column: "plan_id");

            migrationBuilder.CreateIndex(
                name: "IX_timelines_heritage_id",
                table: "timelines",
                column: "heritage_id");

            migrationBuilder.CreateIndex(
                name: "IX_users_organization_id",
                table: "users",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_users_role_id",
                table: "users",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "UX_users_email",
                table: "users",
                column: "email",
                unique: true,
                filter: "[email] IS NOT NULL AND [deleted_at] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_users_username",
                table: "users",
                column: "username",
                unique: true,
                filter: "[deleted_at] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attempt_answers");

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "heritage_references");

            migrationBuilder.DropTable(
                name: "lesson_heritages");

            migrationBuilder.DropTable(
                name: "media");

            migrationBuilder.DropTable(
                name: "password_reset_tokens");

            migrationBuilder.DropTable(
                name: "payments");

            migrationBuilder.DropTable(
                name: "quiz_questions");

            migrationBuilder.DropTable(
                name: "answers");

            migrationBuilder.DropTable(
                name: "attempts");

            migrationBuilder.DropTable(
                name: "events");

            migrationBuilder.DropTable(
                name: "invoices");

            migrationBuilder.DropTable(
                name: "payment_providers");

            migrationBuilder.DropTable(
                name: "questions");

            migrationBuilder.DropTable(
                name: "quizzes");

            migrationBuilder.DropTable(
                name: "students");

            migrationBuilder.DropTable(
                name: "timelines");

            migrationBuilder.DropTable(
                name: "subscriptions");

            migrationBuilder.DropTable(
                name: "ai_generations");

            migrationBuilder.DropTable(
                name: "heritages");

            migrationBuilder.DropTable(
                name: "plans");

            migrationBuilder.DropTable(
                name: "lessons");

            migrationBuilder.DropTable(
                name: "provinces");

            migrationBuilder.DropTable(
                name: "classes");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "organizations");

            migrationBuilder.DropTable(
                name: "roles");
        }
    }
}
