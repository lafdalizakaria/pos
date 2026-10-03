using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Newrest.Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "pos");

            migrationBuilder.CreateTable(
                name: "AuditLog",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    BeforeJson = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    AfterJson = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Categories",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    ColorHex = table.Column<string>(type: "nvarchar(9)", maxLength: 9, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Companies",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LegalName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Ice = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: true),
                    TaxId = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    TradeRegister = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Companies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Articles",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    ReceiptLabel = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BasePrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    VatRate = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    VisualDescription = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsSubsidizable = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Articles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Articles_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalSchema: "pos",
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ClientCompanies",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Ice = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: true),
                    BillingAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientCompanies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientCompanies_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "pos",
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Sites",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TimeZone = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sites_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "pos",
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ArticlePhotos",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArticleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StoragePath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Caption = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArticlePhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArticlePhotos_Articles_ArticleId",
                        column: x => x.ArticleId,
                        principalSchema: "pos",
                        principalTable: "Articles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Contracts",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientCompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    BillingMode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contracts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Contracts_ClientCompanies_ClientCompanyId",
                        column: x => x.ClientCompanyId,
                        principalSchema: "pos",
                        principalTable: "ClientCompanies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Diners",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientCompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeNumber = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Diners", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Diners_ClientCompanies_ClientCompanyId",
                        column: x => x.ClientCompanyId,
                        principalSchema: "pos",
                        principalTable: "ClientCompanies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Operators",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Roles = table.Column<int>(type: "int", nullable: false),
                    PinHash = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    FailedPinAttempts = table.Column<int>(type: "int", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Operators", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Operators_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "pos",
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Operators_Sites_SiteId",
                        column: x => x.SiteId,
                        principalSchema: "pos",
                        principalTable: "Sites",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PointsOfSale",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PointsOfSale", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PointsOfSale_Sites_SiteId",
                        column: x => x.SiteId,
                        principalSchema: "pos",
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SubsidyRules",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Value = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    MaxPerMeal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    MaxPerDay = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    MaxMealsPerDay = table.Column<int>(type: "int", nullable: true),
                    DinerCategory = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubsidyRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubsidyRules_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalSchema: "pos",
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Accounts",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DinerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    OverdraftLimit = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CachedBalance = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                    table.CheckConstraint("CK_Accounts_OverdraftLimit", "[OverdraftLimit] >= 0");
                    table.ForeignKey(
                        name: "FK_Accounts_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalSchema: "pos",
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Accounts_Diners_DinerId",
                        column: x => x.DinerId,
                        principalSchema: "pos",
                        principalTable: "Diners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Badges",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DinerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeactivatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReplacedByBadgeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Badges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Badges_Badges_ReplacedByBadgeId",
                        column: x => x.ReplacedByBadgeId,
                        principalSchema: "pos",
                        principalTable: "Badges",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Badges_Diners_DinerId",
                        column: x => x.DinerId,
                        principalSchema: "pos",
                        principalTable: "Diners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ContractPointsOfSale",
                schema: "pos",
                columns: table => new
                {
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PointOfSaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractPointsOfSale", x => new { x.ContractId, x.PointOfSaleId });
                    table.ForeignKey(
                        name: "FK_ContractPointsOfSale_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalSchema: "pos",
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContractPointsOfSale_PointsOfSale_PointOfSaleId",
                        column: x => x.PointOfSaleId,
                        principalSchema: "pos",
                        principalTable: "PointsOfSale",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DailyMenus",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PointOfSaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Service = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    IsPublished = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyMenus", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DailyMenus_PointsOfSale_PointOfSaleId",
                        column: x => x.PointOfSaleId,
                        principalSchema: "pos",
                        principalTable: "PointsOfSale",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PriceLists",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PointOfSaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceLists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceLists_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "pos",
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PriceLists_PointsOfSale_PointOfSaleId",
                        column: x => x.PointOfSaleId,
                        principalSchema: "pos",
                        principalTable: "PointsOfSale",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PriceLists_Sites_SiteId",
                        column: x => x.SiteId,
                        principalSchema: "pos",
                        principalTable: "Sites",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Registers",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PointOfSaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TicketPrefix = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    LastSyncedTicketSequence = table.Column<long>(type: "bigint", nullable: false),
                    LastSyncedTicketHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true),
                    LastZNumber = table.Column<int>(type: "int", nullable: false),
                    DeviceKeyHash = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Registers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Registers_PointsOfSale_PointOfSaleId",
                        column: x => x.PointOfSaleId,
                        principalSchema: "pos",
                        principalTable: "PointsOfSale",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DailyMenuItems",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DailyMenuId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArticleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EffectivePrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    IsAvailable = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyMenuItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DailyMenuItems_Articles_ArticleId",
                        column: x => x.ArticleId,
                        principalSchema: "pos",
                        principalTable: "Articles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DailyMenuItems_DailyMenus_DailyMenuId",
                        column: x => x.DailyMenuId,
                        principalSchema: "pos",
                        principalTable: "DailyMenus",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PriceOverrides",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PriceListId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArticleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Price = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceOverrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceOverrides_Articles_ArticleId",
                        column: x => x.ArticleId,
                        principalSchema: "pos",
                        principalTable: "Articles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PriceOverrides_PriceLists_PriceListId",
                        column: x => x.PriceListId,
                        principalSchema: "pos",
                        principalTable: "PriceLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccountMovements",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReversesMovementId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RegisterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OperatorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PerformedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PaymentMethod = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    BalanceAfter = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    IsOfflineReplay = table.Column<bool>(type: "bit", nullable: false),
                    ExceededOverdraft = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountMovements", x => x.Id);
                    table.CheckConstraint("CK_AccountMovements_Amount", "[Amount] <> 0");
                    table.ForeignKey(
                        name: "FK_AccountMovements_AccountMovements_ReversesMovementId",
                        column: x => x.ReversesMovementId,
                        principalSchema: "pos",
                        principalTable: "AccountMovements",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AccountMovements_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "pos",
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountMovements_Operators_OperatorId",
                        column: x => x.OperatorId,
                        principalSchema: "pos",
                        principalTable: "Operators",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AccountMovements_Registers_RegisterId",
                        column: x => x.RegisterId,
                        principalSchema: "pos",
                        principalTable: "Registers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "RecognitionLogs",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegisterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CapturedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    LatencyMs = table.Column<int>(type: "int", nullable: false),
                    TimedOut = table.Column<bool>(type: "bit", nullable: false),
                    ImageStorageKey = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RawPredictionsJson = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: false),
                    ValidatedLinesJson = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    PredictedCount = table.Column<int>(type: "int", nullable: false),
                    AutoAcceptedCount = table.Column<int>(type: "int", nullable: false),
                    CorrectedCount = table.Column<int>(type: "int", nullable: false),
                    AddedManuallyCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecognitionLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecognitionLogs_Registers_RegisterId",
                        column: x => x.RegisterId,
                        principalSchema: "pos",
                        principalTable: "Registers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CashSessions",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegisterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OpenedByOperatorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    OpenedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OpeningFloat = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClosedByOperatorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WasForced = table.Column<bool>(type: "bit", nullable: false),
                    ZReportId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CashSessions_Operators_ClosedByOperatorId",
                        column: x => x.ClosedByOperatorId,
                        principalSchema: "pos",
                        principalTable: "Operators",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CashSessions_Operators_OpenedByOperatorId",
                        column: x => x.OpenedByOperatorId,
                        principalSchema: "pos",
                        principalTable: "Operators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashSessions_Registers_RegisterId",
                        column: x => x.RegisterId,
                        principalSchema: "pos",
                        principalTable: "Registers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Tickets",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegisterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CashSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperatorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DinerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BadgeNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SubsidyRuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreditedTicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreditReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TotalAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalVat = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    SubsidyAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DinerShare = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PreviousHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    Hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tickets", x => x.Id);
                    table.CheckConstraint("CK_Tickets_Shares", "[TotalAmount] = [SubsidyAmount] + [DinerShare]");
                    table.ForeignKey(
                        name: "FK_Tickets_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "pos",
                        principalTable: "Accounts",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Tickets_CashSessions_CashSessionId",
                        column: x => x.CashSessionId,
                        principalSchema: "pos",
                        principalTable: "CashSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_Diners_DinerId",
                        column: x => x.DinerId,
                        principalSchema: "pos",
                        principalTable: "Diners",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Tickets_Operators_OperatorId",
                        column: x => x.OperatorId,
                        principalSchema: "pos",
                        principalTable: "Operators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_Registers_RegisterId",
                        column: x => x.RegisterId,
                        principalSchema: "pos",
                        principalTable: "Registers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_SubsidyRules_SubsidyRuleId",
                        column: x => x.SubsidyRuleId,
                        principalSchema: "pos",
                        principalTable: "SubsidyRules",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Tickets_Tickets_CreditedTicketId",
                        column: x => x.CreditedTicketId,
                        principalSchema: "pos",
                        principalTable: "Tickets",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ZReports",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegisterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CashSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ZNumber = table.Column<int>(type: "int", nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    OpenedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FirstTicketSequence = table.Column<long>(type: "bigint", nullable: true),
                    LastTicketSequence = table.Column<long>(type: "bigint", nullable: true),
                    SaleCount = table.Column<int>(type: "int", nullable: false),
                    CreditNoteCount = table.Column<int>(type: "int", nullable: false),
                    GrossSales = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreditNotesTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    NetSales = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalVat = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    SubsidyTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DinerShareTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    AccountTopUpTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    OpeningFloat = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ExpectedCash = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CountedCash = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CashDifference = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    LastTicketHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ZReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ZReports_CashSessions_CashSessionId",
                        column: x => x.CashSessionId,
                        principalSchema: "pos",
                        principalTable: "CashSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ZReports_Registers_RegisterId",
                        column: x => x.RegisterId,
                        principalSchema: "pos",
                        principalTable: "Registers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Index = table.Column<int>(type: "int", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Tendered = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Change = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    AuthorizationCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AccountMovementId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Payments_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalSchema: "pos",
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TicketLines",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    ArticleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArticleCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    VatRate = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    LineTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    VatAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    IsSubsidizable = table.Column<bool>(type: "bit", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketLines_Articles_ArticleId",
                        column: x => x.ArticleId,
                        principalSchema: "pos",
                        principalTable: "Articles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketLines_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalSchema: "pos",
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ZReportLines",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ZReportId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Section = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Key = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Count = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    BaseAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    TaxAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ZReportLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ZReportLines_ZReports_ZReportId",
                        column: x => x.ZReportId,
                        principalSchema: "pos",
                        principalTable: "ZReports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountMovements_AccountId_OccurredAt",
                schema: "pos",
                table: "AccountMovements",
                columns: new[] { "AccountId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountMovements_IdempotencyKey",
                schema: "pos",
                table: "AccountMovements",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountMovements_OperatorId",
                schema: "pos",
                table: "AccountMovements",
                column: "OperatorId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountMovements_RegisterId_OccurredAt",
                schema: "pos",
                table: "AccountMovements",
                columns: new[] { "RegisterId", "OccurredAt" },
                filter: "[RegisterId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AccountMovements_ReversesMovementId",
                schema: "pos",
                table: "AccountMovements",
                column: "ReversesMovementId",
                unique: true,
                filter: "[ReversesMovementId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AccountMovements_TicketId",
                schema: "pos",
                table: "AccountMovements",
                column: "TicketId",
                filter: "[TicketId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_ContractId",
                schema: "pos",
                table: "Accounts",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_DinerId_ContractId",
                schema: "pos",
                table: "Accounts",
                columns: new[] { "DinerId", "ContractId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_RowVersion",
                schema: "pos",
                table: "Accounts",
                column: "RowVersion");

            migrationBuilder.CreateIndex(
                name: "IX_ArticlePhotos_ArticleId_DisplayOrder",
                schema: "pos",
                table: "ArticlePhotos",
                columns: new[] { "ArticleId", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_Articles_CategoryId",
                schema: "pos",
                table: "Articles",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Articles_Code",
                schema: "pos",
                table: "Articles",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Articles_RowVersion",
                schema: "pos",
                table: "Articles",
                column: "RowVersion");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_EntityType_EntityId",
                schema: "pos",
                table: "AuditLog",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_OccurredAt",
                schema: "pos",
                table: "AuditLog",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_Badges_DinerId",
                schema: "pos",
                table: "Badges",
                column: "DinerId");

            migrationBuilder.CreateIndex(
                name: "IX_Badges_Number",
                schema: "pos",
                table: "Badges",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Badges_ReplacedByBadgeId",
                schema: "pos",
                table: "Badges",
                column: "ReplacedByBadgeId");

            migrationBuilder.CreateIndex(
                name: "IX_Badges_RowVersion",
                schema: "pos",
                table: "Badges",
                column: "RowVersion");

            migrationBuilder.CreateIndex(
                name: "IX_CashSessions_ClosedByOperatorId",
                schema: "pos",
                table: "CashSessions",
                column: "ClosedByOperatorId");

            migrationBuilder.CreateIndex(
                name: "IX_CashSessions_OpenedByOperatorId",
                schema: "pos",
                table: "CashSessions",
                column: "OpenedByOperatorId");

            migrationBuilder.CreateIndex(
                name: "IX_CashSessions_RegisterId_BusinessDate",
                schema: "pos",
                table: "CashSessions",
                columns: new[] { "RegisterId", "BusinessDate" });

            migrationBuilder.CreateIndex(
                name: "IX_CashSessions_ZReportId",
                schema: "pos",
                table: "CashSessions",
                column: "ZReportId");

            migrationBuilder.CreateIndex(
                name: "UX_CashSessions_OneOpenPerRegister",
                schema: "pos",
                table: "CashSessions",
                column: "RegisterId",
                unique: true,
                filter: "[Status] = 'Open'");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_Code",
                schema: "pos",
                table: "Categories",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Categories_RowVersion",
                schema: "pos",
                table: "Categories",
                column: "RowVersion");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCompanies_CompanyId_Code",
                schema: "pos",
                table: "ClientCompanies",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientCompanies_RowVersion",
                schema: "pos",
                table: "ClientCompanies",
                column: "RowVersion");

            migrationBuilder.CreateIndex(
                name: "IX_Companies_Code",
                schema: "pos",
                table: "Companies",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Companies_RowVersion",
                schema: "pos",
                table: "Companies",
                column: "RowVersion");

            migrationBuilder.CreateIndex(
                name: "IX_ContractPointsOfSale_PointOfSaleId",
                schema: "pos",
                table: "ContractPointsOfSale",
                column: "PointOfSaleId");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_ClientCompanyId",
                schema: "pos",
                table: "Contracts",
                column: "ClientCompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_Reference",
                schema: "pos",
                table: "Contracts",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_RowVersion",
                schema: "pos",
                table: "Contracts",
                column: "RowVersion");

            migrationBuilder.CreateIndex(
                name: "IX_DailyMenuItems_ArticleId",
                schema: "pos",
                table: "DailyMenuItems",
                column: "ArticleId");

            migrationBuilder.CreateIndex(
                name: "IX_DailyMenuItems_DailyMenuId_ArticleId",
                schema: "pos",
                table: "DailyMenuItems",
                columns: new[] { "DailyMenuId", "ArticleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DailyMenus_PointOfSaleId_Date_Service",
                schema: "pos",
                table: "DailyMenus",
                columns: new[] { "PointOfSaleId", "Date", "Service" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DailyMenus_RowVersion",
                schema: "pos",
                table: "DailyMenus",
                column: "RowVersion");

            migrationBuilder.CreateIndex(
                name: "IX_Diners_ClientCompanyId_EmployeeNumber",
                schema: "pos",
                table: "Diners",
                columns: new[] { "ClientCompanyId", "EmployeeNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Diners_RowVersion",
                schema: "pos",
                table: "Diners",
                column: "RowVersion");

            migrationBuilder.CreateIndex(
                name: "IX_Operators_CompanyId_Code",
                schema: "pos",
                table: "Operators",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Operators_RowVersion",
                schema: "pos",
                table: "Operators",
                column: "RowVersion");

            migrationBuilder.CreateIndex(
                name: "IX_Operators_SiteId",
                schema: "pos",
                table: "Operators",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_TicketId_Index",
                schema: "pos",
                table: "Payments",
                columns: new[] { "TicketId", "Index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PointsOfSale_RowVersion",
                schema: "pos",
                table: "PointsOfSale",
                column: "RowVersion");

            migrationBuilder.CreateIndex(
                name: "IX_PointsOfSale_SiteId_Code",
                schema: "pos",
                table: "PointsOfSale",
                columns: new[] { "SiteId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceLists_CompanyId_Code",
                schema: "pos",
                table: "PriceLists",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceLists_PointOfSaleId",
                schema: "pos",
                table: "PriceLists",
                column: "PointOfSaleId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceLists_RowVersion",
                schema: "pos",
                table: "PriceLists",
                column: "RowVersion");

            migrationBuilder.CreateIndex(
                name: "IX_PriceLists_SiteId",
                schema: "pos",
                table: "PriceLists",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceOverrides_ArticleId",
                schema: "pos",
                table: "PriceOverrides",
                column: "ArticleId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceOverrides_PriceListId_ArticleId",
                schema: "pos",
                table: "PriceOverrides",
                columns: new[] { "PriceListId", "ArticleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionLogs_RegisterId_CapturedAt",
                schema: "pos",
                table: "RecognitionLogs",
                columns: new[] { "RegisterId", "CapturedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionLogs_TicketId",
                schema: "pos",
                table: "RecognitionLogs",
                column: "TicketId",
                filter: "[TicketId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Registers_PointOfSaleId_Code",
                schema: "pos",
                table: "Registers",
                columns: new[] { "PointOfSaleId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Registers_RowVersion",
                schema: "pos",
                table: "Registers",
                column: "RowVersion");

            migrationBuilder.CreateIndex(
                name: "IX_Registers_TicketPrefix",
                schema: "pos",
                table: "Registers",
                column: "TicketPrefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sites_CompanyId_Code",
                schema: "pos",
                table: "Sites",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sites_RowVersion",
                schema: "pos",
                table: "Sites",
                column: "RowVersion");

            migrationBuilder.CreateIndex(
                name: "IX_SubsidyRules_ContractId_ValidFrom",
                schema: "pos",
                table: "SubsidyRules",
                columns: new[] { "ContractId", "ValidFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_SubsidyRules_RowVersion",
                schema: "pos",
                table: "SubsidyRules",
                column: "RowVersion");

            migrationBuilder.CreateIndex(
                name: "IX_TicketLines_ArticleId",
                schema: "pos",
                table: "TicketLines",
                column: "ArticleId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketLines_TicketId_LineNumber",
                schema: "pos",
                table: "TicketLines",
                columns: new[] { "TicketId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_AccountId",
                schema: "pos",
                table: "Tickets",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_CashSessionId",
                schema: "pos",
                table: "Tickets",
                column: "CashSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_CreditedTicketId",
                schema: "pos",
                table: "Tickets",
                column: "CreditedTicketId",
                unique: true,
                filter: "[CreditedTicketId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_DinerId_BusinessDate",
                schema: "pos",
                table: "Tickets",
                columns: new[] { "DinerId", "BusinessDate" },
                filter: "[DinerId] IS NOT NULL")
                .Annotation("SqlServer:Include", new[] { "SubsidyAmount", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_Number",
                schema: "pos",
                table: "Tickets",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_OperatorId",
                schema: "pos",
                table: "Tickets",
                column: "OperatorId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_RegisterId_BusinessDate",
                schema: "pos",
                table: "Tickets",
                columns: new[] { "RegisterId", "BusinessDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_RegisterId_Sequence",
                schema: "pos",
                table: "Tickets",
                columns: new[] { "RegisterId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_SubsidyRuleId",
                schema: "pos",
                table: "Tickets",
                column: "SubsidyRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_ZReportLines_ZReportId_Section_Key",
                schema: "pos",
                table: "ZReportLines",
                columns: new[] { "ZReportId", "Section", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ZReports_CashSessionId",
                schema: "pos",
                table: "ZReports",
                column: "CashSessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ZReports_RegisterId_ZNumber",
                schema: "pos",
                table: "ZReports",
                columns: new[] { "RegisterId", "ZNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CashSessions_ZReports_ZReportId",
                schema: "pos",
                table: "CashSessions",
                column: "ZReportId",
                principalSchema: "pos",
                principalTable: "ZReports",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CashSessions_Operators_ClosedByOperatorId",
                schema: "pos",
                table: "CashSessions");

            migrationBuilder.DropForeignKey(
                name: "FK_CashSessions_Operators_OpenedByOperatorId",
                schema: "pos",
                table: "CashSessions");

            migrationBuilder.DropForeignKey(
                name: "FK_CashSessions_Registers_RegisterId",
                schema: "pos",
                table: "CashSessions");

            migrationBuilder.DropForeignKey(
                name: "FK_ZReports_Registers_RegisterId",
                schema: "pos",
                table: "ZReports");

            migrationBuilder.DropForeignKey(
                name: "FK_CashSessions_ZReports_ZReportId",
                schema: "pos",
                table: "CashSessions");

            migrationBuilder.DropTable(
                name: "AccountMovements",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "ArticlePhotos",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "AuditLog",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "Badges",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "ContractPointsOfSale",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "DailyMenuItems",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "Payments",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "PriceOverrides",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "RecognitionLogs",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "TicketLines",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "ZReportLines",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "DailyMenus",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "PriceLists",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "Articles",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "Tickets",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "Categories",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "Accounts",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "SubsidyRules",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "Diners",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "Contracts",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "ClientCompanies",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "Operators",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "Registers",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "PointsOfSale",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "Sites",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "Companies",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "ZReports",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "CashSessions",
                schema: "pos");
        }
    }
}
