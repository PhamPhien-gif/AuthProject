using AuthProject.Configuration;
using AuthProject.Exceptions;
using AuthProject.Middlewares;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddApiFoundation(builder.Configuration);
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseExceptionHandler(new ExceptionHandlerOptions { SuppressDiagnosticsCallback = _ => true });
app.UseStatusCodePages(async statusContext =>
{
    var context = statusContext.HttpContext;
    var problems = context.RequestServices.GetRequiredService<ApiProblems>();
    await problems.WriteAsync(context, problems.Create(context, ApiErrors.ForStatus(context.Response.StatusCode)), context.RequestAborted);
});
app.UseRouting();
app.UseRequestTimeouts();
app.UseMiddleware<RequestFoundationMiddleware>();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();

// Expose the entry point to WebApplicationFactory without adding test endpoints.
public partial class Program { }
