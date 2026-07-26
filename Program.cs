using StellaCareApi.Interfaces;
using StellaCareApi.Managers;

namespace StellaCareApi;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddControllers();
        builder.Services.AddOpenApi();

        // Dataset path is configurable; defaults to the docs folder in the repo.
        var datasetPath = builder.Configuration["DatasetPath"]
            ?? Path.Combine(builder.Environment.ContentRootPath, "Docs", "case_backend_dataset_v3.json");

        builder.Services.AddSingleton<IDeviceStore>(_ => new DeviceStore(datasetPath));
        builder.Services.AddSingleton<ITrackingAlgorithm, TrackingAlgorithm>();

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            // Interactive docs at /swagger, backed by the generated /openapi/v1.json.
            app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "StellaCareApi v1"));
        }

        app.MapControllers();

        app.Run();
    }
}
