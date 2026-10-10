using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataTrace.Infrastructure.Persistence.Migrations.Config
{
    /// <inheritdoc />
    public partial class InitialConfigSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActiveSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PalletCode = table.Column<string>(type: "TEXT", nullable: false),
                    SerialNo = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<long>(type: "INTEGER", nullable: false),
                    MonthKey = table.Column<string>(type: "TEXT", nullable: false),
                    StartTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RecipeCode = table.Column<string>(type: "TEXT", nullable: false),
                    LastStationId = table.Column<int>(type: "INTEGER", nullable: false),
                    LastStationCode = table.Column<string>(type: "TEXT", nullable: false),
                    LastActivityAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActiveSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AlarmIncidents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    RaisedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AcknowledgedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "TEXT", nullable: true),
                    ClearedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmIncidents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    MustChangePassword = table.Column<bool>(type: "INTEGER", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: true),
                    SecurityStamp = table.Column<string>(type: "TEXT", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true),
                    PhoneNumber = table.Column<string>(type: "TEXT", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", nullable: false),
                    EntityKey = table.Column<string>(type: "TEXT", nullable: true),
                    OldValue = table.Column<string>(type: "TEXT", nullable: true),
                    NewValue = table.Column<string>(type: "TEXT", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    SourceIp = table.Column<string>(type: "TEXT", nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConfigVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MesOutbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MonthKey = table.Column<string>(type: "TEXT", nullable: false),
                    PalletSessionId = table.Column<long>(type: "INTEGER", nullable: false),
                    SerialNo = table.Column<string>(type: "TEXT", nullable: false),
                    PalletCode = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MesOutbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlcConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Brand = table.Column<int>(type: "INTEGER", nullable: false),
                    Host = table.Column<string>(type: "TEXT", nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeoutMs = table.Column<int>(type: "INTEGER", nullable: false),
                    FloatWordOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    StringHighByteFirst = table.Column<bool>(type: "INTEGER", nullable: false),
                    MergeGapWords = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Extra = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlcConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QualityIncidents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IncidentNumber = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    StationId = table.Column<int>(type: "INTEGER", nullable: true),
                    StationCode = table.Column<string>(type: "TEXT", nullable: false),
                    RecipeCode = table.Column<string>(type: "TEXT", nullable: false),
                    SourceAlarmId = table.Column<long>(type: "INTEGER", nullable: true),
                    ScopeFrom = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ScopeTo = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    Owner = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityIncidents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Recipes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Remark = table.Column<string>(type: "TEXT", nullable: true),
                    PreviousCodes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Recipes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SerialCounters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DayKey = table.Column<string>(type: "TEXT", nullable: false),
                    LastValue = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SerialCounters", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScanIntervalMs = table.Column<int>(type: "INTEGER", nullable: false),
                    WriteRetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    WriteRetryDelayMs = table.Column<int>(type: "INTEGER", nullable: false),
                    RetentionYears = table.Column<int>(type: "INTEGER", nullable: false),
                    AuditRetentionYears = table.Column<int>(type: "INTEGER", nullable: false),
                    CurveRootPath = table.Column<string>(type: "TEXT", nullable: false),
                    SpoolPath = table.Column<string>(type: "TEXT", nullable: false),
                    RuntimeDbPath = table.Column<string>(type: "TEXT", nullable: false),
                    CollectEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    MesEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    MesEndpoint = table.Column<string>(type: "TEXT", nullable: true),
                    MesTimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    SimulatorAutoRun = table.Column<bool>(type: "INTEGER", nullable: false),
                    SimulatorIntervalMs = table.Column<int>(type: "INTEGER", nullable: false),
                    SimulatorNgPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    SimulatorPalletPool = table.Column<int>(type: "INTEGER", nullable: false),
                    ActiveRecipeId = table.Column<int>(type: "INTEGER", nullable: true),
                    ShiftStartHour = table.Column<int>(type: "INTEGER", nullable: false),
                    ShiftLengthHours = table.Column<int>(type: "INTEGER", nullable: false),
                    AlarmWebhookUrl = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RoleId = table.Column<string>(type: "TEXT", nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    RoleId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    LoginProvider = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Heartbeats",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlcConnectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Address = table.Column<string>(type: "TEXT", nullable: false),
                    IntervalMs = table.Column<int>(type: "INTEGER", nullable: false),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Heartbeats", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Heartbeats_PlcConnections_PlcConnectionId",
                        column: x => x.PlcConnectionId,
                        principalTable: "PlcConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Stations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlcConnectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    IsFirstStation = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsLastStation = table.Column<bool>(type: "INTEGER", nullable: false),
                    TriggerAddress = table.Column<string>(type: "TEXT", nullable: false),
                    TriggerValue = table.Column<short>(type: "INTEGER", nullable: false),
                    PalletCodeAddress = table.Column<string>(type: "TEXT", nullable: false),
                    PalletCodeLength = table.Column<int>(type: "INTEGER", nullable: false),
                    PalletCodeDataType = table.Column<int>(type: "INTEGER", nullable: false),
                    PositionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DataFilePath = table.Column<string>(type: "TEXT", nullable: false),
                    DataFileFormat = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Stations_PlcConnections_PlcConnectionId",
                        column: x => x.PlcConnectionId,
                        principalTable: "PlcConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QualityIncidentActivities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IncidentId = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    FromStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    ToStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    CauseCategory = table.Column<string>(type: "TEXT", nullable: true),
                    ActionTaken = table.Column<string>(type: "TEXT", nullable: true),
                    VerificationResult = table.Column<string>(type: "TEXT", nullable: true),
                    Comment = table.Column<string>(type: "TEXT", nullable: true),
                    Actor = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityIncidentActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QualityIncidentActivities_QualityIncidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "QualityIncidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QualityIncidentEvidence",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IncidentId = table.Column<long>(type: "INTEGER", nullable: false),
                    MonthKey = table.Column<string>(type: "TEXT", nullable: false),
                    RecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    Purpose = table.Column<int>(type: "INTEGER", nullable: false),
                    SerialNo = table.Column<string>(type: "TEXT", nullable: false),
                    PalletCode = table.Column<string>(type: "TEXT", nullable: false),
                    StationCode = table.Column<string>(type: "TEXT", nullable: false),
                    RecipeCode = table.Column<string>(type: "TEXT", nullable: false),
                    TriggerTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Judgement = table.Column<int>(type: "INTEGER", nullable: false),
                    ResultCode = table.Column<short>(type: "INTEGER", nullable: false),
                    TagSnapshotJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityIncidentEvidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QualityIncidentEvidence_QualityIncidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "QualityIncidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecipeLimits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RecipeId = table.Column<int>(type: "INTEGER", nullable: false),
                    TagId = table.Column<int>(type: "INTEGER", nullable: false),
                    LowerLimit = table.Column<double>(type: "REAL", nullable: true),
                    UpperLimit = table.Column<double>(type: "REAL", nullable: true),
                    WarningLowerLimit = table.Column<double>(type: "REAL", nullable: true),
                    WarningUpperLimit = table.Column<double>(type: "REAL", nullable: true),
                    TargetValue = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeLimits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecipeLimits_Recipes_RecipeId",
                        column: x => x.RecipeId,
                        principalTable: "Recipes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Curves",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StationId = table.Column<int>(type: "INTEGER", nullable: false),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    PointCount = table.Column<int>(type: "INTEGER", nullable: false),
                    PositionIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Curves", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Curves_Stations_StationId",
                        column: x => x.StationId,
                        principalTable: "Stations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductPositions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StationId = table.Column<int>(type: "INTEGER", nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    OccupiedAddress = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductPositions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductPositions_Stations_StationId",
                        column: x => x.StationId,
                        principalTable: "Stations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StationId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Address = table.Column<string>(type: "TEXT", nullable: false),
                    DataType = table.Column<int>(type: "INTEGER", nullable: false),
                    Length = table.Column<int>(type: "INTEGER", nullable: false),
                    Scale = table.Column<double>(type: "REAL", nullable: false),
                    Offset = table.Column<double>(type: "REAL", nullable: false),
                    Unit = table.Column<string>(type: "TEXT", nullable: true),
                    LowerLimit = table.Column<double>(type: "REAL", nullable: true),
                    UpperLimit = table.Column<double>(type: "REAL", nullable: true),
                    WarningLowerLimit = table.Column<double>(type: "REAL", nullable: true),
                    WarningUpperLimit = table.Column<double>(type: "REAL", nullable: true),
                    TargetValue = table.Column<double>(type: "REAL", nullable: true),
                    IsRequired = table.Column<bool>(type: "INTEGER", nullable: false),
                    PositionIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    SpcRuleMask = table.Column<int>(type: "INTEGER", nullable: true),
                    ControlCenterLine = table.Column<double>(type: "REAL", nullable: true),
                    ControlUpperLimit = table.Column<double>(type: "REAL", nullable: true),
                    ControlLowerLimit = table.Column<double>(type: "REAL", nullable: true),
                    ControlSampleCount = table.Column<int>(type: "INTEGER", nullable: true),
                    ControlCapturedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ControlCapturedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tags_Stations_StationId",
                        column: x => x.StationId,
                        principalTable: "Stations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CurveCriteria",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CurveDefinitionId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeriesName = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    PeakMin = table.Column<double>(type: "REAL", nullable: true),
                    PeakMax = table.Column<double>(type: "REAL", nullable: true),
                    MeanMin = table.Column<double>(type: "REAL", nullable: true),
                    MeanMax = table.Column<double>(type: "REAL", nullable: true),
                    AreaMin = table.Column<double>(type: "REAL", nullable: true),
                    AreaMax = table.Column<double>(type: "REAL", nullable: true),
                    RiseSlopeMin = table.Column<double>(type: "REAL", nullable: true),
                    RiseSlopeMax = table.Column<double>(type: "REAL", nullable: true),
                    HoldSlopeMin = table.Column<double>(type: "REAL", nullable: true),
                    HoldSlopeMax = table.Column<double>(type: "REAL", nullable: true),
                    FallRatioMax = table.Column<double>(type: "REAL", nullable: true),
                    MaxStepMax = table.Column<double>(type: "REAL", nullable: true),
                    StdDevMax = table.Column<double>(type: "REAL", nullable: true),
                    OscillationMax = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CurveCriteria", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CurveCriteria_Curves_CurveDefinitionId",
                        column: x => x.CurveDefinitionId,
                        principalTable: "Curves",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CurveSeries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CurveDefinitionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    StartAddress = table.Column<string>(type: "TEXT", nullable: false),
                    DataType = table.Column<int>(type: "INTEGER", nullable: false),
                    StrideWords = table.Column<int>(type: "INTEGER", nullable: false),
                    Scale = table.Column<double>(type: "REAL", nullable: false),
                    Offset = table.Column<double>(type: "REAL", nullable: false),
                    Unit = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CurveSeries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CurveSeries_Curves_CurveDefinitionId",
                        column: x => x.CurveDefinitionId,
                        principalTable: "Curves",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActiveSessions_PalletCode",
                table: "ActiveSessions",
                column: "PalletCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActiveSessions_SerialNo",
                table: "ActiveSessions",
                column: "SerialNo");

            migrationBuilder.CreateIndex(
                name: "IX_AlarmIncidents_RaisedAt",
                table: "AlarmIncidents",
                column: "RaisedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Time",
                table: "AuditLogs",
                column: "Time");

            migrationBuilder.CreateIndex(
                name: "IX_CurveCriteria_CurveDefinitionId",
                table: "CurveCriteria",
                column: "CurveDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_Curves_StationId_Code_PositionIndex",
                table: "Curves",
                columns: new[] { "StationId", "Code", "PositionIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CurveSeries_CurveDefinitionId",
                table: "CurveSeries",
                column: "CurveDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_Heartbeats_PlcConnectionId",
                table: "Heartbeats",
                column: "PlcConnectionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MesOutbox_Status_CreatedAt",
                table: "MesOutbox",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PlcConnections_Name",
                table: "PlcConnections",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductPositions_StationId",
                table: "ProductPositions",
                column: "StationId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityIncidentActivities_IncidentId_CreatedAt",
                table: "QualityIncidentActivities",
                columns: new[] { "IncidentId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityIncidentEvidence_IncidentId_MonthKey_RecordId",
                table: "QualityIncidentEvidence",
                columns: new[] { "IncidentId", "MonthKey", "RecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QualityIncidentEvidence_MonthKey_RecordId",
                table: "QualityIncidentEvidence",
                columns: new[] { "MonthKey", "RecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityIncidents_IncidentNumber",
                table: "QualityIncidents",
                column: "IncidentNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QualityIncidents_Owner_Status",
                table: "QualityIncidents",
                columns: new[] { "Owner", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityIncidents_RequestId",
                table: "QualityIncidents",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QualityIncidents_Status_UpdatedAt",
                table: "QualityIncidents",
                columns: new[] { "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeLimits_RecipeId_TagId",
                table: "RecipeLimits",
                columns: new[] { "RecipeId", "TagId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_Code",
                table: "Recipes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SerialCounters_DayKey",
                table: "SerialCounters",
                column: "DayKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Stations_Code",
                table: "Stations",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Stations_PlcConnectionId",
                table: "Stations",
                column: "PlcConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_Tags_StationId_Name",
                table: "Tags",
                columns: new[] { "StationId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActiveSessions");

            migrationBuilder.DropTable(
                name: "AlarmIncidents");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "ConfigVersions");

            migrationBuilder.DropTable(
                name: "CurveCriteria");

            migrationBuilder.DropTable(
                name: "CurveSeries");

            migrationBuilder.DropTable(
                name: "Heartbeats");

            migrationBuilder.DropTable(
                name: "MesOutbox");

            migrationBuilder.DropTable(
                name: "ProductPositions");

            migrationBuilder.DropTable(
                name: "QualityIncidentActivities");

            migrationBuilder.DropTable(
                name: "QualityIncidentEvidence");

            migrationBuilder.DropTable(
                name: "RecipeLimits");

            migrationBuilder.DropTable(
                name: "SerialCounters");

            migrationBuilder.DropTable(
                name: "SystemSettings");

            migrationBuilder.DropTable(
                name: "Tags");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "Curves");

            migrationBuilder.DropTable(
                name: "QualityIncidents");

            migrationBuilder.DropTable(
                name: "Recipes");

            migrationBuilder.DropTable(
                name: "Stations");

            migrationBuilder.DropTable(
                name: "PlcConnections");
        }
    }
}
