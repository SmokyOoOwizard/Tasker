using Serilog;
using Serilog.Events;
using Tasker.Server;
using Tasker.Web;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .WriteTo.Console()
    .CreateLogger();

try
{
    Log.Information("Starting Tasker.Server");

    var builder = TaskerWebApp.CreateBuilder<ServerModule>(args, TaskerMode.Server);

    var app = builder.Build();

    app.MapTasker();

    app.Run();
    return 0;
}
catch (Exception e)
{
    Log.Fatal(e, "Tasker.Server crashed");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}
