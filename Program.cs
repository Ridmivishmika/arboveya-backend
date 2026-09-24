using System.Text;
using Arboveya.Api.Data;
using Arboveya.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

// 0. Load .env file if present in the backend directory or project root
void LoadEnvFile(string path)
{
    if (!File.Exists(path)) return;
    foreach (var line in File.ReadAllLines(path))
    {
        var trimmed = line.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#')) continue;
        var sep = trimmed.IndexOf('=');
        if (sep > 0)
        {
            var key = trimmed[..sep].Trim();
            var val = trimmed[(sep + 1)..].Trim().Trim('"', '\'');
            if (!string.IsNullOrEmpty(key))
            {
                Environment.SetEnvironmentVariable(key, val);
            }
        }
    }
}

LoadEnvFile(Path.Combine(Directory.GetCurrentDirectory(), ".env"));
LoadEnvFile(Path.Combine(Directory.GetCurrentDirectory(), "..", ".env"));

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();

// 1. Add Controllers
builder.Services.AddControllers();

// 2. Configure PostgreSQL DbContext
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
    ?? Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? builder.Configuration["ConnectionStrings:DefaultConnection"]
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseNpgsql(connectionString);
    options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
});

// 3. Register Application Services
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ICategoryService, CategoryService>();
builder.Services.AddScoped<IWellnessNeedService, WellnessNeedService>();
builder.Services.AddScoped<IProductService, ProductService>();
builder.Services.AddScoped<IProductReviewService, ProductReviewService>();
builder.Services.AddScoped<IBlogPostService, BlogPostService>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<ISiteSettingsService, SiteSettingsService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IContactMessageService, ContactMessageService>();

// 4. Configure JWT Authentication
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? Environment.GetEnvironmentVariable("Jwt__Key")
    ?? Environment.GetEnvironmentVariable("JWT_KEY")
    ?? "arboveya_super_secret_jwt_key_with_at_least_256_bits_length_12345!";
var jwtIssuer = builder.Configuration["Jwt:Issuer"]
    ?? Environment.GetEnvironmentVariable("Jwt__Issuer")
    ?? Environment.GetEnvironmentVariable("JWT_ISSUER")
    ?? "ArboveyaApi";
var jwtAudience = builder.Configuration["Jwt:Audience"]
    ?? Environment.GetEnvironmentVariable("Jwt__Audience")
    ?? Environment.GetEnvironmentVariable("JWT_AUDIENCE")
    ?? "ArboveyaClient";

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = false; // Set to true in production
    options.SaveToken = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ValidateIssuer = true,
        ValidIssuer = jwtIssuer,
        ValidateAudience = true,
        ValidAudience = jwtAudience,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero,
        RoleClaimType = System.Security.Claims.ClaimTypes.Role
    };
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var authHeader = context.Request.Headers.Authorization.ToString().Trim();
            if (!string.IsNullOrEmpty(authHeader))
            {
                var token = authHeader;
                // Strip redundant "Bearer " prefixes (e.g., "Bearer Bearer ...")
                while (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    token = token["Bearer ".Length..].Trim();
                }
                // Strip any accidental surrounding quotes
                token = token.Trim('\"', '\'');
                context.Token = token;
            }
            return Task.CompletedTask;
        }
    };
});

builder.Services.AddAuthorization();

// 5. Configure CORS for Frontend Integration
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// 6. Configure Swagger/OpenAPI with JWT Bearer support
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Arboveya Auth & User API",
        Version = "v1",
        Description = "ASP.NET Core Web API with PostgreSQL database and JWT Authentication"
    });

    // Define JWT Bearer security scheme as HTTP Bearer
    var securityScheme = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Description = "Paste your JWT token (with or without 'Bearer ')",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    };

    options.AddSecurityDefinition("Bearer", securityScheme);
    options.AddSecurityRequirement(doc => new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecuritySchemeReference("Bearer", doc),
            new List<string>()
        }
    });
});

var app = builder.Build();

// Auto create / migrate database schema if available
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILogger<Program>>();
    try
    {
        var dbContext = services.GetRequiredService<AppDbContext>();

        // Ensure __EFMigrationsHistory and Users table exist, then record InitialCreate
        dbContext.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                ""MigrationId"" character varying(150) NOT NULL,
                ""ProductVersion"" character varying(32) NOT NULL,
                CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY (""MigrationId"")
            );

            CREATE TABLE IF NOT EXISTS ""Users"" (
                ""Id"" uuid NOT NULL,
                ""FirstName"" character varying(100) NOT NULL,
                ""LastName"" character varying(100) NOT NULL,
                ""Email"" character varying(255) NOT NULL,
                ""PasswordHash"" text NOT NULL,
                ""Role"" character varying(50) NOT NULL DEFAULT 'Customer',
                ""Address"" text,
                ""Nationality"" character varying(100),
                ""PhoneNumber"" character varying(30),
                ""IsSellerApproved"" boolean NOT NULL DEFAULT false,
                ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                CONSTRAINT ""PK_Users"" PRIMARY KEY (""Id"")
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Users_Email"" ON ""Users"" (""Email"");

            INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
            VALUES ('20260912033313_InitialCreate', '9.0.0')
            ON CONFLICT (""MigrationId"") DO NOTHING;

            DO $$
            BEGIN
                IF EXISTS (SELECT FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'SiteSettings') THEN
                    INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                    VALUES ('20260915043901_AddSiteSettingsEntity', '9.0.0')
                    ON CONFLICT (""MigrationId"") DO NOTHING;
                END IF;
                IF EXISTS (SELECT FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'ContactMessages') THEN
                    INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                    VALUES ('20260915050257_AddContactMessageEntity', '9.0.0')
                    ON CONFLICT (""MigrationId"") DO NOTHING;
                END IF;
            END $$;
        ");

        // Automatically applies any pending migrations to the database
        dbContext.Database.Migrate();
        dbContext.Database.ExecuteSqlRaw(@"
            ALTER TABLE ""BlogPosts"" ADD COLUMN IF NOT EXISTS ""Category"" character varying(100) DEFAULT 'Wellness';
            ALTER TABLE ""SiteSettings"" ADD COLUMN IF NOT EXISTS ""AboutHeroSubtitle"" character varying(300);
            ALTER TABLE ""SiteSettings"" ADD COLUMN IF NOT EXISTS ""Mission"" text;
            ALTER TABLE ""SiteSettings"" ADD COLUMN IF NOT EXISTS ""Vision"" text;
            ALTER TABLE ""ContactMessages"" ADD COLUMN IF NOT EXISTS ""UserType"" character varying(50) DEFAULT 'General';
            ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""CountryOfOrigin"" character varying(100);
            ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""ExpiryDate"" timestamp with time zone;
            ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""ManufactureDate"" timestamp with time zone;
            ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""Condition"" character varying(100) DEFAULT 'Brand New';
            ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""Specifications"" text;
            ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""ShippingMethod"" character varying(150);
            ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""EstimatedDeliveryTime"" character varying(100);
            ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""IsFreeShipping"" boolean DEFAULT false;
            ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""ShippingCost"" numeric(18,2) DEFAULT 0;
            ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""HandlingTime"" character varying(100);
            ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""ReturnPolicy"" text;
            ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""ShippingOptions"" text;
            ALTER TABLE ""Orders"" ADD COLUMN IF NOT EXISTS ""TrackingNumber"" character varying(150);
            ALTER TABLE ""Orders"" ADD COLUMN IF NOT EXISTS ""ShippingCarrier"" character varying(100);
            ALTER TABLE ""Orders"" ADD COLUMN IF NOT EXISTS ""ShippingMethod"" character varying(150);
            ALTER TABLE ""Orders"" ADD COLUMN IF NOT EXISTS ""ShippingCost"" numeric(18,2) DEFAULT 0;
            ALTER TABLE ""Orders"" ADD COLUMN IF NOT EXISTS ""ShippedAt"" timestamp with time zone;
            ALTER TABLE ""BlogPosts"" ALTER COLUMN ""ImageUrl"" TYPE text;
            DELETE FROM ""Users"" WHERE ""Email"" IN ('test_arboveya_check@example.com', 'test_cleanup_user@example.com');
        ");

        // Ensure user-requested Admin account exists with configured credentials (from .env or appsettings)
        var adminEmail = (builder.Configuration["Admin:Email"]
            ?? Environment.GetEnvironmentVariable("ADMIN_EMAIL")
            ?? "admin@arboveya.com").Trim();
        var adminPassword = builder.Configuration["Admin:Password"]
            ?? Environment.GetEnvironmentVariable("ADMIN_PASSWORD")
            ?? "arboveya!";

        var existingAdmin = dbContext.Users.FirstOrDefault(u => u.Email.ToLower() == adminEmail.ToLower());
        if (existingAdmin == null)
        {
            var adminUser = new Arboveya.Api.Models.User
            {
                Id = Guid.NewGuid(),
                FirstName = "Admin",
                LastName = "Arboveya",
                Email = adminEmail,
                PasswordHash = BCrypt.Net.BCrypt.EnhancedHashPassword(adminPassword, workFactor: 12),
                Role = "Admin",
                IsSellerApproved = true,
                CreatedAt = DateTime.UtcNow
            };
            dbContext.Users.Add(adminUser);
            dbContext.SaveChanges();
            logger.LogInformation("Admin user {Email} created successfully.", adminEmail);
        }
        else
        {
            existingAdmin.FirstName = "Admin";
            existingAdmin.LastName = "Arboveya";
            existingAdmin.Role = "Admin";
            existingAdmin.IsSellerApproved = true;
            existingAdmin.PasswordHash = BCrypt.Net.BCrypt.EnhancedHashPassword(adminPassword, workFactor: 12);
            dbContext.SaveChanges();
            logger.LogInformation("Admin user {Email} updated with latest credentials.", adminEmail);
        }



        logger.LogInformation("Database verified and migrations applied.");
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Could not automatically initialize database. Please ensure PostgreSQL is running and credentials in appsettings.json are valid.");
    }
}

// 7. Configure HTTP Request Pipeline
// Enable Swagger in all environments (Development & Production)
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Arboveya Auth API v1");
    c.RoutePrefix = "swagger";
});


app.UseCors("AllowAll");
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Redirect root URL to Swagger UI for convenience
app.MapGet("/", () => Results.Redirect("/swagger"));

app.Run();
