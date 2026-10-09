using AuthProject.IntegrationTests.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using FluentValidation;

namespace AuthProject.IntegrationTests.Foundation;

internal static class FoundationFactory
{
    public static WebApplicationFactory<Program> Create(string environment = "Testing") =>
        new AuthWebApplicationFactory(environment).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.AddControllers().AddApplicationPart(typeof(ProbeController).Assembly);
                services.AddScoped<IValidator<AsyncRequest>, AsyncRequestValidator>();
            }));

    public static HttpClient Client(this WebApplicationFactory<Program> factory) => factory.CreateClient(
        new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
}
