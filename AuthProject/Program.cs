using AuthProject.Configuration;
using AuthProject.Exceptions;
using AuthProject.Middlewares;
using AuthProject.Security.Authentication;
using Microsoft.OpenApi;

if (args.Length == 2 && args[0] == "--generate-jwt-key")
{
    JwtKeyFile.Generate(args[1]);
    Console.WriteLine("JWT key file created. Restrict its access permissions and keep it outside source control.");
    return;
}

var builder = WebApplication.CreateBuilder(args);
LocalDotEnv.AddToConfiguration(builder.Configuration, builder.Environment.EnvironmentName, builder.Environment.ContentRootPath);

// Add services to the container.

builder.Services.AddApiFoundation(builder.Configuration);
builder.Services.AddAuthPersistence(builder.Configuration, builder.Environment);
builder.Services.AddBruteForceProtection(builder.Configuration);
builder.Services.AddRegistration(builder.Configuration);
builder.Services.AddLogin(builder.Configuration);
builder.Services.AddRefreshSessions(builder.Configuration);
builder.Services.AddPasswordRecovery(builder.Configuration);
builder.Services.AddEmailMfa();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] });
    options.OperationFilter<AnonymousSwaggerFilter>();
    options.OperationFilter<CookieCsrfSwaggerFilter>();
});

var app = builder.Build();

app.UseExceptionHandler(new ExceptionHandlerOptions { SuppressDiagnosticsCallback = _ => true });
app.UseForwardedHeaders();
app.UseStatusCodePages(async statusContext =>
{
    var context = statusContext.HttpContext;
    var problems = context.RequestServices.GetRequiredService<ApiProblems>();
    await problems.WriteAsync(context, problems.Create(context, ApiErrors.ForStatus(context.Response.StatusCode)), context.RequestAborted);
});
app.UseRouting();
app.UseCors(RefreshServices.CorsPolicy);
app.UseRequestTimeouts();
app.UseMiddleware<GlobalRateLimitMiddleware>();
app.UseMiddleware<RequestFoundationMiddleware>();
app.UseAuthentication();
app.UseMiddleware<AuthRateLimitMiddleware>();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers().RequireAuthorization();

app.Run();

// Expose the entry point to WebApplicationFactory without adding test endpoints.
public partial class Program { }
