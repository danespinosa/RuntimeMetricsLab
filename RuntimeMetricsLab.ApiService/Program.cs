using RuntimeMetricsLab.ApiService.Diagnostics;
using RuntimeMetricsLab.ApiService.Endpoints;
using RuntimeMetricsLab.ApiService.Grpc;
using RuntimeMetricsLab.ApiService.Http;
using RuntimeMetricsLab.Experiments;
using RuntimeMetricsLab.Experiments.Abstractions;
using RuntimeMetricsLab.Experiments.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddExceptionHandler<ExperimentExceptionHandler>();
builder.Services.AddProblemDetails();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Experiment framework: catalog, coordinator, metrics, and in-process workloads.
builder.Services.AddRuntimeMetricsLabExperiments();
builder.Services.AddRuntimeMetricsMonitoring();

// Host-only implementations that require ASP.NET Core hosting / gRPC packages, which
// RuntimeMetricsLab.Experiments intentionally does not reference.
builder.Services.AddSingleton<ILoopbackHttpClientProvider, LoopbackHttpClientProvider>();
builder.Services.AddSingleton<IGrpcWorkloadInvoker, GrpcWorkloadInvoker>();

builder.Services.AddGrpc();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGrpcService<WorkloadGrpcService>();

app.MapExperimentEndpoints();
app.MapMetricsEndpoints();
app.MapWorkloadEndpoints();

app.MapDefaultEndpoints();

app.Run();
