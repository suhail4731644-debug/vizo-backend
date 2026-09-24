// ═══════════════════════════════════════════════════════════════════════════
//  CONFIGURATION
// ═══════════════════════════════════════════════════════════════════════════
//
//  Backend settings, INCLUDING the credentials, live in
//  vizo-backend/appsettings.json, which is committed to this repository. The
//  Neon connection string, the JWT signing key, both Cloudinary API secrets,
//  the Gmail app password and the VAPID pair are all in that file.
//
//  Frontend variables live in vizo-erp/.env.local, which is NOT committed --
//  copy vizo-erp/.env.example and fill it in. The only one that matters beyond
//  the API URL is NEXT_PUBLIC_VAPID_PUBLIC_KEY, which must match
//  VapidSettings:PublicKey in appsettings.json or push subscriptions are
//  rejected when the server tries to use them.
//
//  Environment variables override appsettings.json where they are set, using
//  the double-underscore form (ConnectionStrings__DefaultConnection). Nothing
//  has to be set for the app to run.
//
//  ---------------------------------------------------------------------------
//  CORS
//  ---------------------------------------------------------------------------
//
//  Cors:AllowedOrigins must list the exact origin the browser sends, scheme and
//  port included. A missing entry fails the preflight before any of this code
//  is written, which looks exactly like a CORS bug and is not one.
//
// ═══════════════════════════════════════════════════════════════════════════

using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using vizo_backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using vizo_backend.Models;

var builder = WebApplication.CreateBuilder(args);

/*  Configuration comes from appsettings.json.

    The connection string, the JWT signing key, both Cloudinary API secrets, the
    Gmail app password and the VAPID pair all live there, by the project owner's
    decision.

    Environment variables still win where they are set -- ASP.NET Core layers
    configuration that way by default, with the double-underscore form
    (ConnectionStrings__DefaultConnection) -- so a deployment can override any
    of it without editing the file. Nothing has to be set for the app to run. */


/* ─────────────────────────── Database ─────────────────────────── */
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

/* ─────────────────────────── AI ────────────────────────────────
   One client, server-side only. It never calculates -- it reads numbers the
   database already worked out and puts them into a sentence. See
   Services/GeminiClient.cs for why that separation matters. */
builder.Services.AddHttpClient();
builder.Services.AddScoped<vizo_backend.Services.GeminiClient>();

/* ─────────────────────────── Notifications ─────────────────────────
   Writes the bell row and, best effort, pushes to the person's browsers.
   Every trigger point in the app goes through this one class -- see
   Services/PushNotificationService.cs for why a failure here must never fail
   the request that caused it. */
builder.Services.AddScoped<vizo_backend.Services.PushNotificationService>();

/* Once a night: the low-stock digest and the anomaly check. The deviation
   arithmetic is done here, not by a model -- see NightlyInsightsService for
   why asking an AI "is anything wrong" every night produces something wrong
   every night. */
builder.Services.AddHostedService<vizo_backend.Services.NightlyInsightsService>();

/* The bell's live channel. See Services/NotificationHub.cs. */
builder.Services.AddSignalR();

/* Chases the Super Admin every six hours about orders still sitting at
   SUBMITTED. See Services/ConfirmReminderService.cs. */
builder.Services.AddHostedService<vizo_backend.Services.ConfirmReminderService>();

/* ─────────────────────────── Controllers ───────────────────────── */
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        // The graph is heavily self-referencing (Account -> parent -> children).
        // Ignore cycles rather than throwing when a controller returns an entity.
        o.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });

/* ─────────────────────────── JWT bearer ────────────────────────── */
var jwt = builder.Configuration.GetSection("Jwt");
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["Key"]!));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.SaveToken = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt["Issuer"],
            ValidateAudience = true,
            ValidAudience = jwt["Audience"],
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = ClaimTypes.Role,
            NameClaimType = ClaimTypes.NameIdentifier
        };

        /* A WebSocket handshake cannot carry an Authorization header, so the
           browser puts the JWT in the query string instead. Lifted out here,
           and only for the hub paths -- a token in a query string is a token in
           the server log, and that is a trade worth making for one endpoint
           rather than for the whole API. */
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;

                if (!string.IsNullOrEmpty(token) && path.StartsWithSegments("/hubs"))
                    context.Token = token;

                return Task.CompletedTask;
            }
        };
    });

/* ───────────────────────── Authorization ───────────────────────── */
/* Role claim values are the role_key column from the "Role" table, so the
   backend, the JWT and the Next.js proxy (src/proxy.ts) all speak one
   vocabulary: super-admin | accountant | order-dept | sales               */
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("SuperAdmin", p => p.RequireRole("super-admin"));
    options.AddPolicy("Accountant", p => p.RequireRole("accountant", "super-admin"));
    options.AddPolicy("OrderDept", p => p.RequireRole("order-dept", "super-admin"));
    options.AddPolicy("Sales", p => p.RequireRole("sales", "super-admin"));
    options.AddPolicy("Staff", p => p.RequireRole(
        "super-admin", "accountant", "order-dept", "sales"));

    /* Purchases, Inventory, Delivery, Claims and the supplier side of Parties:
       everyone except a sales rep. Mirrors ROUTE_RULES in the front end's
       src/proxy.ts so a screen the proxy allows is a screen the API allows. */
    options.AddPolicy("BackOffice", p => p.RequireRole(
        "super-admin", "accountant", "order-dept"));
});

/* Policies named "perm:something" are built on demand from the holder's
   permissions instead of from a list of role names -- see
   Services/PermissionPolicy.cs for why that had to change. */
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();

/* ─────────────────────────── CORS ──────────────────────────────── */
var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
              ?? new[] { "http://localhost:3000", "https://advpos-frontend.vercel.app" };

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins(origins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

/* ─────────────────────────── Swagger ───────────────────────────── */
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "AdvPOS API", Version = "v1" });
    c.CustomSchemaIds(type => type.FullName);
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the token from POST /api/auth/login. No \"Bearer \" prefix needed."
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

/* ───────────────────── Global exception handler ─────────────────────
   Every controller action already sits inside its own try/catch and reports
   through Fail(). This is the safety net for everything that happens OUTSIDE
   an action body and therefore cannot be caught there: model binding, action
   filters, authorization handlers, JSON serialisation of the response.

   Without it those failures return a bare 500 with an empty body -- which is
   exactly the "no error was shown" problem, just moved one layer out.

   It must be registered FIRST so it wraps everything after it.            */
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                     .CreateLogger("UnhandledException");
        log.LogError(ex, "Unhandled {Method} {Path}", ctx.Request.Method, ctx.Request.Path);

        if (ctx.Response.HasStarted) throw;   // too late to rewrite the response

        ctx.Response.Clear();
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        ctx.Response.ContentType = "application/json";

        var dev = ctx.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment();
        await ctx.Response.WriteAsJsonAsync(new
        {
            message = "The request failed before it reached the controller.",
            error = ex.GetBaseException().Message,
            type = ex.GetBaseException().GetType().Name,
            detail = dev ? ex.ToString() : null
        });
    }
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHttpsRedirection();
}

/* CORS must sit ahead of auth so pre-flight requests are answered. */
app.UseCors("AllowFrontend");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<NotificationHub>(NotificationHub.Path);

app.MapGet("/", () => "AdvPOS API is running. Swagger: /swagger");

app.Run();